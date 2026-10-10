using BH3.Persistence;
using BH3.Protocol.Messages;

namespace BH3.Game.Operations;

public sealed record AvatarRankSpec(uint Id,uint Type,uint RankType);
public sealed record AvatarRankStep(uint Star,uint SubStar,uint Type,uint RankType,uint Cost);
public sealed record AvatarRankCatalog(AvatarRankSpec[] Avatars,AvatarRankStep[] Steps);

public sealed partial class GmService
{
    public static readonly AvatarRankCatalog AvatarRanks=GrantService.Resource<AvatarRankCatalog>("avatar-promotion.json");
    public AvatarStarUpRsp PromoteAvatar(uint uid,AvatarStarUpReq request)=>store.Campaign(uid,tx=>
    {
        var(a,e)=GrantService.Inventory(tx);
        var avatar=a.AvatarList.FirstOrDefault(x=>x.AvatarId==request.AvatarId);
        AvatarStarUpRsp Result(AvatarStarUpRsp.Types.Retcode code)=>new(){Retcode=code};
        if(avatar is null)return Result(AvatarStarUpRsp.Types.Retcode.AvatarNotExist);
        if(avatar.Star>=5)return Result(AvatarStarUpRsp.Types.Retcode.StarFull);
        var spec=AvatarRanks.Avatars.FirstOrDefault(x=>x.Id==avatar.AvatarId);
        if(spec is null)return Result(AvatarStarUpRsp.Types.Retcode.FeatureClosed);
        var steps=AvatarRanks.Steps.Where(x=>x.Type==spec.Type&&x.RankType==spec.RankType)
            .OrderBy(x=>x.Star).ThenBy(x=>x.SubStar).ToArray();
        int index=Array.FindIndex(steps,x=>x.Star==avatar.Star&&x.SubStar==avatar.SubStar);
        if(index<0||index+1>=steps.Length)return Result(AvatarStarUpRsp.Types.Retcode.Fail);
        uint cost=steps[index].Cost;
        if(cost==0||avatar.Fragment<cost)return Result(AvatarStarUpRsp.Types.Retcode.FragmentLack);
        avatar.Fragment-=cost;avatar.Star=steps[index+1].Star;avatar.SubStar=steps[index+1].SubStar;
        GrantService.Save(tx,a,e,uid);
        tx.Campaign.Operations.Revision++;
        return Result(AvatarStarUpRsp.Types.Retcode.Succ);
    });
}
