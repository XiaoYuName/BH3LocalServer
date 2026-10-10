using System.Security.Cryptography;
using BH3.Persistence;
using BH3.Game.Operations;
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
        result.FinishedChapterList.Add(Data.Chapters.Where(c => Won(tx.Campaign, c.LastStage)).Select(c => c.Id));
        return result;
    });
    public GetStageChapterRsp Chapters(uint uid) => store.Campaign(uid, tx => new GetStageChapterRsp {
        Retcode = GetStageChapterRsp.Types.Retcode.Succ, ChapterList = { Data.Chapters.Select(c => new StageChapterInfo { ChapterId = c.Id, EnterPlayerLevel = tx.Lobby.Level, HasTakeChallenge = 0 }) } });

    public ChapterGroupGetDataRsp ChapterGroups(uint uid, ChapterGroupGetDataReq request) => store.Campaign(uid, tx =>
    {
        var response = new ChapterGroupGetDataRsp { Retcode = ChapterGroupGetDataRsp.Types.Retcode.Succ,
            ChapterGroupId = request.ChapterGroupId, IsAll = request.ChapterGroupId == 0 };
        // Group/site identities come from the 9.1 capture, never its account completion flags.
        foreach (var group in Data.Chapters.GroupBy(c => c.Group).Where(g => request.ChapterGroupId == 0 || g.Key == request.ChapterGroupId))
            response.ChapterGroupList.Add(new ChapterGroup { Id = group.Key, SiteList = { group.Select(c => new ChapterGroupSite {
                SiteId = c.Site, ChapterId = c.Id, Status = Won(tx.Campaign, c.LastStage) ? ChapterGroupSiteStatus.Finished
                    : tx.Lobby.Level >= c.Level ? ChapterGroupSiteStatus.Unlocked : ChapterGroupSiteStatus.Locked }) } });
        return response;
    });

    public GetStageActDifficultyRsp Acts(uint uid) => store.Campaign(uid, tx => new GetStageActDifficultyRsp {
        Retcode = GetStageActDifficultyRsp.Types.Retcode.Succ,
        ActDifficultyList = { Data.Acts.Select(a => new StageActDifficultyInfo { ActId = a.Id, Difficulty = a.Difficulty,
            HasTakeChallengeNumIndex = { Enumerable.Range(1, 3).Select(i => (uint)i).Where(i => tx.Campaign.ClaimedActRewards.Contains(ActKey(a, i))) } }) } });
    private static string ActKey(ActDefinition act, uint index) => act.Difficulty == 1 ? $"{act.Id}:{index}" : $"{act.Id}:{act.Difficulty}:{index}";
    public GetTrialAvatarRsp TrialAvatars() => new() { Retcode = GetTrialAvatarRsp.Types.Retcode.Succ, IsAllUpdate = true,
        // Captured 9.1 global trial list is empty. Story trials are stage-local; advertising every
        // legacy stage sample globally crashes AvatarSampleDataItem.Init on removed 9.1 samples.
        AvatarList = { } };
    public GetPlotListRsp Plots(uint uid) => store.Campaign(uid, tx => new GetPlotListRsp { Retcode = GetPlotListRsp.Types.Retcode.Succ, PlotList = { tx.Campaign.FinishedPlots.Order() } });
    public FinishPlotRsp FinishPlot(uint uid, FinishPlotReq request) => store.Campaign(uid, tx =>
    {
        var result = new FinishPlotRsp { Retcode = FinishPlotRsp.Types.Retcode.PlotError, PlotId = request.PlotId, PlotType = request.PlotType, DialogId = request.DialogId };
        var plot = Data.Plots.SingleOrDefault(p => p.Id == request.PlotId);
        if (plot is null || request.VisualNovelId != 0 || (request.DialogId != 0 && (request.DialogId < plot.FirstDialog || request.DialogId > plot.LastDialog))) return result;
        var stage = Data.Stages.Single(s => s.Id == plot.Stage);
        if (tx.Lobby.Level < stage.Level || stage.Previous.Any(p => !Won(tx.Campaign, p))) return result;
        // Remember only the dialogue bookmark; this cannot mark its battle won or grant a reward.
        tx.Campaign.FinishedPlots.Add(plot.Id); result.Retcode = FinishPlotRsp.Types.Retcode.Succ; return result;
    });

    public StageBeginRsp Begin(uint uid, StageBeginReq request) => store.Campaign(uid, tx =>
    {
        if(request.StageId==ModeEntryService.GodWarLobbyStage)return ModeEntryService.BeginLobby(tx,request,Now);
        StageBeginRsp Error(StageBeginRsp.Types.Retcode code) => new() { Retcode = code, StageId = request.StageId };
        var stage = Data.Stages.Concat(Data.ExtraStages).SingleOrDefault(s => s.Id == request.StageId);
        if (stage is null) return Error(StageBeginRsp.Types.Retcode.StageNotExist);
        if (Data.ExtraStages.Contains(stage) && (Now >= tx.Campaign.Challenges.ExpiresAt || !ChallengeCatalog.Default.Sites.Any(s => s.Stage == stage.Id && tx.Campaign.Challenges.ActiveSite == s.Id)))
            return Error(StageBeginRsp.Types.Retcode.StageMismatch);
        uint[] avatars = request.AvatarIdList.Where(a => a != 0).ToArray();
        uint[] trials = request.AvatarTrialIdList.Where(a => a != 0).ToArray();
        if (avatars.Length > 3 || trials.Length > 3 || avatars.Length + trials.Length == 0 || avatars.Distinct().Count() != avatars.Length || trials.Distinct().Count() != trials.Length)
            return Error(StageBeginRsp.Types.Retcode.AvatarNumError);
        var owned = OwnedAvatars(tx);
        if (trials.Any(t => !(stage.Trials ?? []).Contains(t)) || !CompanionService.ValidTeam(tx,request.ElfIdList))
            return Error(StageBeginRsp.Types.Retcode.AvatarError);
        uint[] trialAvatars = trials.Select(t => Data.Trials.Single(x => x.Id == t).Avatar).ToArray();
        if (avatars.Any(a => !owned.Contains(a) && !trialAvatars.Contains(a)) || avatars.Union(trialAvatars).Count() > 3)
            return Error(StageBeginRsp.Types.Retcode.AvatarError);
        if (request.HasAvatarTeamType && (int)request.AvatarTeamType != 1) return Error(StageBeginRsp.Types.Retcode.NotMeetRestrict);
        if (request.IsSpeedUpStage || request.AssistantUid != 0) return Error(StageBeginRsp.Types.Retcode.NotMeetRestrict);
        if (tx.Lobby.Level < stage.Level) return Error(StageBeginRsp.Types.Retcode.LevelLack);
        if (stage.Previous.Any(p => !Won(tx.Campaign, p))) return Error(StageBeginRsp.Types.Retcode.PreStageNotFinish);
        // A retry/re-begin of the active stage reuses its reservation and never spends twice.
        var elfs=request.ElfIdList.Where(x=>x!=0).ToArray();
        if (tx.Campaign.Run is { } active && active.StageId == stage.Id && active.Avatars.SequenceEqual(avatars) && (active.Trials ?? []).SequenceEqual(trials) && (active.Elfs ?? []).SequenceEqual(elfs) && Now - active.StartedAt < 86400)
            return StageBeginRsp.Parser.ParseFrom(active.BeginResponse);
        Recover(tx);
        if (tx.Lobby.Stamina < stage.Cost) return Error(StageBeginRsp.Types.Retcode.StaminaLack);
        var response = new StageBeginRsp { Retcode = StageBeginRsp.Types.Retcode.Succ, StageId = stage.Id,
            Progress = Won(tx.Campaign, stage.Id) ? 1u : 0u, SignKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            StageTransactionStr = Guid.NewGuid().ToString("N"), IsCollectCheatData = false, Tag = 0 };
        tx.Lobby = tx.Lobby with { Stamina = tx.Lobby.Stamina - stage.Cost };
        SystemsService.Track(tx,Now,"stamina",stage.Cost);
        var progress = Progress(tx.Campaign, stage.Id); progress.Entries = checked(progress.Entries + 1);
        tx.Campaign.Stages[stage.Id] = progress;
        tx.Campaign.Run = new(stage.Id, response.SignKey, Now, stage.Cost, avatars, response.ToByteArray(), trials, elfs);
        if (trials.Length == 0)
        {
            if (Data.ExtraStages.Contains(stage)) tx.Campaign.ModeTeams[60] = avatars;
            else tx.Campaign.Team = avatars;
        }
        return response;
    });

    public StageEndRsp End(uint uid, StageEndReq request)
    {
        if (request.Body.Length is 0 or > 262144 || request.Sign.Length > 256) return new() { Retcode = StageEndRsp.Types.Retcode.StageError };
        StageEndReqBody body;
        try { body = StageEndReqBody.Parser.ParseFrom(request.Body); }
        catch (InvalidProtocolBufferException) { return new() { Retcode = StageEndRsp.Types.Retcode.StageError }; }
        // 9.1's constructor defaults to WIN=1 and its writer omits field 2 for wins.
        // The imported reference schema defaults to 0; only absent fields use the client default.
        if (!body.HasEndStatus) body.EndStatus = StageEndStatus.StageWin;
        // Local simulation trusts client combat outcomes. The opaque client checksum is part of
        // the durable retry identity, not claimed to be an authoritative combat signature.
        string fingerprint = Convert.ToHexString(SHA256.HashData(request.ToByteArray()));
        return store.Campaign(uid, tx =>
        {
            if (tx.Receipt(fingerprint) is { } saved)
            {
                var receipt = StageEndRsp.Parser.ParseFrom(saved);
                // 1.3.9 emitted an empty optional bonus. Repair only its presentation;
                // retain the committed receipt, wallet, and any currently active run.
                if (receipt.LineEnhanceRewardData is { } bonus && bonus.CalculateSize() == 0)
                    receipt.LineEnhanceRewardData = null;
                return receipt;
            }
            if(body.StageId==ModeEntryService.GodWarLobbyStage)return ModeEntryService.EndLobby(tx,body,fingerprint,Now);
            var stage = Data.Stages.Concat(Data.ExtraStages).SingleOrDefault(s => s.Id == body.StageId);
            StageEndRsp Error(StageEndRsp.Types.Retcode code)
            {
                var error = new StageEndRsp { Retcode = code, StageId = body.StageId };
                // The client rejects unknown enum values during deserialization, even on errors.
                if ((int)body.EndStatus is >= 1 and <= 4) error.EndStatus = body.EndStatus;
                return error;
            }
            if (stage is null || tx.Campaign.Run is not { } run || run.StageId != body.StageId || Now - run.StartedAt >= 86400)
                return Error(StageEndRsp.Types.Retcode.StageError);
            if (Data.ExtraStages.Contains(stage) && (Now >= tx.Campaign.Challenges.ExpiresAt || !ChallengeCatalog.Default.Sites.Any(s => s.Stage == stage.Id && tx.Campaign.Challenges.ActiveSite == s.Id)))
                return Error(StageEndRsp.Types.Retcode.StageError);
            if ((int)body.EndStatus is < 1 or > 4) return Error(StageEndRsp.Types.Retcode.StageError);
            if (body.ChallengeIndexList.Count > stage.Challenges.Length || body.ChallengeIndexList.Distinct().Count() != body.ChallengeIndexList.Count || body.ChallengeIndexList.Any(i => i >= stage.Challenges.Length))
                return Error(StageEndRsp.Types.Retcode.ChallengeError);
            var progress = Progress(tx.Campaign, stage.Id);
            bool win = (int)body.EndStatus == 1, first = progress.Wins == 0 && win;
            var response = new StageEndRsp { Retcode = StageEndRsp.Types.Retcode.Succ, StageId = stage.Id, EndStatus = body.EndStatus,
                IsFirstWin = first, OldMaxScore = progress.BestScore, IsNewMaxScore = win && body.Score > progress.BestScore,
                // An absent bonus and an explicitly empty bonus are different on the wire.
                // Ordinary stages have no line-enhance grant; do not advertise one.
                StageScore = body.Score, BuffReward = new AccountBuffReward() };
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
                SystemsService.Track(tx,Now,"stage");SystemsService.Track(tx,Now,"story");
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
    private static IEnumerable<MissionDefinition> MissionDefinitions => Data.Missions.Concat(ChallengeCatalog.Default.AbyssRewards.Select(r => new MissionDefinition(r.Id, 0, 0, Math.Max(1, r.Score), r.Reward, 0)));
    private uint MissionProgress(CampaignTransaction tx, MissionDefinition mission) => mission.Way == 0
        ? ChallengeService.MissionProgress(ChallengeService.State(tx, Now), mission.Id) : mission.Way == 10171
        ? Math.Min(tx.Lobby.Level, mission.Total) : Won(tx.Campaign, mission.Target) ? mission.Total : 0;
    private static bool Claimed(CampaignTransaction tx, MissionDefinition mission) => mission.Way == 0 ? tx.Campaign.Challenges.AbyssRewards.Contains(mission.Id) : tx.Campaign.ClaimedMissions.Contains(mission.Id);
    private static bool Available(CampaignTransaction tx, MissionDefinition mission) => mission.Way == 0 ? tx.Lobby.Level >= 81 : mission.Step == 0
        ? Data.Stages.Single(s => s.Id == mission.Target).Previous.All(p => Won(tx.Campaign, p))
        : mission.Step <= (CurrentStep(tx.Campaign) == 0 ? 2 : CurrentStep(tx.Campaign));
    public GetMissionDataRsp Missions(uint uid) => store.Campaign(uid, tx =>
    {
        ChallengeService.State(tx, Now);
        var result = new GetMissionDataRsp { Retcode = GetMissionDataRsp.Types.Retcode.Succ, IsAll = true,
            MainlineStep = new MainlineStepMission { IsUpdate = true }, ChallengeMission = new ChallengeMissionData() };
        SystemsService.AppendMissions(tx,result,Now);
        uint step = CurrentStep(tx.Campaign);
        if (step != 0) result.MainlineStep.CurMainlineStepList.Add(step);
        for (uint completed = 1; completed <= 2 && (step == 0 || completed < step); completed++) result.MainlineStep.FinishedMainlineStepList.Add(completed);
        result.CloseMissionList.Add(tx.Campaign.ClaimedMissions.Order());
        result.CloseMissionList.Add(tx.Campaign.Challenges.AbyssRewards.Order());
        foreach (var mission in MissionDefinitions.Where(m => Available(tx, m) && !Claimed(tx, m)))
        {
            uint progress = MissionProgress(tx, mission);
            result.MissionList.Add(new Mission { MissionId = mission.Id, Status = progress >= mission.Total ? MissionStatus.Finish : MissionStatus.Doing,
                Progress = progress, FinishedTimes = 0, FinishedTimesLimit = 1 });
        }
        SystemsService.AppendActivityMissions(tx,result,Now);
        return result;
    });
    public GetMissionRewardRsp Claim(uint uid, GetMissionRewardReq request) => store.Campaign(uid, tx =>
    {
        ChallengeService.State(tx, Now);
        var ids = request.MissionIdList.ToArray();
        if (ids.Length is 0 or > 64 || ids.Distinct().Count() != ids.Length || ids.Any(i => MissionDefinitions.All(m => m.Id != i) && SystemsService.DailyMissions.All(m=>SystemsService.N(m,"id")!=i)))
            return new GetMissionRewardRsp { Retcode = GetMissionRewardRsp.Types.Retcode.MissionIdError };
        var daily=ids.Where(id=>SystemsService.DailyMissions.Any(m=>SystemsService.N(m,"id")==id)).ToArray();
        if(daily.Any(id=>!SystemsService.DailyClaimable(tx,id,Now)))return new GetMissionRewardRsp{Retcode=GetMissionRewardRsp.Types.Retcode.MissionStatusError};
        var missions = ids.Except(daily).Select(id => MissionDefinitions.Single(m => m.Id == id)).ToArray();
        if (missions.Any(m => !Available(tx, m) || Claimed(tx, m) || MissionProgress(tx, m) < m.Total))
            return new GetMissionRewardRsp { Retcode = GetMissionRewardRsp.Types.Retcode.MissionStatusError };
        var result = new GetMissionRewardRsp { Retcode = GetMissionRewardRsp.Types.Retcode.Succ, RewardData = new RewardData(), MissionIdList = { ids } };
        foreach(uint id in daily)Add(result.RewardData,SystemsService.ClaimDaily(tx,id,Now));
        foreach (var mission in missions)
        {
            var reward = mission.Way == 0 ? ChallengeCatalog.Default.Rewards.Single(r => r.Id == mission.Reward).Message() : Data.Reward(mission.Reward);
            Apply(tx, reward); Add(result.RewardData, reward);
            if (mission.Way == 0) tx.Campaign.Challenges.AbyssRewards.Add(mission.Id); else tx.Campaign.ClaimedMissions.Add(mission.Id);
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
        var act = Data.Acts.SingleOrDefault(a => a.Id == request.ActId && a.Difficulty == request.Difficulty);
        uint[] indices = request.ChallengeNumIndexList.Count == 0 ? [request.ChallengeNumIndex] : request.ChallengeNumIndexList.ToArray();
        if (act is null || indices.Length > 3 || indices.Any(i => i is < 1 or > 3) || indices.Distinct().Count() != indices.Length) return result;
        int stars = Data.Stages.Where(s => s.Act == act.Id && s.Difficulty == act.Difficulty).Sum(s => Progress(tx.Campaign, s.Id).Challenges.Count);
        if (indices.Any(i => tx.Campaign.ClaimedActRewards.Contains(ActKey(act, i)))) { result.Retcode = TakeStageActChallengeRewardRsp.Types.Retcode.HasTake; return result; }
        if (indices.Any(i => stars < act.Thresholds[i - 1])) { result.Retcode = TakeStageActChallengeRewardRsp.Types.Retcode.ChallengeNumLack; return result; }
        foreach (uint index in indices)
        {
            var reward = Data.Reward(act.Rewards[index - 1]); Apply(tx, reward); result.RewardList.Add(reward);
            tx.Campaign.ClaimedActRewards.Add(ActKey(act, index)); result.SuccChallengeNumIndexList.Add(index);
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
    internal static uint[] OwnedAvatars(CampaignTransaction tx) => tx.Inventory is { } inventory
        ? GetAvatarDataRsp.Parser.ParseFrom(inventory.Avatars).AvatarList.Select(a => a.AvatarId).ToArray() : [tx.Lobby.AvatarId];
    public uint[] OwnedAvatars(uint uid) => store.Campaign(uid, OwnedAvatars);
    public uint[] Team(uint uid, uint type = 1) => store.Campaign(uid, tx => type != 1 && tx.Campaign.ModeTeams.TryGetValue(type, out var team) ? team : tx.Campaign.Team.Length > 0 ? tx.Campaign.Team : [tx.Lobby.AvatarId]);
    public void SetTeam(uint uid, UpdateAvatarTeamNotify request) => store.Campaign(uid, tx =>
    {
        uint[] ids = request.Team?.AvatarIdList.Where(i => i != 0).ToArray() ?? [];
        var owned = OwnedAvatars(tx);
        if (request.Team?.StageType is 1 or 60 && ids.Length is >= 1 and <= 3 && ids.Distinct().Count() == ids.Length && ids.All(owned.Contains))
        {
            if (request.Team.StageType == 1) tx.Campaign.Team = ids;
            else tx.Campaign.ModeTeams[request.Team.StageType] = ids;
        }
        return true;
    });
    public Material[] Materials(uint uid) => store.Campaign(uid, tx => tx.Campaign.Materials.OrderBy(p => p.Key).Select(p => new Material { Id = p.Key, Num = p.Value }).ToArray());
    private static void Add(RewardData total, RewardData reward)
    {
        total.Exp = checked(total.Exp + reward.Exp); total.Scoin = checked(total.Scoin + reward.Scoin);
        total.Hcoin = checked(total.Hcoin + reward.Hcoin); total.Stamina = checked(total.Stamina + reward.Stamina);
        total.ItemList.Add(reward.ItemList.Select(i => i.Clone()));
    }
    public void Apply(CampaignTransaction tx, RewardData reward)
    {
        Recover(tx);
        uint level = tx.Lobby.Level, exp = checked(tx.Lobby.Exp + reward.Exp), stamina = checked(tx.Lobby.Stamina + reward.Stamina);
        while (level < Data.Levels.Length && exp >= Data.Level(level).Exp)
        { exp -= Data.Level(level).Exp; level++; stamina = checked(stamina + Data.Level(level).Bonus); }
        tx.Lobby = tx.Lobby with { Level = level, Exp = exp, Stamina = stamina, Scoin = checked(tx.Lobby.Scoin + reward.Scoin), Hcoin = checked(tx.Lobby.Hcoin + reward.Hcoin) };
        foreach (var item in reward.ItemList)
        {
            var spec = GrantService.Catalog.Items.FirstOrDefault(x => x.Id == item.Id && x.Kind is "material" or "weapon" or "stigmata");
            var card = spec is null ? GrantService.Catalog.Items.FirstOrDefault(x => x.Kind == "avatar" && x.Card == item.Id) : null;
            if (card is not null)
                for (uint i = 0; i < item.Num; i++) GrantService.Apply(tx, tx.Uid, new GrantItem("avatar", card.Id));
            else if (spec is not null)
                GrantService.Apply(tx, tx.Uid, new GrantItem(spec.Kind, item.Id, item.Num, Math.Max(1, item.Level)));
            else throw new InvalidOperationException($"Unknown campaign reward item {item.Id}.");
        }
    }
    private static void ApplyAvatarExp(CampaignTransaction tx, uint amount)
    {
        if (tx.Inventory is { } inventory)
        {
            var snapshot = GetAvatarDataRsp.Parser.ParseFrom(inventory.Avatars);
            uint cap = Math.Min(Data.Level(tx.Lobby.Level).AvatarLimit, (uint)Data.AvatarLevels.Length);
            foreach (var avatar in snapshot.AvatarList.Where(a => tx.Campaign.Run!.Avatars.Contains(a.AvatarId)))
            {
                // A captured high-level avatar must never be lowered by a local cap.
                if (avatar.Level >= cap) continue;
                uint xp = checked(avatar.Exp + amount);
                while (avatar.Level < cap && xp >= Data.AvatarLevels.Single(l => l.Level == avatar.Level).Exp)
                { xp -= Data.AvatarLevels.Single(l => l.Level == avatar.Level).Exp; avatar.Level++; }
                avatar.Exp = avatar.Level >= cap ? Math.Min(xp, Data.AvatarLevels.Single(l => l.Level == avatar.Level).Exp - 1) : xp;
            }
            tx.Inventory = inventory with { Avatars = snapshot.ToByteArray() };
            var primary = snapshot.AvatarList.Single(a => a.AvatarId == tx.Lobby.AvatarId);
            tx.Lobby = tx.Lobby with { AvatarLevel = primary.Level, AvatarExp = primary.Exp };
            return;
        }
        if (!tx.Campaign.Run!.Avatars.Contains(tx.Lobby.AvatarId)) return;
        uint level = Math.Max(1, tx.Lobby.AvatarLevel), limit = Data.Level(tx.Lobby.Level).AvatarLimit, exp = checked(tx.Lobby.AvatarExp + amount);
        while (level < Math.Min(limit, Data.AvatarLevels.Length) && exp >= Data.AvatarLevels.Single(l => l.Level == level).Exp)
        { exp -= Data.AvatarLevels.Single(l => l.Level == level).Exp; level++; }
        if (level >= limit) exp = Math.Min(exp, Data.AvatarLevels.Single(l => l.Level == level).Exp - 1);
        tx.Lobby = tx.Lobby with { AvatarLevel = level, AvatarExp = exp };
    }
}
