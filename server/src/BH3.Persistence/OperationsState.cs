namespace BH3.Persistence;

public sealed record GrantItem(string Kind, uint Id, uint Num = 1, uint Level = 1);
public sealed record PoolEntry(GrantItem Item, uint Weight, bool Rare);
public sealed record LocalPool(int Type, string Name, bool Enabled, uint Cost, uint Ticket, uint Pity, PoolEntry[] Entries,
    uint BeginTime = 1, uint EndTime = 2147483647);
public sealed record LocalShop(uint Id, string Name, bool Enabled, uint[] Goods, bool DailyRefresh = false);
public sealed record DrawLog(int Type, uint Time, uint Id, uint Num, bool Rare);
public sealed record OperationLog(uint Time, string Action, string Description);
public sealed class OperationsState
{
    public uint Revision { get; set; }
    public uint NextMailId { get; set; } = 1;
    public List<byte[]> Mails { get; set; } = [];
    public List<LocalPool>? Pools { get; set; }
    public uint DisplayVersion { get; set; }
    public List<LocalShop>? Shops { get; set; }
    public Dictionary<string, uint> ShopPurchases { get; set; } = [];
    public uint ShopDay { get; set; }
    public uint MallVersion { get; set; }
    public uint EconomyVersion { get; set; }
    public uint Mcoin { get; set; }
    public Dictionary<string,uint> ProductPurchases { get; set; } = [];
    public uint? TotalPayHcoin { get; set; }
    public HashSet<uint> VipRewardClaims { get; set; } = [];
    public HashSet<uint> OwnedMedals { get; set; } = [];
    public HashSet<uint> OwnedFrames { get; set; } = [];
    public uint CardExpireTime { get; set; }
    public uint CardLastRewardDay { get; set; }
    public uint CardRewardDays { get; set; }
    public uint CardBonusClaimed { get; set; }
    public Dictionary<uint,GrantItem[]> ShopRewardOverrides { get; set; } = [];
    public HashSet<uint> OwnedDresses { get; set; } = [];
    public Dictionary<int, uint> Pity { get; set; } = [];
    public Dictionary<int, uint> TenPullPity { get; set; } = [];
    public Dictionary<int, uint> Draws { get; set; } = [];
    public uint GachaRandom { get; set; }
    public List<DrawLog> DrawLog { get; set; } = [];
    public List<OperationLog> Audit { get; set; } = [];
}

public sealed partial class LobbyStore
{
    public (uint Uid, string Nickname)[] Accounts()
    {
        using var c = factory.Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT p.uid,p.nickname FROM player_profile p JOIN player_lobby l ON p.uid=l.uid ORDER BY p.uid;";
        using var r=q.ExecuteReader(); var result = new List<(uint,string)>();
        while (r.Read()) result.Add((checked((uint)r.GetInt64(0)),r.GetString(1)));
        return result.ToArray();
    }
}
