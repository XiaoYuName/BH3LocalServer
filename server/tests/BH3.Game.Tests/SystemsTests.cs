using BH3.Game.Campaign;
using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Operations;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed partial class SystemsTests:IDisposable
{
    private readonly TestDirectory temp=new();
    private readonly SqliteConnectionFactory db;
    private readonly LobbyStore store;
    private readonly Clock clock=new();
    private readonly SystemsService systems;
    private readonly GmService gm;
    private const uint Uid=10001;
    public SystemsTests()
    {
        db=new(Path.Combine(temp.Path,"systems.db"));SchemaMigrator.Initialize(db);store=new(db);
        store.EnsurePlayer(Uid,"fixture",new(88,200,101,20001,59101,1,[],Hcoin:10000,Scoin:10000000));
        systems=new(store,clock);gm=new(store,clock);
    }
    private void Grant(params GrantItem[] items)=>gm.Execute(Uid,new(Guid.NewGuid().ToString(),"grant",items));
    [Fact] public void TreasureConsumesRealInventoryHasFourUseGuaranteeAndSurvivesRestart()
    {
        Grant(new GrantItem("material",3509,4));
        var result=systems.Use(Uid,new(){MaterialId=3509,Num=4});Assert.Equal(UseMaterialRsp.Types.Retcode.Succ,result.Retcode);
        Assert.Equal(4,result.GiftRewardList.Count);Assert.Contains(result.GiftRewardList,r=>r.ItemList.Any(i=>i.Id==1110));
        Assert.Equal(0u,store.Campaign(Uid,tx=>tx.Campaign.Materials[3509]));
        Assert.Equal(UseMaterialRsp.Types.Retcode.MaterialLack,new SystemsService(new LobbyStore(db),clock).Use(Uid,new(){MaterialId=3509,Num=1}).Retcode);
    }
    [Fact] public void InvalidMaterialUseDoesNotChangeAnyBalance()
    {
        Grant(new GrantItem("material",3509,1));var before=store.Read(Uid);
        Assert.Equal(UseMaterialRsp.Types.Retcode.InvalidNum,systems.Use(Uid,new(){MaterialId=3509,Num=0}).Retcode);
        Assert.Equal(UseMaterialRsp.Types.Retcode.FeatureClosed,systems.Use(Uid,new(){MaterialId=1001,Num=1}).Retcode);
        Assert.Equal(before,store.Read(Uid));Assert.Equal(1u,store.Campaign(Uid,tx=>tx.Campaign.Materials[3509]));
    }
    [Fact] public void AstralOp180UsesOnePartForTheReportedPromotion()
    {
        Grant(new GrantItem("elf",180));Grant(new GrantItem("material",370180,30));
        var rsp=new CompanionService(store).StarUp(Uid,new(){ElfId=180});Assert.Equal(ElfStarUpRsp.Types.Retcode.Succ,rsp.Retcode);
        Assert.Equal(2u,store.Campaign(Uid,tx=>tx.Campaign.Companions[180].Star));Assert.Equal(29u,store.Campaign(Uid,tx=>tx.Campaign.Materials[370180]));
    }
    [Fact] public void Free65GiftGrantsBothItemsOnceAndChecksLevel()
    {
        uint shop=GmService.ShopCatalog.Shops.First(x=>x.Goods.Contains(700597u)).Id;
        var result=gm.BuyGoods(Uid,new(){ShopId=shop,GoodsId=700597});Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,result.Retcode);Assert.Equal(1u,result.Num);
        Assert.Equal(2u,store.Campaign(Uid,tx=>tx.Campaign.Materials[6004]));Assert.Equal(600u,store.Campaign(Uid,tx=>tx.Campaign.Materials[915]));
        Assert.Equal(BuyGoodsRsp.Types.Retcode.BuyTimesLack,gm.BuyGoods(Uid,new(){ShopId=shop,GoodsId=700597}).Retcode);
        Assert.Equal(10000u,store.Read(Uid).Hcoin);
    }
    [Fact] public void DailyLoginClaimsOnlyOnceAndRolloverRestoresTheTask()
    {
        var campaign=new CampaignService(store,clock);Assert.Contains(campaign.Missions(Uid).MissionList,x=>x.MissionId==38169&&x.Status==MissionStatus.Finish);
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.Succ,campaign.Claim(Uid,new(){MissionIdList={38169}}).Retcode);
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.MissionStatusError,campaign.Claim(Uid,new(){MissionIdList={38169}}).Retcode);
        Assert.Contains(38169u,campaign.Missions(Uid).CloseMissionList);clock.Now=clock.Now.AddDays(1);
        Assert.Contains(campaign.Missions(Uid).MissionList,x=>x.MissionId==38169&&x.Status==MissionStatus.Finish);
    }
    [Fact] public void MissionBatchFailureDoesNotPartiallyGrant()
    {
        var before=store.Read(Uid);var campaign=new CampaignService(store,clock);
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.MissionStatusError,campaign.Claim(Uid,new(){MissionIdList={38169,38161}}).Retcode);
        Assert.Equal(before,store.Read(Uid));Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Systems.DailyClaims));
    }
    [Fact] public void PassFreeExpClaimsOnceAndRewardsPersistAcrossRestart()
    {
        Assert.Equal(1u,systems.Pass(Uid).Level);Assert.Equal(1000u,systems.PhaseExp(Uid,new()).AddExp);
        Assert.Equal(2u,systems.Pass(Uid).Level);Assert.NotEqual(TakeBattlePassPhaseExpRsp.Types.Retcode.Succ,systems.PhaseExp(Uid,new()).Retcode);
        Assert.Equal(2,systems.PassClaim(Uid,new()).BasicRewardList.Count);
        Assert.Empty(new SystemsService(new LobbyStore(db),clock).PassClaim(Uid,new()).BasicRewardList);
    }
    [Fact] public void PassUpgradeDebitsAndAllowsRetroactivePremiumRewardsOnlyOnce()
    {
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Mcoin=2000;return 0;});systems.PassClaim(Uid,new());
        Assert.Equal(BuyBattlePassTicketRsp.Types.Retcode.Succ,systems.BuyTicket(Uid,new(){Type=(BattlePassTicketType)2}).Retcode);
        Assert.Equal(1400u,store.Campaign(Uid,tx=>tx.Campaign.Operations.Mcoin));
        var claim=systems.PassClaim(Uid,new());Assert.Empty(claim.BasicRewardList);Assert.Single(claim.AdvancedRewardList);
        Assert.Equal(BuyBattlePassTicketRsp.Types.Retcode.HasGot,systems.BuyTicket(Uid,new(){Type=(BattlePassTicketType)2}).Retcode);
    }
    [Fact] public void WikiRewardPersistsAndRejectsDuplicateAndUnattainedRanks()
    {
        Assert.Equal(TakeWikiRatingRewardRsp.Types.Retcode.Succ,systems.WikiClaim(Uid,new(){RatingId=1,RatingScore=110}).Retcode);
        Assert.Equal(10020u,store.Read(Uid).Hcoin);Assert.Equal(10050000u,store.Read(Uid).Scoin);
        Assert.Contains(1u,new SystemsService(new LobbyStore(db),clock).Wiki(Uid).HasTakeRatingRewardList);
        Assert.NotEqual(TakeWikiRatingRewardRsp.Types.Retcode.Succ,systems.WikiClaim(Uid,new(){RatingId=1,RatingScore=110}).Retcode);
        Assert.NotEqual(TakeWikiRatingRewardRsp.Types.Retcode.Succ,systems.WikiClaim(Uid,new(){RatingId=7,RatingScore=uint.MaxValue}).Retcode);
    }
    [Fact] public void GrandKeySelectionActivationDebitExpirationAndResetArePersisted()
    {
        store.Campaign(Uid,tx=>{tx.Campaign.Systems.GrandKeys[201]=new GrandKey{Id=201,Level=10}.ToByteArray();return 0;});
        var skill=new GrandKeySkill{KeyId=201,SkillId=20110,LastTime=10800};
        Assert.Equal(GrandKeySetSkillRsp.Types.Retcode.Succ,systems.KeySelect(Uid,new(){KeyList={skill}}).Retcode);
        Assert.Equal(GrandKeyActivateSkillRsp.Types.Retcode.Succ,systems.KeyActivate(Uid,new(){KeyList={skill}}).Retcode);
        var key=Assert.Single(systems.GrandKeys(Uid,new()).KeyList);Assert.Equal(10u,key.ActivateLevel);Assert.True(key.EndTime>clock.Now.ToUnixTimeSeconds());
        Assert.True(store.Read(Uid).Scoin<10000000);
        Assert.Equal(GrandKeyResetRsp.Types.Retcode.Succ,systems.KeyReset(Uid,new(){KeyIdList={201}}).Retcode);
        Assert.Equal(0u,Assert.Single(systems.GrandKeys(Uid,new()).KeyList).EndTime);
    }
    [Fact] public void GmActivityTimeAndRevisionAreEnforced()
    {
        var a=new LocalActivity(17001,"测试","内容",true,1,2145916800);
        Assert.Equal(1u,gm.Execute(Uid,new(Guid.NewGuid().ToString(),"activity",Activity:a)).Revision);
        Assert.Contains(systems.Bulletins(Uid,new()).BulletinList,x=>x.Id==17001);
        Assert.Throws<ArgumentException>(()=>gm.Execute(Uid,new(Guid.NewGuid().ToString(),"activity",Activity:a)));
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"activity",Revision:1,Activity:a with{Enabled=false}));
        Assert.DoesNotContain(systems.Bulletins(Uid,new()).BulletinList,x=>x.Id==17001);
    }
    public void Dispose()=>temp.Dispose();
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1791558000);public override DateTimeOffset GetUtcNow()=>Now;}
}
