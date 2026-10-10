using System.Security.Cryptography;
using System.Text.Json;
using BH3.Game.Campaign;
using BH3.Persistence;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class EmptyRewardTests : IDisposable
{
    private readonly TestDirectory directory = new();
    private readonly SqliteConnectionFactory database;
    private readonly LobbyStore store;
    private readonly CampaignService service;
    private const uint Uid = 10001;

    public EmptyRewardTests()
    {
        database = new(Path.Combine(directory.Path, "rewards.db"));
        SchemaMigrator.Initialize(database); store = new(database); service = new(store, TimeProvider.System);
        store.EnsurePlayer(Uid, "captain", new(1, 80, 101, 20001, 59101, 1, [1]));
    }

    private StageEndReq Start(int status = 1, params uint[] challenges)
    {
        var begin = service.Begin(Uid, new() { StageId = 10101, AvatarIdList = { 101 } });
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, begin.Retcode);
        return new() { Sign = begin.SignKey, Body = new StageEndReqBody {
            StageId = 10101, EndStatus = (StageEndStatus)status, ChallengeIndexList = { challenges }
        }.ToByteString() };
    }

    [Fact]
    public void FirstWinAndReplayKeepEarnedRewardsWithoutEmptyBonusDialog()
    {
        var request = Start(1, 0, 1, 2);
        var first = service.End(Uid, request);
        Assert.Equal(750u, first.ScoinReward); Assert.Equal(6u, first.PlayerExpReward);
        Assert.Equal(3, first.ChallengeList.Count); Assert.Equal(15u, store.Read(Uid).Hcoin);
        Assert.Null(StageEndRsp.Parser.ParseFrom(first.ToByteArray()).LineEnhanceRewardData);
        var wallet = JsonSerializer.Serialize(store.Read(Uid));
        var restarted = new CampaignService(new LobbyStore(database), TimeProvider.System);
        Assert.Equal(first.ToByteArray(), restarted.End(Uid, request).ToByteArray());
        Assert.Equal(wallet, JsonSerializer.Serialize(store.Read(Uid)));
        var replay = restarted.End(Uid, Start(1, 0, 1, 2));
        Assert.Null(replay.LineEnhanceRewardData); Assert.Empty(replay.ChallengeList);
        Assert.Equal(750u, replay.ScoinReward); Assert.Equal(1500u, store.Read(Uid).Scoin);
        Assert.Equal(15u, store.Read(Uid).Hcoin);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void UnsuccessfulRunHasNoRewardDialogOrGrant(int status)
    {
        var result = service.End(Uid, Start(status));
        Assert.Equal(StageEndRsp.Types.Retcode.Succ, result.Retcode);
        Assert.Null(result.LineEnhanceRewardData); Assert.Empty(result.ChallengeList);
        Assert.Equal(0u, result.ScoinReward); Assert.Equal(0u, store.Read(Uid).Scoin);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OldEmptyReceiptIsPresentedWithoutDialogAndCannotRepeatGrant(bool nextRun)
    {
        var request = Start(1, 0, 1, 2);
        var earned = service.End(Uid, request);
        earned.LineEnhanceRewardData = new RewardData(); // Exact empty field 19 sent by 1.3.9.
        byte[] legacy = earned.ToByteArray();
        string fingerprint = Convert.ToHexString(SHA256.HashData(request.ToByteArray()));
        ReplaceFixtureReceipt(fingerprint, legacy);
        if (nextRun) Start();
        var lobby = JsonSerializer.Serialize(store.Read(Uid));
        var campaign = JsonSerializer.Serialize(store.Campaign(Uid, tx => tx.Campaign));
        var restarted = new CampaignService(new LobbyStore(database), TimeProvider.System);
        var result = restarted.End(Uid, request);
        Assert.Null(result.LineEnhanceRewardData);
        Assert.Equal(earned.ScoinReward, result.ScoinReward); Assert.Equal(earned.ChallengeList, result.ChallengeList);
        Assert.Equal(lobby, JsonSerializer.Serialize(store.Read(Uid)));
        Assert.Equal(campaign, JsonSerializer.Serialize(store.Campaign(Uid, tx => tx.Campaign)));
        Assert.Equal(legacy, store.Campaign(Uid, tx => tx.Receipt(fingerprint)));
        Assert.Equal(result.ToByteArray(), restarted.End(Uid, request).ToByteArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NonemptyLegacyBonusIncludingUnknownFieldsIsPreserved(bool unknown)
    {
        var request = Start(); var result = service.End(Uid, request);
        result.LineEnhanceRewardData = unknown ? RewardData.Parser.ParseFrom(Convert.FromHexString("A00601")) : new RewardData { Scoin = 10 };
        byte[] original = result.ToByteArray();
        string fingerprint = Convert.ToHexString(SHA256.HashData(request.ToByteArray()));
        ReplaceFixtureReceipt(fingerprint, original);
        Assert.Equal(original, service.End(Uid, request).ToByteArray());
    }

    private void ReplaceFixtureReceipt(string fingerprint, byte[] response)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE stage_receipt SET response=$response WHERE uid=$uid AND fingerprint=$fingerprint";
        command.Parameters.AddWithValue("$response", response); command.Parameters.AddWithValue("$uid", Uid);
        command.Parameters.AddWithValue("$fingerprint", fingerprint); Assert.Equal(1, command.ExecuteNonQuery());
    }

    public void Dispose() => directory.Dispose();
}
