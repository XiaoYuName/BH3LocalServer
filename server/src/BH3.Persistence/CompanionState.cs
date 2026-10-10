namespace BH3.Persistence;

public sealed class CompanionState
{
    public uint Level { get; set; } = 1;
    public uint Star { get; set; } = 1;
    public uint Exp { get; set; }
    public Dictionary<uint,uint> Skills { get; set; } = [];
    public HashSet<uint> MaskedSkills { get; set; } = [];
}
