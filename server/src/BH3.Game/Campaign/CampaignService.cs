using System.Security.Cryptography;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Campaign;

public sealed class CampaignService(LobbyStore store, TimeProvider clock)
{
    private static CampaignCatalog Data => CampaignCatalog.Default;
    private uint Now => checked((uint)clock.GetUtcNow().ToUnixTimeSeconds());
    private static StageProgress Progress(CampaignState state, uint id) => state.Stages.GetValueOrDefault(id) ?? new();
    private static bool All(ICollection<uint> ids) => ids.Count == 0 || ids.Contains(0);
    private static bool Won(CampaignState state, uint id) => Progress(state, id).Wins > 0;

    public LobbyState Refresh(uint uid) => store.Campaign(uid, tx => { Recover(tx); return tx.Lobby; });
    private void Recover(CampaignTransaction tx)
    {
        uint now = Now, last = tx.Lobby.StaminaUpdatedAt, cap = Data.Level(tx.Lobby.Level).Stamina;
        if (last == 0 || last > now || tx.Lobby.Stamina >= cap) tx.Lobby = tx.Lobby with { StaminaUpdatedAt = now };
        else
        {
            uint ticks = (now - last) / 360, stamina = Math.Min(cap, tx.Lobby.Stamina + ticks);
            tx.Lobby = tx.Lobby with { Stamina = stamina, StaminaUpdatedAt = stamina == cap ? now : last + ticks * 360 };
        }
    }
    public GetStageDataRsp Stages(uint uid, GetStageDataReq request) => store.Campaign(uid, tx =>
    {
        bool all = All(request.StageIdList);
        if (!all && request.StageIdList.Any(id => Data.Stages.All(s => s.Id != id)))
            return new GetStageDataRsp { Retcode = GetStageDataRsp.Types.Retcode.StageNotExist, IsAll = false };
        var result = new GetStageDataRsp { Retcode = GetStageDataRsp.Types.Retcode.Succ, IsAll = all };
        foreach (var definition in Data.Stages.Where(s => all || request.StageIdList.Contains(s.Id)))
        {
            var progress = Progress(tx.Campaign, definition.Id);
            result.StageList.Add(new Stage { Id = definition.Id, Progress = progress.Wins > 0 ? 1u : 0u,
                IsDone = progress.Wins > 0, EnterTimes = progress.Entries, MaxScore = progress.BestScore,
                MinStageTime = progress.BestTime, ChallengeIndexList = { progress.Challenges.Order() } });
        }
        if (Won(tx.Campaign, 10115)) result.FinishedChapterList.Add(1);
        return result;
    });
    public GetStageChapterRsp Chapters(uint uid) => store.Campaign(uid, tx => new GetStageChapterRsp {
        Retcode = GetStageChapterRsp.Types.Retcode.Succ, ChapterList = { new StageChapterInfo { ChapterId = 1, EnterPlayerLevel = tx.Lobby.Level, HasTakeChallenge = 0 } } });
    public GetStageActDifficultyRsp Acts(uint uid) => store.Campaign(uid, tx => new GetStageActDifficultyRsp {
        Retcode = GetStageActDifficultyRsp.Types.Retcode.Succ,
        ActDifficultyList = { Data.Acts.Select(a => new StageActDifficultyInfo { ActId = a.Id, Difficulty = 1,
            HasTakeChallengeNumIndex = { Enumerable.Range(1, 3).Select(i => (uint)i).Where(i => tx.Campaign.ClaimedActRewards.Contains($"{a.Id}:{i}")) } }) } });

    public StageBeginRsp Begin(uint uid, StageBeginReq request) => store.Campaign(uid, tx =>
    {
        StageBeginRsp Error(StageBeginRsp.Types.Retcode code) => new() { Retcode = code, StageId = request.StageId };
        var stage = Data.Stages.SingleOrDefault(s => s.Id == request.StageId);
        if (stage is null) return Error(StageBeginRsp.Types.Retcode.StageNotExist);
        uint[] avatars = request.AvatarIdList.Where(a => a != 0).ToArray();
        if (avatars.Length is < 1 or > 3 || avatars.Distinct().Count() != avatars.Length) return Error(StageBeginRsp.Types.Retcode.AvatarNumError);
        if (avatars.Any(a => a != tx.Lobby.AvatarId) || request.AvatarTrialIdList.Any(a => a != 0) || request.ElfIdList.Any(a => a != 0))
            return Error(StageBeginRsp.Types.Retcode.AvatarError);
        if (request.HasAvatarTeamType && (int)request.AvatarTeamType != 1) return Error(StageBeginRsp.Types.Retcode.NotMeetRestrict);
        if (request.IsSpeedUpStage || request.AssistantUid != 0) return Error(StageBeginRsp.Types.Retcode.NotMeetRestrict);
        if (tx.Lobby.Level < stage.Level) return Error(StageBeginRsp.Types.Retcode.LevelLack);
        if (stage.Previous.Any(p => !Won(tx.Campaign, p))) return Error(StageBeginRsp.Types.Retcode.PreStageNotFinish);
        // A retry/re-begin of the active stage reuses its reservation and never spends twice.
        if (tx.Campaign.Run is { } active && active.StageId == stage.Id && active.Avatars.SequenceEqual(avatars) && Now - active.StartedAt < 86400)
            return StageBeginRsp.Parser.ParseFrom(active.BeginResponse);
        Recover(tx);
        if (tx.Lobby.Stamina < stage.Cost) return Error(StageBeginRsp.Types.Retcode.StaminaLack);
        var response = new StageBeginRsp { Retcode = StageBeginRsp.Types.Retcode.Succ, StageId = stage.Id,
            Progress = Won(tx.Campaign, stage.Id) ? 1u : 0u, SignKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            StageTransactionStr = Guid.NewGuid().ToString("N"), IsCollectCheatData = false, Tag = 0 };
        tx.Lobby = tx.Lobby with { Stamina = tx.Lobby.Stamina - stage.Cost };
        var progress = Progress(tx.Campaign, stage.Id); progress.Entries = checked(progress.Entries + 1);
        tx.Campaign.Stages[stage.Id] = progress;
        tx.Campaign.Run = new(stage.Id, response.SignKey, Now, stage.Cost, avatars, response.ToByteArray());
        tx.Campaign.Team = avatars;
        return response;
    });

    public StageEndRsp End(uint uid, StageEndReq request)
    {
        if (request.Body.Length is 0 or > 262144 || request.Sign.Length > 256) return new() { Retcode = StageEndRsp.Types.Retcode.StageError };
        StageEndReqBody body;
        try { body = StageEndReqBody.Parser.ParseFrom(request.Body); }
        catch (InvalidProtocolBufferException) { return new() { Retcode = StageEndRsp.Types.Retcode.StageError }; }
        // Local simulation trusts client combat outcomes. The opaque client checksum is part of
        // the durable retry identity, not claimed to be an authoritative combat signature.
        string fingerprint = Convert.ToHexString(SHA256.HashData(request.ToByteArray()));
        return store.Campaign(uid, tx =>
        {
            if (tx.Receipt(fingerprint) is { } saved) return StageEndRsp.Parser.ParseFrom(saved);
            var stage = Data.Stages.SingleOrDefault(s => s.Id == body.StageId);
            StageEndRsp Error(StageEndRsp.Types.Retcode code) => new() { Retcode = code, StageId = body.StageId, EndStatus = body.EndStatus };
            if (stage is null || tx.Campaign.Run is not { } run || run.StageId != body.StageId || Now - run.StartedAt >= 86400)
                return Error(StageEndRsp.Types.Retcode.StageError);
            if ((int)body.EndStatus is < 1 or > 4) return Error(StageEndRsp.Types.Retcode.StageError);
            if (body.ChallengeIndexList.Count > stage.Challenges.Length || body.ChallengeIndexList.Distinct().Count() != body.ChallengeIndexList.Count || body.ChallengeIndexList.Any(i => i >= stage.Challenges.Length))
                return Error(StageEndRsp.Types.Retcode.ChallengeError);
            var progress = Progress(tx.Campaign, stage.Id);
            bool win = (int)body.EndStatus == 1, first = progress.Wins == 0 && win;
            var response = new StageEndRsp { Retcode = StageEndRsp.Types.Retcode.Succ, StageId = stage.Id, EndStatus = body.EndStatus,
                IsFirstWin = first, OldMaxScore = progress.BestScore, IsNewMaxScore = win && body.Score > progress.BestScore,
                StageScore = body.Score, BuffReward = new AccountBuffReward(), LineEnhanceRewardData = new RewardData() };
            if (win)
            {
                response.PlayerExpReward = stage.Exp; response.AvatarExpReward = stage.AvatarExp; response.ScoinReward = stage.Scoin;
                Apply(tx, new RewardData { Exp = stage.Exp, Scoin = stage.Scoin }); ApplyAvatarExp(tx, stage.AvatarExp);
                foreach (uint index in body.ChallengeIndexList.Order())
                    if (progress.Challenges.Add(index))
                    {
                        var reward = Data.Reward(stage.Challenges[index]); Apply(tx, reward);
                        response.ChallengeList.Add(new StageChallengeData { ChallengeIndex = index, Reward = reward });
                    }
                progress.Wins = checked(progress.Wins + 1); progress.BestScore = Math.Max(progress.BestScore, body.Score);
                if (body.StagePassTime > 0) progress.BestTime = progress.BestTime == 0 ? body.StagePassTime : Math.Min(progress.BestTime, body.StagePassTime);
            }
            // Failure/exit spends the reserved stamina, awards nothing, and never completes a mission.
            response.Progress = progress.Wins > 0 ? 1u : 0u;
            tx.Campaign.Stages[stage.Id] = progress; tx.Campaign.Run = null;
            tx.SaveReceipt(fingerprint, response.ToByteArray());
            return response;
        });
    }

    private static uint CurrentStep(CampaignState state) => !state.ClaimedMissions.Contains(97001) ? 1u
        : !state.ClaimedMissions.Contains(97002) || !state.ClaimedMissions.Contains(97003) ? 2u : 0u;
    private static uint MissionProgress(CampaignTransaction tx, MissionDefinition mission) => mission.Way == 10171
        ? Math.Min(tx.Lobby.Level, mission.Total) : Won(tx.Campaign, mission.Target) ? mission.Total : 0;
    private static bool Available(CampaignTransaction tx, MissionDefinition mission) => mission.Step == 0
        ? Data.Stages.Single(s => s.Id == mission.Target).Previous.All(p => Won(tx.Campaign, p))
        : mission.Step <= (CurrentStep(tx.Campaign) == 0 ? 2 : CurrentStep(tx.Campaign));
    public GetMissionDataRsp Missions(uint uid) => store.Campaign(uid, tx =>
    {
        var result = new GetMissionDataRsp { Retcode = GetMissionDataRsp.Types.Retcode.Succ, IsAll = true,
            MainlineStep = new MainlineStepMission { IsUpdate = true }, ChallengeMission = new ChallengeMissionData() };
        uint step = CurrentStep(tx.Campaign);
        if (step != 0) result.MainlineStep.CurMainlineStepList.Add(step);
        for (uint completed = 1; completed <= 2 && (step == 0 || completed < step); completed++) result.MainlineStep.FinishedMainlineStepList.Add(completed);
        result.CloseMissionList.Add(tx.Campaign.ClaimedMissions.Order());
        foreach (var mission in Data.Missions.Where(m => Available(tx, m) && !tx.Campaign.ClaimedMissions.Contains(m.Id)))
        {
            uint progress = MissionProgress(tx, mission);
            result.MissionList.Add(new Mission { MissionId = mission.Id, Status = (MissionStatus)(progress >= mission.Total ? 2 : 1),
                Progress = progress, FinishedTimes = 0, FinishedTimesLimit = 1 });
        }
        return result;
    });
    public GetMissionRewardRsp Claim(uint uid, GetMissionRewardReq request) => store.Campaign(uid, tx =>
    {
        var ids = request.MissionIdList.ToArray();
        if (ids.Length is 0 or > 64 || ids.Distinct().Count() != ids.Length || ids.Any(i => Data.Missions.All(m => m.Id != i)))
            return new GetMissionRewardRsp { Retcode = GetMissionRewardRsp.Types.Retcode.MissionIdError };
        var missions = ids.Select(id => Data.Missions.Single(m => m.Id == id)).ToArray();
        if (missions.Any(m => !Available(tx, m) || tx.Campaign.ClaimedMissions.Contains(m.Id) || MissionProgress(tx, m) < m.Total))
            return new GetMissionRewardRsp { Retcode = GetMissionRewardRsp.Types.Retcode.MissionStatusError };
        var result = new GetMissionRewardRsp { Retcode = GetMissionRewardRsp.Types.Retcode.Succ, RewardData = new RewardData(), MissionIdList = { ids } };
        foreach (var mission in missions)
        {
            var reward = Data.Reward(mission.Reward); Apply(tx, reward); Add(result.RewardData, reward); tx.Campaign.ClaimedMissions.Add(mission.Id);
        }
        return result;
    });
    public UpdateMissionProgressRsp Update(uint uid, UpdateMissionProgressReq request) => store.Campaign(uid, tx =>
        new UpdateMissionProgressRsp { Retcode = (uint)request.FinishWay is 10171 or 10180
            ? UpdateMissionProgressRsp.Types.Retcode.Succ : UpdateMissionProgressRsp.Types.Retcode.FinishWayError });
    public TakeStageActChallengeRewardRsp ClaimAct(uint uid, TakeStageActChallengeRewardReq request) => store.Campaign(uid, tx =>
    {
        var result = new TakeStageActChallengeRewardRsp { Retcode = TakeStageActChallengeRewardRsp.Types.Retcode.Fail,
            ActId = request.ActId, Difficulty = request.Difficulty, ChallengeNumIndex = request.ChallengeNumIndex };
        var act = Data.Acts.SingleOrDefault(a => a.Id == request.ActId);
        uint[] indices = request.ChallengeNumIndexList.Count == 0 ? [request.ChallengeNumIndex] : request.ChallengeNumIndexList.ToArray();
        if (act is null || request.Difficulty != 1 || indices.Length > 3 || indices.Any(i => i is < 1 or > 3) || indices.Distinct().Count() != indices.Length) return result;
        int stars = Data.Stages.Where(s => s.Act == act.Id).Sum(s => Progress(tx.Campaign, s.Id).Challenges.Count);
        if (indices.Any(i => tx.Campaign.ClaimedActRewards.Contains($"{act.Id}:{i}"))) { result.Retcode = TakeStageActChallengeRewardRsp.Types.Retcode.HasTake; return result; }
        if (indices.Any(i => stars < act.Thresholds[i - 1])) { result.Retcode = TakeStageActChallengeRewardRsp.Types.Retcode.ChallengeNumLack; return result; }
        foreach (uint index in indices)
        {
            var reward = Data.Reward(act.Rewards[index - 1]); Apply(tx, reward); result.RewardList.Add(reward);
            tx.Campaign.ClaimedActRewards.Add($"{act.Id}:{index}"); result.SuccChallengeNumIndexList.Add(index);
        }
        result.Retcode = TakeStageActChallengeRewardRsp.Types.Retcode.Succ; return result;
    });
    public GetStageDropDisplayRsp Drops(GetStageDropDisplayReq request)
    {
        bool all = All(request.StageIdList);
        if (!all && request.StageIdList.Any(id => Data.Stages.All(s => s.Id != id))) return new() { Retcode = GetStageDropDisplayRsp.Types.Retcode.StageNotExist };
        // Only implemented deterministic rewards are advertised; unimported random drop groups are not promised.
        return new() { Retcode = GetStageDropDisplayRsp.Types.Retcode.Succ,
            StageDropList = { Data.Stages.Where(s => all || request.StageIdList.Contains(s.Id)).Select(s => new StageDropDisplayInfo { StageId = s.Id, DropItemIdList = { 100 } }) } };
    }
    public uint[] Team(uint uid) => store.Campaign(uid, tx => tx.Campaign.Team.Length > 0 ? tx.Campaign.Team : [tx.Lobby.AvatarId]);
    public void SetTeam(uint uid, UpdateAvatarTeamNotify request) => store.Campaign(uid, tx =>
    {
        uint[] ids = request.Team?.AvatarIdList.Where(i => i != 0).ToArray() ?? [];
        if (request.Team?.StageType == 1 && ids.Length == 1 && ids[0] == tx.Lobby.AvatarId) tx.Campaign.Team = ids;
        return true;
    });
    public Material[] Materials(uint uid) => store.Campaign(uid, tx => tx.Campaign.Materials.OrderBy(p => p.Key).Select(p => new Material { Id = p.Key, Num = p.Value }).ToArray());
    private static void Add(RewardData total, RewardData reward)
    {
        total.Exp = checked(total.Exp + reward.Exp); total.Scoin = checked(total.Scoin + reward.Scoin);
        total.Hcoin = checked(total.Hcoin + reward.Hcoin); total.Stamina = checked(total.Stamina + reward.Stamina);
        total.ItemList.Add(reward.ItemList.Select(i => i.Clone()));
    }
    private void Apply(CampaignTransaction tx, RewardData reward)
    {
        Recover(tx);
        uint level = tx.Lobby.Level, exp = checked(tx.Lobby.Exp + reward.Exp), stamina = checked(tx.Lobby.Stamina + reward.Stamina);
        while (level < Data.Levels.Length && exp >= Data.Level(level).Exp)
        { exp -= Data.Level(level).Exp; level++; stamina = checked(stamina + Data.Level(level).Bonus); }
        tx.Lobby = tx.Lobby with { Level = level, Exp = exp, Stamina = stamina, Scoin = checked(tx.Lobby.Scoin + reward.Scoin), Hcoin = checked(tx.Lobby.Hcoin + reward.Hcoin) };
        foreach (var item in reward.ItemList) tx.Campaign.Materials[item.Id] = checked(tx.Campaign.Materials.GetValueOrDefault(item.Id) + item.Num);
    }
    private static void ApplyAvatarExp(CampaignTransaction tx, uint amount)
    {
        uint level = Math.Max(1, tx.Lobby.AvatarLevel), limit = Data.Level(tx.Lobby.Level).AvatarLimit, exp = checked(tx.Lobby.AvatarExp + amount);
        while (level < Math.Min(limit, Data.AvatarLevels.Length) && exp >= Data.AvatarLevels.Single(l => l.Level == level).Exp)
        { exp -= Data.AvatarLevels.Single(l => l.Level == level).Exp; level++; }
        if (level >= limit) exp = Math.Min(exp, Data.AvatarLevels.Single(l => l.Level == level).Exp - 1);
        tx.Lobby = tx.Lobby with { AvatarLevel = level, AvatarExp = exp };
    }
}
