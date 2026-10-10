using System.Text.Json;
using System.Text.RegularExpressions;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed record ShopProduct(uint Id, string Name, GrantItem Item, GrantItem[] Costs, uint MaxBuyTimes, uint MaxPerPurchase,
    GrantItem[]? Rewards = null, bool LocalPayment = false, bool ReceiptOnly = false, string? SourceNote = null, JsonElement? Display = null);
public sealed record ShopTemplate(uint Id, string Name, bool Mall, JsonElement Template, uint[] Goods);
public sealed record ShopCatalog(ShopProduct[] Goods, ShopTemplate[] Shops);

public sealed partial class GmService
{
    public static readonly ShopCatalog ShopCatalog = LoadShops();
    private static ShopCatalog LoadShops()
    {
        var data=GrantService.Resource<ShopCatalog>("shops.json");
        return data with {Goods=data.Goods.Select(x=>x.Id is 700597 or 700598?x with {ReceiptOnly=false,Rewards=[new("material",6004,2),new("material",915,600)],SourceNote=$"9.1 screenshot: {(x.Id==700597?65:75)}级福利礼袋"}:x).ToArray()};
    }
    private static readonly Dictionary<uint, ShopProduct> Products = ShopCatalog.Goods.ToDictionary(x => x.Id);
    private static readonly Dictionary<uint, ShopTemplate> ShopTemplates = ShopCatalog.Shops.ToDictionary(x => x.Id);
    private OperationsState ShopState(CampaignTransaction tx)
    {
        var state = State(tx);
        if(state.EconomyVersion<1)
        {
            // Undo only the old, explicitly audited no-delivery receipts. There
            // was no debit or grant to reverse; real completed purchases stay put.
            uint restored=0;
            foreach(var log in state.Audit.Where(x=>x.Action=="shop-buy"&&x.Description.Contains("仅记录本地购买成功，奖励待 GM 配置",StringComparison.Ordinal)))
            {
                var match=Regex.Match(log.Description,@"商店 (\d+) / 商品 (\d+) × (\d+)");
                if(!match.Success)continue;
                string key=match.Groups[1].Value+":"+match.Groups[2].Value;
                uint count=Math.Min(state.ShopPurchases.GetValueOrDefault(key),uint.Parse(match.Groups[3].Value,System.Globalization.CultureInfo.InvariantCulture));
                if(count==0)continue;
                state.ShopPurchases[key]-=count;restored+=count;
            }
            if(restored>0)state.Audit.Add(new(Now,"economy-repair",$"已返还 {restored} 次未扣款且未发奖的旧购买限额。"));
            state.EconomyVersion=1;
        }
        state.Shops ??= ShopCatalog.Shops.Select(s => new LocalShop(s.Id, s.Name, true, s.Goods, true)).ToList();
        if (state.MallVersion < 1)
        {
            // Upgrade only the obsolete seven-item placeholder; preserve ordinary shops and stock.
            foreach (var mall in ShopCatalog.Shops.Where(x => x.Mall))
            {
                int index = state.Shops.FindIndex(x => x.Id == mall.Id);
                var old = index < 0 ? null : state.Shops[index];
                var restored = new LocalShop(mall.Id, mall.Name, old?.Enabled ?? true, mall.Goods, false);
                if (index < 0) state.Shops.Add(restored); else state.Shops[index] = restored;
            }
            state.MallVersion = 1;
        }
        // Daily shop stock resets at 04:00 Asia/Shanghai, independent of host timezone.
        uint day = (Now + 4 * 3600) / 86400;
        if (state.ShopDay != day)
        {
            foreach (var shop in state.Shops.Where(s => s.DailyRefresh)) ResetStock(state, shop.Id);
            state.ShopDay = day;
        }
        return state;
    }
    private static void ResetStock(OperationsState state, uint shop)
    {
        foreach (string key in state.ShopPurchases.Keys.Where(k => k.StartsWith(shop + ":", StringComparison.Ordinal)).ToArray())
            state.ShopPurchases.Remove(key);
    }
    private static void ValidateShop(LocalShop shop)
    {
        if (!ShopTemplates.ContainsKey(shop.Id) || string.IsNullOrWhiteSpace(shop.Name) || shop.Name.Length > 40 ||
            shop.Goods is null || shop.Goods.Length > 200 || shop.Goods.Distinct().Count() != shop.Goods.Length ||
            shop.Goods.Any(id => !Products.ContainsKey(id)))
            throw new ArgumentException("请选择已支持的商店与商品；名称 1–40 字，最多 200 项且不可重复。");
        if (shop.Enabled && shop.Goods.Length == 0) throw new ArgumentException("开启商店前至少添加一项商品。");
    }
    private Shop ShopView(OperationsState state, LocalShop config)
    {
        var spec = ShopTemplates[config.Id];
        var shop = JsonParser.Default.Parse<Shop>(spec.Template.GetRawText());
        // Preserve the captured localization key; the literal GM label is separate.
        shop.ShopName = config.Name;
        shop.IsOpen = config.Enabled; shop.IsShow = config.Enabled; shop.UnlockLevel = 1;
        shop.GoodsList.Clear(); shop.AllGoodsIdList.Clear(); shop.CurrencyList.Clear(); shop.NewCurrencyList.Clear();
        shop.NextAutoRefreshTime = config.DailyRefresh ? (state.ShopDay + 1) * 86400 - 4 * 3600 : 2147483647;
        foreach (uint id in config.Goods)
        {
            var item = Products[id];
            var goods = item.Display is {} display ? JsonParser.Default.Parse<Goods>(display.GetRawText()) : new Goods { GoodsId = id,
                CanBeRefresh = config.DailyRefresh, RefreshTimeType = (Goods.Types.RefreshTimeType)(config.DailyRefresh ? 1 : 0),
                BeginTime = 1, EndTime = 2147483647, ShowType = (Goods.Types.ShowType)1, SortId = (uint)shop.GoodsList.Count + 1,
                ShowSettingStr = "NORMAL", MinLevel = 1, MaxLevel = 99, PrepareLevel = 1, IsIgnore = false, IsHidePrice = false };
            goods.BuyTimes = state.ShopPurchases.GetValueOrDefault(config.Id + ":" + id);
            goods.BeginTime = 1; goods.EndTime = 2147483647; goods.PrepareTime = 1;
            if (spec.Mall && goods.MallAnchorList.Count == 0) goods.MallAnchorList.Add(9);
            shop.GoodsList.Add(goods); shop.AllGoodsIdList.Add(id);
            // Hcoin is displayed by toolbar slot 2, never as a material currency ID.
            foreach (uint currency in item.Costs.Where(c => c.Kind is not ("hcoin" or "mcoin")).Select(c => c.Id))
                if (!shop.CurrencyList.Contains(currency)) { shop.CurrencyList.Add(currency); shop.NewCurrencyList.Add(currency); }
        }
        return shop;
    }
    public GetShopListRsp Shops(uint uid) => store.Campaign(uid, tx =>
    {
        var s = ShopState(tx);
        return new GetShopListRsp { Retcode = GetShopListRsp.Types.Retcode.Succ, IsAll = true,
            ShopList = { s.Shops!.Where(x => !ShopTemplates[x.Id].Mall).Select(x => ShopView(s, x)) } };
    });
    public GetShoppingMallListRsp ShoppingMall(uint uid) => store.Campaign(uid, tx =>
    {
        var s = ShopState(tx);
        return new GetShoppingMallListRsp { Retcode = GetShoppingMallListRsp.Types.Retcode.Succ,
            ShopList = { s.Shops!.Where(x => ShopTemplates[x.Id].Mall).Select(x => ShopView(s, x)) } };
    });
    public GetSingleShopWithoutRefreshRsp SingleShop(uint uid, GetSingleShopWithoutRefreshReq request) => store.Campaign(uid, tx =>
    {
        var s = ShopState(tx); var shop = s.Shops!.Find(x => x.Id == request.ShopId);
        return shop is null ? new() { Retcode = GetSingleShopWithoutRefreshRsp.Types.Retcode.NotOpen } :
            new GetSingleShopWithoutRefreshRsp { Retcode = GetSingleShopWithoutRefreshRsp.Types.Retcode.Succ, Shop = ShopView(s, shop) };
    });
    public BuyGoodsRsp BuyGoods(uint uid, BuyGoodsReq request)
    {
        try { return store.Campaign(uid, tx =>
        {
            var s = ShopState(tx); var shop = s.Shops!.Find(x => x.Id == request.ShopId);
            BuyGoodsRsp Fail(BuyGoodsRsp.Types.Retcode code) => new() { Retcode = code, ShopId = request.ShopId, GoodsId = request.GoodsId };
            if (shop is null || !shop.Enabled) return Fail(BuyGoodsRsp.Types.Retcode.ShopClose);
            if (!shop.Goods.Contains(request.GoodsId) || !Products.TryGetValue(request.GoodsId, out var goods)) return Fail(BuyGoodsRsp.Types.Retcode.GoodsNotExist);
            var levelGift=SystemsService.Rows("PlayerLevelShopGoods").FirstOrDefault(x=>x.GetProperty("GoodsIDList").EnumerateArray().Any(v=>v.GetUInt32()==goods.Id));
            if(levelGift.ValueKind!=JsonValueKind.Undefined && tx.Lobby.Level<SystemsService.N(levelGift,"Level"))return Fail(BuyGoodsRsp.Types.Retcode.Fail);
            uint count = request.HasGoodsNum ? request.GoodsNum : 1;
            if (count == 0 || count > goods.MaxPerPurchase) return Fail(BuyGoodsRsp.Types.Retcode.Fail);
            if (request.CouponNum != 0 || request.CouponMaterialId != 0 || request.McoinCouponMaterialId != 0 || request.DiscountSaveNum != 0 ||
                request.GiftPackSelectRewardId != 0 || request.AutoOpenSelectRewardId != 0) return Fail(BuyGoodsRsp.Types.Retcode.CouponError);
            string key = shop.Id + ":" + goods.Id; uint bought = s.ShopPurchases.GetValueOrDefault(key);
            if (goods.MaxBuyTimes > 0 && (ulong)bought + count > goods.MaxBuyTimes) return Fail(BuyGoodsRsp.Types.Retcode.BuyTimesLack);
            var rewards = s.ShopRewardOverrides.GetValueOrDefault(goods.Id) ?? goods.Rewards ?? [goods.Item];
            // Never consume a purchase allowance for an undefined/empty reward.
            if (rewards.Length == 0) return Fail(BuyGoodsRsp.Types.Retcode.FeatureClosed);
            var costs = goods.LocalPayment ? [] : goods.Costs;
            foreach (var cost in costs)
            {
                uint amount = checked(cost.Num * count);
                uint balance = cost.Kind switch { "scoin" => tx.Lobby.Scoin, "hcoin" => tx.Lobby.Hcoin, "mcoin" => s.Mcoin, _ => tx.Campaign.Materials.GetValueOrDefault(cost.Id) };
                if (balance < amount) return Fail(BuyGoodsRsp.Types.Retcode.MoneyLack);
            }
            // Grant and debit are committed together; inventory failure rolls back stock and wallet.
            foreach (var reward in rewards) GrantService.Apply(tx, uid, reward with { Num = checked(reward.Num * count) });
            foreach (var cost in costs)
            {
                uint amount = checked(cost.Num * count);
                if (cost.Kind == "scoin") tx.Lobby = tx.Lobby with { Scoin = tx.Lobby.Scoin - amount };
                else if (cost.Kind == "hcoin") tx.Lobby = tx.Lobby with { Hcoin = tx.Lobby.Hcoin - amount };
                else if (cost.Kind == "mcoin") s.Mcoin -= amount;
                else tx.Campaign.Materials[cost.Id] = tx.Campaign.Materials.GetValueOrDefault(cost.Id) - amount;
            }
            s.ShopPurchases[key] = checked(bought + count); s.Revision++;
            s.Audit.Add(new(Now, "shop-buy", $"商店 {shop.Id} / 商品 {goods.Id} × {count}，发放 {rewards.Length} 项奖励。"));
            if (s.Audit.Count > 1000) s.Audit.RemoveAt(0);
            var result = new BuyGoodsRsp { Retcode = BuyGoodsRsp.Types.Retcode.Succ, ShopId = shop.Id, GoodsId = goods.Id,
                GoodsBuyTimes = s.ShopPurchases[key], Num = checked(goods.Item.Num * count), Level = goods.Item.Level };
            // The client builds the gift reward matrix from goods_id and num.
            if (goods.Item.Id != 0) result.ItemId = goods.Item.Id;
            return result;
        }); }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        { return new() { Retcode = BuyGoodsRsp.Types.Retcode.EquipmentFull, ShopId = request.ShopId, GoodsId = request.GoodsId }; }
    }
}
