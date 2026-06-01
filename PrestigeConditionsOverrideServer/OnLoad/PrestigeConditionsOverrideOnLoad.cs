using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Json;
using PrestigeConditionsOverrideServer.Config;

namespace PrestigeConditionsOverrideServer.OnLoad;

[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 10)]
public sealed class PrestigeConditionsOverrideOnLoad(
    ISptLogger<PrestigeConditionsOverrideOnLoad> logger,
    DatabaseService databaseService,
    ModHelper modHelper
) : IOnLoad
{
    private static readonly MongoId RoublesItemId = new("5449016a4bdc2d6f028b456f");

    private string ModPath { get; } = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

    public Task OnLoad()
    {
        var conditionsByPrestige = modHelper.GetJsonDataFromFile<Dictionary<string, PrestigeNonQuestConfig>>(
            ModPath,
            "db/prestige_non_quest_conditions.json"
        );

        var questBlacklist = modHelper.GetJsonDataFromFile<Dictionary<string, List<string>>>(
            ModPath,
            "db/quest_blacklist.json"
        );

        var questStartOverrides = modHelper.GetJsonDataFromFile<Dictionary<string, QuestStartOverrideConfig>>(
            ModPath,
            "db/quest_start_overrides.json"
        );

        var prestiges = databaseService.GetTemplates().Prestige?.Elements ?? [];
        var changesApplied = 0;

        foreach (var (prestigeId, config) in conditionsByPrestige)
        {
            var prestige = prestiges.FirstOrDefault(p => string.Equals(p.Id, prestigeId, StringComparison.Ordinal));
            if (prestige is null)
            {
                logger.Warning($"[PrestigeConditionsOverride] Prestige id not found in database: {prestigeId}");
                continue;
            }

            changesApplied += RemoveBlacklistedQuests(prestige, questBlacklist);

            if (config.Level <= 0 && config.Roubles <= 0 && config.Skills is null && config.Hideout is null)
            {
                logger.Warning(
                    $"[PrestigeConditionsOverride] Empty config for prestige {prestigeId} — skipping non-quest patch (check JSON property names)"
                );
                continue;
            }

            if (NonQuestConditionsAlreadyMatch(prestige, config))
            {
                continue;
            }

            logger.Info(
                $"[PrestigeConditionsOverride] Config for {prestigeId}: level={config.Level}, roubles={config.Roubles}, skills={config.Skills?.Count ?? 0}, hideout={config.Hideout?.Count ?? 0}"
            );
            changesApplied++;
            PatchNonQuestConditions(prestige, config);
        }

        changesApplied += PatchQuestStartConditions(questStartOverrides);

        if (changesApplied == 0)
        {
            logger.Info("[PrestigeConditionsOverride] All configured values already match the database — no patches applied.");
        }
        else
        {
            logger.Success($"[PrestigeConditionsOverride] Applied {changesApplied} override(s).");
        }

        return Task.CompletedTask;
    }

    private int PatchQuestStartConditions(Dictionary<string, QuestStartOverrideConfig> overrides)
    {
        var quests = databaseService.GetQuests();
        var changesApplied = 0;

        foreach (var (questId, config) in overrides)
        {
            if (config.Level <= 0)
            {
                logger.Warning(
                    $"[PrestigeConditionsOverride] Invalid start level for quest {questId} — skipping (level must be > 0)"
                );
                continue;
            }

            if (!quests.TryGetValue(new MongoId(questId), out var quest))
            {
                logger.Warning($"[PrestigeConditionsOverride] Quest not found in database: {questId}");
                continue;
            }

            var startConditions = quest.Conditions?.AvailableForStart;
            if (startConditions is null || startConditions.Count == 0)
            {
                logger.Warning(
                    $"[PrestigeConditionsOverride] Quest {questId} has no AvailableForStart conditions — skipping"
                );
                continue;
            }

            var levelCondition = startConditions.FirstOrDefault(c => c.ConditionType == "Level");
            if (levelCondition is null)
            {
                logger.Warning(
                    $"[PrestigeConditionsOverride] Quest {questId} has no Level start condition — skipping"
                );
                continue;
            }

            var prestigeCondition = startConditions.FirstOrDefault(c => c.ConditionType == "PrestigeLevel");
            if (QuestStartAlreadyMatches(levelCondition, prestigeCondition, config))
            {
                continue;
            }

            var previousLevel = levelCondition.Value;
            levelCondition.Value = config.Level;
            levelCondition.CompareMethod = ">=";

            var prestigePatched = false;
            if (config.PrestigeLevel.HasValue && prestigeCondition is not null)
            {
                prestigeCondition.Value = config.PrestigeLevel.Value;
                prestigeCondition.CompareMethod = "==";
                prestigePatched = true;
            }

            changesApplied++;
            logger.Info(
                $"[PrestigeConditionsOverride] Quest {questId} start level {previousLevel} -> {config.Level}"
                + (prestigePatched ? $", prestigeLevel=={config.PrestigeLevel!.Value}" : "")
            );
        }

        return changesApplied;
    }

    private int RemoveBlacklistedQuests(PrestigeElement prestige, Dictionary<string, List<string>> blacklist)
    {
        var removed = 0;

        foreach (var (questId, prestigeIds) in blacklist)
        {
            if (!prestigeIds.Contains(prestige.Id, StringComparer.Ordinal))
            {
                continue;
            }

            var toRemove = prestige
                .Conditions.Where(c =>
                    c.ConditionType == "Quest"
                    && c.Target is not null
                    && c.Target.IsItem
                    && string.Equals(c.Target.Item, questId, StringComparison.Ordinal)
                )
                .ToList();

            foreach (var condition in toRemove)
            {
                prestige.Conditions.Remove(condition);
                removed++;
                logger.Info(
                    $"[PrestigeConditionsOverride] Removed quest {questId} from prestige {prestige.Id} (condition {condition.Id})"
                );
            }
        }

        return removed;
    }

    private void PatchNonQuestConditions(PrestigeElement prestige, PrestigeNonQuestConfig config)
    {
        if (config.Level > 0)
        {
            var levelCondition = prestige.Conditions.FirstOrDefault(c => c.ConditionType == "Level");
            if (levelCondition is null || !MatchesIntValue(levelCondition, config.Level))
            {
                PatchLevel(prestige, config.Level);
            }
        }

        if (!SkillConditionsMatch(prestige, config.Skills ?? []))
        {
            PatchSkills(prestige, config.Skills);
        }

        if (!HideoutConditionsMatch(prestige, config.Hideout ?? []))
        {
            PatchHideout(prestige, config.Hideout);
        }

        if (config.Roubles > 0)
        {
            var roublesCondition = prestige.Conditions.FirstOrDefault(c =>
                c.ConditionType is "HasItem" or "Item"
                && c.Target is not null
                && TargetIncludesItem(c.Target, RoublesItemId)
            );
            if (roublesCondition is null || !MatchesNumericValue(roublesCondition, config.Roubles))
            {
                PatchRoubles(prestige, config.Roubles);
            }
        }
    }

    private void PatchLevel(PrestigeElement prestige, int level)
    {
        if (level <= 0)
        {
            return;
        }

        var condition = prestige.Conditions.FirstOrDefault(c => c.ConditionType == "Level");
        if (condition is not null && MatchesIntValue(condition, level))
        {
            return;
        }

        if (condition is null)
        {
            prestige.Conditions.Add(CreateBaseCondition("Level", NextIndex(prestige), c =>
            {
                c.Value = level;
                c.CompareMethod = ">=";
            }));
            logger.Info($"[PrestigeConditionsOverride] Added Level>={level} to prestige {prestige.Id}");
            return;
        }

        condition.Value = level;
        condition.CompareMethod = ">=";
    }

    private void PatchSkills(PrestigeElement prestige, Dictionary<string, int>? skills)
    {
        var desired = skills ?? [];
        var existing = prestige.Conditions.Where(c => c.ConditionType == "Skill").ToList();

        foreach (var condition in existing)
        {
            var skillName = GetTargetString(condition.Target);
            if (skillName is null || !desired.ContainsKey(skillName))
            {
                prestige.Conditions.Remove(condition);
                logger.Info(
                    $"[PrestigeConditionsOverride] Removed skill '{skillName ?? "?"}' from prestige {prestige.Id}"
                );
            }
        }

        foreach (var (skillName, value) in desired)
        {
            var condition = prestige.Conditions.FirstOrDefault(c =>
                c.ConditionType == "Skill" && GetTargetString(c.Target) == skillName
            );

            if (condition is not null && MatchesIntValue(condition, value))
            {
                continue;
            }

            if (condition is null)
            {
                prestige.Conditions.Add(CreateBaseCondition("Skill", NextIndex(prestige), c =>
                {
                    c.Target = new ListOrT<string>(null, skillName);
                    c.Value = value;
                    c.CompareMethod = ">=";
                }));
                logger.Info($"[PrestigeConditionsOverride] Added Skill {skillName}>={value} to prestige {prestige.Id}");
                continue;
            }

            condition.Value = value;
            condition.CompareMethod = ">=";
        }
    }

    private void PatchHideout(PrestigeElement prestige, Dictionary<string, int>? hideout)
    {
        var desired = hideout ?? [];
        var existing = prestige.Conditions.Where(c => c.ConditionType == "HideoutArea").ToList();

        foreach (var condition in existing)
        {
            var areaKey = condition.AreaType.HasValue ? ((int)condition.AreaType.Value).ToString() : null;
            if (areaKey is null || !desired.ContainsKey(areaKey))
            {
                prestige.Conditions.Remove(condition);
                logger.Info(
                    $"[PrestigeConditionsOverride] Removed hideout area '{areaKey ?? "?"}' from prestige {prestige.Id}"
                );
            }
        }

        foreach (var (areaKey, value) in desired)
        {
            if (!int.TryParse(areaKey, out var areaTypeInt) || !Enum.IsDefined(typeof(HideoutAreas), areaTypeInt))
            {
                logger.Warning(
                    $"[PrestigeConditionsOverride] Unknown hideout area key '{areaKey}' for prestige {prestige.Id}"
                );
                continue;
            }

            var areaType = (HideoutAreas)areaTypeInt;

            var condition = prestige.Conditions.FirstOrDefault(c =>
                c.ConditionType == "HideoutArea" && c.AreaType == areaType
            );

            if (condition is not null && MatchesIntValue(condition, value))
            {
                continue;
            }

            if (condition is null)
            {
                prestige.Conditions.Add(CreateBaseCondition("HideoutArea", NextIndex(prestige), c =>
                {
                    c.AreaType = areaType;
                    c.Value = value;
                    c.CompareMethod = ">=";
                }));
                logger.Info(
                    $"[PrestigeConditionsOverride] Added HideoutArea {areaType}>={value} to prestige {prestige.Id}"
                );
                continue;
            }

            condition.Value = value;
            condition.CompareMethod = ">=";
        }
    }

    private void PatchRoubles(PrestigeElement prestige, long roubles)
    {
        if (roubles <= 0)
        {
            return;
        }

        var condition = prestige.Conditions.FirstOrDefault(c =>
            c.ConditionType is "HasItem" or "Item"
            && c.Target is not null
            && TargetIncludesItem(c.Target, RoublesItemId)
        );

        if (condition is not null && MatchesNumericValue(condition, roubles))
        {
            return;
        }

        if (condition is null)
        {
            prestige.Conditions.Add(CreateBaseCondition("HasItem", NextIndex(prestige), c =>
            {
                c.Target = new ListOrT<string>([RoublesItemId.ToString()], null);
                c.Value = roubles;
                c.CompareMethod = ">=";
                c.MinDurability = 0;
                c.MaxDurability = 100;
                c.DogtagLevel = 0;
                c.OnlyFoundInRaid = false;
                c.IsEncoded = false;
            }));
            logger.Info($"[PrestigeConditionsOverride] Added roubles>={roubles} to prestige {prestige.Id}");
            return;
        }

        condition.ConditionType = "HasItem";
        condition.Value = roubles;
        condition.CompareMethod = ">=";
    }

    private static bool NonQuestConditionsAlreadyMatch(PrestigeElement prestige, PrestigeNonQuestConfig config)
    {
        if (config.Level > 0)
        {
            var levelCondition = prestige.Conditions.FirstOrDefault(c => c.ConditionType == "Level");
            if (levelCondition is null || !MatchesIntValue(levelCondition, config.Level))
            {
                return false;
            }
        }

        if (config.Roubles > 0)
        {
            var roublesCondition = prestige.Conditions.FirstOrDefault(c =>
                c.ConditionType is "HasItem" or "Item"
                && c.Target is not null
                && TargetIncludesItem(c.Target, RoublesItemId)
            );
            if (roublesCondition is null || !MatchesNumericValue(roublesCondition, config.Roubles))
            {
                return false;
            }
        }

        if (!SkillConditionsMatch(prestige, config.Skills ?? []))
        {
            return false;
        }

        return HideoutConditionsMatch(prestige, config.Hideout ?? []);
    }

    private static bool SkillConditionsMatch(PrestigeElement prestige, Dictionary<string, int> desired)
    {
        var existing = prestige.Conditions
            .Where(c => c.ConditionType == "Skill")
            .Select(c => (Name: GetTargetString(c.Target), Condition: c))
            .Where(x => x.Name is not null)
            .ToDictionary(x => x.Name!, x => x.Condition);

        if (existing.Count != desired.Count)
        {
            return false;
        }

        foreach (var (skillName, requiredLevel) in desired)
        {
            if (!existing.TryGetValue(skillName, out var condition) || !MatchesIntValue(condition, requiredLevel))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HideoutConditionsMatch(PrestigeElement prestige, Dictionary<string, int> desired)
    {
        var existing = prestige.Conditions
            .Where(c => c.ConditionType == "HideoutArea" && c.AreaType.HasValue)
            .ToDictionary(c => ((int)c.AreaType!.Value).ToString(), c => c);

        if (existing.Count != desired.Count)
        {
            return false;
        }

        foreach (var (areaKey, requiredLevel) in desired)
        {
            if (!int.TryParse(areaKey, out var areaTypeInt) || !Enum.IsDefined(typeof(HideoutAreas), areaTypeInt))
            {
                return false;
            }

            var areaType = (HideoutAreas)areaTypeInt;
            if (!existing.TryGetValue(areaKey, out var condition)
                || condition.AreaType != areaType
                || !MatchesIntValue(condition, requiredLevel))
            {
                return false;
            }
        }

        return true;
    }

    private static bool QuestStartAlreadyMatches(
        QuestCondition levelCondition,
        QuestCondition? prestigeCondition,
        QuestStartOverrideConfig config
    )
    {
        if (!MatchesIntValue(levelCondition, config.Level))
        {
            return false;
        }

        if (!config.PrestigeLevel.HasValue)
        {
            return true;
        }

        return prestigeCondition is not null && MatchesIntValue(prestigeCondition, config.PrestigeLevel.Value, "==");
    }

    private static bool MatchesIntValue(QuestCondition condition, int expected, string compareMethod = ">=")
    {
        return MatchesNumericValue(condition, expected, compareMethod);
    }

    private static bool MatchesNumericValue(QuestCondition condition, long expected, string compareMethod = ">=")
    {
        return ValuesEqual(condition.Value, expected)
            && CompareMethodsEqual(condition.CompareMethod, compareMethod);
    }

    private static bool ValuesEqual(double? actual, long expected)
    {
        if (!actual.HasValue)
        {
            return false;
        }

        return Math.Abs(actual.Value - expected) < 0.001;
    }

    /// <summary>SPT/WTT may use "&gt;=" or "GreaterOrEqual" — treat as equivalent when checking.</summary>
    private static bool CompareMethodsEqual(string? actual, string expected)
    {
        return string.Equals(NormalizeCompareMethod(actual), NormalizeCompareMethod(expected), StringComparison.Ordinal);
    }

    private static string NormalizeCompareMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return string.Empty;
        }

        return method.Trim().ToLowerInvariant() switch
        {
            ">=" or "ge" or "greaterorequal" or "greater or equal" => ">=",
            "==" or "eq" or "equal" or "equals" => "==",
            ">" or "gt" or "greater" => ">",
            "<=" or "le" or "lessorequal" or "less or equal" => "<=",
            "<" or "lt" or "less" => "<",
            "!=" or "ne" or "notequal" or "not equal" => "!=",
            _ => method.Trim(),
        };
    }

    private static bool TargetIncludesItem(ListOrT<string> target, MongoId itemId)
    {
        var itemIdString = itemId.ToString();

        if (target.IsItem && string.Equals(target.Item, itemIdString, StringComparison.Ordinal))
        {
            return true;
        }

        return target.List is not null && target.List.Any(id => string.Equals(id, itemIdString, StringComparison.Ordinal));
    }

    private static string? GetTargetString(ListOrT<string>? target)
    {
        if (target is null)
        {
            return null;
        }

        if (target.IsItem)
        {
            return target.Item;
        }

        return target.List?.FirstOrDefault();
    }

    private static int NextIndex(PrestigeElement prestige)
    {
        if (prestige.Conditions.Count == 0)
        {
            return 0;
        }

        return prestige.Conditions.Max(c => c.Index ?? 0) + 1;
    }

    private static QuestCondition CreateBaseCondition(
        string conditionType,
        int index,
        Action<QuestCondition> configure
    )
    {
        var condition = new QuestCondition
        {
            Id = new MongoId(),
            Index = index,
            DynamicLocale = false,
            VisibilityConditions = [],
            GlobalQuestCounterId = string.Empty,
            ParentId = string.Empty,
            ConditionType = conditionType,
            CompareMethod = ">=",
        };

        configure(condition);
        return condition;
    }
}
