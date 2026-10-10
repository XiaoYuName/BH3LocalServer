using BH3.Game.Campaign;
using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Operations;
using BH3.Game.Players;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;
using System.Text.Json;

namespace BH3.Game.Tests;

public sealed class CompanionTests:IDisposable
{
    private const uint Uid=10001;
    private readonly TestDirectory temp=new();
    private readonly SqliteConnectionFactory db;
    private readonly LobbyStore store;
    private readonly CompanionService service;
    private readonly GmService gm;
    public CompanionTests()
    {
        db=new(Path.Combine(temp.Path,"companions.db"));SchemaMigrator.Initialize(db);store=new(db);
        store.EnsurePlayer(Uid,"captain",new(88,200,101,20001,59101,1,[],Scoin:100000,Hcoin:10000));service=new(store);gm=new(store);
    }
    private void Grant(params GrantItem[] items)=>gm.Execute(Uid,new(Guid.NewGuid().ToString(),"grant",items));
    private Elf Elf(uint id)=>service.Get(Uid).ElfList.Single(x=>x.ElfId==id);
    private uint Material(uint id)=>store.Campaign(Uid,tx=>tx.Campaign.Materials.GetValueOrDefault(id));
    private string Saved()=>store.Campaign(Uid,tx=>JsonSerializer.Serialize(new {tx.Lobby,tx.Campaign}));
    [Fact] public void Client91SupplyGateOpensOnlyAfterRealMissionCompletion()
    {
        var campaign=new CampaignService(store,TimeProvider.System);
        store.Campaign(Uid,tx=>{tx.Campaign.Stages[10106]=new(){Wins=1};return 0;});
        var pending=campaign.Missions(Uid).MissionList.Single(x=>x.MissionId==10009);
        Assert.Equal(2,(int)pending.Status);Assert.False((int)pending.Status==3);
        store.Campaign(Uid,tx=>{tx.Campaign.Stages[10109]=new(){Wins=1};return 0;});
        var complete=GetMissionDataRsp.Parser.ParseFrom(campaign.Missions(Uid).ToByteArray()).MissionList.Single(x=>x.MissionId==10009);
        Assert.Equal(3,(int)complete.Status);Assert.Empty(store.Campaign(Uid,tx=>tx.Campaign.ClaimedMissions));
        Assert.Equal(10000u,store.Read(Uid).Hcoin);
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.Succ,campaign.Claim(Uid,new(){MissionIdList={10009}}).Retcode);
        Assert.Contains(10009u,campaign.Missions(Uid).CloseMissionList);
    }
    [Fact] public void AllKnownCompanionsGrantWithOwnSkillsAndDuplicateCardsBecomeSharedFragments()
    {
        foreach(var spec in CompanionService.Catalog.Companions)Grant(new GrantItem("elf",spec.Id));
        Assert.Equal(20,service.Get(Uid).ElfList.Count);Assert.All(service.Get(Uid).ElfList,x=>Assert.NotEmpty(x.SkillList));
        Grant(new GrantItem("material",380101));Assert.Equal(30u,Material(370101));Assert.Single(service.Get(Uid).ElfList,x=>x.ElfId==101);
        Assert.Equal(service.Get(Uid).ToByteArray(),new CompanionService(new LobbyStore(db)).Get(Uid).ToByteArray());
    }
    [Fact] public void UnlockStarUpAndExpSpendExactlyAndRejectInsufficientFunds()
    {
        Assert.Equal(ElfStarUpRsp.Types.Retcode.FragmentLack,service.StarUp(Uid,new(){ElfId=101}).Retcode);
        Grant(new GrantItem("material",370101,150));var unlocked=service.StarUp(Uid,new(){ElfId=101});Assert.True(unlocked.IsUnlock);Assert.Equal(50u,Material(370101));
        Assert.Equal(ElfStarUpRsp.Types.Retcode.Succ,service.StarUp(Uid,new(){ElfId=101}).Retcode);Assert.Equal(2u,Elf(101).Star);Assert.Equal(0u,Material(370101));
        Grant(new GrantItem("material",1002,2));var result=service.AddExp(Uid,new(){ElfId=101,MaterialId=1002,MaterialNum=1});
        Assert.Equal(AddElfExpByMaterialRsp.Types.Retcode.Succ,result.Retcode);Assert.Equal(1u,result.OldLevel);Assert.Equal(3u,Elf(101).Level);Assert.Equal(70u,Elf(101).Exp);Assert.Equal(99000u,store.Read(Uid).Scoin);Assert.Equal(1u,Material(1002));
        string before=Saved();Assert.Equal(AddElfExpByMaterialRsp.Types.Retcode.MaterialNotEnough,service.AddExp(Uid,new(){ElfId=101,MaterialId=1002,MaterialNum=2}).Retcode);Assert.Equal(before,Saved());
    }
    [Fact] public void ExperienceNeverLowersImportedLevelAndRejectsZeroOrWrongMaterial()
    {
        Grant(new GrantItem("elf",101,1,80),new GrantItem("material",1002,5));store.Campaign(Uid,tx=>{tx.Lobby=tx.Lobby with{Level=20};return 0;});string before=Saved();
        Assert.Equal(AddElfExpByMaterialRsp.Types.Retcode.ElfLevelFull,service.AddExp(Uid,new(){ElfId=101,MaterialId=1002,MaterialNum=1}).Retcode);Assert.Equal(before,Saved());
        Grant(new GrantItem("elf",102));before=Saved();Assert.NotEqual(AddElfExpByMaterialRsp.Types.Retcode.Succ,service.AddExp(Uid,new(){ElfId=102,MaterialId=1002,MaterialNum=0}).Retcode);Assert.Equal(before,Saved());
    }
    [Fact] public void SkillsEnforceOwnershipPrerequisitesCostsAndMaskPersists()
    {
        Assert.Equal(ElfSkillLevelUpRsp.Types.Retcode.ElfLocked,service.SkillUp(Uid,new(){ElfId=101,ElfSkillId=10101}).Retcode);
        Grant(new GrantItem("elf",101,1,80));gm.Execute(Uid,new(Guid.NewGuid().ToString(),"companion",Level:80,Star:7,ElfId:101));
        var skill=CompanionService.Find(101).Skills.First(s=>s.MaxLevel>1);
        Grant(new GrantItem("scoin",0,1000000));
        store.Campaign(Uid,tx=>{foreach(var s in CompanionService.Find(101).Skills)tx.Campaign.Companions[101].Skills[s.Id]=s.MaxLevel;tx.Campaign.Companions[101].Skills[skill.Id]=0;foreach(var c in skill.Costs.SelectMany(x=>x.Materials).Where(x=>x.Id!=100))tx.Campaign.Materials[c.Id]=100000;return 0;});
        Assert.Equal(ElfSkillLevelUpRsp.Types.Retcode.Succ,service.SkillUp(Uid,new(){ElfId=101,ElfSkillId=skill.Id,IsLevelUpAll=true}).Retcode);
        Assert.Equal(skill.MaxLevel,Elf(101).SkillList.Single(x=>x.SkillId==skill.Id).SkillLevel);
        Assert.Equal(ElfSkillLevelUpRsp.Types.Retcode.LevelFull,service.SkillUp(Uid,new(){ElfId=101,ElfSkillId=skill.Id}).Retcode);
        Assert.Equal(SwitchElfSkillRsp.Types.Retcode.Succ,service.Switch(Uid,new(){ElfId=101,SkillId=skill.Id,IsMask=true}).Retcode);
        Assert.True(new CompanionService(new LobbyStore(db)).Get(Uid).ElfList.Single(x=>x.ElfId==101).SkillList.Single(x=>x.SkillId==skill.Id).IsMask);
        string before=Saved();Assert.Equal(SwitchElfSkillRsp.Types.Retcode.Fail,service.Switch(Uid,new(){ElfId=101,SkillId=999999,IsMask=true}).Retcode);Assert.Equal(before,Saved());
    }
    [Fact] public void AstralSkillsFollowRankAndDreamseekerSharesProgress()
    {
        Grant(new GrantItem("elf",120),new GrantItem("elf",130),new GrantItem("material",370120,50));
        Assert.Equal(ElfStarUpRsp.Types.Retcode.Succ,service.StarUp(Uid,new(){ElfId=130}).Retcode);Assert.Equal(2u,Elf(120).Star);Assert.Equal(Elf(120).SkillList,Elf(130).SkillList);
        Assert.Equal(ElfSkillLevelUpRsp.Types.Retcode.NotNormalElf,service.SkillUp(Uid,new(){ElfId=120,ElfSkillId=20001}).Retcode);
    }
    [Fact] public void BatchFragmentConversionIsAtomicAndRejectsDuplicates()
    {
        Grant(new GrantItem("material",370101,10));string before=Saved();
        Assert.Equal(ElfFragmentTransformRsp.Types.Retcode.FragmentLack,service.Transform(Uid,new(){FragmentList={new GenericItemNum{Id=370101,Num=2},new GenericItemNum{Id=370102,Num=1}}}).Retcode);Assert.Equal(before,Saved());
        Assert.Equal(ElfFragmentTransformRsp.Types.Retcode.Fail,service.Transform(Uid,new(){ElfFragmentId=370101,ElfFragmentNum=1,FragmentList={new GenericItemNum{Id=370101,Num=1}}}).Retcode);Assert.Equal(before,Saved());
        var result=service.Transform(Uid,new(){ElfFragmentId=370101,ElfFragmentNum=2});Assert.Equal(ElfFragmentTransformRsp.Types.Retcode.Succ,result.Retcode);Assert.Equal(150u,Material(3129));Assert.Equal(8u,Material(370101));
    }
    [Fact] public void CompanionMailCardIsClaimedOnceAndGmTransactionIsAtomic()
    {
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"mail",[new GrantItem("elf",101)],Title:"companion",Content:"test"));
        var result=gm.TakeMail(Uid,new());Assert.Single(result.SuccMailKeyList);Assert.Equal(101u,Elf(101).ElfId);
        Assert.Empty(gm.TakeMail(Uid,new()).SuccMailKeyList);Assert.Equal(0u,Material(370101));
        Assert.Throws<ArgumentException>(()=>Grant(new GrantItem("elf",102),new GrantItem("elf",999)));Assert.DoesNotContain(service.Get(Uid).ElfList,x=>x.ElfId==102);
    }
    [Fact] public void StoryArenaAndAbyssAcceptOwnedCompanionsButRejectInvalidTeams()
    {
        var campaign=new CampaignService(store,TimeProvider.System);var challenge=new ChallengeService(store,TimeProvider.System);
        Assert.NotEqual(StageBeginRsp.Types.Retcode.Succ,campaign.Begin(Uid,new(){StageId=10101,AvatarIdList={101},ElfIdList={101}}).Retcode);
        Grant(new GrantItem("elf",101));var begin=new StageBeginReq{StageId=10101,AvatarIdList={101},ElfIdList={101}};
        var response=campaign.Begin(Uid,begin);Assert.Equal(StageBeginRsp.Types.Retcode.Succ,response.Retcode);Assert.Equal(response,campaign.Begin(Uid,begin));
        begin.ElfIdList.Add(101);Assert.NotEqual(StageBeginRsp.Types.Retcode.Succ,campaign.Begin(Uid,begin).Retcode);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.Succ,challenge.BossBegin(Uid,new(){BossId=49016,AvatarIdList={101},ElfIdList={101},IsTraining=true}).Retcode);
        challenge.EnterSite(Uid,new(){SiteId=1011});Assert.Equal(UltraEndlessReportSiteFloorRsp.Types.Retcode.Succ,challenge.ReportFloor(Uid,new(){SiteId=1011,Floor=1,Score=100,AvatarIdList={101},ElfIdList={101}}).Response.Retcode);
    }
    [Fact] public void NativeHandlersSyncCompanionBeforeMutationCallback()
    {
        Grant(new GrantItem("elf",101));var session=new GameSession(1,"test",1);session.Authenticate(Uid);
        var dispatcher=new GameDispatcher(new LobbyHandlers(store,new SqlitePlayerStore(db),new byte[32]).Create());
        var replies=dispatcher.Dispatch(session,new GamePacket(new byte[26],1742,[],new SwitchElfSkillReq{ElfId=101,SkillId=10101,IsMask=true}.ToByteArray())).Replies;
        Assert.True(replies.ToList().FindIndex(x=>x.CommandId==2102)<replies.ToList().FindIndex(x=>x.CommandId==1743));
        Assert.True(SyncElfDataNotify.Parser.ParseFrom(replies.Single(x=>x.CommandId==2102).Body).ElfList.Single().SkillList.Single(x=>x.SkillId==10101).IsMask);
        Assert.Equal(1,SwitchElfSkillReq.ElfIdFieldNumber);Assert.Equal(2,SwitchElfSkillReq.SkillIdFieldNumber);Assert.Equal(3,ElfSkill.IsMaskFieldNumber);
    }
    [Fact] public void CompanionPoolUsesCapturedTemplateAndReplayCannotChargeTwice()
    {
        var display=gm.GachaDisplay(Uid,new(){IsAll=true});
        var original=Assert.Single(display.GachaDisplayInfoList,x=>(int)x.GachaType==48);
        Assert.Equal(380180u,original.PjmsGachaData.ProtectDisplayInfo.DisplayKeyElfCardId);
        uint revision=store.Campaign(Uid,tx=>tx.Campaign.Operations.Revision);
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"pool",Pool:new(48,"协同者测试",true,280,1102,1,[new(new("elf",180),1,true)]),Revision:revision));
        var req=new GachaReq{Type=(GachaType)48,Num=10,IsUseHcoin=true,GachaRandom=gm.GachaDisplay(Uid,new()).GachaRandom};
        var result=gm.Draw(Uid,req);Assert.Equal(GachaRsp.Types.Retcode.Succ,result.Retcode);Assert.Equal(10,result.ItemList.Count);Assert.All(result.ItemList,x=>Assert.Equal(380180u,x.ItemId));
        Assert.Equal(7200u,store.Read(Uid).Hcoin);Assert.Equal(270u,Material(370180));Assert.Equal(180u,Elf(180).ElfId);
        Assert.Equal(result.ToByteArray(),new GmService(store).Draw(Uid,req).ToByteArray());Assert.Equal(7200u,store.Read(Uid).Hcoin);Assert.Equal(270u,Material(370180));
        revision=store.Campaign(Uid,tx=>tx.Campaign.Operations.Revision);var pool=store.Campaign(Uid,tx=>tx.Campaign.Operations.Pools!.Single(x=>x.Type==48));
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"pool",Pool:pool with{Enabled=false},Revision:revision));
        Assert.DoesNotContain(new GmService(store).GachaDisplay(Uid,new()).GachaDisplayInfoList,x=>(int)x.GachaType==48);
    }
    [Fact] public void CompanionCaptureEnrichesOnlyMatchingAccountAndPreservesExistingLocalValues()
    {
        const string hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        store.Campaign(Uid,tx=>{tx.Inventory=new(new GetAvatarDataRsp().ToByteArray(),new GetEquipmentDataRsp().ToByteArray(),hash,23,"fixture");tx.Campaign.Materials[370101]=77;tx.Campaign.Stages[10109]=new(){Wins=2};return 0;});
        var plan=new AccountCopyPlan(23,hash,false,new(),new(),new(),[],new(){Retcode=GetElfDataRsp.Types.Retcode.Succ,ElfList={new Elf{ElfId=101,Level=80,Star=5,SkillList={new ElfSkill{SkillId=10101,SkillLevel=1,IsMask=true}}}},ElfFragmentList={new ElfFragment{ElfId=101,FragmentNum=3}}});
        var wallet=store.Read(Uid);
        Assert.Equal(1,AccountCopyImport.ApplyCompanions(store,Uid,plan));Assert.Equal(0,AccountCopyImport.ApplyCompanions(store,Uid,plan));
        Assert.Equal(wallet,store.Read(Uid));Assert.Equal(77u,Material(370101));Assert.Equal(2u,store.Campaign(Uid,tx=>tx.Campaign.Stages[10109].Wins));Assert.True(Elf(101).SkillList.Single().IsMask);
        Assert.Throws<InvalidDataException>(()=>AccountCopyImport.ApplyCompanions(store,Uid,plan with{SourceUid=99}));
    }
    public void Dispose()=>temp.Dispose();
}
