using System.Security.Cryptography;
using BH3.Game.Campaign;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed record BossDefinition(uint Id, uint Family, uint Level, uint MaxScore, uint Rank);
public sealed record ArenaTier(uint Id, uint Min, uint Max, uint DailyEntries);
public sealed record ScoreReward(uint Id, uint Score, uint Reward, uint Rank = 0);
public sealed record FloorDefinition(uint Id, uint Need, uint MaxScore);
public sealed record SiteDefinition(uint Id, uint Stage, uint[] Previous, FloorDefinition[] Floors);
public sealed record ChallengeCatalog(uint BossSchedule, uint Rank, BossDefinition[] Bosses, ScoreReward[] BossRewards,
    uint AbyssSchedule, SiteDefinition[] Sites, ScoreReward[] AbyssRewards, RewardDefinition[] Rewards, ArenaTier[] Tiers)
{
    public static ChallengeCatalog Default { get; } = GrantService.Resource<ChallengeCatalog>("gameplay.json");
}

// Local solo rules. The client reports combat outcomes; scores are bounded by catalog maxima.
// Wallet, thresholds, entries and progress commit together in the existing account transaction.
public sealed class ChallengeService(LobbyStore store, TimeProvider clock)
{
    private static ChallengeCatalog Data => ChallengeCatalog.Default;
    private readonly CampaignService campaign = new(store, clock);
    private uint Now => checked((uint)clock.GetUtcNow().ToUnixTimeSeconds());
    internal static uint CycleAt(uint now) => now >= 1791230400 ? 1791230400 + (now - 1791230400) / 604800 * 604800 : now / 604800 * 604800;
    private uint Cycle => CycleAt(Now);
    private ChallengeState State(CampaignTransaction tx) => State(tx, Now);
    internal static ChallengeState State(CampaignTransaction tx, uint now)
    {
        uint cycle = CycleAt(now);
        var state = tx.Campaign.Challenges;
        if (state.Cycle != cycle) tx.Campaign.Challenges = state = new() { Cycle = cycle, ExpiresAt = cycle + 604800 };
        if (state.ArenaRank == 0 && tx.Lobby.Level >= 38) state.ArenaRank = Data.Tiers.Last(t => tx.Lobby.Level >= t.Min).Id;
        return state;
    }
    private bool Open(CampaignTransaction tx) => tx.Lobby.Level >= 38 && Now - Cycle < 518400;
    private uint Entries(ChallengeState state) => Math.Min(6, (Now - Cycle) / 86400 + 1) * Data.Tiers.Single(t => t.Id == state.ArenaRank).DailyEntries;
    private static bool AbyssOpen(CampaignTransaction tx) => tx.Lobby.Level >= 81;
    private static uint Total(ChallengeState s) => checked((uint)s.Bosses.Values.Sum(x => (long)x.BestScore));
    private static uint AbyssScore(ChallengeState s) => checked((uint)s.Floors.Values.Sum(f => f.Values.Sum(x => (long)x)));
    private static uint[] Owned(CampaignTransaction tx) => GrantService.Inventory(tx).Avatars.AvatarList.Select(a => a.AvatarId).ToArray();
    private static BossProgress Progress(ChallengeState s, uint family) => s.Bosses.GetValueOrDefault(family) ?? new();
    private static bool Finished(ChallengeState s, SiteDefinition site) => site.Floors.All(f => s.Floors.GetValueOrDefault(site.Id)?.GetValueOrDefault(f.Id) >= f.Need);
    internal static uint MissionProgress(ChallengeState state, uint id)
    {
        var reward = Data.AbyssRewards.Single(r => r.Id == id);
        return reward.Score == 0 ? (Data.Sites.Any(site => Finished(state, site)) ? 1u : 0u) : Math.Min(reward.Score, AbyssScore(state));
    }
    private RewardData Grant(CampaignTransaction tx, ScoreReward definition)
    {
        var reward = Data.Rewards.Single(r => r.Id == definition.Reward).Message();
        campaign.Apply(tx, reward); return reward;
    }
    public GetExBossScheduleRsp Schedule(uint uid) => store.Campaign(uid, tx => new GetExBossScheduleRsp {
        Retcode = Open(tx) ? GetExBossScheduleRsp.Types.Retcode.Succ : GetExBossScheduleRsp.Types.Retcode.FeatureClosed,
        BeginTime = Cycle, EndTime = Cycle + 518399, MinLevel = 38, ScheduleId = Data.BossSchedule, RankId = State(tx).ArenaRank });
    public GetExBossInfoRsp BossInfo(uint uid) => store.Campaign(uid, tx =>
    {
        if (!Open(tx)) return new GetExBossInfoRsp { Retcode = GetExBossInfoRsp.Types.Retcode.NotOpen, BossInfo = new ExBossInfo { CurMaxEnterTimes = 0 } };
        var s = State(tx); var info = new ExBossInfo { EnterTimes = s.BossEntries, ScheduleId = Data.BossSchedule,
            NowScheduleId = Data.BossSchedule, RankId = s.ArenaRank, CurMaxEnterTimes = Entries(s), HasTakenScoreRewardIdList = { s.BossRewards } };
        foreach (var family in Data.Bosses.Where(b => b.Rank == s.ArenaRank).GroupBy(b => b.Family))
        {
            var p = Progress(s, family.Key); var next = family.OrderBy(b => b.Level).FirstOrDefault(b => b.Level > p.HighestLevel) ?? family.MaxBy(b => b.Level)!;
            info.BossIdList.Add(new ExBossIdInfo { BossId = next.Id, Score = p.BestScore, AvatarIdList = { p.LockedAvatars }, LastAvatarIdList = { p.LockedAvatars } });
        }
        return new GetExBossInfoRsp { Retcode = Open(tx) ? GetExBossInfoRsp.Types.Retcode.Succ : GetExBossInfoRsp.Types.Retcode.NotOpen, BossInfo = info };
    });
    public ExBossStageBeginRsp BossBegin(uint uid, ExBossStageBeginReq r) => store.Campaign(uid, tx =>
    {
        ExBossStageBeginRsp Error(ExBossStageBeginRsp.Types.Retcode code) => new() { Retcode = code };
        if (!Open(tx)) return Error(ExBossStageBeginRsp.Types.Retcode.NotOpen);
        var s = State(tx);
        var boss = Data.Bosses.SingleOrDefault(b => b.Id == r.BossId && b.Rank == s.ArenaRank);
        if (boss is null) return Error(ExBossStageBeginRsp.Types.Retcode.NoAvailableBoss);
        uint[] avatars = r.AvatarIdList.Where(a => a != 0).ToArray();
        if (avatars.Length is < 1 or > 3) return Error(ExBossStageBeginRsp.Types.Retcode.AvatarNumError);
        if (avatars.Distinct().Count() != avatars.Length) return Error(ExBossStageBeginRsp.Types.Retcode.DupAvatar);
        if (avatars.Any(a => !Owned(tx).Contains(a))) return Error(ExBossStageBeginRsp.Types.Retcode.AvatarError);
        if (!CompanionService.ValidTeam(tx,r.ElfIdList)) return Error(ExBossStageBeginRsp.Types.Retcode.ElfError);
        var elfs=r.ElfIdList.Where(x=>x!=0).ToArray();
        if (r.IsTurbo) return Error(ExBossStageBeginRsp.Types.Retcode.NotAllowTurbo);
        if (!r.IsTraining && boss.Level > Progress(s, boss.Family).HighestLevel + 1) return Error(ExBossStageBeginRsp.Types.Retcode.PreBossNotFinish);
        if (!r.IsTraining && s.Bosses.Any(b => b.Key != boss.Family && b.Value.LockedAvatars.Intersect(avatars).Any())) return Error(ExBossStageBeginRsp.Types.Retcode.AvatarError);
        if (s.BossRun is { } active && active.Boss == boss.Id && active.Training == r.IsTraining && active.Avatars.SequenceEqual(avatars) && (active.Elfs??[]).SequenceEqual(elfs) && Now - active.StartedAt < 86400)
            return ExBossStageBeginRsp.Parser.ParseFrom(active.Response);
        if (!r.IsTraining && s.BossEntries >= Entries(s)) return Error(ExBossStageBeginRsp.Types.Retcode.EnterTimesLack);
        var response = new ExBossStageBeginRsp { Retcode = ExBossStageBeginRsp.Types.Retcode.Succ, StageTransactionStr = Guid.NewGuid().ToString("N") };
        if (!r.IsTraining) s.BossEntries++;
        s.BossRun = new(boss.Id, Now, avatars, r.IsTraining, response.ToByteArray(),elfs);
        return response;
    });
    public (ExBossStageEndRsp Response, RewardData[] Rewards) BossEnd(uint uid, ExBossStageEndReq r) => store.Campaign(uid, tx =>
    {
        var s = State(tx); string fingerprint = Convert.ToHexString(SHA256.HashData(r.ToByteArray()));
        if (s.BossRun is null && s.LastBossRequest == fingerprint && s.LastBossResponse is { } cached)
            return (ExBossStageEndRsp.Parser.ParseFrom(cached), Array.Empty<RewardData>());
        var status = r.HasEndStatus ? r.EndStatus : StageEndStatus.StageWin;
        var result = new ExBossStageEndRsp { Retcode = ExBossStageEndRsp.Types.Retcode.NotBegin, BossId = r.BossId };
        if ((int)status is < 1 or > 4) return (result, Array.Empty<RewardData>());
        result.EndStatus = status;
        if (!Open(tx) || s.BossRun is not { } run || run.Boss != r.BossId || Now - run.StartedAt >= 86400) return (result, Array.Empty<RewardData>());
        var rewards = new List<RewardData>(); var boss = Data.Bosses.Single(b => b.Id == run.Boss);
        if (status == StageEndStatus.StageWin && !run.Training)
        {
            var p = Progress(s, boss.Family); p.HighestLevel = Math.Max(p.HighestLevel, boss.Level);
            p.BestScore = Math.Max(p.BestScore, Math.Min(r.Score, boss.MaxScore)); p.LockedAvatars = p.LockedAvatars.Union(run.Avatars).ToArray(); s.Bosses[boss.Family] = p;
            foreach (var reward in Data.BossRewards.Where(x => x.Rank == s.ArenaRank && x.Score <= Total(s) && !s.BossRewards.Contains(x.Id)))
            { rewards.Add(Grant(tx, reward)); s.BossRewards.Add(reward.Id); }
        }
        s.BossRun = null; result.Retcode = ExBossStageEndRsp.Types.Retcode.Succ;
        s.LastBossRequest = fingerprint; s.LastBossResponse = result.ToByteArray();
        return (result, rewards.ToArray());
    });
    public GetExBossRankRsp BossRank(uint uid, GetExBossRankReq r) => store.Campaign(uid, tx =>
    {
        var s = State(tx); uint score = r.BossId == 0 ? Total(s) : Data.Bosses.FirstOrDefault(b => b.Id == r.BossId) is { } boss ? Progress(s, boss.Family).BestScore : 0;
        return new GetExBossRankRsp { Retcode = Open(tx) ? GetExBossRankRsp.Types.Retcode.Succ : GetExBossRankRsp.Types.Retcode.NotOpen,
            RankId = s.ArenaRank, BossId = r.BossId, RankData = new RankShowData { MyRankType = 1, MyRank = score > 0 ? 1u : 0u, MyScore = score } };
    });
    public GetEndlessStatusRsp EndlessStatus(uint uid) => store.Campaign(uid, tx => new GetEndlessStatusRsp {
        Retcode = GetEndlessStatusRsp.Types.Retcode.Succ, CurStatus = new EndlessStatus { EndlessType = (EndlessType)4,
            BeginTime = Cycle, EndTime = Cycle + 604799, CloseTime = Cycle + 604799, CanJoinIn = AbyssOpen(tx) } });
    public UltraEndlessGetMainDataRsp AbyssInfo(uint uid, string nickname = "本地舰长") => store.Campaign(uid, tx =>
    {
        var s = State(tx); var data = new UltraEndlessMainData { ScheduleId = Data.AbyssSchedule, EffectTime = Cycle, BeginTime = Cycle,
            EndTime = Cycle + 604799, CloseTime = Cycle + 604799, CurSeasonId = 1 };
        foreach (var site in Data.Sites) data.SiteList.Add(new UltraEndlessSite { SiteId = site.Id,
            FloorList = { site.Floors.Select(f => new UltraEndlessFloor { Floor = f.Id, MaxScore = s.Floors.GetValueOrDefault(site.Id)?.GetValueOrDefault(f.Id) ?? 0 }) },
            MaxScoreCostTime = s.SiteTimes.GetValueOrDefault(site.Id) });
        return new UltraEndlessGetMainDataRsp { Retcode = AbyssOpen(tx) ? UltraEndlessGetMainDataRsp.Types.Retcode.Succ : UltraEndlessGetMainDataRsp.Types.Retcode.PlayerLevelLack,
            ScheduleId = Data.AbyssSchedule, GroupLevel = 3, TopGroupLevel = 9, CupNum = 355, MainData = data, DynamicHardLevel = 409,
            // 9.1 RefreshCupLevel reads LastSettleInfo.BufferCup even before any settlement.
            // A neutral, non-null snapshot keeps the panel readable without inventing a past season.
            LastSettleInfo = new UltraEndlessSettleInfo { ScheduleId = 0, GroupLevel = 3, BufferCup = 0,
                CupNum = 355, CupNumBefore = 355, CupNumAfterScheduleSettle = 355,
                CupNumBeforeSeasonSettle = 355, CupNumAfterSeasonSettle = 355 },
            EndlessPlayerList = { new UltraEndlessPlayer { Uid = uid, GroupLevel = 3, CupNum = 355, MaxStageScore = AbyssScore(s) } },
            BriefDataList = { new PlayerFriendBriefData { Uid = uid, Nickname = nickname, Level = tx.Lobby.Level } } };
    });
    public UltraEndlessEnterSiteRsp EnterSite(uint uid, UltraEndlessEnterSiteReq r) => store.Campaign(uid, tx =>
    {
        var result = new UltraEndlessEnterSiteRsp { Retcode = UltraEndlessEnterSiteRsp.Types.Retcode.NotInSchedule, SiteId = r.SiteId };
        var s = State(tx); var site = Data.Sites.SingleOrDefault(x => x.Id == r.SiteId);
        if (!AbyssOpen(tx) || site is null) return result;
        if (site.Previous.Any(p => !Finished(s, Data.Sites.Single(x => x.Id == p)))) { result.Retcode = UltraEndlessEnterSiteRsp.Types.Retcode.PreNotFinish; return result; }
        s.ActiveSite = site.Id; result.Retcode = UltraEndlessEnterSiteRsp.Types.Retcode.Succ; return result;
    });
    public (UltraEndlessReportSiteFloorRsp Response, RewardData[] Rewards) ReportFloor(uint uid, UltraEndlessReportSiteFloorReq r) => store.Campaign(uid, tx =>
    {
        var result = new UltraEndlessReportSiteFloorRsp { Retcode = UltraEndlessReportSiteFloorRsp.Types.Retcode.NotInSchedule, SiteId = r.SiteId, Floor = r.Floor, IsUpFloor = r.IsUpFloor };
        var s = State(tx); var site = Data.Sites.SingleOrDefault(x => x.Id == r.SiteId); var floor = site?.Floors.SingleOrDefault(f => f.Id == r.Floor);
        if (!AbyssOpen(tx) || site is null || floor is null || s.ActiveSite != site.Id || r.AvatarIdList.Where(a => a != 0).Any(a => !Owned(tx).Contains(a))) return (result, Array.Empty<RewardData>());
        var avatars = r.AvatarIdList.Where(a => a != 0).ToArray();
        if (avatars.Length > 3 || avatars.Distinct().Count() != avatars.Length || !CompanionService.ValidTeam(tx,r.ElfIdList)) return (result, Array.Empty<RewardData>());
        var progress = s.Floors.GetValueOrDefault(site.Id) ?? new();
        if (site.Floors.Any(f => f.Id < floor.Id && progress.GetValueOrDefault(f.Id) < f.Need)) { result.Retcode = UltraEndlessReportSiteFloorRsp.Types.Retcode.PreNotFinish; return (result, Array.Empty<RewardData>()); }
        if(r.Score>=floor.Need&&progress.GetValueOrDefault(floor.Id)<floor.Need)SystemsService.Track(tx,Now,"abyss");
        uint score = Math.Min(r.Score, floor.MaxScore); bool improved = score > progress.GetValueOrDefault(floor.Id);
        progress[floor.Id] = Math.Max(progress.GetValueOrDefault(floor.Id), score); s.Floors[site.Id] = progress;
        if (improved) s.SiteTimes[site.Id] = r.TotalCostTime;
        result.Retcode = UltraEndlessReportSiteFloorRsp.Types.Retcode.Succ; return (result, Array.Empty<RewardData>());
    });
    public UltraEndlessGetTopRankRsp AbyssRank(uint uid, UltraEndlessGetTopRankReq request) => store.Campaign(uid, tx => {
        // The client caches by the response schedule ID, including zero (no previous period).
        uint score = request.ScheduleId == Data.AbyssSchedule ? AbyssScore(State(tx)) : 0;
        return new UltraEndlessGetTopRankRsp { Retcode = UltraEndlessGetTopRankRsp.Types.Retcode.Succ, ScheduleId = request.ScheduleId,
            RankData = new RankShowData { MyRankType = 1, MyRank = score > 0 ? 1u : 0u, MyScore = score } }; });
}
