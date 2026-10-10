using System.Text.Json;
using System.Buffers.Binary;
using BH3.Game.Campaign;
using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class CampaignTests : IDisposable
{
    private readonly TestDirectory directory = new();
    private readonly SqliteConnectionFactory database;
    private readonly LobbyStore store;
    private readonly TestClock clock = new();
    private CampaignService service;
    private const uint Uid = 10001;
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Value = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        public override DateTimeOffset GetUtcNow() => Value;
    }
    public CampaignTests()
    {
        database = new(Path.Combine(directory.Path, "campaign.db")); SchemaMigrator.Initialize(database);
        store = new(database); service = new(store, clock);
        foreach (uint uid in new[] { Uid, 10002u }) store.EnsurePlayer(uid, "captain", new(1, 80, 101, 20001, 59101, 1, [1]));
    }
    private StageBeginRsp Begin(uint stage = 10101) => service.Begin(Uid, new StageBeginReq { StageId = stage, AvatarIdList = { 101, 0, 0 } });
    private static StageEndReq EndRequest(StageBeginRsp begin, int status = 1, params uint[] challenges) => new() {
        Sign = begin.SignKey, Body = new StageEndReqBody { StageId = begin.StageId, EndStatus = (StageEndStatus)status,
            StagePassTime = 55000, Score = 1234, ScoinReward = uint.MaxValue, AvatarExpReward = uint.MaxValue,
            ChallengeIndexList = { challenges }, DropItemList = { new DropItem { ItemId = 999999, Num = uint.MaxValue } } }.ToByteString() };

    [Fact]
    public void Client91OmittedWinStatusSettlesOnceAndUnlocksNextStageAcrossRestart()
    {
        Begin();
        // Independently derived from the 9.1 writer: field 2 is omitted for WIN=1.
        // Field 1=10101, challenges 0/1/2, pass time 55000; no explicit end_status.
        var wire = Convert.FromHexString("08F54E30003001300250D8AD03");
        Assert.False(StageEndReqBody.Parser.ParseFrom(wire).HasEndStatus);
        var request = new StageEndReq { Body = ByteString.CopyFrom(wire), Sign = "client91-fixture" };
        var response = service.End(Uid, request);
        Assert.Equal(StageEndRsp.Types.Retcode.Succ, response.Retcode);
        Assert.Equal(StageEndStatus.StageWin, response.EndStatus); Assert.True(response.IsFirstWin);
        Assert.Equal(750u, store.Read(Uid).Scoin);
        Assert.True(Assert.Single(service.Stages(Uid, new() { StageIdList = { 10101 } }).StageList).IsDone);
        var wallet = JsonSerializer.Serialize(store.Read(Uid));
        service = new(new LobbyStore(database), clock);
        Assert.Equal(response.ToByteArray(), service.End(Uid, request).ToByteArray());
        Assert.Equal(wallet, JsonSerializer.Serialize(store.Read(Uid)));
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, Begin(10102).Retcode);
    }

    [Theory]
    [InlineData(0)] [InlineData(5)]
    public void InvalidExplicitSettlementStatusIsRejectedWithoutUnmappedResponseEnum(int status)
    {
        var begin = Begin(); var before = JsonSerializer.Serialize(store.Read(Uid));
        var result = service.End(Uid, EndRequest(begin, status));
        Assert.Equal(StageEndRsp.Types.Retcode.StageError, result.Retcode);
        Assert.False(result.HasEndStatus); // 9.1 rejects unmapped enum values even in error responses.
        Assert.Equal(before, JsonSerializer.Serialize(store.Read(Uid)));
        Assert.Equal(StageBeginRsp.Types.Retcode.PreStageNotFinish, Begin(10102).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, Begin().Retcode); // Invalid packet did not consume the run.
    }

    [Theory]
    [InlineData(0u, true)] [InlineData(1u, false)]
    public void ChapterGroupUnlockDoesNotCompleteStagesOrRemoveTheirPrerequisites(uint selector, bool all)
    {
        var before = JsonSerializer.Serialize(store.Read(Uid));
        var response = service.ChapterGroups(Uid, new() { ChapterGroupId = selector });
        Assert.Equal(all, response.IsAll); Assert.Equal(selector, response.ChapterGroupId);
        var group = Assert.Single(response.ChapterGroupList, g => g.Id == 1); Assert.Equal(1u, group.Id);
        var site = Assert.Single(group.SiteList, s => s.SiteId == 1); Assert.Equal(1u, site.SiteId); Assert.Equal(1u, site.ChapterId);
        Assert.Equal(ChapterGroupSiteStatus.Unlocked, site.Status);
        Assert.All(service.Stages(Uid, new()).StageList, s => { Assert.False(s.IsDone); Assert.Equal(0u, s.Progress); Assert.Empty(s.ChallengeIndexList); });
        Assert.Equal(StageBeginRsp.Types.Retcode.PreStageNotFinish, Begin(10102).Retcode);
        Assert.Equal(before, JsonSerializer.Serialize(store.Read(Uid)));
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, Begin().Retcode);
        service.End(Uid, EndRequest(Begin(), 1));
        // One completed stage does not mark the whole chapter finished.
        Assert.Equal(ChapterGroupSiteStatus.Unlocked, service.ChapterGroups(Uid, new()).ChapterGroupList[0].SiteList[0].Status);
        var unavailable = service.ChapterGroups(Uid, new() { ChapterGroupId = 17 });
        Assert.Empty(unavailable.ChapterGroupList); Assert.False(unavailable.IsAll); Assert.Equal(17u, unavailable.ChapterGroupId);
        Assert.Equal(ChapterGroupSiteStatus.Unlocked, service.ChapterGroups(10002, new()).ChapterGroupList[0].SiteList[0].Status);
    }

    [Fact]
    public void FullChapterUnlocksCompletesAndPersistsMissionsWalletAndStars()
    {
        var catalog = CampaignCatalog.Default;
        Assert.Equal(CampaignCatalog.Default.Stages.Length, service.Stages(Uid, new()).StageList.Count);
        Assert.All(service.Stages(Uid, new()).StageList, s => Assert.False(s.IsDone));
        foreach (var definition in catalog.Stages.Where(s => s.Id >= 10101 && s.Id <= 10115).OrderBy(s => s.Id))
        {
            var begin = Begin(definition.Id); Assert.Equal(StageBeginRsp.Types.Retcode.Succ, begin.Retcode);
            var request = EndRequest(begin, 1, 0, 1, 2); var end = service.End(Uid, request);
            Assert.Equal(StageEndRsp.Types.Retcode.Succ, end.Retcode); Assert.True(end.IsFirstWin);
            Assert.Equal(definition.Scoin, end.ScoinReward); Assert.Equal(definition.Exp, end.PlayerExpReward);
            Assert.Equal(definition.AvatarExp, end.AvatarExpReward); Assert.Equal(3, end.ChallengeList.Count);
            foreach (var mission in service.Missions(Uid).MissionList.Where(m => m.Status == MissionStatus.Finish).ToArray())
                Assert.Equal(GetMissionRewardRsp.Types.Retcode.Succ, service.Claim(Uid, new() { MissionIdList = { mission.MissionId } }).Retcode);
        }
        service = new(new LobbyStore(database), clock);
        var saved = service.Stages(Uid, new());
        Assert.All(saved.StageList.Where(s => s.Id >= 10101 && s.Id <= 10115), s => { Assert.True(s.IsDone); Assert.Equal(3, s.ChallengeIndexList.Count); });
        Assert.All(saved.StageList.Where(s => s.Id < 10101 || s.Id > 10115), s => Assert.False(s.IsDone));
        Assert.Contains(1u, saved.FinishedChapterList);
        var group = service.ChapterGroups(Uid, new() { ChapterGroupId = 1 });
        Assert.Equal(ChapterGroupSiteStatus.Finished, Assert.Single(Assert.Single(group.ChapterGroupList).SiteList, s => s.SiteId == 1).Status);
        Assert.True(store.Read(Uid).Scoin >= 15 * 750); Assert.True(store.Read(Uid).AvatarLevel > 1);
        Assert.Contains(10001u, service.Missions(Uid).CloseMissionList);
        Assert.DoesNotContain(service.Materials(Uid), m => m.Id == 999999);
    }
    [Fact]
    public void BeginRetryEndRetryAndRestartCannotChargeOrRewardTwice()
    {
        var begin = Begin(); var afterBegin = store.Read(Uid);
        Assert.Equal(74u, afterBegin.Stamina); Assert.Equal(begin.ToByteArray(), Begin().ToByteArray());
        service = new(new LobbyStore(database), clock);
        Assert.Equal(begin.ToByteArray(), Begin().ToByteArray()); Assert.Equal(74u, store.Read(Uid).Stamina);
        var request = EndRequest(begin, 1, 0, 1, 2); var response = service.End(Uid, request); var wallet = store.Read(Uid);
        Assert.Equal(750u, wallet.Scoin); Assert.Equal(15u, wallet.Hcoin);
        service = new(new LobbyStore(database), clock);
        Assert.Equal(response.ToByteArray(), service.End(Uid, request).ToByteArray()); Assert.Equal(JsonSerializer.Serialize(wallet), JsonSerializer.Serialize(store.Read(Uid)));
        var second = Begin(); Assert.NotEqual(begin.SignKey, second.SignKey);
        var reserved = store.Read(Uid); Assert.Equal(response.ToByteArray(), service.End(Uid, request).ToByteArray());
        Assert.Equal(JsonSerializer.Serialize(reserved), JsonSerializer.Serialize(store.Read(Uid)));
        var replay = service.End(Uid, EndRequest(second, 1, 0, 1, 2));
        Assert.False(replay.IsFirstWin); Assert.Empty(replay.ChallengeList); Assert.Equal(15u, store.Read(Uid).Hcoin);
        Assert.Equal(1500u, store.Read(Uid).Scoin);
    }
    [Fact]
    public void ConcurrentSettlementCommitsExactlyOnce()
    {
        var begin = Begin(); var request = EndRequest(begin, 1, 0, 1, 2);
        var results = new StageEndRsp[8];
        Parallel.For(0, results.Length, i => results[i] = new CampaignService(new LobbyStore(database), clock).End(Uid, request));
        Assert.All(results, r => Assert.Equal(StageEndRsp.Types.Retcode.Succ, r.Retcode));
        Assert.All(results, r => Assert.Equal(results[0].ToByteArray(), r.ToByteArray())); Assert.Equal(750u, store.Read(Uid).Scoin);
    }
    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void LossOrExitGivesNoRewardsAndCanBeRetried(int status)
    {
        var begin = Begin(); var end = service.End(Uid, EndRequest(begin, status));
        Assert.Equal(StageEndRsp.Types.Retcode.Succ, end.Retcode); Assert.Equal(0u, end.Progress);
        Assert.Equal(0u, store.Read(Uid).Scoin); Assert.Equal(0u, store.Read(Uid).Exp);
        Assert.Equal(74u, store.Read(Uid).Stamina);
        Assert.Equal(StageBeginRsp.Types.Retcode.PreStageNotFinish, Begin(10102).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, Begin().Retcode);
    }
    [Fact]
    public void InvalidStageTeamPrerequisiteOrLowStaminaCannotStart()
    {
        Assert.Equal(StageBeginRsp.Types.Retcode.StageNotExist, Begin(99999).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.PreStageNotFinish, Begin(10102).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.AvatarNumError, service.Begin(Uid, new() { StageId = 10101 }).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.AvatarError, service.Begin(Uid, new() { StageId = 10101, AvatarIdList = { 999 } }).Retcode);
        Assert.Equal(80u, store.Read(Uid).Stamina);
        store.Campaign(Uid, tx => { tx.Lobby = tx.Lobby with { Stamina = 5 }; return true; });
        Assert.Equal(StageBeginRsp.Types.Retcode.StaminaLack, Begin().Retcode); Assert.Equal(5u, store.Read(Uid).Stamina);
    }
    [Fact]
    public void WrongStageAccountMalformedBodyAndChallengeIndexCannotSettle()
    {
        var begin = Begin(); var request = EndRequest(begin, 1, 0);
        Assert.Equal(StageEndRsp.Types.Retcode.StageError, service.End(10002, request).Retcode);
        Assert.Equal(StageEndRsp.Types.Retcode.StageError, service.End(Uid, new() { Body = ByteString.CopyFrom(new byte[] { 0xff }) }).Retcode);
        Assert.Equal(StageEndRsp.Types.Retcode.StageError, service.End(Uid, EndRequest(new() { StageId = 10102 })).Retcode);
        Assert.Equal(StageEndRsp.Types.Retcode.ChallengeError, service.End(Uid, EndRequest(begin, 1, 3)).Retcode);
        Assert.Equal(StageEndRsp.Types.Retcode.ChallengeError, service.End(Uid, EndRequest(begin, 1, 0, 0)).Retcode);
        Assert.Equal(0u, store.Read(Uid).Scoin); Assert.Equal(StageEndRsp.Types.Retcode.Succ, service.End(Uid, request).Retcode);
    }
    [Fact]
    public void MissionAndActRewardsRequireProgressAndOnlyGrantOnce()
    {
        var mission = new GetMissionRewardReq { MissionIdList = { 10001 } };
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.MissionStatusError, service.Claim(Uid, mission).Retcode);
        service.Update(Uid, new() { FinishWay = (MissionFinishWay)10180, FinishPara = 10101, ProgressAdd = uint.MaxValue });
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.MissionStatusError, service.Claim(Uid, mission).Retcode);
        var act = new TakeStageActChallengeRewardReq { ActId = 101, Difficulty = 1, ChallengeNumIndex = 1 };
        Assert.Equal(TakeStageActChallengeRewardRsp.Types.Retcode.ChallengeNumLack, service.ClaimAct(Uid, act).Retcode);
        service.End(Uid, EndRequest(Begin(), 1, 0, 1, 2));
        var reward = service.Claim(Uid, mission); Assert.Equal(GetMissionRewardRsp.Types.Retcode.Succ, reward.Retcode);
        Assert.Equal(12u, reward.RewardData.Exp); var saved = store.Read(Uid);
        Assert.Equal(GetMissionRewardRsp.Types.Retcode.MissionStatusError, service.Claim(Uid, mission).Retcode); Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(store.Read(Uid)));
        service.End(Uid, EndRequest(Begin(10102), 1, 0, 1, 2));
        Assert.Equal(TakeStageActChallengeRewardRsp.Types.Retcode.Succ, service.ClaimAct(Uid, act).Retcode); saved = store.Read(Uid);
        Assert.Equal(TakeStageActChallengeRewardRsp.Types.Retcode.HasTake, service.ClaimAct(Uid, act).Retcode); Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(store.Read(Uid)));
        Assert.Contains(1u, service.Acts(Uid).ActDifficultyList.Single(a => a.ActId == 101 && a.Difficulty == 1).HasTakeChallengeNumIndex);
        Assert.DoesNotContain(service.Materials(Uid), m => m.Id == 30004);
        Assert.Contains(GetEquipmentDataRsp.Parser.ParseFrom(store.Inventory(Uid)!.Equipment).StigmataList, s => s.Id == 30004);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActClaimUpdatesClientCacheBeforeRewardCallbackAndSurvivesRetry(bool batch)
    {
        foreach (uint stage in new uint[] { 10101, 10102, 10105 })
            Assert.Equal(StageEndRsp.Types.Retcode.Succ, service.End(Uid, EndRequest(Begin(stage), 1, 0, 1, 2)).Retcode);
        var dispatcher = new GameDispatcher(new LobbyHandlers(store, new SqlitePlayerStore(database), new byte[32], clock).Create());
        var session = new GameSession(1, "test", 1); session.Authenticate(Uid);
        byte[] prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), 10002);
        var request = new TakeStageActChallengeRewardReq { ActId = 101, Difficulty = 1 };
        uint[] indices = batch ? [1, 2, 3] : [1];
        if (batch) request.ChallengeNumIndexList.Add(indices); else request.ChallengeNumIndex = 1;
        var packet = new GamePacket(prefix, 458, [], request.ToByteArray());
        Assert.Equal(DispatchStatus.InvalidState, dispatcher.Dispatch(new GameSession(2, "guest", 1), packet).Status);
        var replies = dispatcher.Dispatch(session, packet).Replies;
        Assert.All(replies, reply => Assert.Equal(Uid, reply.ClaimedUserId));
        // 9.1 reads the LevelModule act cache while handling 459. Replaying the
        // packets in order catches both a missing 457 and a 457 sent too late.
        var cached = new HashSet<uint>();
        foreach (var reply in replies)
        {
            if (reply.CommandId == 457)
                cached.UnionWith(GetStageActDifficultyRsp.Parser.ParseFrom(reply.Body).ActDifficultyList.Single(a => a.ActId == 101 && a.Difficulty == 1).HasTakeChallengeNumIndex);
            if (reply.CommandId == 459) Assert.Equal(indices, cached.Order().ToArray());
        }
        Assert.Equal(new ushort[] { 457, 459, 11, 42, 113, 25, 27 }, replies.Select(r => r.CommandId));
        Assert.Equal(indices, TakeStageActChallengeRewardRsp.Parser.ParseFrom(replies.Single(r => r.CommandId == 459).Body).SuccChallengeNumIndexList);
        var wallet = JsonSerializer.Serialize(store.Read(Uid));
        var materials = service.Materials(Uid).Select(m => m.ToByteArray()).ToArray();
        // Retry after reopening persistence heals a stale cache without granting again.
        var reopened = new LobbyStore(database);
        dispatcher = new(new LobbyHandlers(reopened, new SqlitePlayerStore(database), new byte[32], clock).Create());
        var retry = dispatcher.Dispatch(session, packet).Replies;
        Assert.Equal(new ushort[] { 457, 459 }, retry.Select(r => r.CommandId));
        Assert.Equal(indices, GetStageActDifficultyRsp.Parser.ParseFrom(retry[0].Body).ActDifficultyList.Single(a => a.ActId == 101 && a.Difficulty == 1).HasTakeChallengeNumIndex);
        var duplicate = TakeStageActChallengeRewardRsp.Parser.ParseFrom(retry[1].Body);
        Assert.Equal(TakeStageActChallengeRewardRsp.Types.Retcode.HasTake, duplicate.Retcode); Assert.Empty(duplicate.RewardList);
        Assert.Equal(wallet, JsonSerializer.Serialize(reopened.Read(Uid)));
        Assert.Equal(materials, new CampaignService(reopened, clock).Materials(Uid).Select(m => m.ToByteArray()).ToArray());
        Assert.Empty(new CampaignService(reopened, clock).Acts(10002).ActDifficultyList.Single(a => a.ActId == 101 && a.Difficulty == 1).HasTakeChallengeNumIndex);
        request.Difficulty = 99;
        Assert.Single(dispatcher.Dispatch(session, new(prefix, 458, [], request.ToByteArray())).Replies);
    }

    [Fact]
    public void StaminaRecoversUsingServerTimeAndExpiredRunsCannotSettle()
    {
        var begin = Begin(); clock.Value = clock.Value.AddSeconds(720);
        Assert.Equal(76u, service.Refresh(Uid).Stamina);
        clock.Value = clock.Value.AddDays(1); Assert.Equal(80u, service.Refresh(Uid).Stamina);
        Assert.Equal(StageEndRsp.Types.Retcode.StageError, service.End(Uid, EndRequest(begin)).Retcode);
        Assert.NotEqual(begin.SignKey, Begin().SignKey);
    }
    [Fact]
    public void TransactionFailureRollsBackWalletCampaignAndReceiptTogether()
    {
        Assert.Throws<InvalidOperationException>(() => store.Campaign<int>(Uid, tx => {
            tx.Lobby = tx.Lobby with { Scoin = 999 }; tx.Campaign.ClaimedMissions.Add(10001);
            tx.SaveReceipt("failed", [1, 2, 3]); throw new InvalidOperationException("injected persistence failure"); }));
        Assert.Equal(0u, store.Read(Uid).Scoin);
        Assert.False(store.Campaign(Uid, tx => tx.Campaign.ClaimedMissions.Contains(10001)));
        Assert.Null(store.Campaign(Uid, tx => tx.Receipt("failed")));
    }
    [Fact]
    public void DispatchUsesAuthenticatedIdentityAndPushesCommittedSnapshots()
    {
        var dispatcher = new GameDispatcher(new LobbyHandlers(store, new SqlitePlayerStore(database), new byte[32], clock).Create());
        var session = new GameSession(1, "test", 1); var prefix = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head); BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), 10002);
        var packet = new GamePacket(prefix, 43, [], new StageBeginReq { StageId = 10101, AvatarIdList = { 101 } }.ToByteArray());
        Assert.Equal(DispatchStatus.InvalidState, dispatcher.Dispatch(session, packet).Status); session.Authenticate(Uid);
        var result = dispatcher.Dispatch(session, packet); Assert.Equal(6, result.Replies.Count);
        Assert.All(result.Replies, p => Assert.Equal(Uid, p.ClaimedUserId)); Assert.Equal(74u, store.Read(Uid).Stamina); Assert.Equal(80u, store.Read(10002).Stamina);
        Assert.Equal(74u, GetMainDataRsp.Parser.ParseFrom(result.Replies.Single(p => p.CommandId == 11).Body).Stamina);
        foreach (var id in new ushort[] { 121, 476, 502, 813, 6706, 4167, 3460 })
            Assert.Equal(DispatchStatus.Handled, dispatcher.Dispatch(session, new(prefix, id, [], [])).Status);
    }
    public void Dispose() => directory.Dispose();
}
