using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Operations;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed partial class EconomyRegressionTests
{
    [Fact] public void LunaScreenshotS3ToSSCosts25AndAllRemainingStepsReachSSS()
    {
        Grant(new("avatar",507),new("material",10507,65));
        store.Campaign(Uid,tx=>{var(a,e)=GrantService.Inventory(tx);var v=a.AvatarList.Single(x=>x.AvatarId==507);
            v.Star=3;v.SubStar=3;GrantService.Save(tx,a,e,Uid);return 0;});
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.Succ,gm.PromoteAvatar(Uid,new(){AvatarId=507}).Retcode);
        Assert.Equal((4u,0u,40u),(Avatar(507).Star,Avatar(507).SubStar,Avatar(507).Fragment));
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.FragmentLack,gm.PromoteAvatar(Uid,new(){AvatarId=507}).Retcode);
        Grant(new GrantItem("material",10507,160));
        for(int i=0;i<4;i++)Assert.Equal(AvatarStarUpRsp.Types.Retcode.Succ,new GmService(store,clock).PromoteAvatar(Uid,new(){AvatarId=507}).Retcode);
        Assert.Equal((5u,0u,0u),(Avatar(507).Star,Avatar(507).SubStar,Avatar(507).Fragment));
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.StarFull,gm.PromoteAvatar(Uid,new(){AvatarId=507}).Retcode);
    }
    [Fact] public void HallUsesTwoNativeImageBannersAndKeepsActivitiesSeparate()
    {
        var systems=new SystemsService(store,clock);var full=systems.Bulletins(Uid,new());
        var banners=full.BulletinList.Where(x=>x.Type==2).ToArray();Assert.Equal(2,banners.Length);
        Assert.All(banners,x=>{Assert.StartsWith("event/MainMenu/9.1_",x.BannerPath);Assert.Contains('|',x.Content);});
        Assert.Equal(31,systems.ActivityConfigs(Uid).Length);
        var selected=systems.Bulletins(Uid,new(){Type=GetBulletinReq.Types.ReqBulletinType.BulletinContent,BulletinIdList={banners[0].Id}});
        Assert.False(selected.IsAll);Assert.Single(selected.BulletinList);Assert.Equal(banners[0].BannerPath,selected.BulletinList[0].BannerPath);
        var stamps=systems.Bulletins(Uid,new(){Type=GetBulletinReq.Types.ReqBulletinType.BulletinUpdateTime});
        Assert.Equal(full.BulletinList.Select(x=>x.Id),stamps.BulletinList.Select(x=>x.Id));
        Assert.All(stamps.BulletinList,x=>{Assert.True(x.UpdateTime>0);Assert.Equal(1u,x.ClientReqType);Assert.False(x.HasBannerPath);});
    }
    [Fact] public void VipListsAllThirteenTiersWithoutImportingCapturedAccountProgress()
    {
        var data=gm.VipRewards(Uid);Assert.Equal(13,data.VipRewardList.Count);Assert.Equal(0u,data.TotalPayHcoin);
        Assert.All(data.VipRewardList,t=>{Assert.NotEmpty(t.RewardIdList);Assert.Empty(t.TakenRewardIdList);});
        Assert.Equal(GetVipRewardRsp.Types.Retcode.PayHcoinLack,gm.ClaimVip(Uid,new(){VipLevelList={1}}).Retcode);
        Assert.Equal(GetVipRewardRsp.Types.Retcode.Fail,gm.ClaimVip(Uid,new(){VipLevelList={999}}).Retcode);
        Assert.Equal(GetVipRewardRsp.Types.Retcode.Fail,gm.ClaimVip(Uid,new()).Retcode);
    }
    [Fact] public void VipCountsCashEquivalentOnceExcludesGmAndGiftCoinExchanges()
    {
        Grant(new GrantItem("hcoin",101,300));Assert.Equal(0u,gm.VipRewards(Uid).TotalPayHcoin);
        gm.PurchaseProduct(Uid,"Bh3MCoinTier1");Assert.Equal(60u,gm.VipRewards(Uid).TotalPayHcoin);
        Assert.Equal(ExchangeHcoinByMcoinRsp.Types.Retcode.Succ,gm.ExchangeMcoin(Uid,new(){ProductName="Bh3FirstHardCoinTier1",McoinPrice=60}).Retcode);
        Assert.Equal(60u,gm.VipRewards(Uid).TotalPayHcoin);
        gm.PurchaseProduct(Uid,"Bh3GiftHardCoinTier5");Assert.Equal(360u,new GmService(store,clock).VipRewards(Uid).TotalPayHcoin);
        gm.TakeCardDaily(Uid);Assert.Equal(360u,gm.VipRewards(Uid).TotalPayHcoin);
    }
    [Fact] public void LegacyPaymentReceiptsBackfillExactlyOnceWithoutCountingExchanges()
    {
        store.Campaign(Uid,tx=>{var s=tx.Campaign.Operations;s.ProductPurchases["Bh3FirstHardCoinTier60"]=2;
            s.ProductPurchases["Bh3MCoinTier60"]=2;s.ProductPurchases["Bh3GiftHardCoinTier5"]=1;
            s.Audit.Add(new(1,"local-payment","本地模拟购买 6480枚礼包币；水晶 +0，礼包币 +6480。"));
            s.Audit.Add(new(1,"local-payment","本地模拟购买 30天水晶大礼包；水晶 +300，礼包币 +0。"));return 0;});
        // Use the catalogue's exact description, as old payment receipts do.
        var p=gm.ProductList(Uid).ProductList.First(x=>x.Name=="Bh3MCoinTier60");
        store.Campaign(Uid,tx=>{
            tx.Campaign.Operations.Audit[0]=new(1,"local-payment",$"本地模拟购买 {p.Desc}；水晶 +0，礼包币 +6480。");return 0;});
        Assert.Equal(6780u,gm.VipRewards(Uid).TotalPayHcoin);Assert.Equal(6780u,gm.VipRewards(Uid).TotalPayHcoin);
        gm.PurchaseProduct(Uid,"Bh3MCoinTier1");Assert.Equal(6840u,gm.VipRewards(Uid).TotalPayHcoin);
    }
    [Fact] public void VipClaimsPersistGrantAllRewardKindsAndRejectDuplicatesAtomically()
    {
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.TotalPayHcoin=250000;return 0;});
        var request=new GetVipRewardReq{VipLevelList={Enumerable.Range(1,13).Select(i=>(uint)i)}};
        var result=gm.ClaimVip(Uid,request);Assert.Equal(GetVipRewardRsp.Types.Retcode.Succ,result.Retcode);Assert.Equal(37,result.RewardList.Count);
        Assert.Equal(new uint[]{101001,101084},gm.Medals(Uid,new()).MedalList.Select(x=>x.Id));
        Assert.Equal(new uint[]{200004,200029},gm.Frames(Uid).FrameList.Select(x=>x.Id));
        Assert.All(gm.VipRewards(Uid).VipRewardList,t=>Assert.Equal(t.RewardIdList,t.TakenRewardIdList));
        var lobby=store.Read(Uid);var inventory=store.Inventory(Uid)!;
        Assert.Equal(GetVipRewardRsp.Types.Retcode.RepeatGet,new GmService(store,clock).ClaimVip(Uid,request).Retcode);
        Assert.Equal(lobby,store.Read(Uid));Assert.Equal(inventory.Equipment,store.Inventory(Uid)!.Equipment);
        store.EnsurePlayer(10002,"other",new(88,80,101,20001,59101,1,[]));Assert.Equal(0u,gm.VipRewards(10002).TotalPayHcoin);
        Assert.All(gm.VipRewards(10002).VipRewardList,t=>Assert.Empty(t.TakenRewardIdList));
    }
    [Fact] public void VipInvalidBatchAndInventoryOverflowDoNotPartiallyGrant()
    {
        gm.PurchaseProduct(Uid,"Bh3MCoinTier1");
        Assert.Equal(GetVipRewardRsp.Types.Retcode.PayHcoinLack,gm.ClaimVip(Uid,new(){VipLevelList={1,2}}).Retcode);
        Assert.All(gm.VipRewards(Uid).VipRewardList,t=>Assert.Empty(t.TakenRewardIdList));
        store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Scoin=999999999};return 0;});
        var before=store.Inventory(Uid);
        Assert.Equal(GetVipRewardRsp.Types.Retcode.Fail,gm.ClaimVip(Uid,new(){VipLevelList={1}}).Retcode);
        Assert.Equal(before?.Equipment,store.Inventory(Uid)?.Equipment);Assert.Empty(gm.VipRewards(Uid).VipRewardList[0].TakenRewardIdList);
    }
    [Fact] public void VipWirePublishesClaimsBeforeRewardCallback()
    {
        gm.PurchaseProduct(Uid,"Bh3MCoinTier1");var d=new GameDispatcher(new LobbyHandlers(store,new SqlitePlayerStore(db),new byte[32],clock).Create());
        var s=new GameSession(1,"fixture",1);s.Authenticate(Uid);
        var result=d.Dispatch(s,new GamePacket(new byte[26],6719,[],new GetVipRewardReq{VipLevelList={1}}.ToByteArray()));
        Assert.Equal(GetVipRewardRsp.Types.Retcode.Succ,GetVipRewardRsp.Parser.ParseFrom(result.Replies.Single(p=>p.CommandId==6720).Body).Retcode);
        Assert.Equal(6718,result.Replies[0].CommandId);
        Assert.NotEmpty(GetVipRewardDataRsp.Parser.ParseFrom(result.Replies[0].Body).VipRewardList[0].TakenRewardIdList);
    }
}
