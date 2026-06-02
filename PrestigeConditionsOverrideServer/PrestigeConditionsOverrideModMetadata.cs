using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace PrestigeConditionsOverrideServer;

public sealed record PrestigeConditionsOverrideModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.zloymolodoy.prestigeconditionoverride";
    public override string Name { get; init; } = "Prestige Conditions Override";
    public override string Author { get; init; } = "ZloyMolodoy";
    public override List<string>? Contributors { get; init; } = [];
    public override Version Version { get; init; } = new("1.0.1");
    public override Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; } = [];
    public override Dictionary<string, Range>? ModDependencies { get; init; } =
        new()
        {
            ["wtf.archangel.contentbackportprestiges"] = new Range("~1.0.1"),
            ["com.wtt.contentbackport"] = new Range("~1.0.1"),
            ["com.wtt.commonlib"] = new Range("~2.0.9"),
        };
    public override string? Url { get; init; } = null;
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}
