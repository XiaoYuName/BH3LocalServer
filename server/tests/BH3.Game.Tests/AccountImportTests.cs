using System.Buffers.Binary;
using System.Text.Json;
using BH3.Game.Campaign;
using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Players;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class AccountImportTests : IDisposable
{
    private readonly TestDirectory temp = new();
    private readonly SqliteConnectionFactory factory;
    private readonly LobbyStore store;
    private readonly uint now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public AccountImportTests()
    {
        factory = new(Path.Combine(temp.Path, "import.db")); SchemaMigrator.Initialize(factory); store = new(factory);
        foreach (uint uid in new[] { 10001u, 10002u })
            store.EnsurePlayer(uid, "local-captain", new(4, 73, 101, 20001, 59101, 123, [11, 12], 17, 20250, 75, 4, 1, now));
        store.Campaign(10001, tx =>
        {
            tx.Campaign.Team = [101]; tx.Campaign.Stages[10101] = new() { Wins = 2, Entries = 3 };
            tx.Campaign.ClaimedActRewards.Add("101:1"); tx.Campaign.ClaimedMissions.Add(97001);
            tx.Campaign.Materials[3000] = 41; tx.SaveReceipt("old-receipt", [1, 2, 3]); return true;
        });
        store.WriteClient(10001, new(1, 2, [7, 8]));
    }
    private string Fixture(bool full = true, bool duplicate = false, bool badReference = false)
    {
        var a = new Avatar { AvatarId = 101, Level = 80, Exp = 1325, Star = 5, WeaponUniqueId = badReference ? 900u : 7,
            StigmataUniqueId1 = 8, DressId = 59101, SkillList = { new AvatarSkill { SkillId = 11 } } };
        // Unknown field 100 must survive import, query, persistence and settlement.
        a = Avatar.Parser.ParseFrom(a.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x7b }).ToArray());
        var roster = new GetAvatarDataRsp { Retcode = GetAvatarDataRsp.Types.Retcode.Succ, IsAll = full, AvatarList = { a,
            new Avatar { AvatarId = 102, Level = 1, Star = 3, WeaponUniqueId = 7 }, new Avatar { AvatarId = 103, Level = 1, Star = 0, WeaponUniqueId = 7 } } };
        if (duplicate) roster.AvatarList.Add(a.Clone());
        var equipment = new GetEquipmentDataRsp { Retcode = GetEquipmentDataRsp.Types.Retcode.Succ, IsAll = full,
            WeaponList = { new Weapon { UniqueId = 7, Id = 20009, Level = 65 } },
            StigmataList = { new Stigmata { UniqueId = 8, Id = 30009, Level = 50, RuneList = { new StigmataRune { RuneId = 1, StrengthPercent = 100 } } } },
            MaterialList = { new Material { Id = 3000, Num = 999 } } };
        object Row(int command, IMessage message, int seconds) => new { CommandId = command, BodyBase64 = Convert.ToBase64String(message.ToByteArray()), Time = DateTimeOffset.FromUnixTimeSeconds(now + seconds) };
        var responses = new[] {
            Row(11, new GetMainDataRsp { Retcode = GetMainDataRsp.Types.Retcode.Succ, IsAll = true, Level = 88, Exp = 1000, Hcoin = 57, Scoin = 999, Stamina = 587 }, 0),
            Row(27, equipment, 1), Row(25, roster, 2),
            Row(25, new GetAvatarDataRsp { Retcode = GetAvatarDataRsp.Types.Retcode.Succ, AvatarList = { new Avatar { AvatarId = 102, Level = 2, Star = 3, WeaponUniqueId = 7 } } }, 3)
        };
        string path = Path.Combine(temp.Path, Guid.NewGuid()+".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Format = "BH3Capture.AccountCopy", FormatVersion = 1, ClientVersion = "崩坏3 9.1.0",
            Accounts = new[] { new { SourceUid = 17001, TransportComplete = false, CoreSnapshotComplete = false, Responses = responses } } }));
        return path;
    }
    private AccountCopyPlan Plan() => AccountCopyImport.Read(Fixture(), true);
    private GameDispatcher Dispatcher() => new(new LobbyHandlers(new LobbyStore(factory), new SqlitePlayerStore(factory), new byte[32]).Create());
    private GamePacket Query(ushort command, IMessage request, uint uid = 10001)
    {
        var session = new GameSession(1, "fixture", 1); session.Authenticate(uid);
        byte[] prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head);
        return Assert.Single(Dispatcher().Dispatch(session, new(prefix, command, [], request.ToByteArray())).Replies);
    }
    [Fact]
    public void ImportPreservesWalletIdentityProgressMaterialsReceiptsAndUnknownFieldsAcrossRestart()
    {
        var plan = Plan(); var before = store.Read(10001);
        string campaign = store.Campaign(10001, tx => JsonSerializer.Serialize(tx.Campaign));
        var result = AccountCopyImport.Apply(store, 10001, plan);
        Assert.Equal(3, result.Avatars); Assert.False(result.AlreadyImported); Assert.True(result.BalancesPreserved);
        var saved = new LobbyStore(factory).Read(10001);
        Assert.Equal(88u, saved.Level); Assert.Equal(1000u, saved.Exp);
        Assert.Equal(before.Hcoin, saved.Hcoin); Assert.Equal(before.Scoin, saved.Scoin); Assert.Equal(before.Stamina, saved.Stamina);
        Assert.Equal(before.RegisteredAt, saved.RegisteredAt); Assert.Equal(before.CompletedGuides, saved.CompletedGuides);
        Assert.Equal(campaign, store.Campaign(10001, tx => JsonSerializer.Serialize(tx.Campaign)));
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Campaign(10001, tx => tx.Receipt("old-receipt")));
        Assert.Equal(new byte[] { 7, 8 }, Assert.Single(store.ReadClient(10001, 1, 2)).Data);
        Assert.Equal("local-captain", new SqlitePlayerStore(factory).Find(10001)!.Nickname);
        var roster = GetAvatarDataRsp.Parser.ParseFrom(Query(24, new GetAvatarDataReq { AvatarIdList = { 0 } }).Body);
        Assert.True(roster.IsAll); Assert.Equal(3, roster.AvatarList.Count); Assert.Equal(2u, roster.AvatarList.Single(a => a.AvatarId == 102).Level);
        Assert.Equal(plan.Avatars.AvatarList[0].ToByteArray(), roster.AvatarList[0].ToByteArray());
        var equipment = GetEquipmentDataRsp.Parser.ParseFrom(Query(26, new GetEquipmentDataReq()).Body);
        Assert.Equal(65u, Assert.Single(equipment.WeaponList).Level); Assert.Equal(50u, Assert.Single(equipment.StigmataList).Level);
        Assert.Equal(plan.Equipment.StigmataList[0].ToByteArray(), equipment.StigmataList[0].ToByteArray());
        Assert.Equal(41u, Assert.Single(equipment.MaterialList).Num);
        Assert.True(AccountCopyImport.Apply(store, 10001, plan).AlreadyImported);
        Assert.Equal(saved, store.Read(10001) with { CompletedGuides = saved.CompletedGuides });
        Assert.Null(store.Inventory(10002)); Assert.Equal(4u, store.Read(10002).Level);
    }
    [Fact]
    public void SelectedQueriesReturnOnlyRequestedEntitiesWithoutZeroPlaceholders()
    {
        AccountCopyImport.Apply(store, 10001, Plan());
        var a = GetAvatarDataRsp.Parser.ParseFrom(Query(24, new GetAvatarDataReq { AvatarIdList = { 102 } }).Body);
        Assert.False(a.IsAll); Assert.Equal(102u, Assert.Single(a.AvatarList).AvatarId);
        var e = GetEquipmentDataRsp.Parser.ParseFrom(Query(26, new GetEquipmentDataReq { WeaponUniqueIdList = { 7 }, StigmataUniqueIdList = { 999 }, MaterialIdList = { 999 }, MechaUniqueIdList = { 999 } }).Body);
        Assert.False(e.IsAll); Assert.Single(e.WeaponList); Assert.Empty(e.StigmataList); Assert.Empty(e.MaterialList);
        Assert.Single(GetAvatarDataRsp.Parser.ParseFrom(Query(24, new GetAvatarDataReq(), 10002).Body).AvatarList);
    }
    [Fact]
    public void ThreeMemberTeamSettlesSelectedAvatarExperienceOnceAndRetainsCapturedMaxLevel()
    {
        AccountCopyImport.Apply(store, 10001, Plan()); var game = new CampaignService(store, TimeProvider.System);
        game.SetTeam(10001, new UpdateAvatarTeamNotify { Team = new AvatarTeam { StageType = 1, AvatarIdList = { 101, 102 } } });
        Assert.Equal(new uint[] { 101, 102 }, new CampaignService(new LobbyStore(factory), TimeProvider.System).Team(10001));
        var begin = game.Begin(10001, new StageBeginReq { StageId = 10101, AvatarIdList = { 101, 102 } });
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, begin.Retcode);
        var request = new StageEndReq { Body = new StageEndReqBody { StageId = 10101, EndStatus = StageEndStatus.StageWin }.ToByteString(), Sign = "fixture" };
        Assert.Equal(StageEndRsp.Types.Retcode.Succ, game.End(10001, request).Retcode);
        byte[] first = store.Inventory(10001)!.Avatars;
        Assert.Equal(StageEndRsp.Types.Retcode.Succ, game.End(10001, request).Retcode); Assert.Equal(first, store.Inventory(10001)!.Avatars);
        var avatars = GetAvatarDataRsp.Parser.ParseFrom(first).AvatarList;
        Assert.Equal(80u, avatars[0].Level); Assert.Equal(1325u, avatars[0].Exp);
        Assert.True(avatars[1].Exp > 0 || avatars[1].Level > 2); Assert.Equal(0u, avatars[2].Exp); Assert.Equal(1u, avatars[2].Level);
        game.SetTeam(10001, new UpdateAvatarTeamNotify { Team = new AvatarTeam { StageType = 1, AvatarIdList = { 101, 102, 103 } } });
        Assert.Equal(3, game.Team(10001).Length);
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ, game.Begin(10001, new StageBeginReq { StageId = 10101, AvatarIdList = { 101, 102, 103 } }).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.AvatarError, game.Begin(10001, new StageBeginReq { StageId = 10101, AvatarIdList = { 999 } }).Retcode);
        Assert.Equal(StageBeginRsp.Types.Retcode.AvatarNumError, game.Begin(10001, new StageBeginReq { StageId = 10101, AvatarIdList = { 101, 101 } }).Retcode);
    }
    [Fact]
    public void PartialCaptureRequiresExplicitChoiceAndStillRequiresFullLists()
    {
        Assert.Throws<InvalidDataException>(() => AccountCopyImport.Read(Fixture(), false));
        Assert.Throws<InvalidDataException>(() => AccountCopyImport.Read(Fixture(full: false), true));
        Assert.Null(store.Inventory(10001));
    }
    [Fact]
    public void DuplicateIdentityOrDanglingEquipmentCannotBeImported()
    {
        Assert.Throws<InvalidDataException>(() => AccountCopyImport.Read(Fixture(duplicate: true), true));
        Assert.Throws<InvalidDataException>(() => AccountCopyImport.Read(Fixture(badReference: true), true));
    }
    [Fact]
    public void ActiveStageAndDifferentSourceRejectWithoutChangingInventoryOrPlayerState()
    {
        var plan = Plan(); var game = new CampaignService(store, TimeProvider.System);
        game.Begin(10001, new StageBeginReq { StageId = 10101, AvatarIdList = { 101 } });
        string before = JsonSerializer.Serialize(store.Read(10001));
        Assert.Throws<InvalidDataException>(() => AccountCopyImport.Apply(store, 10001, plan));
        Assert.Equal(before, JsonSerializer.Serialize(store.Read(10001))); Assert.Null(store.Inventory(10001));
        store.Campaign(10001, tx => { tx.Campaign.Run = null; return true; });
        AccountCopyImport.Apply(store, 10001, plan, syncLevel: false); Assert.Equal(4u, store.Read(10001).Level);
        byte[] original = store.Inventory(10001)!.Avatars;
        Assert.Throws<InvalidDataException>(() => AccountCopyImport.Apply(store, 10001, plan with { SourceSha256 = new string('a',64) }));
        Assert.Equal(original, store.Inventory(10001)!.Avatars);
        Assert.Throws<InvalidOperationException>(() => AccountCopyImport.Apply(store, 99999, plan));
    }
    public void Dispose() => temp.Dispose();
}
