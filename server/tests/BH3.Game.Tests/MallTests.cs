using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;
public sealed class MallTests : IDisposable
{
    private readonly TestDirectory temp=new();
    private readonly LobbyStore store;
    private readonly GmService gm;
    private const uint Uid=10001;
    public MallTests()
    {
        var db=new SqliteConnectionFactory(Path.Combine(temp.Path,"mall.db"));SchemaMigrator.Initialize(db);
        store=new(db);store.EnsurePlayer(Uid,"舰长",new(88,80,101,20001,59101,1,[],Scoin:100000,Hcoin:10000));gm=new(store);
    }
    [Fact] public void UpgradeRestoresCapturedMallButPreservesStockOrdinaryShopsAndSubsequentGmChoices()
    {
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Shops=[new(27,"本地兑换",true,[10204]),new(1,"custom",false,[100302])];tx.Campaign.Operations.ShopPurchases["27:710055"]=2;return 0;});
        var mall=gm.ShoppingMall(Uid).ShopList.Single();Assert.Equal(179,mall.GoodsList.Count);
        Assert.Equal(179,mall.GoodsList.Select(x=>x.GoodsId).Distinct().Count());
        Assert.True(mall.GoodsList.SelectMany(x=>x.MallAnchorList).Distinct().Count()>1);
        Assert.Equal(2u,mall.GoodsList.Single(x=>x.GoodsId==710055).BuyTimes);
        Assert.Equal("custom",gm.Shops(Uid).ShopList.Single().ShopName);Assert.False(gm.Shops(Uid).ShopList.Single().IsOpen);
        var s=store.Campaign(Uid,tx=>tx.Campaign.Operations);
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"shop",Revision:s.Revision,Shop:new(27,"关闭",false,[710055])));
        Assert.Single(new GmService(store).ShoppingMall(Uid).ShopList.Single().GoodsList);
        Assert.False(gm.ShoppingMall(Uid).ShopList.Single().IsOpen);
    }
    [Fact] public void RechargeCreditsCurrenciesAndFirstBonusOnlyOnce()
    {
        Assert.Equal(13,gm.ProductList(Uid).ProductList.Count);
        Assert.Equal(RechargeFinishNotify.Types.Retcode.Fail,gm.PurchaseProduct(Uid,"invalid").Retcode);
        Assert.Equal(10000u,store.Read(Uid).Hcoin);
        var first=gm.PurchaseProduct(Uid,"Bh3FirstHardCoinTier1");Assert.Equal(RechargeFinishNotify.Types.Retcode.Succ,first.Retcode);
        Assert.Equal("0",first.PayPrice);Assert.Equal(10120u,store.Read(Uid).Hcoin);
        gm.PurchaseProduct(Uid,"Bh3FirstHardCoinTier1");Assert.Equal(10180u,store.Read(Uid).Hcoin);
        gm.PurchaseProduct(Uid,"Bh3MCoinTier1");Assert.Equal(60u,new GmService(store).Mcoin(Uid));
    }
    [Fact] public void MonthlyCardAndRepeatedDailyClaimDoNotDuplicateRewards()
    {
        Assert.Empty(gm.TakeCardDaily(Uid).CardProductRewardList);
        gm.PurchaseProduct(Uid,"Bh3GiftHardCoinTier5");Assert.Equal(10300u,store.Read(Uid).Hcoin);
        Assert.Equal(30u,gm.CardInfo(Uid).CardProductInfoList.Single().CardLeftDays);
        Assert.Single(gm.TakeCardDaily(Uid).CardProductRewardList);Assert.Empty(new GmService(store).TakeCardDaily(Uid).CardProductRewardList);
        Assert.Equal(10360u,store.Read(Uid).Hcoin);Assert.Empty(gm.TakeCardBonus(Uid).CardProductRewardList);
        gm.PurchaseProduct(Uid,"Bh3GiftHardCoinTier5");Assert.Equal(60u,gm.CardInfo(Uid).CardProductInfoList.Single().CardLeftDays);
    }
    [Fact] public void ExchangeRejectsForgedCostAndCommitsWalletsAtomically()
    {
        gm.PurchaseProduct(Uid,"Bh3MCoinTier1");
        Assert.Equal(ExchangeHcoinByMcoinRsp.Types.Retcode.ProductInvalid,gm.ExchangeMcoin(Uid,new(){ProductName="Bh3FirstHardCoinTier1",McoinPrice=1}).Retcode);
        Assert.Equal(60u,gm.Mcoin(Uid));
        Assert.Equal(ExchangeHcoinByMcoinRsp.Types.Retcode.Succ,gm.ExchangeMcoin(Uid,new(){ProductName="Bh3FirstHardCoinTier1",McoinPrice=60}).Retcode);
        Assert.Equal(0u,gm.Mcoin(Uid));Assert.Equal(10120u,store.Read(Uid).Hcoin);
        Assert.Equal(ExchangeHcoinByMcoinRsp.Types.Retcode.LackMcoin,gm.ExchangeMcoin(Uid,new(){ProductName="Bh3FirstHardCoinTier1",McoinPrice=60}).Retcode);
    }
    [Fact] public void GiftCoinPurchaseDeliversKnownRewardsAndUnknownGoodsDoNotConsumeQuota()
    {
        var gift=GmService.ShopCatalog.Goods.Single(x=>x.Id==710055);
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Mcoin=100;return 0;});
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=gift.Id}).Retcode);
        Assert.Equal(0u,gm.Mcoin(Uid));
        foreach(var r in gift.Rewards!.Where(x=>x.Kind=="material"))Assert.True(store.Campaign(Uid,tx=>tx.Campaign.Materials.GetValueOrDefault(r.Id))>=r.Num);
        var missing=GmService.ShopCatalog.Goods.First(x=>x.ReceiptOnly);var before=store.Read(Uid);
        var result=gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=missing.Id});Assert.Equal(BuyGoodsRsp.Types.Retcode.FeatureClosed,result.Retcode);Assert.False(result.HasItemId);
        Assert.Equal(before.Hcoin,store.Read(Uid).Hcoin);Assert.Equal(before.Scoin,store.Read(Uid).Scoin);
        Assert.Equal(0u,store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases.GetValueOrDefault($"27:{missing.Id}")));
    }
    [Fact] public void GmCanAssignMissingRewardsAndStaleConfigurationIsRejected()
    {
        gm.ShoppingMall(Uid);var g=GmService.ShopCatalog.Goods.First(x=>x.ReceiptOnly&&x.MaxBuyTimes==0);
        uint rev=store.Campaign(Uid,tx=>tx.Campaign.Operations.Revision);
        var command=new GmCommand(Guid.NewGuid().ToString(),"shop-reward",Items:[new("hcoin",101,123)],Revision:rev,GoodsId:g.Id);
        gm.Execute(Uid,command);gm.Execute(Uid,command);
        Assert.Throws<ArgumentException>(()=>gm.Execute(Uid,command with{RequestId=Guid.NewGuid().ToString()}));
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,new GmService(store).BuyGoods(Uid,new(){ShopId=27,GoodsId=g.Id}).Retcode);
        Assert.Equal(10123u,store.Read(Uid).Hcoin);
    }
    [Fact] public void OutfitOwnershipPersistsAndIsAppliedWhenAvatarIsUnlocked()
    {
        var g=GmService.ShopCatalog.Goods.First(x=>x.Rewards?.Any(r=>r.Kind=="dress")==true);
        var dress=g.Rewards!.Single();var owner=GrantService.Dresses[dress.Id][0];
        // Purchase can precede character ownership.
        store.Campaign(Uid,tx=>{GrantService.Apply(tx,Uid,dress);return 0;});
        Assert.Contains(dress.Id,store.Campaign(Uid,tx=>tx.Campaign.Operations.OwnedDresses));
        store.Campaign(Uid,tx=>{GrantService.Apply(tx,Uid,new("avatar",owner));return 0;});
        Assert.Contains(dress.Id,GetAvatarDataRsp.Parser.ParseFrom(store.Inventory(Uid)!.Avatars).AvatarList.Single(x=>x.AvatarId==owner).DressList);
    }
    [Fact] public void OverflowRollsBackPaymentAndPurchaseCounter()
    {
        store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Hcoin=999999999};return 0;});
        Assert.Throws<ArgumentException>(()=>gm.PurchaseProduct(Uid,"Bh3FirstHardCoinTier1"));
        Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Operations.ProductPurchases));
    }
    [Fact] public void DiscountedMallProductDebitsDisplayedNinetyFiveCrystalPrice()
    {
        Assert.Equal(95u,GmService.ShopCatalog.Goods.Single(x=>x.Id==710193).Costs.Single().Num);
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,gm.BuyGoods(Uid,new(){ShopId=27,GoodsId=710193}).Retcode);
        Assert.Equal(9905u,store.Read(Uid).Hcoin);
    }
    public void Dispose()=>temp.Dispose();
}
