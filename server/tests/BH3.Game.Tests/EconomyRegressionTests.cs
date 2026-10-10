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

public sealed partial class EconomyRegressionTests:IDisposable
{
    private readonly TestDirectory temp=new();
    private readonly SqliteConnectionFactory db;
    private readonly LobbyStore store;
    private readonly Clock clock=new();
    private readonly GmService gm;
    private const uint Uid=10001;
    public EconomyRegressionTests()
    {
        db=new(Path.Combine(temp.Path,"economy.db"));SchemaMigrator.Initialize(db);
        store=new(db);store.EnsurePlayer(Uid,"fixture",new(88,80,101,20001,59101,1,[],Hcoin:10000,Scoin:10000));gm=new(store,clock);
    }
    private Avatar Avatar(uint id)=>GetAvatarDataRsp.Parser.ParseFrom(store.Inventory(Uid)!.Avatars).AvatarList.Single(x=>x.AvatarId==id);
    private void Grant(params GrantItem[] items)=>gm.Execute(Uid,new(Guid.NewGuid().ToString(),"grant",items));
    [Fact] public void MonthlyCardButtonFollowsPurchaseClaimDayRolloverAndExpiry()
    {
        Assert.Equal(0u,gm.CardInfo(Uid).CardProductInfoList.Single().Hcoin);
        Assert.Equal(TakeCardProductDailyRewardRsp.Types.Retcode.NotOpen,gm.TakeCardDaily(Uid).Retcode);
        gm.PurchaseProduct(Uid,"Bh3GiftHardCoinTier5");
        Assert.Equal(10300u,store.Read(Uid).Hcoin);
        Assert.Equal(60u,gm.CardInfo(Uid).CardProductInfoList.Single().Hcoin);
        Assert.Single(gm.TakeCardDaily(Uid).CardProductRewardList);
        Assert.Equal(0u,gm.CardInfo(Uid).CardProductInfoList.Single().Hcoin);
        Assert.NotEqual(TakeCardProductDailyRewardRsp.Types.Retcode.Succ,gm.TakeCardDaily(Uid).Retcode);
        clock.Now=DateTimeOffset.FromUnixTimeSeconds(1791576000); // 04:00 China, next day.
        Assert.Equal(60u,new GmService(store,clock).CardInfo(Uid).CardProductInfoList.Single().Hcoin);
        Assert.Single(gm.TakeCardDaily(Uid).CardProductRewardList);
        clock.Now=clock.Now.AddDays(31);
        Assert.Equal(0u,gm.CardInfo(Uid).CardProductInfoList.Single().Hcoin);
        Assert.Equal(TakeCardProductDailyRewardRsp.Types.Retcode.NotOpen,gm.TakeCardDaily(Uid).Retcode);
        Assert.Equal(10420u,store.Read(Uid).Hcoin);
    }
    [Fact] public void FifteenDailyClaimsAwardExactlyOneBonusAndResetDisplayedProgress()
    {
        gm.PurchaseProduct(Uid,"Bh3GiftHardCoinTier5");
        for(int i=0;i<15;i++){Assert.Single(gm.TakeCardDaily(Uid).CardProductRewardList);clock.Now=clock.Now.AddDays(1);}
        Assert.Equal(15u,gm.CardInfo(Uid).CardProductInfoList.Single().TakeRewardDays);
        Assert.Equal(500u,Assert.Single(gm.TakeCardBonus(Uid).CardProductRewardList).Hcoin);
        Assert.Equal(0u,gm.CardInfo(Uid).CardProductInfoList.Single().TakeRewardDays);
        Assert.Empty(gm.TakeCardBonus(Uid).CardProductRewardList);Assert.Equal(11700u,store.Read(Uid).Hcoin);
    }
    [Fact] public void ScreenshotBundleDebitsGiftCoinsGrantsContentsAndReturnsPopupQuantity()
    {
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Mcoin=5000;return 0;});
        var small=gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=721565});
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,small.Retcode);Assert.Equal(1u,small.Num);
        Assert.Equal(4020u,gm.Mcoin(Uid));Assert.Equal(10188u,store.Read(Uid).Hcoin);
        Assert.Equal(5u,store.Campaign(Uid,tx=>tx.Campaign.Materials[1103]));
        var large=gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=721566});
        Assert.Equal(1u,large.Num);Assert.Equal(2040u,gm.Mcoin(Uid));Assert.Equal(10476u,store.Read(Uid).Hcoin);
        Assert.Equal(15u,store.Campaign(Uid,tx=>tx.Campaign.Materials[1103]));
        Assert.Equal(2040u,new GmService(store,clock).Mcoin(Uid));
    }
    [Fact] public void EquipmentBundlesDeliverEquipmentTicketsAndPersistPurchaseLimit()
    {
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Mcoin=10000;return 0;});
        Assert.Equal(1u,gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=721570}).Num);
        for(int i=0;i<3;i++)Assert.Equal(1u,gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=721571}).Num);
        Assert.Equal(3080u,gm.Mcoin(Uid));Assert.Equal(11052u,store.Read(Uid).Hcoin);
        Assert.Equal(35u,store.Campaign(Uid,tx=>tx.Campaign.Materials[1102]));
        Assert.NotEqual(BuyGoodsRsp.Types.Retcode.Succ,new GmService(store,clock).BuyGoods(Uid,new(){ShopId=27,GoodsId=721571}).Retcode);
        Assert.Equal(3080u,gm.Mcoin(Uid));Assert.Equal(3u,store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases["27:721571"]));
    }
    [Fact] public void FailedPurchaseDoesNotDebitGrantOrConsumeStock()
    {
        Assert.Equal(BuyGoodsRsp.Types.Retcode.MoneyLack,gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=721565}).Retcode);
        Assert.Equal(10000u,store.Read(Uid).Hcoin);Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases));
        var unknown=GmService.ShopCatalog.Goods.First(x=>x.ReceiptOnly);
        Assert.Equal(BuyGoodsRsp.Types.Retcode.FeatureClosed,gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=unknown.Id}).Retcode);
        Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases));
    }
    [Fact] public void OldNoDeliveryReceiptsRestoreOnlyTheirOwnQuotaOnce()
    {
        store.Campaign(Uid,tx=>{var s=tx.Campaign.Operations;s.ShopPurchases["27:721565"]=2;s.ShopPurchases["27:710159"]=1;
            s.Audit.Add(new(1,"shop-buy","商店 27 / 商品 721565 × 1，仅记录本地购买成功，奖励待 GM 配置。"));return 0;});
        gm.ShoppingMall(Uid);gm.ShoppingMall(Uid);
        Assert.Equal(1u,store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases["27:721565"]));
        Assert.Equal(1u,store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases["27:710159"]));
        Assert.Equal(10000u,store.Read(Uid).Hcoin);
    }
    [Fact] public void ShopSidebarRetainsLocalizationKeys()
    {
        var shops=gm.Shops(Uid);
        Assert.All(shops.ShopList,s=>Assert.StartsWith("ShopName",s.TextMapName));
        Assert.Equal("ShopName1",shops.ShopList.Single(x=>x.ShopId==1).TextMapName);
    }
    [Fact] public void SenaS1PromotesPersistentlyFromThirtyFragmentsToFive()
    {
        Grant(new("avatar",20201),new("material",2020201,30));
        Assert.Equal(30u,Avatar(20201).Fragment);
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.Succ,gm.PromoteAvatar(Uid,new(){AvatarId=20201}).Retcode);
        var a=Avatar(20201);Assert.Equal(3u,a.Star);Assert.Equal(1u,a.SubStar);Assert.Equal(5u,a.Fragment);
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.FragmentLack,new GmService(store,clock).PromoteAvatar(Uid,new(){AvatarId=20201}).Retcode);
        Assert.Equal(5u,Avatar(20201).Fragment);
    }
    [Fact] public void PromotionCrossesSAndSSBoundariesAndStopsAtSSS()
    {
        Grant(new("avatar",20201),new("material",2020201,300));
        for(int i=0;i<4;i++)Assert.Equal(AvatarStarUpRsp.Types.Retcode.Succ,gm.PromoteAvatar(Uid,new(){AvatarId=20201}).Retcode);
        Assert.Equal(4u,Avatar(20201).Star);Assert.Equal(0u,Avatar(20201).SubStar);Assert.Equal(200u,Avatar(20201).Fragment);
        for(int i=0;i<4;i++)Assert.Equal(AvatarStarUpRsp.Types.Retcode.Succ,gm.PromoteAvatar(Uid,new(){AvatarId=20201}).Retcode);
        Assert.Equal(5u,Avatar(20201).Star);Assert.Equal(0u,Avatar(20201).Fragment);
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.StarFull,gm.PromoteAvatar(Uid,new(){AvatarId=20201}).Retcode);
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.AvatarNotExist,gm.PromoteAvatar(Uid,new(){AvatarId=9999}).Retcode);
    }
    [Fact] public void ExistingMaterialFragmentsAreMergedOnceWithoutLosingImportedFragments()
    {
        Grant(new("avatar",20201),new("material",2020201,30));
        store.Campaign(Uid,tx=>{tx.Campaign.Materials[2020201]=60;return 0;});
        gm.Snapshot(Uid);gm.Snapshot(Uid);
        Assert.Equal(90u,Avatar(20201).Fragment);Assert.False(store.Campaign(Uid,tx=>tx.Campaign.Materials.ContainsKey(2020201)));
    }
    [Fact] public void SupplyUsesActualFeaturedAvatarPityAndDuplicateFragmentsAfterRestart()
    {
        Grant(new GrantItem("avatar",21101));gm.GachaDisplay(Uid,new());
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Pity[44]=89;tx.Campaign.Operations.Draws[44]=89;return 0;});
        var before=new GmService(store,clock).GachaDisplay(Uid,new()).GachaDisplayInfoList.Single(x=>(int)x.GachaType==44);
        Assert.Equal(21101u,before.PjmsGachaData.ProtectDisplayInfo.DisplayKeyAvatar);
        Assert.Equal(1u,before.PjmsGachaData.DisplayProtectTimes-before.PjmsGachaData.NoProtectGachaTimes);
        var request=new GachaReq{Type=(GachaType)44,Num=1,IsUseHcoin=true,GachaRandom=gm.GachaDisplay(Uid,new()).GachaRandom};
        var draw=gm.Draw(Uid,request);Assert.Equal(GachaRsp.Types.Retcode.Succ,draw.Retcode);
        Assert.Equal(3021101u,Assert.Single(draw.ItemList).ItemId);Assert.Equal(30u,draw.ItemList[0].SplitFragmentNum);
        Assert.Equal(30u,Avatar(21101).Fragment);Assert.Equal(9720u,store.Read(Uid).Hcoin);
        var after=new GmService(store,clock).GachaDisplay(Uid,new()).GachaDisplayInfoList.Single(x=>(int)x.GachaType==44);
        Assert.Equal(0u,after.PjmsGachaData.NoProtectGachaTimes);Assert.Equal(90u,after.PjmsGachaData.GachaTimes);
        Assert.Equal(draw.ToByteArray(),gm.Draw(Uid,request).ToByteArray());Assert.Equal(30u,Avatar(21101).Fragment);
    }
    [Fact] public void WirePromotionIsHandledAndPublishesAuthoritativeAvatar()
    {
        Grant(new("avatar",20201),new("material",2020201,30));
        var d=new GameDispatcher(new LobbyHandlers(store,new SqlitePlayerStore(db),new byte[32],clock).Create());
        var session=new GameSession(1,"fixture",1);session.Authenticate(Uid);
        var result=d.Dispatch(session,new GamePacket(new byte[26],29,[],new AvatarStarUpReq{AvatarId=20201}.ToByteArray()));
        Assert.Equal(AvatarStarUpRsp.Types.Retcode.Succ,AvatarStarUpRsp.Parser.ParseFrom(result.Replies.Single(p=>p.CommandId==30).Body).Retcode);
        var av=GetAvatarDataRsp.Parser.ParseFrom(result.Replies.Single(p=>p.CommandId==25).Body).AvatarList.Single(a=>a.AvatarId==20201);
        Assert.Equal(1u,av.SubStar);Assert.Equal(5u,av.Fragment);
    }
    public void Dispose()=>temp.Dispose();
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1791558000);public override DateTimeOffset GetUtcNow()=>Now;}
}
