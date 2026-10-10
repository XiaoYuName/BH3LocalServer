using System.Text.Json;
using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class OperationsTests:IDisposable
{
    private readonly TestDirectory temp=new();private readonly LobbyStore store;private readonly GmService service;
    private const uint Uid=10001;
    public OperationsTests(){var db=new SqliteConnectionFactory(Path.Combine(temp.Path,"gm.db"));SchemaMigrator.Initialize(db);store=new(db);store.EnsurePlayer(Uid,"舰长",new(88,80,101,20001,59101,1,[],Scoin:2000,Hcoin:10000));service=new(store);}
    private static GmCommand Cmd(string action,params GrantItem[] items)=>new(Guid.NewGuid().ToString(),action,items);
    [Fact]public void CatalogAndEmptyStateLoadWithCapturedTemplates(){Assert.True(GrantService.Catalog.Items.Length>12000);Assert.NotNull(service.Snapshot(Uid));Assert.Equal(4,service.GachaDisplay(Uid,new(){IsAll=true}).GachaDisplayInfoList.Count);}
    [Fact]public void GrantIsAtomicIdempotentAndPersistent()
    {
        var c=Cmd("grant",new GrantItem("hcoin",0,2800),new GrantItem("weapon",20001,2));var result=service.Execute(Uid,c);Assert.Equal(result,service.Execute(Uid,c));
        Assert.Equal(12800u,store.Read(Uid).Hcoin);Assert.Equal(3,GetEquipmentDataRsp.Parser.ParseFrom(store.Inventory(Uid)!.Equipment).WeaponList.Count);
        Assert.Throws<ArgumentException>(()=>service.Execute(Uid,c with{Items=[new GrantItem("hcoin",0,1)]}));
        Assert.Throws<ArgumentException>(()=>service.Execute(Uid,Cmd("grant",new GrantItem("hcoin",0,1),new GrantItem("weapon",uint.MaxValue))));Assert.Equal(12800u,store.Read(Uid).Hcoin);
        Assert.Equal(result,new GmService(store).Execute(Uid,c));
    }
    [Fact]public async Task ConcurrentGrantRetriesOnlyApplyOnce()
    {var c=Cmd("grant",new GrantItem("hcoin",0,100));await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>service.Execute(Uid,c),TestContext.Current.CancellationToken)));Assert.Equal(10100u,store.Read(Uid).Hcoin);}
    [Fact]public void MailRoundTripReadFavoriteClaimReplayAndRestart()
    {
        var c=Cmd("mail",new GrantItem("hcoin",0,200),new GrantItem("weapon",20001),new GrantItem("material",3000,10)) with{Title="测试邮件",Content="正文",Days=7};service.Execute(Uid,c);service.Execute(Uid,c);
        var mail=Assert.Single(service.Mail(Uid,new(){Stop=9}).MailList);Assert.Equal(1u,mail.MailStyle);Assert.False(mail.IsAttachmentGot);Assert.Equal(10000u,store.Read(Uid).Hcoin);
        Assert.Equal(MarkReadClientMailRsp.Types.Retcode.Succ,service.ReadMail(Uid,new(){MailKey=mail.Key}).Retcode);
        service.FavoriteMail(Uid,new(){MailKey=mail.Key,IsFavorite=true});Assert.Single(service.Mail(Uid,new(){FilterType=(ClientMailFilterType)2}).MailList);
        var req=new TakeClientMailAttachmentReq{IsShowAttachment=true};var take=new GmService(store).TakeMail(Uid,req);Assert.Single(take.SuccMailKeyList);Assert.True(take.IsTakeAll);Assert.Equal(200u,take.MailAttachment.Hcoin);Assert.Equal(10200u,store.Read(Uid).Hcoin);
        Assert.Empty(new GmService(store).TakeMail(Uid,req).SuccMailKeyList);Assert.Equal(10200u,store.Read(Uid).Hcoin);
        Assert.Equal(DelClientMailRsp.Types.Retcode.MailNotDelete,service.DeleteMail(Uid,new(){MailKey=mail.Key}).Retcode);
        service.FavoriteMail(Uid,new(){MailKey=mail.Key,IsFavorite=false});Assert.Equal(DelClientMailRsp.Types.Retcode.Succ,service.DeleteMail(Uid,new(){IsOneClickDelete=true}).Retcode);Assert.Empty(service.Mail(Uid,new()).MailList);
    }
    [Fact]public void MailOverflowRollsBackEarlierItemsAndClaimFlag()
    {
        store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with {Hcoin=999999999};return 0;});
        service.Execute(Uid,Cmd("mail",new GrantItem("weapon",20001),new GrantItem("hcoin",0,1)) with{Title="overflow",Content="test"});
        Assert.Equal(TakeClientMailAttachmentRsp.Types.Retcode.Fail,service.TakeMail(Uid,new()).Retcode);Assert.Null(store.Inventory(Uid));Assert.False(Assert.Single(service.Mail(Uid,new()).MailList).IsAttachmentGot);
    }
    [Fact]public void MailPagingAndExpiredMailCannotGrant()
    {
        for(int i=0;i<12;i++)service.Execute(Uid,Cmd("mail",new GrantItem("hcoin",0,1)) with{Title="mail"+i,Content="test"});
        var first=service.Mail(Uid,new(){Stop=9});Assert.Equal(10,first.MailList.Count);Assert.False(first.IsEnd);Assert.Equal(2,service.Mail(Uid,new(){Start=10,Stop=19}).MailList.Count);
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Mails=tx.Campaign.Operations.Mails.Select(b=>{var m=ClientMail.Parser.ParseFrom(b);m.ExpireTime=1;return m.ToByteArray();}).ToList();return 0;});
        Assert.Empty(service.Mail(Uid,new()).MailList);Assert.Empty(service.TakeMail(Uid,new()).SuccMailKeyList);Assert.Equal(10000u,store.Read(Uid).Hcoin);
    }
    private void Enable(uint pity=3)
    {
        var s=store.Campaign(Uid,tx=>tx.Campaign.Operations.Revision);service.Execute(Uid,Cmd("pool") with{Revision=s,Pool=new(46,"测试装备",true,280,1102,pity,[new(new GrantItem("weapon",20001),1,true),new(new GrantItem("material",3000,2),100,false)])});
    }
    [Fact]public void DrawChargesOnceAndReturnsSameReceiptAfterRestart()
    {
        Enable();uint token=service.GachaDisplay(Uid,new(){IsAll=true}).GachaRandom;
        var req=new GachaReq{Type=(GachaType)46,Num=10,IsUseHcoin=true,GachaRandom=token};var r=service.Draw(Uid,req);
        Assert.Equal(GachaRsp.Types.Retcode.Succ,r.Retcode);Assert.Equal(10,r.ItemList.Count);Assert.True(r.ItemList.Count(x=>x.IsRareDrop)>=3);Assert.Equal(7200u,store.Read(Uid).Hcoin);
        Assert.Equal(r.ToByteArray(),new GmService(store).Draw(Uid,req).ToByteArray());Assert.Equal(7200u,store.Read(Uid).Hcoin);
        Assert.NotEqual(GachaRsp.Types.Retcode.Succ,service.Draw(Uid,withCloneNum(1)).Retcode);
        Assert.Equal(10,Assert.Single(service.GachaLog(Uid).LogInfoList).LogList.Count);Assert.Equal(2,service.Probability(Uid,new(){GachaType=46}).DetailProbList.Count);
        GachaReq withCloneNum(uint num){var q=req.Clone();q.Num=num;return q;}
    }
    [Fact]public void TicketPurchaseAndDrawUseSameInventory()
    {
        Enable();Assert.Equal(560u,service.BuyTickets(Uid,new(){MaterialId=1102,Num=2}).HcoinCost);Assert.Equal(9440u,store.Read(Uid).Hcoin);
        var req=new GachaReq{Type=(GachaType)46,Num=1,GachaRandom=service.GachaDisplay(Uid,new()).GachaRandom};Assert.Equal(GachaRsp.Types.Retcode.Succ,service.Draw(Uid,req).Retcode);
        Assert.Equal(1u,store.Campaign(Uid,tx=>tx.Campaign.Materials[1102]));Assert.Equal(9440u,store.Read(Uid).Hcoin);
    }
    [Fact]public void DisabledPoolInsufficientFundsAndInvalidCountNeverCharge()
    {
        service.Snapshot(Uid);store.Campaign(Uid,tx=>{tx.Campaign.Operations.Pools=tx.Campaign.Operations.Pools!.Select(p=>p with {Enabled=false}).ToList();return 0;});var req=new GachaReq{Type=(GachaType)46,Num=1,IsUseHcoin=true,GachaRandom=service.GachaDisplay(Uid,new()).GachaRandom};Assert.Equal(GachaRsp.Types.Retcode.GachaClosed,service.Draw(Uid,req).Retcode);
        Enable();store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Hcoin=1};return 0;});req.GachaRandom=service.GachaDisplay(Uid,new()).GachaRandom;Assert.Equal(GachaRsp.Types.Retcode.HcoinLack,service.Draw(Uid,req).Retcode);Assert.Equal(1u,store.Read(Uid).Hcoin);req.Num=9;Assert.Equal(GachaRsp.Types.Retcode.Fail,service.Draw(Uid,req).Retcode);
    }
    [Fact]public void GrantAvatarBuildsOwnedEquipmentAndDuplicateBecomesFragments()
    {
        service.Execute(Uid,Cmd("grant",new GrantItem("avatar",102)));var a=GetAvatarDataRsp.Parser.ParseFrom(store.Inventory(Uid)!.Avatars);var av=Assert.Single(a.AvatarList,x=>x.AvatarId==102);Assert.NotEmpty(av.SkillList);
        Assert.Contains(GetEquipmentDataRsp.Parser.ParseFrom(store.Inventory(Uid)!.Equipment).WeaponList,x=>x.UniqueId==av.WeaponUniqueId);
        service.Execute(Uid,Cmd("grant",new GrantItem("avatar",102)));Assert.Equal(30u,GetAvatarDataRsp.Parser.ParseFrom(store.Inventory(Uid)!.Avatars).AvatarList.Single(x=>x.AvatarId==102).Fragment);
    }
    public void Dispose()=>temp.Dispose();
}
