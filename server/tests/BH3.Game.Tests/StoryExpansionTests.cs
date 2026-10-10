using System.Text.Json;
using BH3.Game.Campaign;
using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class StoryExpansionTests : IDisposable
{
    private const uint Uid = 10001;
    private readonly TestDirectory temp = new();
    private readonly SqliteConnectionFactory db;
    private readonly LobbyStore store;
    private readonly CampaignService service;
    public StoryExpansionTests()
    {
        db = new(Path.Combine(temp.Path, "story.db")); SchemaMigrator.Initialize(db); store = new(db);
        store.EnsurePlayer(Uid, "captain", new(88, 9999, 101, 20001, 59101, 1, [])); service = new(store, TimeProvider.System);
    }
    private StageBeginRsp Begin(uint id) => service.Begin(Uid, new() { StageId = id, AvatarIdList = { 101 } });
    private StageEndRsp End(StageBeginRsp begin, StageDefinition stage) => service.End(Uid, new() { Sign = begin.SignKey,
        Body = new StageEndReqBody { StageId = stage.Id, StagePassTime = 60000, ChallengeIndexList = { Enumerable.Range(0, stage.Challenges.Length).Select(i => (uint)i) } }.ToByteString() });
    [Fact]
    public void EveryImportedOrdinaryStageCanProgressThroughItsPrerequisiteGraph()
    {
        var catalog = CampaignCatalog.Default; var retired = catalog.Stages.Where(s => s.Level > 88).Select(s => s.Id).ToHashSet();
        int oldCount;
        do { oldCount = retired.Count; retired.UnionWith(catalog.Stages.Where(s => s.Previous.Any(retired.Contains)).Select(s => s.Id)); } while (oldCount != retired.Count);
        var pending = catalog.Stages.Where(s => !retired.Contains(s.Id)).ToDictionary(s => s.Id); var completed = new HashSet<uint>();
        Assert.Equal(1314, catalog.Stages.Length); Assert.Equal(38, catalog.Chapters.Length);
        Assert.DoesNotContain(catalog.Stages, s => s.Difficulty == 1 && retired.Contains(s.Id));
        Assert.Equal(StageBeginRsp.Types.Retcode.LevelLack, Begin(20103).Retcode); // Retired hard/supreme stages keep the original level-99 lock.
        Assert.All(service.Stages(Uid, new()).StageList, s => Assert.False(s.IsDone));
        while (pending.Count > 0)
        {
            var next = pending.Values.FirstOrDefault(s => s.Previous.All(completed.Contains));
            Assert.NotNull(next); // Cycles or absent prerequisites must fail instead of silently skipping stages.
            store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Stamina = 9999 }; return 0; });
            var begin = Begin(next.Id); Assert.True(begin.Retcode == StageBeginRsp.Types.Retcode.Succ, $"begin {next.Id}: {begin.Retcode}");
            var end = End(begin, next); Assert.True(end.Retcode == StageEndRsp.Types.Retcode.Succ, $"end {next.Id}: {end.Retcode}");
            Assert.True(end.IsFirstWin); completed.Add(next.Id); pending.Remove(next.Id);
        }
        var reopened = new CampaignService(new LobbyStore(db), TimeProvider.System); var stages = reopened.Stages(Uid, new());
        Assert.All(stages.StageList, s => Assert.Equal(!retired.Contains(s.Id), s.IsDone)); Assert.Equal(38, stages.FinishedChapterList.Count);
        Assert.All(reopened.ChapterGroups(Uid, new()).ChapterGroupList.SelectMany(g => g.SiteList), s => Assert.Equal(ChapterGroupSiteStatus.Finished, s.Status));
        Assert.Equal(GetStageDataRsp.Types.Retcode.StageNotExist, service.Stages(Uid, new() { StageIdList = { uint.MaxValue } }).Retcode);
    }
    [Fact]
    public void DifficultyRewardsAreSeparateAndOldNormalClaimKeysRemainValid()
    {
        store.Campaign(Uid, tx => {
            tx.Campaign.ClaimedActRewards.Add("101:1");
            foreach (var stage in CampaignCatalog.Default.Stages.Where(s => s.Act == 101 && s.Difficulty == 2))
                tx.Campaign.Stages[stage.Id] = new() { Wins = 1, Challenges = Enumerable.Range(0, stage.Challenges.Length).Select(i => (uint)i).ToHashSet() };
            return 0;
        });
        Assert.Equal(TakeStageActChallengeRewardRsp.Types.Retcode.HasTake, service.ClaimAct(Uid, new() { ActId = 101, Difficulty = 1, ChallengeNumIndex = 1 }).Retcode);
        var request = new TakeStageActChallengeRewardReq { ActId = 101, Difficulty = 2, ChallengeNumIndex = 1 };
        Assert.Equal(TakeStageActChallengeRewardRsp.Types.Retcode.Succ, service.ClaimAct(Uid, request).Retcode);
        uint wallet = store.Read(Uid).Hcoin; Assert.Equal(TakeStageActChallengeRewardRsp.Types.Retcode.HasTake, service.ClaimAct(Uid, request).Retcode); Assert.Equal(wallet, store.Read(Uid).Hcoin);
        Assert.Empty(service.Acts(Uid).ActDifficultyList.Single(a => a.ActId == 101 && a.Difficulty == 3).HasTakeChallengeNumIndex);
    }
    [Fact]
    public void StageTrialsDoNotBecomeOwnedAvatarsOrOverwriteTheSavedTeam()
    {
        var stage = CampaignCatalog.Default.Stages.First(s => s.Trials is { Length: > 0 } && s.Challenges.All(id => id == 0 || CampaignCatalog.Default.Reward(id).ItemList.Count == 0));
        uint trial = stage.Trials![0];
        store.Campaign(Uid, tx => { foreach (var id in stage.Previous) tx.Campaign.Stages[id] = new() { Wins = 1 }; tx.Campaign.Team = [101]; return 0; });
        Assert.Empty(service.TrialAvatars().AvatarList); // No legacy global sample injection; the stage still accepts its own trial.
        Assert.Equal(StageBeginRsp.Types.Retcode.AvatarError, service.Begin(Uid, new() { StageId = 10101, AvatarTrialIdList = { trial } }).Retcode);
        var begin = service.Begin(Uid, new() { StageId = stage.Id, AvatarTrialIdList = { trial } }); Assert.Equal(StageBeginRsp.Types.Retcode.Succ, begin.Retcode);
        Assert.Equal(StageEndRsp.Types.Retcode.Succ, End(begin, stage).Retcode);
        Assert.Equal(new uint[] { 101 }, service.Team(Uid)); Assert.Equal(new uint[] { 101 }, service.OwnedAvatars(Uid));
        Assert.Equal(0u, store.Read(Uid).AvatarExp);
    }
    [Fact]
    public void RewardEquipmentAndCharacterCardsUseTheirActualInventoryKinds()
    {
        store.Campaign(Uid, tx => { service.Apply(tx, CampaignCatalog.Default.Reward(10001)); service.Apply(tx, CampaignCatalog.Default.Reward(10005)); return 0; });
        var inventory = store.Inventory(Uid)!;
        Assert.Contains(GetEquipmentDataRsp.Parser.ParseFrom(inventory.Equipment).StigmataList, s => s.Id == 30004);
        Assert.Contains(GetAvatarDataRsp.Parser.ParseFrom(inventory.Avatars).AvatarList, a => a.AvatarId == 201);
        Assert.DoesNotContain(service.Materials(Uid), m => m.Id is 30004 or 60201);
    }
    [Fact]
    public void AllImportedRewardItemsResolveBeforeAnyPlayerCanClaimThem()
    {
        var items = CampaignCatalog.Default.Rewards.SelectMany(r => r.Items).Concat(ChallengeCatalog.Default.Rewards.SelectMany(r => r.Items));
        foreach (var item in items)
            Assert.Contains(GrantService.Catalog.Items, x => (x.Id == item.Id && x.Kind is "material" or "weapon" or "stigmata") || (x.Kind == "avatar" && x.Card == item.Id));
    }
    [Fact]
    public void PlotBookmarkPersistsWithoutGrantingUnplayedStageProgress()
    {
        var request = new FinishPlotReq { PlotId = 20001, DialogId = 100 };
        Assert.Equal(FinishPlotRsp.Types.Retcode.PlotError, service.FinishPlot(Uid, request).Retcode);
        End(Begin(10101), CampaignCatalog.Default.Stages.Single(s => s.Id == 10101));
        var wallet = JsonSerializer.Serialize(store.Read(Uid));
        Assert.Equal(FinishPlotRsp.Types.Retcode.Succ, service.FinishPlot(Uid, request).Retcode);
        Assert.Equal(FinishPlotRsp.Types.Retcode.Succ, service.FinishPlot(Uid, request).Retcode);
        Assert.Contains(20001u, new CampaignService(new LobbyStore(db), TimeProvider.System).Plots(Uid).PlotList);
        Assert.False(Assert.Single(service.Stages(Uid, new() { StageIdList = { 10102 } }).StageList).IsDone);
        Assert.Equal(wallet, JsonSerializer.Serialize(store.Read(Uid)));
        request.PlotId = uint.MaxValue; Assert.Equal(FinishPlotRsp.Types.Retcode.PlotError, service.FinishPlot(Uid, request).Retcode);
    }
    public void Dispose() => temp.Dispose();
}
