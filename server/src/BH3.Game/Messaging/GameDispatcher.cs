using BH3.Game.Sessions;
using BH3.Protocol;

namespace BH3.Game.Messaging;

public enum DispatchStatus { Handled, Unsupported, InvalidState, Closed }
public sealed record DispatchResult(DispatchStatus Status, IReadOnlyList<GamePacket> Replies);
public interface IGameMessageHandler
{
    ushort CommandId { get; }
    SessionState RequiredState { get; }
    bool Accepts(SessionState state) => state == RequiredState;
    IReadOnlyList<GamePacket> Handle(GameSession session, GamePacket packet);
}

public sealed class GameDispatcher
{
    private readonly Dictionary<ushort, IGameMessageHandler> handlers;
    public GameDispatcher(IEnumerable<IGameMessageHandler> handlers) => this.handlers = handlers.ToDictionary(h => h.CommandId);
    public ushort[] RegisteredCommands => handlers.Keys.Order().ToArray();
    public DispatchResult Dispatch(GameSession session, GamePacket packet) => session.Execute(() =>
    {
        if (session.State == SessionState.Closed) return new DispatchResult(DispatchStatus.Closed, []);
        if (!handlers.TryGetValue(packet.CommandId, out var handler)) return new DispatchResult(DispatchStatus.Unsupported, []);
        if (!handler.Accepts(session.State)) return new DispatchResult(DispatchStatus.InvalidState, []);
        // ClaimedUserId belongs to the wire. Authorization uses session.PlayerId only.
        return new DispatchResult(DispatchStatus.Handled, handler.Handle(session, packet));
    });
}
