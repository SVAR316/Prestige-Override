using System.Text.Json.Serialization;

namespace PrestigeConditionsOverrideServer.Config;

public sealed class PrestigeNonQuestConfig
{
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>Skill name → minimum level (Charisma, Strength, Endurance). Omit or empty to remove all skill requirements.</summary>
    [JsonPropertyName("skills")]
    public Dictionary<string, int>? Skills { get; set; }

    /// <summary>HideoutAreas enum value as string → minimum level.</summary>
    [JsonPropertyName("hideout")]
    public Dictionary<string, int>? Hideout { get; set; }

    [JsonPropertyName("roubles")]
    public long Roubles { get; set; }
}
