namespace BH3.Persistence;

public sealed record BossRun(uint Boss, uint StartedAt, uint[] Avatars, bool Training, byte[] Response, uint[]? Elfs = null);
public sealed class BossProgress
{
    public uint BestScore { get; set; }
    public uint HighestLevel { get; set; }
    public uint[] LockedAvatars { get; set; } = [];
}
public sealed class ChallengeState
{
    public uint Cycle { get; set; }
    public uint ArenaRank { get; set; }
    public uint ExpiresAt { get; set; }
    public uint BossEntries { get; set; }
    public Dictionary<uint, BossProgress> Bosses { get; set; } = [];
    public BossRun? BossRun { get; set; }
    public string LastBossRequest { get; set; } = "";
    public byte[]? LastBossResponse { get; set; }
    public HashSet<uint> BossRewards { get; set; } = [];
    public uint ActiveSite { get; set; }
    public Dictionary<uint, Dictionary<uint, uint>> Floors { get; set; } = [];
    public Dictionary<uint, uint> SiteTimes { get; set; } = [];
    public HashSet<uint> AbyssRewards { get; set; } = [];
}
