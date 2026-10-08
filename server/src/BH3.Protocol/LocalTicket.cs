using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BH3.Protocol;

public sealed record LocalIdentity(uint Uid, string Name, long Expires);

public static class LocalTicket
{
    public static string Issue(byte[] key, uint uid, string name, DateTimeOffset now)
    {
        if (key.Length != 32 || uid == 0 || string.IsNullOrWhiteSpace(name) || name.Length > 32 || name.Any(char.IsControl))
            throw new ArgumentException("Invalid local identity/key.");
        string body = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new LocalIdentity(uid, name, now.AddHours(1).ToUnixTimeSeconds())));
        return body + "." + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)));
    }
    public static bool TryValidate(byte[] key, string ticket, DateTimeOffset now, out LocalIdentity? identity)
    {
        identity = null;
        if (key.Length != 32 || ticket.Length > 2048) return false;
        var parts = ticket.Split('.'); if (parts.Length != 2) return false;
        try
        {
            byte[] mac = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0])))) return false;
            var value = JsonSerializer.Deserialize<LocalIdentity>(Convert.FromBase64String(parts[0]));
            if (value is null || value.Uid == 0 || value.Expires <= now.ToUnixTimeSeconds() || value.Expires > now.AddHours(1).ToUnixTimeSeconds()
                || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 32 || value.Name.Any(char.IsControl)) return false;
            identity = value; return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return false; }
    }
}
