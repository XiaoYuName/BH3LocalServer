using System.Text.Json;
using BH3.Game.Campaign;
using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class ChallengeTests : IDisposable
{
    private const uint Uid = 10001;
    private readonly TestDirectory temp = new();
    private readonly SqliteConnectionFactory db;
    private readonly LobbyStore store;
    private readonly Clock clock = new();
    private readonly ChallengeService service;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Value = DateTimeOffset.FromUnixTimeSeconds(1791230400 + 3 * 86400 + 3600);
        public override DateTimeOffset GetUtcNow() => Value;
    }
    public ChallengeTests()
    {
        db = new(Path.Combine(temp.Path, "challenges.db")); SchemaMigrator.Initialize(db);
        store = new(db); store.EnsurePlayer(Uid, "captain", new(88, 80, 101, 20001, 59101, 1, []));
        service = new(store, clock);
    }
    private ExBossStageBeginRsp Begin(uint id = 49016, bool training = false) => service.BossBegin(Uid, new() { BossId = id, AvatarIdList = { 101 }, IsTraining = training });
    private static ExBossStageEndReq Win(uint id = 49016) => new() { BossId = id, Score = uint.MaxValue }; // 9.1 omits WIN=1.
    private string Saved() => store.Campaign(Uid, tx => JsonSerializer.Serialize(new { tx.Lobby, tx.Campaign }));

    [Fact]
    public void AbyssEntryHasSerializedBufferCupSnapshotWithoutGrantingRewards()
    {
        service.AbyssInfo(Uid); // Initialize the current local cycle before the read-only comparison.
        string before = Saved();
        var rsp = UltraEndlessGetMainDataRsp.Parser.ParseFrom(service.AbyssInfo(Uid).ToByteArray());
        Assert.NotNull(rsp.LastSettleInfo);
        Assert.Equal(0u, rsp.LastSettleInfo.BufferCup);
        Assert.Equal(0u, rsp.LastSettleInfo.ScheduleId);
        Assert.Equal(rsp.CupNum, rsp.LastSettleInfo.CupNumBefore);
        Assert.Equal(rsp.CupNum, rsp.LastSettleInfo.CupNumAfterSeasonSettle);
        Assert.Equal(before, Saved());
    }
    [Theory]
    [InlineData(0u)]
    [InlineData(1028u)]
    [InlineData(999999u)]
    public void AbyssRankCacheUsesRequestedScheduleAndDoesNotLeakCurrentScore(uint schedule)
    {
        service.EnterSite(Uid, new() { SiteId = 1011 });
        service.ReportFloor(Uid, new() { SiteId = 1011, Floor = 1, Score = 6000, AvatarIdList = { 101 } });
        string before = Saved();
        var rsp = UltraEndlessGetTopRankRsp.Parser.ParseFrom(service.AbyssRank(Uid, new() { ScheduleId = schedule }).ToByteArray());
        Assert.Equal(schedule, rsp.ScheduleId);
        Assert.NotNull(rsp.RankData);
        Assert.Equal(schedule == 1028 ? 1000u : 0u, rsp.RankData.MyScore);
        Assert.Equal(before, Saved());
    }
    [Theory]
    [InlineData(38u, 101u, 49001u, 16u)]
    [InlineData(56u, 102u, 49006u, 16u)]
    [InlineData(70u, 103u, 49011u, 20u)]
    [InlineData(81u, 104u, 49016u, 24u)]
    public void ArenaUsesLevelTierAndAccumulatedDailyEntries(uint level, uint rank, uint boss, uint entries)
    {
        store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Level = level }; return 0; });
        var info = service.BossInfo(Uid).BossInfo;
        Assert.Equal(rank, info.RankId); Assert.Equal(entries, info.CurMaxEnterTimes);
        Assert.Equal(3, info.BossIdList.Count); Assert.Contains(info.BossIdList, b => b.BossId == boss);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.Succ, Begin(boss).Retcode);
        // A level change inside a period does not reset the rank or unlock a second reward track.
        store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Level = 88 }; return 0; });
        Assert.Equal(rank, service.BossInfo(Uid).BossInfo.RankId);
    }
    [Fact]
    public void BeginRetryEndRetryAndRestartNeverSpendOrRewardTwice()
    {
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.PreBossNotFinish, Begin(49017).Retcode);
        var begin = Begin(); Assert.Equal(ExBossStageBeginRsp.Types.Retcode.Succ, begin.Retcode);
        Assert.Equal(begin.ToByteArray(), Begin().ToByteArray()); Assert.Equal(1u, service.BossInfo(Uid).BossInfo.EnterTimes);
        var end = service.BossEnd(Uid, Win()); Assert.Equal(ExBossStageEndRsp.Types.Retcode.Succ, end.Response.Retcode); Assert.Single(end.Rewards);
        Assert.Equal(2000u, service.BossInfo(Uid).BossInfo.BossIdList[0].Score);
        Assert.Equal(49017u, service.BossInfo(Uid).BossInfo.BossIdList[0].BossId);
        string saved = Saved(); var reopened = new ChallengeService(new LobbyStore(db), clock);
        var retry = reopened.BossEnd(Uid, Win()); Assert.Equal(end.Response, retry.Response); Assert.Empty(retry.Rewards); Assert.Equal(saved, Saved());
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.AvatarError, Begin(36021).Retcode);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.Succ, Begin(49017).Retcode);
    }
    [Fact]
    public void LossTrainingAndWrongBossCannotAwardOrLockAvatars()
    {
        Begin(); Assert.Equal(ExBossStageEndRsp.Types.Retcode.NotBegin, service.BossEnd(Uid, Win(9021)).Response.Retcode);
        var loss = Win(); loss.EndStatus = StageEndStatus.StageAllDead;
        Assert.Empty(service.BossEnd(Uid, loss).Rewards); Assert.Equal(0u, service.BossRank(Uid, new()).RankData.MyScore);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.Succ, Begin(49021, true).Retcode);
        Assert.Empty(service.BossEnd(Uid, Win(49021)).Rewards);
        Assert.Equal(1u, service.BossInfo(Uid).BossInfo.EnterTimes);
        Assert.Empty(service.BossInfo(Uid).BossInfo.BossIdList[0].AvatarIdList);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.Succ, Begin(36021).Retcode);
    }
    [Fact]
    public void ArenaLimitsUnknownActorsAndInvalidStatusWithoutConsumingRun()
    {
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.NoAvailableBoss, Begin(999999).Retcode);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.AvatarError, service.BossBegin(Uid, new() { BossId = 49016, AvatarIdList = { 99999 } }).Retcode);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.DupAvatar, service.BossBegin(Uid, new() { BossId = 49016, AvatarIdList = { 101, 101 } }).Retcode);
        Begin(); var invalid = Win(); invalid.EndStatus = (StageEndStatus)0;
        Assert.Equal(ExBossStageEndRsp.Types.Retcode.NotBegin, service.BossEnd(Uid, invalid).Response.Retcode);
        Assert.Equal(ExBossStageEndRsp.Types.Retcode.Succ, service.BossEnd(Uid, Win()).Response.Retcode);
    }
    [Fact]
    public void EntryLimitReplenishesDailyAndNewWeekPreservesWallet()
    {
        for (int i = 0; i < 24; i++)
        { Assert.Equal(ExBossStageBeginRsp.Types.Retcode.Succ, Begin().Retcode); var loss = Win(); loss.EndStatus = StageEndStatus.StageAllDead; service.BossEnd(Uid, loss); }
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.EnterTimesLack, Begin().Retcode);
        clock.Value = clock.Value.AddDays(1); Assert.Equal(30u, service.BossInfo(Uid).BossInfo.CurMaxEnterTimes);
        Begin(); service.BossEnd(Uid, Win()); uint coin = store.Read(Uid).Scoin; Assert.True(coin > 0);
        clock.Value = clock.Value.AddDays(3);
        var reset = service.BossInfo(Uid).BossInfo; Assert.Equal(0u, reset.EnterTimes); Assert.Empty(reset.HasTakenScoreRewardIdList);
        Assert.All(reset.BossIdList, b => { Assert.Equal(0u, b.Score); Assert.Empty(b.AvatarIdList); }); Assert.Equal(coin, store.Read(Uid).Scoin);
    }
    [Fact]
    public void FailedRewardTransactionRetainsRunAndDoesNotCommitScore()
    {
        Begin(); store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Scoin = uint.MaxValue }; return 0; }); string before = Saved();
        Assert.Throws<OverflowException>(() => service.BossEnd(Uid, Win())); Assert.Equal(before, Saved());
        store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Scoin = 0 }; return 0; }); Assert.Single(service.BossEnd(Uid, Win()).Rewards);
    }
    [Fact]
    public async Task ConcurrentArenaSettlementAwardsOneThresholdOnce()
    {
        Begin(); var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => service.BossEnd(Uid, Win()), TestContext.Current.CancellationToken)));
        Assert.Equal(1, results.Sum(r => r.Rewards.Length)); Assert.All(results, r => Assert.Equal(ExBossStageEndRsp.Types.Retcode.Succ, r.Response.Retcode));
        Assert.Equal(60000u, store.Read(Uid).Scoin);
    }
    private UltraEndlessReportSiteFloorReq Report(uint site, FloorDefinition floor) => new() { SiteId = site, Floor = floor.Id, Score = uint.MaxValue, CostTime = 60, TotalCostTime = 180, AvatarIdList = { 101 } };
    [Fact]
    public void AbyssEntryAndFloorOrderAreEnforcedBeforeRewards()
    {
        var site = ChallengeCatalog.Default.Sites[0]; var floor = site.Floors[0];
        Assert.Equal(UltraEndlessReportSiteFloorRsp.Types.Retcode.NotInSchedule, service.ReportFloor(Uid, Report(site.Id, floor)).Response.Retcode);
        Assert.Equal(UltraEndlessEnterSiteRsp.Types.Retcode.PreNotFinish, service.EnterSite(Uid, new() { SiteId = 1012 }).Retcode);
        Assert.Equal(UltraEndlessEnterSiteRsp.Types.Retcode.Succ, service.EnterSite(Uid, new() { SiteId = site.Id }).Retcode);
        var unknown = Report(site.Id, floor); unknown.Floor = uint.MaxValue;
        Assert.Equal(UltraEndlessReportSiteFloorRsp.Types.Retcode.NotInSchedule, service.ReportFloor(Uid, unknown).Response.Retcode);
        if (site.Floors.Length > 1) Assert.Equal(UltraEndlessReportSiteFloorRsp.Types.Retcode.PreNotFinish, service.ReportFloor(Uid, Report(site.Id, site.Floors[1])).Response.Retcode);
        var actor = Report(site.Id, floor); actor.AvatarIdList.Clear(); actor.AvatarIdList.Add(999999);
        Assert.Equal(UltraEndlessReportSiteFloorRsp.Types.Retcode.NotInSchedule, service.ReportFloor(Uid, actor).Response.Retcode);
        Assert.Equal(0u, service.AbyssRank(Uid, new() { ScheduleId = 1028 }).RankData.MyScore);
    }
    [Fact]
    public void AllFourAbyssSitesSettlePersistAndGrantThresholdsOnlyOnce()
    {
        var campaign = new CampaignService(store, clock); int rewards = 0;
        Assert.Equal(StageBeginRsp.Types.Retcode.StageMismatch, campaign.Begin(Uid, new() { StageId = 146005, AvatarIdList = { 101 } }).Retcode);
        foreach (var site in ChallengeCatalog.Default.Sites)
        {
            Assert.Equal(UltraEndlessEnterSiteRsp.Types.Retcode.Succ, service.EnterSite(Uid, new() { SiteId = site.Id }).Retcode);
            var begin = campaign.Begin(Uid, new() { StageId = site.Stage, AvatarIdList = { 101 } }); Assert.Equal(StageBeginRsp.Types.Retcode.Succ, begin.Retcode);
            foreach (var floor in site.Floors)
            {
                var report = Report(site.Id, floor); var result = service.ReportFloor(Uid, report);
                Assert.Equal(UltraEndlessReportSiteFloorRsp.Types.Retcode.Succ, result.Response.Retcode); rewards += result.Rewards.Length;
                string after = Saved(); Assert.Empty(service.ReportFloor(Uid, report).Rewards); Assert.Equal(after, Saved());
            }
            Assert.Equal(StageEndRsp.Types.Retcode.Succ, campaign.End(Uid, new() { Sign = begin.SignKey, Body = new StageEndReqBody { StageId = site.Stage }.ToByteString() }).Retcode);
        }
        Assert.Equal(0, rewards); // Floor reports update mission progress; claims use the normal mission API.
        var claim = new GetMissionRewardReq { MissionIdList = { 86002, 86003, 86004 } };
        Assert.All(campaign.Missions(Uid).MissionList.Where(m => claim.MissionIdList.Contains(m.MissionId)), m => Assert.Equal(MissionStatus.Finish, m.Status));
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.Succ, campaign.Claim(Uid, claim).Retcode);
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.MissionStatusError, campaign.Claim(Uid, claim).Retcode);
        uint score = (uint)ChallengeCatalog.Default.Sites.SelectMany(s => s.Floors).Sum(f => (long)f.MaxScore);
        Assert.Equal(score, service.AbyssRank(Uid, new() { ScheduleId = 1028 }).RankData.MyScore);
        Assert.Equal(service.AbyssInfo(Uid), new ChallengeService(new LobbyStore(db), clock).AbyssInfo(Uid));
        uint wallet = store.Read(Uid).Hcoin; Assert.True(wallet > 0); clock.Value = clock.Value.AddDays(7);
        Assert.Equal(StageBeginRsp.Types.Retcode.StageMismatch, campaign.Begin(Uid, new() { StageId = 146004, AvatarIdList = { 101 } }).Retcode);
        Assert.Equal(0u, service.AbyssRank(Uid, new() { ScheduleId = 1028 }).RankData.MyScore); Assert.Equal(wallet, store.Read(Uid).Hcoin);
        Assert.Equal(3, campaign.Missions(Uid).MissionList.Count(m => claim.MissionIdList.Contains(m.MissionId)));
    }
    [Fact]
    public void LowLevelAndClosedArenaCannotEnter()
    {
        store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Level = 37 }; return 0; });
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.NotOpen, Begin().Retcode); Assert.Empty(service.BossInfo(Uid).BossInfo.BossIdList);
        Assert.False(service.EndlessStatus(Uid).CurStatus.CanJoinIn);
        Assert.Equal(UltraEndlessEnterSiteRsp.Types.Retcode.NotInSchedule, service.EnterSite(Uid, new() { SiteId = 1011 }).Retcode);
        store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Level = 88 }; return 0; }); clock.Value = clock.Value.AddDays(3);
        Assert.Equal(ExBossStageBeginRsp.Types.Retcode.NotOpen, Begin().Retcode);
    }
    [Fact]
    public void AbyssTeamIsIndependentFromStoryTeamAndPersists()
    {
        store.Campaign(Uid, tx => { GrantService.Apply(tx, Uid, new GrantItem("avatar", 102)); return 0; });
        var campaign = new CampaignService(store, clock);
        campaign.SetTeam(Uid, new() { Team = new AvatarTeam { StageType = 60, AvatarIdList = { 102 } } });
        service.EnterSite(Uid, new() { SiteId = 1011 });
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, campaign.Begin(Uid, new() { StageId = 146005, AvatarIdList = { 102 } }).Retcode);
        var reopened = new CampaignService(new LobbyStore(db), clock);
        Assert.Equal(new uint[] { 101 }, reopened.Team(Uid)); Assert.Equal(new uint[] { 102 }, reopened.Team(Uid, 60));
    }
    public void Dispose() => temp.Dispose();
}
