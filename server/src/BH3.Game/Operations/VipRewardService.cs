using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;
using System.Text.Json;

namespace BH3.Game.Operations;

public sealed partial class GmService
{
    // 9.1 capture: tier definitions only; captured account totals/claims are removed.
    private static readonly GetVipRewardDataRsp VipTemplate=JsonParser.Default.Parse<GetVipRewardDataRsp>(
        GrantService.Resource<JsonElement>("vip-rewards.json").GetRawText());
    private OperationsState VipState(CampaignTransaction tx)
    {
        var s=State(tx);
        if(s.TotalPayHcoin is null)
        {
            // Before 1.6.3 ProductPurchases also included gift-coin exchanges.
            // Only successful local-payment audit receipts prove a cash purchase.
            uint total=0;
            foreach(var receipt in s.Audit.Where(x=>x.Action=="local-payment"))
            {
                var prices=ProductTemplate.ProductList.Where(p=>receipt.Description.StartsWith($"本地模拟购买 {p.Desc}；",StringComparison.Ordinal))
                    .Select(p=>p.Price/10).Distinct().ToArray();
                if(prices.Length==1)total=checked(total+prices[0]);
            }
            s.TotalPayHcoin=total;
        }
        return s;
    }
    public GetVipRewardDataRsp VipRewards(uint uid)=>store.Campaign(uid,tx=>
    {
        var s=VipState(tx);var result=VipTemplate.Clone();result.TotalPayHcoin=s.TotalPayHcoin!.Value;
        foreach(var tier in result.VipRewardList)
        {tier.TakenRewardIdList.Clear();tier.TakenRewardIdList.Add(tier.RewardIdList.Where(s.VipRewardClaims.Contains));}
        return result;
    });
    public GetVipRewardRsp ClaimVip(uint uid,GetVipRewardReq request)
    {
        try{return store.Campaign(uid,tx=>
        {
            var result=new GetVipRewardRsp{Retcode=GetVipRewardRsp.Types.Retcode.Fail};
            if(request.VipLevelList.Count==0||request.VipLevelList.Count>VipTemplate.VipRewardList.Count)return result;
            var tiers=request.VipLevelList.Distinct().Select(id=>VipTemplate.VipRewardList.FirstOrDefault(t=>t.VipLevel==id)).ToArray();
            if(tiers.Any(t=>t is null))return result;
            var s=VipState(tx);
            if(tiers.Any(t=>t!.PayHcoin>s.TotalPayHcoin!.Value)){result.Retcode=GetVipRewardRsp.Types.Retcode.PayHcoinLack;return result;}
            var ids=tiers.SelectMany(t=>t!.RewardIdList).Distinct().Where(id=>!s.VipRewardClaims.Contains(id)).ToArray();
            if(ids.Length==0){result.Retcode=GetVipRewardRsp.Types.Retcode.RepeatGet;return result;}
            foreach(uint id in ids)
            {
                var reward=SystemsService.Reward(id);var inventoryReward=reward.Clone();
                foreach(var item in inventoryReward.ItemList.ToArray())
                {
                    if(item.Id is 101001 or 101084){s.OwnedMedals.Add(item.Id);inventoryReward.ItemList.Remove(item);}
                    if(item.Id is 200004 or 200029){s.OwnedFrames.Add(item.Id);inventoryReward.ItemList.Remove(item);}
                }
                SystemsService.Apply(tx,inventoryReward);s.VipRewardClaims.Add(id);result.RewardList.Add(reward);
            }
            s.Revision++;result.Retcode=GetVipRewardRsp.Types.Retcode.Succ;return result;
        });}
        catch(Exception e) when(e is ArgumentException or OverflowException)
        {return new(){Retcode=GetVipRewardRsp.Types.Retcode.Fail};}
    }
    public GetMedalDataRsp Medals(uint uid,GetMedalDataReq request)=>store.Campaign(uid,tx=>
    {
        bool all=request.MedalIdList.Count==0||request.MedalIdList.Contains(0);
        return new GetMedalDataRsp{Retcode=GetMedalDataRsp.Types.Retcode.Succ,IsAll=all,
            MedalList={tx.Campaign.Operations.OwnedMedals.Where(id=>all||request.MedalIdList.Contains(id)).Order().Select(id=>new Medal{Id=id,EndTime=0})}};
    });
    public GetFrameDataRsp Frames(uint uid)=>store.Campaign(uid,tx=>new GetFrameDataRsp
    {Retcode=GetFrameDataRsp.Types.Retcode.Succ,IsAll=true,
        FrameList={tx.Campaign.Operations.OwnedFrames.Order().Select(id=>new FrameData{Id=id,ExpireTime=0})}});
}
