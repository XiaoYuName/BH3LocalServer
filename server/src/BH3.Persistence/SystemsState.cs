namespace BH3.Persistence;

public sealed record LocalActivity(uint Id, string Title, string Content, bool Enabled, uint BeginTime, uint EndTime,
    uint Weight = 100, uint Panel = 0, uint[]? Missions = null);
public sealed record LocalBattlePass(uint Schedule = 34, bool Enabled = true, uint BeginTime = 1,
    uint EndTime = 2145916800, uint WeeklyLimit = 10000, uint FreeExp = 1000);
public sealed class SystemsState
{
    public uint ActivityCatalogVersion { get; set; }
    public uint TreasureMisses { get; set; }
    public Dictionary<uint, byte[]> GrandKeys { get; set; } = [];
    public HashSet<uint> WikiClaims { get; set; } = [];
    public HashSet<uint> Photos { get; set; } = [];
    public HashSet<uint> PhonePendants { get; set; } = [];
    public List<LocalActivity>? Activities { get; set; }
    public LocalBattlePass Pass { get; set; } = new();
    public uint PassSchedule { get; set; }
    public uint PassLevel { get; set; } = 1;
    public uint PassExp { get; set; }
    public uint WeekExp { get; set; }
    public bool PhaseExpTaken { get; set; }
    public HashSet<uint> Tickets { get; set; } = [1];
    public Dictionary<uint,uint> PassClaims { get; set; } = [];
    public uint Day { get; set; }
    public uint Week { get; set; }
    public uint DailyDuty { get; set; }
    public uint WeeklyDuty { get; set; }
    public HashSet<uint> DailyDutyClaims { get; set; } = [];
    public HashSet<uint> WeeklyDutyClaims { get; set; } = [];
    public Dictionary<uint,uint> DailyProgress { get; set; } = [];
    public HashSet<uint> DailyClaims { get; set; } = [];
}
