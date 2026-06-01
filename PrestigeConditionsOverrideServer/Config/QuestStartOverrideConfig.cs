using System.Text.Json.Serialization;

namespace PrestigeConditionsOverrideServer.Config;

public sealed class QuestStartOverrideConfig
{
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>Expected player prestige level (0 for P1, 1 for P2, …). Optional.</summary>
    [JsonPropertyName("prestigeLevel")]
    public int? PrestigeLevel { get; set; }
}
