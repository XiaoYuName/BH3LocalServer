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
    public void FullChapterUnlocksCompletesAndPersistsMissionsWalletAndStars()
    {
        var catalog = CampaignCatalog.Default;
        Assert.Equal(15, service.Stages(Uid, new()).StageList.Count);
        Assert.All(service.Stages(Uid, new()).StageList, s => Assert.False(s.IsDone));
        foreach (var definition in catalog.Stages.OrderBy(s => s.Id))
        {
            var begin = Begin(definition.Id); Assert.Equal(StageBeginRsp.Types.Retcode.Succ, begin.Retcode);
            var request = EndRequest(begin, 1, 0, 1, 2); var end = service.End(Uid, request);
            Assert.Equal(StageEndRsp.Types.Retcode.Succ, end.Retcode); Assert.True(end.IsFirstWin);
            Assert.Equal(definition.Scoin, end.ScoinReward); Assert.Equal(definition.Exp, end.PlayerExpReward);
            Assert.Equal(definition.AvatarExp, end.AvatarExpReward); Assert.Equal(3, end.ChallengeList.Count);
            foreach (var mission in service.Missions(Uid).MissionList.Where(m => (int)m.Status == 2).ToArray())
                Assert.Equal(GetMissionRewardRsp.Types.Retcode.Succ, service.Claim(Uid, new() { MissionIdList = { mission.MissionId } }).Retcode);
        }
        service = new(new LobbyStore(database), clock);
        var saved = service.Stages(Uid, new()); Assert.All(saved.StageList, s => Assert.True(s.IsDone));
        Assert.Contains(1u, saved.FinishedChapterList); Assert.All(saved.StageList, s => Assert.Equal(3, s.ChallengeIndexList.Count));
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
        Assert.Contains(1u, service.Acts(Uid).ActDifficultyList.Single(a => a.ActId == 101).HasTakeChallengeNumIndex);
        Assert.Contains(service.Materials(Uid), m => m.Id == 30004 && m.Num == 1);
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
