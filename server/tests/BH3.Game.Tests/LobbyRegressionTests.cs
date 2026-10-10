using BH3.Game.Campaign;
using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
namespace BH3.Game.Tests;
public sealed class LobbyRegressionTests:IDisposable
{
    private readonly TestDirectory temp=new();
    private readonly SqliteConnectionFactory db;
    private readonly LobbyStore store;
    private readonly GmService gm;
    private readonly SystemsService systems;
    private const uint Uid=10001;
    public LobbyRegressionTests()
    {
        db=new(Path.Combine(temp.Path,"regressions.db"));SchemaMigrator.Initialize(db);store=new(db);
        store.EnsurePlayer(Uid,"fixture",new(88,200,101,20001,59101,1,[],Hcoin:10000,Scoin:10000));
        gm=new(store);systems=new(store,TimeProvider.System);
    }
    private void Grant(params GrantItem[] items)=>gm.Execute(Uid,new(Guid.NewGuid().ToString(),"grant",items));
    [Fact] public void SsToSssDebitsOnePartAndPersistsWithMaxRankAndInsufficientGuards()
    {
        Grant(new GrantItem("elf",180));
        store.Campaign(Uid,tx=>{tx.Campaign.Companions[180].Star=2;return 0;});
        var service=new CompanionService(store);
        Assert.Equal(ElfStarUpRsp.Types.Retcode.FragmentLack,service.StarUp(Uid,new(){ElfId=180}).Retcode);
        Grant(new GrantItem("material",370180,59));
        Assert.Equal(ElfStarUpRsp.Types.Retcode.Succ,service.StarUp(Uid,new(){ElfId=180}).Retcode);
        var restarted=new CompanionService(new LobbyStore(db));
        Assert.Equal(3u,Assert.Single(restarted.Get(Uid).ElfList).Star);
        Assert.Equal(58u,store.Campaign(Uid,tx=>tx.Campaign.Materials[370180]));
        Assert.Equal(ElfStarUpRsp.Types.Retcode.StarFull,restarted.StarUp(Uid,new(){ElfId=180}).Retcode);
        Assert.Equal(58u,store.Campaign(Uid,tx=>tx.Campaign.Materials[370180]));
    }
    [Fact] public void CapturedActivityTemplatesHaveNativeArtAndValidMissionMode()
    {
        var configs=systems.ActivityConfigs(Uid);
        Assert.Equal(30,SystemsService.CapturedActivities.Count);
        foreach(var source in SystemsService.CapturedActivities.Values)
        {
            var actual=Assert.Single(configs,a=>a.ActivityId==source.ActivityId);
            Assert.Equal(source.ActivityType,actual.ActivityType);Assert.Equal(source.ImagePath,actual.ImagePath);
            Assert.Equal(source.BackgroundPath,actual.BackgroundPath);Assert.Equal(source.TypeParamStr,actual.TypeParamStr);
            Assert.Equal(source.TypeParamList,actual.TypeParamList);Assert.Equal(source.LinkButton,actual.LinkButton);
            Assert.True(actual.BeginTime<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()&&actual.EndTime>DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        Assert.All(configs.Where(a=>a.ActivityType==4),a=>Assert.True(uint.TryParse(a.TypeParamStr,out _)));
        configs[0].TypeParamStr="broken";
        Assert.All(systems.ActivityConfigs(Uid).Where(a=>a.ActivityType==4),a=>Assert.True(uint.TryParse(a.TypeParamStr,out _)));
    }
    [Fact] public void GmCapturedActivitiesKeepNativeTemplatesAndDisabledStateAcrossReload()
    {
        systems.ActivityConfigs(Uid);
        var source=store.Campaign(Uid,tx=>tx.Campaign.Systems.Activities!.Single(a=>a.Id==6408));
        Assert.True(source.Missions!.Length>32);
        gm.Execute(Uid,new(Guid.NewGuid().ToString(),"activity",Activity:source with{Enabled=false}));
        var restarted=new SystemsService(new LobbyStore(db),TimeProvider.System);
        Assert.Equal(1u,Assert.Single(restarted.ActivityConfigs(Uid),a=>a.ActivityId==6408).EndTime);
        Assert.DoesNotContain(restarted.ActivityMissions(Uid,new()).MissionGroupList,a=>a.ActivityId==6408);
        Assert.Single(store.Campaign(Uid,tx=>tx.Campaign.Systems.Activities!.Where(a=>a.Id==6408).ToArray()));
    }
    [Fact] public void ActivityMissionListUsesLocalProgressInsteadOfCapturedCompletion()
    {
        var response=new CampaignService(store,TimeProvider.System).Missions(Uid);
        var ids=SystemsService.CapturedActivities.Values.Where(a=>a.ActivityType==4).SelectMany(a=>a.TypeParamList).Distinct().ToArray();
        Assert.NotEmpty(ids);
        foreach(var id in ids)
        {
            var mission=Assert.Single(response.MissionList,m=>m.MissionId==id);
            Assert.Equal(MissionStatus.Doing,mission.Status);Assert.Equal(0u,mission.Progress);
        }
    }
    [Theory] [InlineData(721562u,1103u)] [InlineData(721567u,1102u)]
    public void SingleTicketBundleUsesGiftCoinsAndPersistsDelivery(uint goods,uint ticket)
    {
        uint shop=GmService.ShopCatalog.Shops.First(s=>s.Goods.Contains(goods)).Id;
        var req=new BuyGoodsReq{ShopId=shop,GoodsId=goods};
        Assert.Equal(BuyGoodsRsp.Types.Retcode.MoneyLack,gm.BuyGoods(Uid,req).Retcode);
        store.Campaign(Uid,tx=>{tx.Campaign.Operations.Mcoin=60;return 0;});
        var rsp=gm.BuyGoods(Uid,req);Assert.Equal(BuyGoodsRsp.Types.Retcode.Succ,rsp.Retcode);Assert.Equal(1u,rsp.Num);
        Assert.Equal(0u,store.Campaign(Uid,tx=>tx.Campaign.Operations.Mcoin));
        Assert.Equal(1u,store.Campaign(Uid,tx=>tx.Campaign.Materials[ticket]));
        Assert.Equal(BuyGoodsRsp.Types.Retcode.BuyTimesLack,new GmService(new LobbyStore(db)).BuyGoods(Uid,req).Retcode);
        Assert.Equal(1u,store.Campaign(Uid,tx=>tx.Campaign.Materials[ticket]));
    }
    public void Dispose()=>temp.Dispose();
}
