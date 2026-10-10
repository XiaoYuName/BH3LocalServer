using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed record GmCommand(string RequestId,string Action,GrantItem[]? Items=null,uint Level=0,uint AvatarId=0,uint Star=0,
    bool MaxSkills=false,string? Title=null,string? Content=null,string? Sender=null,uint Days=30,uint MailId=0,LocalPool? Pool=null,uint Revision=0,
    LocalShop? Shop=null,uint ShopId=0,uint GoodsId=0,uint ElfId=0,LocalActivity? Activity=null,LocalBattlePass? BattlePass=null);
public sealed record GmResult(string Message,uint Revision);
public sealed partial class GmService(LobbyStore store,TimeProvider? clock=null)
{
    private uint Now=>checked((uint)(clock??TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
    public object Accounts()=>store.Accounts().Select(x=>new {x.Uid,x.Nickname,Level=store.Read(x.Uid).Level}).ToArray();
    public object Snapshot(uint uid)=>store.Campaign(uid,tx=>
    {
        var(a,e)=GrantService.Inventory(tx);var s=ShopState(tx);
        return new { uid,lobby=tx.Lobby,revision=s.Revision,
            companions=tx.Campaign.Companions.Select(x=>new {id=x.Key,name=CompanionService.Find(x.Key).Name,type=CompanionService.Find(x.Key).Type,x.Value.Level,x.Value.Star,maxStar=CompanionService.Find(x.Key).MaxStar,skills=x.Value.Skills.Count,fragment=CompanionService.Find(x.Key).Fragment,fragments=tx.Campaign.Materials.GetValueOrDefault(CompanionService.Find(x.Key).Fragment)}),
            avatars=a.AvatarList.Select(x=>new {id=x.AvatarId,name=GrantService.Name("avatar",x.AvatarId),x.Level,x.Star,x.SubStar,x.Fragment,skills=x.SkillList.Count}),
            weapons=e.WeaponList.Select(x=>new {id=x.Id,uniqueId=x.UniqueId,name=GrantService.Name("weapon",x.Id),x.Level}),
            stigmata=e.StigmataList.Select(x=>new {id=x.Id,uniqueId=x.UniqueId,name=GrantService.Name("stigmata",x.Id),x.Level}),
            materials=tx.Campaign.Materials.Select(x=>new {id=x.Key,name=GrantService.Name("material",x.Key),num=x.Value}),
            mails=s.Mails.Select(b=>ClientMail.Parser.ParseFrom(b)).Select(x=>new {id=x.Key.Id,x.Title,x.Content,x.Sender,x.Time,x.ExpireTime,x.IsRead,x.IsAttachmentGot,x.IsFavorite,expired=x.ExpireTime<=Now,attachment=JsonFormatter.Default.Format(x.Attachment??new())}),
            pools=s.Pools,pity=s.Pity,draws=s.Draws,drawLog=s.DrawLog.TakeLast(100).Reverse(),audit=s.Audit.TakeLast(100).Reverse(),
            shops=s.Shops,shopPurchases=s.ShopPurchases,shopRewardOverrides=s.ShopRewardOverrides,mcoin=s.Mcoin,ownedDresses=s.OwnedDresses,serverTime=Now,
            systems=SystemsService.State(tx,Now),dailyMissionIds=SystemsService.DailyMissions.Select(m=>SystemsService.N(m,"id")),
            activePools=s.Pools!.Where(x=>x.Enabled&&x.BeginTime<=Now&&Now<x.EndTime).Select(x=>x.Type) };
    });
    public GmResult Execute(uint uid,GmCommand command)=>store.Campaign(uid,tx=>
    {
        if(!Guid.TryParse(command.RequestId,out _)) throw new ArgumentException("操作编号无效。");
        string json=JsonSerializer.Serialize(command,GrantService.Json);
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        string key="gm:"+command.RequestId;
        if(tx.Receipt(key) is {} receipt)
        {
            var saved=JsonSerializer.Deserialize<SavedOperation>(receipt)!;
            if(saved.Hash!=hash) throw new ArgumentException("操作编号已用于另一项操作，请刷新。");
            return saved.Result;
        }
        var s=State(tx);string message;
        switch(command.Action)
        {
            case "activity":
                if(command.Revision!=s.Revision)throw new ArgumentException("配置已更新，请刷新后重试。");
                var activity=command.Activity??throw new ArgumentException("活动不能为空。");
                if(activity.Id==0||activity.Title.Length is 0 or >100||activity.Content.Length>4000||activity.BeginTime>=activity.EndTime||activity.Panel!=0)
                    throw new ArgumentException("请填写有效的活动编号、标题、正文及时间；本地活动模板为 0。");
                var allowedMissions=SystemsService.DailyMissions.Select(m=>SystemsService.N(m,"id")).ToHashSet();
                if(SystemsService.CapturedActivities.TryGetValue(activity.Id,out var captured))
                {
                    if(captured.ActivityType==4)allowedMissions.UnionWith(captured.TypeParamList);
                    else if((activity.Missions??[]).Length!=0)throw new ArgumentException("此活动使用原生玩法入口，不支持关联作战任务。");
                }
                if((activity.Missions??[]).Length>256||(activity.Missions??[]).Distinct().Count()!=(activity.Missions??[]).Length||(activity.Missions??[]).Any(id=>!allowedMissions.Contains(id)))throw new ArgumentException("活动只能关联其抓包任务或已接入的作战任务编号。");
                var sys=SystemsService.State(tx,Now);sys.Activities!.RemoveAll(a=>a.Id==activity.Id);sys.Activities.Add(activity);
                message="活动配置已保存，重新登录后更新客户端列表。";break;
            case "battle-pass":
                if(command.Revision!=s.Revision)throw new ArgumentException("配置已更新，请刷新后重试。");
                var pass=command.BattlePass??throw new ArgumentException("通行证配置不能为空。");
                if(pass.Schedule!=34||pass.BeginTime>=pass.EndTime||pass.WeeklyLimit is 0 or >100000||pass.FreeExp>100000)
                    throw new ArgumentException("当前支持本地通行证模板 34，请检查时间、周上限和免费历练。");
                SystemsService.State(tx,Now).Pass=pass;message="通行证开放时间及历练配置已保存。";break;
            case "companion":
                CompanionService.Set(tx,command.ElfId,command.Level,command.Star,command.MaxSkills);
                message="协同者 / 人偶属性已更新。";break;
            case "grant":
                var items=command.Items??[];
                if(items.Length is <1 or >20) throw new ArgumentException("请添加 1 至 20 项奖励。");
                foreach(var item in items) GrantService.Validate(item);
                foreach(var item in items) GrantService.Apply(tx,uid,item);
                message=$"已发放 {items.Length} 项奖励。";break;
            case "captain":
                if(command.Level is <1 or >88) throw new ArgumentException("舰长等级范围为 1–88。");
                tx.Lobby=tx.Lobby with {Level=command.Level,Exp=0};message=$"舰长等级已设为 {command.Level}。";break;
            case "avatar":
                if(command.Level is <1 or >80 || command.Star is <1 or >5) throw new ArgumentException("女武神等级 1–80，星级 1–5。");
                var(a,e)=GrantService.Inventory(tx);var av=a.AvatarList.FirstOrDefault(x=>x.AvatarId==command.AvatarId)??throw new ArgumentException("尚未拥有该女武神。");
                av.Level=command.Level;av.Exp=0;if(av.Star!=command.Star)av.SubStar=0;av.Star=command.Star;
                if(command.MaxSkills) foreach(var skill in av.SkillList) foreach(var sub in skill.SubSkillList)
                    sub.Level=GrantService.Catalog.Subskills.FirstOrDefault(x=>x.Id==sub.SubSkillId)?.MaxLevel??sub.Level;
                GrantService.Save(tx,a,e,uid);message="女武神属性已更新。";break;
            case "mail": message=$"邮件 #{SendMail(tx,command)} 已发送。";break;
            case "delete-mail":
                int i=s.Mails.FindIndex(b=>ClientMail.Parser.ParseFrom(b).Key.Id==command.MailId);
                if(i<0) throw new ArgumentException("邮件不存在。");
                s.Mails.RemoveAt(i);message="邮件已删除。";break;
            case "pool":
                if(command.Revision!=s.Revision) throw new ArgumentException("配置已被其他操作更新，请刷新后重试。");
                ValidatePool(command.Pool??throw new ArgumentException("奖池不能为空。"));
                var pool=command.Pool;s.Pools!.RemoveAll(p=>p.Type==pool.Type);s.Pools.Add(pool);
                s.GachaRandom=NewRandom();message="补给配置已保存，下一次抽取生效。";break;
            case "shop":
                if(command.Revision!=s.Revision) throw new ArgumentException("配置已被其他操作更新，请刷新后重试。");
                ValidateShop(command.Shop??throw new ArgumentException("商店不能为空。"));
                ShopState(tx).Shops!.RemoveAll(x=>x.Id==command.Shop.Id);s.Shops!.Add(command.Shop);
                message="商店配置已保存并同步游戏。";break;
            case "shop-restock":
                if(command.Revision!=s.Revision) throw new ArgumentException("配置已更新，请刷新后重试。");
                if(!ShopState(tx).Shops!.Any(x=>x.Id==command.ShopId))throw new ArgumentException("商店不存在。");
                ResetStock(s,command.ShopId);message="该商店购买次数已重置。";break;
            case "shop-reward":
                if(command.Revision!=s.Revision)throw new ArgumentException("配置已更新，请刷新后重试。");
                if(!Products.ContainsKey(command.GoodsId))throw new ArgumentException("商品不存在。");
                if(command.Items is null || command.Items.Length>20)throw new ArgumentException("奖励最多 20 项。");
                foreach(var item in command.Items)GrantService.Validate(item);
                if(command.Items.Length==0)s.ShopRewardOverrides.Remove(command.GoodsId);
                else s.ShopRewardOverrides[command.GoodsId]=command.Items;
                message=command.Items.Length==0?"已恢复商品默认奖励。":"商品购买奖励已保存。";break;
            default: throw new ArgumentException("不支持的 GM 操作。");
        }
        s.Revision=checked(s.Revision+1);s.Audit.Add(new(Now,command.Action,message));if(s.Audit.Count>1000)s.Audit.RemoveAt(0);
        var result=new GmResult(message,s.Revision);tx.SaveReceipt(key,JsonSerializer.SerializeToUtf8Bytes(new SavedOperation(hash,result)));return result;
    });
    private sealed record SavedOperation(string Hash,GmResult Result);
}
