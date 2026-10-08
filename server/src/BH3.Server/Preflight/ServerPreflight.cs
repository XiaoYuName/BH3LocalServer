using BH3.Persistence;
using BH3.Server.Configuration;

namespace BH3.Server.Preflight;

public static class ServerPreflight
{
    public static object Check(ServerConfig config)
    {
        config.Validate();
        int? version = null;
        if (File.Exists(config.ResolveDatabase()))
        {
            using var connection = new SqliteConnectionFactory(config.ResolveDatabase()).Open(readOnly: true);
            version = SchemaMigrator.Check(connection);
        }
        return new { configurationValid = true, clientVersion = config.ClientVersion, gamePort = config.GamePort, healthPort = config.HealthPort,
            database = config.ResolveDatabase(), existingSchema = version, targetSchema = SchemaMigrator.CurrentVersion,
            transport = config.TransportMode, gameplayReady = false, portsProbed = false };
    }
}
