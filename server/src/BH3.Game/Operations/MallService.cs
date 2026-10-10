using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;
using System.Text.Json;
namespace BH3.Game.Operations;

public sealed partial class GmService
{
    private static readonly GetProductListRsp ProductTemplate=JsonParser.Default.Parse<GetProductListRsp>(GrantService.Resource<JsonElement>("products.json").GetRawText());
    public uint Mcoin(uint uid)=>store.Campaign(uid,tx=>tx.Campaign.Operations.Mcoin);
    public GetProductListRsp ProductList(uint uid)=>store.Campaign(uid,tx=>
    {
        var s=State(tx);var result=ProductTemplate.Clone();
        result.NextLimitProductRefreshTime=2147483647;result.NextRandomBoxProductRefreshTime=2147483647;
        foreach(var p in result.ProductList)
        {
            if(p.Type==ProductType.ProductLimit)
            {
                p.LeftBuyTimes=s.ProductPurchases.GetValueOrDefault(p.Name)==0?1u:0u;
                if(p.LeftBuyTimes==0){p.FreeHcoin=0;p.Type=ProductType.ProductNormal;}
            }
            if(p.Type==ProductType.ProductCard)p.CardLeftDays=s.CardExpireTime>Now?(s.CardExpireTime-Now+86399)/86400:0;
        }
        return result;
    });
    // Local emulator only: no payment gateway, bank details, or external callback is contacted.
    public RechargeFinishNotify PurchaseProduct(uint uid,string name)=>store.Campaign<RechargeFinishNotify>(uid,tx=>
    {
        var p=ProductTemplate.ProductList.FirstOrDefault(x=>x.Name==name);
        if(p is null)return new(){Retcode=RechargeFinishNotify.Types.Retcode.Fail,ProductName=name};
        var s=VipState(tx);uint bonus=p.Type==ProductType.ProductLimit&&s.ProductPurchases.GetValueOrDefault(name)>0?0:p.FreeHcoin;
        uint hcoin=checked(p.PayHcoin+bonus);
        if(hcoin>0)GrantService.Apply(tx,uid,new("hcoin",101,hcoin));
        if((ulong)s.Mcoin+p.Mcoin>999999999)throw new ArgumentException("礼包币余额超出上限。");
        s.Mcoin+=p.Mcoin;s.ProductPurchases[name]=checked(s.ProductPurchases.GetValueOrDefault(name)+1);
        s.TotalPayHcoin=checked(s.TotalPayHcoin!.Value+p.Price/10);
        if(p.Type==ProductType.ProductCard)s.CardExpireTime=checked(Math.Max(Now,s.CardExpireTime)+30*86400);
        s.Revision++;s.Audit.Add(new(Now,"local-payment",$"本地模拟购买 {p.Desc}；水晶 +{hcoin}，礼包币 +{p.Mcoin}。"));
        if(s.Audit.Count>1000)s.Audit.RemoveAt(0);
        return new(){Retcode=RechargeFinishNotify.Types.Retcode.Succ,ProductName=p.Name,ProductDesc=p.Desc,
            PayHcoin=p.PayHcoin,FreeHcoin=bonus,Mcoin=p.Mcoin,ProductType=p.Type,ProductPrice=p.Price,
            ChannelName="BH3Local",ChannelOrderNo=Guid.NewGuid().ToString("N"),IsAddHcoin=hcoin>0,PayCurrency="CNY",PayPrice="0"};
    });
    public GetCardProductInfoRsp CardInfo(uint uid)=>store.Campaign<GetCardProductInfoRsp>(uid,tx=>
    {
        var s=State(tx);bool claimable=s.CardExpireTime>Now&&s.CardLastRewardDay!=(Now+14400)/86400;
        return new(){Retcode=GetCardProductInfoRsp.Types.Retcode.Succ,CardProductInfoList={new CardProductInfo{
            ProductName="Bh3GiftHardCoinTier5",ExpireTime=s.CardExpireTime,CardLeftDays=s.CardExpireTime>Now?(s.CardExpireTime-Now+86399)/86400:0,
            // The 9.1 button tests hcoin > 0; Product.card_daily_hcoin advertises 60.
            RewardNum=claimable?1u:0u,Hcoin=claimable?60u:0u,TakeRewardDays=s.CardRewardDays-s.CardBonusClaimed*15,
            LastDailyRewardTime=s.CardLastRewardDay==0?0:s.CardLastRewardDay*86400-14400,BonusNeedDays=15,BonusHcoin=500,BonusMaxSaveDays=60}}};
    });
    public TakeCardProductDailyRewardRsp TakeCardDaily(uint uid)=>store.Campaign(uid,tx=>
    {
        var s=State(tx);uint day=(Now+14400)/86400;var r=new TakeCardProductDailyRewardRsp{Retcode=TakeCardProductDailyRewardRsp.Types.Retcode.Succ};
        if(s.CardExpireTime<=Now){r.Retcode=TakeCardProductDailyRewardRsp.Types.Retcode.NotOpen;return r;}
        if(s.CardLastRewardDay==day){r.Retcode=TakeCardProductDailyRewardRsp.Types.Retcode.Fail;return r;}
        GrantService.Apply(tx,uid,new("hcoin",101,60));s.CardLastRewardDay=day;s.CardRewardDays++;s.Revision++;
        r.CardProductRewardList.Add(new CardProductReward{ProductName="Bh3GiftHardCoinTier5",Hcoin=60,TakeRewardDays=s.CardRewardDays-s.CardBonusClaimed*15});return r;
    });
    public TakeCardProductBonusRewardRsp TakeCardBonus(uint uid)=>store.Campaign(uid,tx=>
    {
        var s=State(tx);var r=new TakeCardProductBonusRewardRsp{Retcode=TakeCardProductBonusRewardRsp.Types.Retcode.Succ};
        uint count=s.CardRewardDays/15;if(count<=s.CardBonusClaimed){r.Retcode=TakeCardProductBonusRewardRsp.Types.Retcode.Fail;return r;}
        uint hcoin=checked((count-s.CardBonusClaimed)*500);GrantService.Apply(tx,uid,new("hcoin",101,hcoin));s.CardBonusClaimed=count;s.Revision++;
        r.CardProductRewardList.Add(new CardProductReward{ProductName="Bh3GiftHardCoinTier5",Hcoin=hcoin,TakeRewardDays=s.CardRewardDays-s.CardBonusClaimed*15});return r;
    });
    public ExchangeHcoinByMcoinRsp ExchangeMcoin(uint uid,ExchangeHcoinByMcoinReq r)=>store.Campaign(uid,tx=>
    {
        var p=ProductTemplate.ProductList.FirstOrDefault(x=>x.Name==r.ProductName&&x.PayHcoin>0&&x.Type!=ProductType.ProductCard);
        var result=new ExchangeHcoinByMcoinRsp{ProductName=r.ProductName,Retcode=ExchangeHcoinByMcoinRsp.Types.Retcode.ProductInvalid};
        if(p is null)return result;uint cost=p.Price/10;var s=State(tx);
        if(r.McoinPrice!=cost)return result;
        if(s.Mcoin<cost){result.Retcode=ExchangeHcoinByMcoinRsp.Types.Retcode.LackMcoin;return result;}
        uint bonus=p.Type==ProductType.ProductLimit&&s.ProductPurchases.GetValueOrDefault(p.Name)>0?0:p.FreeHcoin;
        GrantService.Apply(tx,uid,new("hcoin",101,checked(p.PayHcoin+bonus)));s.Mcoin-=cost;s.Revision++;
        s.ProductPurchases[p.Name]=checked(s.ProductPurchases.GetValueOrDefault(p.Name)+1);
        result.Retcode=ExchangeHcoinByMcoinRsp.Types.Retcode.Succ;result.PayHcoin=p.PayHcoin;result.FreeHcoin=bonus;return result;
    });
}
