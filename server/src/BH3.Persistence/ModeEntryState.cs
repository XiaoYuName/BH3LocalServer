namespace BH3.Persistence;

// Scene entry reservations must never replace an outstanding combat reservation.
public sealed class ModeEntryState
{
    public byte[]? GodWarLobbyBegin { get; set; }
    public uint GodWarLobbyEnteredAt { get; set; }
    public Dictionary<uint,uint> MirageProgress { get; set; } = [];
}
