using System.Text.Json;
using BH3.Protocol.Messages;

namespace BH3.Game.Campaign;

public sealed record StageDefinition(uint Id, uint Chapter, uint Act, uint Level, uint Cost, uint Exp, uint AvatarExp, uint Scoin, uint[] Previous, uint[] Challenges, string Lua, uint Difficulty = 1, uint[]? Trials = null);
public sealed record MissionDefinition(uint Id, uint Way, uint Target, uint Total, uint Reward, uint Step);
public sealed record ActDefinition(uint Id, uint[] Thresholds, uint[] Rewards, uint Difficulty = 1);
public sealed record ChapterDefinition(uint Id, uint Group, uint Site, uint Level, uint LastStage);
public sealed record TrialDefinition(uint Id, uint Avatar);
public sealed record PlotDefinition(uint Id, uint Stage, uint FirstDialog, uint LastDialog);
public sealed record ItemDefinition(uint Id, uint Level, uint Num);
public sealed record RewardDefinition(uint Id, uint Exp, uint Hcoin, uint Stamina, uint Scoin, ItemDefinition[] Items)
{
    public RewardData Message() => new() { Exp = Exp, Hcoin = Hcoin, Stamina = Stamina, Scoin = Scoin,
        ItemList = { Items.Select(i => new RewardItemData { Id = i.Id, Level = i.Level, Num = i.Num }) } };
}
public sealed record LevelDefinition(uint Level, uint Exp, uint Stamina, uint Bonus, uint AvatarLimit);
public sealed record AvatarLevelDefinition(uint Level, uint Exp);
public sealed record CampaignCatalog(StageDefinition[] Stages, MissionDefinition[] Missions, ActDefinition[] Acts,
    RewardDefinition[] Rewards, LevelDefinition[] Levels, AvatarLevelDefinition[] AvatarLevels, ChapterDefinition[] Chapters, TrialDefinition[] Trials, StageDefinition[] ExtraStages, PlotDefinition[] Plots)
{
    public static CampaignCatalog Default { get; } = Load();
    private static CampaignCatalog Load()
    {
        using var stream = typeof(CampaignCatalog).Assembly.GetManifestResourceStream("BH3.Game.Campaign.story.json")!;
        var result = JsonSerializer.Deserialize<CampaignCatalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (result.Stages.Length == 0 || result.Stages.Select(s => s.Id).Distinct().Count() != result.Stages.Length
            || result.Stages.Any(s => s.Previous.Any(p => result.Stages.All(v => v.Id != p)))
            || result.Acts.Select(a => (a.Id, a.Difficulty)).Distinct().Count() != result.Acts.Length)
            throw new InvalidDataException("Incomplete or duplicate story catalog.");
        return result;
    }
    public RewardData Reward(uint id) => id == 0 ? new() : Rewards.Single(r => r.Id == id).Message();
    public LevelDefinition Level(uint level) => Levels.Single(v => v.Level == Math.Clamp(level, 1u, (uint)Levels.Length));
}
