using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
using System.Text.Json;

namespace BH3.Game.Tests;

public sealed class ShopTests : IDisposable
{
    private readonly TestDirectory temp = new();
    private readonly LobbyStore store;
    private readonly GmService gm;
    private const uint Uid = 10001;
    public ShopTests()
    {
        var db = new SqliteConnectionFactory(Path.Combine(temp.Path,"shop.db"));SchemaMigrator.Initialize(db);
        store = new(db);store.EnsurePlayer(Uid,"舰长",new(88,80,101,20001,59101,1,[],Scoin:100000,Hcoin:10000));gm=new(store);
    }
    private GmCommand Command(string action) => new(Guid.NewGuid().ToString(), action,
        Revision:store.Campaign(Uid,tx=>tx.Campaign.Operations.Revision));
    [Fact] public void UpgradeRestoresCurrentSuppliesAndSubsequentDisablePersists()
    {
        var display=gm.GachaDisplay(Uid,new(){IsAll=true});Assert.Equal(new[]{20,44,46,48},display.GachaDisplayInfoList.Select(x=>(int)x.GachaType).Order().ToArray());
        Assert.All(display.GachaDisplayInfoList,x=>{Assert.True(x.CommonData.DataEndTime>DateTimeOffset.UtcNow.ToUnixTimeSeconds());Assert.NotEqual("{}",x.CommonData.DisplayExt);});
        var pool=store.Campaign(Uid,tx=>tx.Campaign.Operations.Pools!.Single(x=>x.Type==44));
        gm.Execute(Uid,Command("pool") with{Pool=pool with{Enabled=false}});
        Assert.DoesNotContain(new GmService(store).GachaDisplay(Uid,new()).GachaDisplayInfoList,x=>(int)x.GachaType==44);
        gm.Execute(Uid,Command("pool") with{Pool=pool with{BeginTime=2000000000,EndTime=2100000000}});
        Assert.Equal(GachaRsp.Types.Retcode.GachaClosed,gm.Draw(Uid,new(){Type=(GachaType)44,Num=1,IsUseHcoin=true,GachaRandom=gm.GachaDisplay(Uid,new()).GachaRandom}).Retcode);
        Assert.Equal(GetGachaProbRsp.Types.Retcode.Fail,gm.Probability(Uid,new(){GachaType=44}).Retcode);
        Assert.Equal(BuyGachaTicketRsp.Types.Retcode.MaterialIdError,gm.BuyTickets(Uid,new(){MaterialId=1103,Num=1}).Retcode);
    }
    [Fact] public void CapturedShopsHaveSupportedGoodsAndMallIsPopulated()
    {
        var shops=gm.Shops(Uid);Assert.True(shops.IsAll);Assert.NotEmpty(shops.ShopList);Assert.NotEmpty(gm.ShoppingMall(Uid).ShopList);
        Assert.All(shops.ShopList,s=>{Assert.True(s.IsOpen);Assert.True(s.IsShow);Assert.NotEmpty(s.GoodsList);});
        Assert.All(GmService.ShopCatalog.Goods,g=>{foreach(var item in g.Rewards??[g.Item])GrantService.Validate(item);Assert.True(g.MaxPerPurchase>0);});
        Assert.Equal(GetSingleShopWithoutRefreshRsp.Types.Retcode.NotOpen,gm.SingleShop(Uid,new(){ShopId=999999}).Retcode);
    }
    [Fact] public void PurchasePersistsWalletRewardAndLimitAndRestockAllowsAnotherPurchase()
    {
        var product=GmService.ShopCatalog.Goods.Single(g=>g.Id==100302);
        var request=new BuyGoodsReq{ShopId=1,GoodsId=product.Id};
        var result=gm.BuyGoods(Uid,request);Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,result.Retcode);
        Assert.Equal(100000u-product.Costs[0].Num,store.Read(Uid).Scoin);
        Assert.Equal(product.Item.Num,store.Campaign(Uid,tx=>tx.Campaign.Materials[product.Item.Id]));
        Assert.Equal(BuyGoodsRsp.Types.Retcode.BuyTimesLack,new GmService(store).BuyGoods(Uid,request).Retcode);
        Assert.Equal(1u,gm.SingleShop(Uid,new(){ShopId=1}).Shop.GoodsList.Single(x=>x.GoodsId==product.Id).BuyTimes);
        var reset=Command("shop-restock") with{ShopId=1};gm.Execute(Uid,reset);
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,gm.BuyGoods(Uid,request).Retcode);
        gm.Execute(Uid,reset); // Replayed GM reset must not restock again.
        Assert.Equal(BuyGoodsRsp.Types.Retcode.BuyTimesLack,gm.BuyGoods(Uid,request).Retcode);
    }
    [Fact] public void InvalidFundsCountsAndDisabledShopNeverGrantOrCharge()
    {
        gm.Shops(Uid);store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Scoin=1};return 0;});
        Assert.Equal(BuyGoodsRsp.Types.Retcode.MoneyLack,gm.BuyGoods(Uid,new(){ShopId=1,GoodsId=100302}).Retcode);
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Fail,gm.BuyGoods(Uid,new(){ShopId=1,GoodsId=100302,GoodsNum=0}).Retcode);
        Assert.Equal(BuyGoodsRsp.Types.Retcode.GoodsNotExist,gm.BuyGoods(Uid,new(){ShopId=1,GoodsId=999999}).Retcode);
        var shop=store.Campaign(Uid,tx=>tx.Campaign.Operations.Shops!.Single(x=>x.Id==1));
        gm.Execute(Uid,Command("shop") with{Shop=shop with{Enabled=false}});
        Assert.Equal(BuyGoodsRsp.Types.Retcode.ShopClose,gm.BuyGoods(Uid,new(){ShopId=1,GoodsId=100302}).Retcode);
        Assert.Equal(1u,store.Read(Uid).Scoin);Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases));
    }
    [Fact] public void OverflowRollsBackRewardDebitAndStock()
    {
        gm.Shops(Uid);store.Campaign(Uid,tx=>{tx.Campaign.Materials[10201]=999999999;return 0;});
        Assert.Equal(BuyGoodsRsp.Types.Retcode.EquipmentFull,gm.BuyGoods(Uid,new(){ShopId=1,GoodsId=100302}).Retcode);
        Assert.Equal(100000u,store.Read(Uid).Scoin);Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases));
    }
    [Fact] public void CoinGoodsCreditWalletAndCrystalCostsAreNotMaterialCurrencies()
    {
        gm.Shops(Uid);store.Campaign(Uid,tx=>{tx.Campaign.Materials[9101]=100;return 0;});
        Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,gm.BuyGoods(Uid,new(){ShopId=9,GoodsId=9272}).Retcode);
        Assert.Equal(150000u,store.Read(Uid).Scoin);Assert.False(store.Campaign(Uid,tx=>tx.Campaign.Materials.ContainsKey(100)));
        Assert.DoesNotContain(101u,gm.SingleShop(Uid,new(){ShopId=1}).Shop.CurrencyList);
    }
    [Fact] public void ConcurrentLimitedPurchasesCommitOnlyOnce()
    {
        var results=Enumerable.Range(0,6).AsParallel().Select(_=>new GmService(store).BuyGoods(Uid,new(){ShopId=1,GoodsId=100302})).ToArray();
        Assert.Single(results,x=>x.Retcode==BuyGoodsRsp.Types.Retcode.Succ);Assert.Equal(71112u,store.Read(Uid).Scoin);
    }
    [Fact] public void DailyResetOnlyResetsConfiguredShopsAndStaleGmWriteFails()
    {
        gm.Shops(Uid);var shop=store.Campaign(Uid,tx=>tx.Campaign.Operations.Shops!.Single(x=>x.Id==1));
        var stale=Command("shop") with{Shop=shop};gm.Execute(Uid,Command("shop") with{Shop=shop with{DailyRefresh=false}});
        Assert.Throws<ArgumentException>(()=>gm.Execute(Uid,stale));
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.ShopDay=0;tx.Campaign.Operations.ShopPurchases["1:100302"]=1;tx.Campaign.Operations.ShopPurchases["5:1"]=1;return 0;});
        gm.Shops(Uid);Assert.Equal(1u,store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases["1:100302"]));
        Assert.False(store.Campaign(Uid,tx=>tx.Campaign.Operations.ShopPurchases.ContainsKey("5:1")));
    }
    public void Dispose()=>temp.Dispose();
}
