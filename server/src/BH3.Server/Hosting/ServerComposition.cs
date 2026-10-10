using BH3.Game.Messaging;
using BH3.Game.Lobby;
using BH3.Game.Players;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Server.Configuration;

namespace BH3.Server.Hosting;

public sealed class ServerComposition
{
    public SqliteConnectionFactory Database { get; }
    public SessionRegistry Sessions { get; }
    public GameDispatcher Dispatcher { get; }
    public LobbyHandlers Lobby { get; }
    public BH3.Game.Operations.GmService Gm { get; }
    public ServerComposition(ServerConfig config, byte[]? authenticationKey = null)
    {
        Database = new(config.ResolveDatabase()); Sessions = new(config.MaxSessions, TimeSpan.FromSeconds(config.SessionIdleSeconds));
        string? encoded = Environment.GetEnvironmentVariable("BH3_LOCAL_AUTH_KEY");
        byte[] key = authenticationKey ?? (encoded is null ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(encoded));
        if (key.Length != 32) throw new ArgumentException("BH3_LOCAL_AUTH_KEY must encode 32 bytes.");
        Lobby = new(new(Database), new SqlitePlayerStore(Database), key);
        Gm = new(new(Database));
        Dispatcher = new(config.TransportMode == "kcp" ? Lobby.Create() : []);
    }
    public PlayerService Players => new(new SqlitePlayerStore(Database));
    public void Initialize() => SchemaMigrator.Initialize(Database);
}
