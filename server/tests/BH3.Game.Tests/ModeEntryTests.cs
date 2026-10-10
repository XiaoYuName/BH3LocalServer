using BH3.Game.Campaign;
using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class ModeEntryTests:IDisposable
{
    private readonly TestDirectory temp=new();
    private readonly SqliteConnectionFactory db;
    private readonly LobbyStore store;
    private readonly Clock clock=new();
    private const uint Uid=10001;
    public ModeEntryTests()
    {
        db=new(Path.Combine(temp.Path,"modes.db"));SchemaMigrator.Initialize(db);store=new(db);
        store.EnsurePlayer(Uid,"fixture",new(88,200,101,20001,59101,1,[],Hcoin:1000,Scoin:5000));
        store.EnsurePlayer(10002,"other",new(1,80,101,20001,59101,1,[]));
    }
    [Fact] public void Free75GiftChecksLevelAndPersistsExactRewardOnce()
    {
        var service=new GmService(store,clock);uint shop=GmService.ShopCatalog.Shops.Single(x=>x.Goods.Contains(700598u)).Id;
        var request=new BuyGoodsReq{ShopId=shop,GoodsId=700598};
        store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Level=74};return 0;});
        Assert.NotEqual(BuyGoodsRsp.Types.Retcode.Succ,service.BuyGoods(Uid,request).Retcode);
        Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Materials));
        store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Level=75};return 0;});
        var result=service.BuyGoods(Uid,request);
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,result.Retcode);Assert.Equal(1u,result.Num);Assert.Equal(1u,result.GoodsBuyTimes);
        var restarted=new GmService(new LobbyStore(db),clock);
        Assert.Equal(BuyGoodsRsp.Types.Retcode.BuyTimesLack,restarted.BuyGoods(Uid,request).Retcode);
        Assert.Equal(2u,store.Campaign(Uid,tx=>tx.Campaign.Materials[6004]));Assert.Equal(600u,store.Campaign(Uid,tx=>tx.Campaign.Materials[915]));
        Assert.Equal(1000u,store.Read(Uid).Hcoin);Assert.Equal(5000u,store.Read(Uid).Scoin);
        Assert.Equal(0u,store.Campaign(Uid,tx=>tx.Campaign.Operations.Mcoin));
    }
    [Fact] public void GodWarSnapshotContainsLobbyWithoutCopyingCapturedProgress()
    {
        var service=new ModeEntryService(store,clock);var data=Assert.Single(service.GodWar(Uid,new()).GodWarList);
        Assert.Equal(2u,data.LobbyId);Assert.Equal(1u,data.CurChapterId);Assert.Equal(3,data.ChapterList.Count);
        Assert.Contains(data.TaleList,t=>t.TaleId==100);Assert.All(data.TaleList,t=>{Assert.NotNull(t.Challenge);Assert.Empty(t.ChallengeHistoryList);Assert.All(t.OverallList,o=>Assert.Equal(0u,o.OverallVal));});
        Assert.Empty(data.TalentList);Assert.Equal(0u,data.MaxSupportPoint);
        Assert.All(data.RoleInfo.RoleRelationList,r=>{Assert.Equal(1u,r.Level);Assert.Equal(0u,r.RewardHasTakeLevel);Assert.Empty(r.RewardHasTakeStoryList);});
        Assert.Empty(data.RoleInfo.MainAvatarIdList); // fixture owns 101 only, not captured account's roster
        Assert.Equal(GetGodWarRsp.Types.Retcode.NotOpen,service.GodWar(10002,new()).Retcode);
        Assert.Equal(GetGodWarRsp.Types.Retcode.NotOpen,service.GodWar(Uid,new(){GodWarId=999}).Retcode);
        Assert.Equal(GetGodWarLobbyRsp.Types.Retcode.Succ,service.Lobby(Uid,new(){GodWarId=1,LobbyId=2}).Retcode);
        Assert.Equal(GetGodWarLobbyRsp.Types.Retcode.NotOpen,service.Lobby(Uid,new(){GodWarId=1,LobbyId=999}).Retcode);
    }
    [Fact] public void LobbyEntryRetryAndExitCannotSpendOrOverwriteCombat()
    {
        var campaign=new CampaignService(store,clock);
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ,campaign.Begin(Uid,new(){StageId=10101,AvatarIdList={101}}).Retcode);
        var oldRun=store.Campaign(Uid,tx=>tx.Campaign.Run);var wallet=store.Read(Uid);
        var request=new StageBeginReq{StageId=170199,AvatarTrialIdList={2214}};
        var begin=campaign.Begin(Uid,request);Assert.Equal(StageBeginRsp.Types.Retcode.Succ,begin.Retcode);
        Assert.Equal(begin.ToByteArray(),new CampaignService(new LobbyStore(db),clock).Begin(Uid,request).ToByteArray());
        Assert.Equal(oldRun!.Key,store.Campaign(Uid,tx=>tx.Campaign.Run!.Key));Assert.Equal(wallet,store.Read(Uid));
        var end=new StageEndReq{Sign="scene",Body=new StageEndReqBody{StageId=170199,EndStatus=StageEndStatus.StageWin}.ToByteString()};
        var result=campaign.End(Uid,end);Assert.Equal(StageEndRsp.Types.Retcode.Succ,result.Retcode);
        Assert.Equal(0u,result.ScoinReward);Assert.Equal(0u,result.Progress);Assert.False(result.IsFirstWin);Assert.Null(result.LineEnhanceRewardData);
        Assert.Equal(result.ToByteArray(),campaign.End(Uid,end).ToByteArray());Assert.Equal(wallet,store.Read(Uid));
        Assert.Equal(oldRun.Key,store.Campaign(Uid,tx=>tx.Campaign.Run!.Key));
        Assert.False(store.Campaign(Uid,tx=>tx.Campaign.Stages.ContainsKey(170199)));
    }
    [Fact] public void LobbyRejectsUnknownAvatarLowLevelAndUnreservedExit()
    {
        var campaign=new CampaignService(store,clock);
        Assert.Equal(StageBeginRsp.Types.Retcode.LevelLack,campaign.Begin(10002,new(){StageId=170199}).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.AvatarError,campaign.Begin(Uid,new(){StageId=170199,AvatarIdList={999999}}).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.AvatarError,campaign.Begin(Uid,new(){StageId=170199,AvatarTrialIdList={999999}}).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.StageNotExist,campaign.Begin(Uid,new(){StageId=170198}).Retcode);
        Assert.Equal(StageEndRsp.Types.Retcode.StageError,campaign.End(Uid,new(){Body=new StageEndReqBody{StageId=170199}.ToByteString()}).Retcode);
        Assert.Null(store.Campaign(Uid,tx=>tx.Campaign.ModeEntries.GodWarLobbyBegin));
    }
    [Fact] public void MirageUsesCapturedScheduleAndPlayerSpecificProgressAcrossRestart()
    {
        var service=new ModeEntryService(store,clock);var view=service.Mirage(Uid);
        Assert.Equal(GetThemeWantedRsp.Types.Retcode.Succ,view.Retcode);Assert.Equal(5u,view.ThemeWantedActivity.ScheduleId);
        Assert.Equal(11105u,view.ThemeWantedActivity.ActivityId);Assert.Equal(new uint[]{17,18,19,20},view.ThemeWantedActivity.OpenStageGroupIdList);
        Assert.All(view.ThemeWantedActivity.StageGroupInfoList,g=>Assert.Equal(0u,g.Progress));
        store.Campaign(Uid,tx=>{tx.Campaign.ModeEntries.MirageProgress[17]=2;return 0;});
        Assert.Equal(2u,new ModeEntryService(new LobbyStore(db),clock).Mirage(Uid).ThemeWantedActivity.StageGroupInfoList.Single(g=>g.StageGroupId==17).Progress);
        Assert.Equal(GetThemeWantedRsp.Types.Retcode.NotOpen,service.Mirage(10002).Retcode);
        var wallet=store.Read(Uid);
        for(int n=0;n<3;n++)Assert.Equal(ThemeWantedRefreshTicketRsp.Types.Retcode.Succ,service.RefreshMirage(Uid).Retcode);
        Assert.Equal(wallet,store.Read(Uid));Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Materials));
    }
    public void Dispose()=>temp.Dispose();
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>DateTimeOffset.FromUnixTimeSeconds(1791558000);}
}
