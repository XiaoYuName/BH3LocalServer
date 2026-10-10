using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed partial class GmService
{
    private static ClientMailKey Key(uint id)=>new(){Id=id,Type=(ClientMailType)1};
    private uint SendMail(CampaignTransaction tx,GmCommand c)
    {
        if(string.IsNullOrWhiteSpace(c.Title)||c.Title.Length>80||string.IsNullOrWhiteSpace(c.Content)||c.Content.Length>4000||c.Sender?.Length>40||c.Days is <1 or >365)
            throw new ArgumentException("请填写标题（80 字以内）、正文（4000 字以内）和 1–365 天有效期。");
        var items=c.Items??[];if(items.Length>20)throw new ArgumentException("最多 20 项附件。");
        var attachment=new MailAttachment();
        foreach(var item in items)
        {
            GrantService.Validate(item);
            switch(item.Kind)
            {
                case "scoin":attachment.Scoin=checked(attachment.Scoin+item.Num);break;
                case "hcoin":attachment.Hcoin=checked(attachment.Hcoin+item.Num);break;
                case "stamina":attachment.Stamina=checked(attachment.Stamina+item.Num);break;
                case "avatar":throw new ArgumentException("邮件请使用角色碎片；女武神可在发放页面直接解锁。");
                case "elf":attachment.ItemList.Add(new MailItem{ItemId=CompanionService.Find(item.Id).Card,Level=1,Num=item.Num});break;
                default:attachment.ItemList.Add(new MailItem{ItemId=item.Id,Level=item.Level,Num=item.Num});break;
            }
        }
        var s=State(tx);if(s.Mails.Count>=1000)throw new ArgumentException("邮箱已达 1000 封，请先清理。");
        uint id=s.NextMailId++;
        s.Mails.Add(new ClientMail{Key=Key(id),Title=c.Title,Content=c.Content,Sender=string.IsNullOrWhiteSpace(c.Sender)?"休伯利安管理组":c.Sender,
            Time=Now,ExpireTime=checked(Now+c.Days*86400),Attachment=attachment,IsRead=false,IsAttachmentGot=items.Length==0,MailStyle=1}.ToByteArray());
        return id;
    }
    public GetClientMailDataRsp Mail(uint uid,GetClientMailDataReq r)=>store.Campaign(uid,tx=>
    {
        var all=tx.Campaign.Operations.Mails.Select(ClientMail.Parser.ParseFrom).Where(x=>x.ExpireTime>Now).OrderByDescending(x=>x.Time).ThenByDescending(x=>x.Key.Id).ToArray();
        var filtered=all.Where(x=>(int)r.FilterType switch {0=>true,1=>!x.IsAttachmentGot,2=>x.IsFavorite,_=>false}).ToArray();
        var rsp=new GetClientMailDataRsp{Retcode=GetClientMailDataRsp.Types.Retcode.Succ,Start=r.Start,FilterType=r.FilterType,
            ClientMailInfo=new(){TotalNum=(uint)all.Length,CanFastDeleteNum=(uint)all.Count(x=>x.IsRead&&x.IsAttachmentGot&&!x.IsFavorite)}};
        rsp.ClientMailInfo.MailStyleInfoList.Add(all.GroupBy(x=>x.MailStyle).Select(g=>new ClientMailStyleInfo{MailStyle=g.Key,UntakenNum=(uint)g.Count(x=>!x.IsAttachmentGot)}));
        if(r.Start>filtered.Length || (r.Stop!=0&&r.Stop<r.Start) || (int)r.FilterType>2)
        {rsp.Retcode=GetClientMailDataRsp.Types.Retcode.PosInvalid;rsp.IsEnd=true;return rsp;}
        int count=r.Stop==0?100:(int)Math.Min(100,r.Stop-r.Start+1);
        rsp.MailList.Add(filtered.Skip((int)r.Start).Take(count));rsp.IsEnd=r.Start+rsp.MailList.Count>=filtered.Length;return rsp;
    });
    public TakeClientMailAttachmentRsp TakeMail(uint uid,TakeClientMailAttachmentReq r)
    {
        try{return store.Campaign(uid,tx=>
        {
            var s=tx.Campaign.Operations;var all=s.Mails.Select(ClientMail.Parser.ParseFrom).ToArray();
            var rsp=new TakeClientMailAttachmentRsp{Retcode=TakeClientMailAttachmentRsp.Types.Retcode.Succ,IsShowAttachment=r.IsShowAttachment,IsTakeAll=r.MailKeyList.Count==0,MailAttachment=new()};
            if(r.MailKeyList.Count>1000)throw new ArgumentException("Too many keys.");
            var keys=r.MailKeyList.Count==0?all.Where(x=>x.ExpireTime>Now&&!x.IsAttachmentGot).Select(x=>x.Key).ToArray():r.MailKeyList.DistinctBy(x=>(x.Type,x.Id)).ToArray();
            foreach(var key in keys)
            {
                var m=all.FirstOrDefault(x=>x.Key.Equals(key));
                if(m is null||m.ExpireTime<=Now){rsp.FailMailList.Add(new ClientMailAttachmentItem{Key=key.Clone()});continue;}
                if(m.IsAttachmentGot)continue;
                var attachment=m.Attachment??new();
                foreach(var item in attachment.ItemList){GrantService.Apply(tx,uid,GrantService.FromMail(item));rsp.MailAttachment.ItemList.Add(item.Clone());}
                if(attachment.Hcoin>0)GrantService.Apply(tx,uid,new("hcoin",0,attachment.Hcoin));
                if(attachment.Scoin>0)GrantService.Apply(tx,uid,new("scoin",0,attachment.Scoin));
                if(attachment.Stamina>0)GrantService.Apply(tx,uid,new("stamina",0,attachment.Stamina));
                rsp.MailAttachment.Hcoin=checked(rsp.MailAttachment.Hcoin+attachment.Hcoin);rsp.MailAttachment.Scoin=checked(rsp.MailAttachment.Scoin+attachment.Scoin);rsp.MailAttachment.Stamina=checked(rsp.MailAttachment.Stamina+attachment.Stamina);
                m.IsAttachmentGot=true;m.IsRead=true;rsp.SuccMailKeyList.Add(key.Clone());
            }
            s.Mails=all.Select(x=>x.ToByteArray()).ToList();
            if(rsp.FailMailList.Count>0)rsp.Retcode=TakeClientMailAttachmentRsp.Types.Retcode.PartFail;
            if(rsp.SuccMailKeyList.Count>0){s.Revision++;s.Audit.Add(new(Now,"mail-claim",$"领取 {rsp.SuccMailKeyList.Count} 封邮件附件。"));}
            return rsp;
        });}catch(ArgumentException){return new(){Retcode=TakeClientMailAttachmentRsp.Types.Retcode.Fail,IsShowAttachment=r.IsShowAttachment};}
    }
    private bool UpdateMail(uint uid,ClientMailKey? key,Action<ClientMail> change)=>store.Campaign(uid,tx=>
    {
        if(key is null)return false;var list=tx.Campaign.Operations.Mails;
        for(int i=0;i<list.Count;i++){var m=ClientMail.Parser.ParseFrom(list[i]);if(!m.Key.Equals(key)||m.ExpireTime<=Now)continue;change(m);list[i]=m.ToByteArray();return true;}return false;
    });
    public MarkReadClientMailRsp ReadMail(uint uid,MarkReadClientMailReq r)=>new(){Retcode=UpdateMail(uid,r.MailKey,m=>m.IsRead=true)?MarkReadClientMailRsp.Types.Retcode.Succ:MarkReadClientMailRsp.Types.Retcode.Fail,MailKey=r.MailKey?.Clone()};
    public SetClientMailFavoriteRsp FavoriteMail(uint uid,SetClientMailFavoriteReq r)=>new(){Retcode=UpdateMail(uid,r.MailKey,m=>m.IsFavorite=r.IsFavorite)?SetClientMailFavoriteRsp.Types.Retcode.Succ:SetClientMailFavoriteRsp.Types.Retcode.Fail,MailKey=r.MailKey?.Clone(),IsFavorite=r.IsFavorite};
    public DelClientMailRsp DeleteMail(uint uid,DelClientMailReq r)=>store.Campaign(uid,tx=>
    {
        var list=tx.Campaign.Operations.Mails;var rsp=new DelClientMailRsp{Retcode=DelClientMailRsp.Types.Retcode.Succ,MailKey=r.MailKey?.Clone(),IsOneClickDelete=r.IsOneClickDelete};
        if(r.IsOneClickDelete)list.RemoveAll(b=>{var m=ClientMail.Parser.ParseFrom(b);return m.ExpireTime<=Now||m.IsRead&&m.IsAttachmentGot&&!m.IsFavorite;});
        else
        {
            int i=list.FindIndex(b=>ClientMail.Parser.ParseFrom(b).Key.Equals(r.MailKey));
            if(i<0)rsp.Retcode=DelClientMailRsp.Types.Retcode.MailNotExist;
            else{var m=ClientMail.Parser.ParseFrom(list[i]);if(m.ExpireTime>Now&&(!m.IsAttachmentGot||m.IsFavorite))rsp.Retcode=DelClientMailRsp.Types.Retcode.MailNotDelete;else list.RemoveAt(i);}
        }
        return rsp;
    });
}
