using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dingler.Game.Campaign;

/// <summary>
/// Loads explicitly authored campaign reward rules. Unknown triggers, conditions
/// and grant types are rejected rather than silently awarding unintended loot.
/// </summary>
public static class CampaignRewardCatalog
{
    public sealed class EncounterCatalogFile
    {
        public List<CampaignEncounterReward> Entries { get; set; } = new();
    }

    public sealed class QuestCatalogFile
    {
        public List<CampaignQuestReward> Entries { get; set; } = new();
    }

    private static readonly IReadOnlyDictionary<string, CampaignEncounterReward> Entries = LoadEncounters();
    private static readonly IReadOnlyDictionary<string, CampaignQuestReward> Quests = LoadQuests();

    public static IReadOnlyCollection<CampaignCaptureReward> CaptureRewards =>
        Entries.Values.SelectMany(entry => entry.Rewards.SelectMany(rule =>
            rule.Grants.Where(grant => grant.Type == "CapturedCard")
                .Select(grant => new CampaignCaptureReward(
                    entry.EncounterGuid, rule.Id, grant.CollectionSlot!.Value,
                    CampaignRewardConditionEvaluator.CaptureTemplateGuids(rule.Conditions))))).ToArray();

    public static bool TryGet(string encounterGuid, out CampaignEncounterReward reward)
    {
        if (Entries.TryGetValue(encounterGuid, out var found))
        {
            reward = found;
            return true;
        }
        reward = null!;
        return false;
    }

    public static bool TryGetQuest(string questScript, out CampaignQuestReward reward)
    {
        if (Quests.TryGetValue(questScript, out var found))
        {
            reward = found;
            return true;
        }
        reward = null!;
        return false;
    }

    private static IReadOnlyDictionary<string, CampaignEncounterReward> LoadEncounters()
    {
        var file = LoadFile<EncounterCatalogFile>("EncounterRewards.json");
        if (file.Entries is null || file.Entries.Count == 0)
            throw new InvalidDataException("Encounter reward catalog has no entries");
        var entries = new Dictionary<string, CampaignEncounterReward>(StringComparer.OrdinalIgnoreCase);
        var captureSlots = new HashSet<int>();
        foreach (var entry in file.Entries)
        {
            if (!Guid.TryParse(entry.EncounterGuid, out var id) || id == Guid.Empty
                || string.IsNullOrWhiteSpace(entry.Campaign) || string.IsNullOrWhiteSpace(entry.Node)
                || !entries.TryAdd(id.ToString("D"), entry))
                throw new InvalidDataException($"Invalid or duplicate encounter reward: {entry.EncounterGuid}");
            entry.EncounterGuid = id.ToString("D");
            ValidateRules(entry.Rewards, "Victory", entry.EncounterGuid, captureSlots);
        }
        return entries;
    }

    private static IReadOnlyDictionary<string, CampaignQuestReward> LoadQuests()
    {
        var file = LoadFile<QuestCatalogFile>("QuestRewards.json");
        if (file.Entries is null || file.Entries.Count == 0)
            throw new InvalidDataException("Quest reward catalog has no entries");
        var entries = new Dictionary<string, CampaignQuestReward>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Quest) || entry.Quest != entry.Quest.Trim()
                || string.IsNullOrWhiteSpace(entry.Campaign)
                || !entries.TryAdd(entry.Quest, entry))
                throw new InvalidDataException($"Invalid or duplicate quest reward: {entry.Quest}");
            ValidateRules(entry.Rewards, "QuestCompleted", entry.Quest);
        }
        return entries;
    }

    private static T LoadFile<T>(string filename)
    {
        var resource = $"Dingler.Game.Campaign.Definitions.{filename}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded campaign reward catalog: {resource}");
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        })
            ?? throw new InvalidDataException($"Campaign reward catalog is empty: {filename}");
    }

    private static void ValidateRules(List<CampaignRewardRule> rules, string trigger,
        string owner, HashSet<int>? captureSlots = null)
    {
        if (rules is null || rules.Count == 0)
            throw new InvalidDataException($"No reward rules for {owner}");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id != rule.Id.Trim()
                || rule.Id.Contains(':') || !ids.Add(rule.Id)
                || rule.Trigger != trigger
                || rule.ClaimPolicy is not ("OncePerCampaign" or "EveryCompletion")
                || rule.Evidence is not ("Provisional" or "OriginalVideo")
                || rule.Grants is null || rule.Grants.Count == 0)
                throw new InvalidDataException($"Invalid reward rule {owner}:{rule.Id}");

            CampaignRewardConditionEvaluator.Validate(rule.Conditions);
            var capturedGrants = 0;
            foreach (var grant in rule.Grants)
            {
                switch (grant.Type)
                {
                    case "XP" or "Gold" when grant.Amount is > 0 && grant.Quantity is null
                        && grant.CollectionSlot is null:
                        break;
                    case "CapturedCard" when grant.Quantity == 1 && grant.Amount is null
                        && grant.CollectionSlot is >= 0 and <= 50004 && captureSlots is not null:
                        if (!captureSlots.Add(grant.CollectionSlot.Value))
                            throw new InvalidDataException($"Duplicate capture collection slot: {grant.CollectionSlot}");
                        capturedGrants++;
                        break;
                    default:
                        throw new InvalidDataException($"Invalid grant type or fields in {owner}:{rule.Id}: {grant.Type}");
                }
            }
            if (capturedGrants > 1 || (capturedGrants > 0 && rule.ClaimPolicy != "OncePerCampaign")
                || (capturedGrants > 0
                && CampaignRewardConditionEvaluator.CaptureTemplateGuids(rule.Conditions).Count == 0))
                throw new InvalidDataException($"CapturedCard must have a qualifying TamedCard condition in {owner}:{rule.Id}");
        }
    }
}

public sealed class CampaignEncounterReward
{
    public string EncounterGuid { get; set; } = string.Empty;
    public string Campaign { get; set; } = string.Empty;
    public string Node { get; set; } = string.Empty;
    public List<CampaignRewardRule> Rewards { get; set; } = new();
}

public sealed class CampaignQuestReward
{
    public string Quest { get; set; } = string.Empty;
    public string Campaign { get; set; } = string.Empty;
    public List<CampaignRewardRule> Rewards { get; set; } = new();
}

public sealed class CampaignRewardRule
{
    public string Id { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string ClaimPolicy { get; set; } = "OncePerCampaign";
    public string Evidence { get; set; } = "Provisional";
    public CampaignRewardCondition? Conditions { get; set; }
    public List<CampaignRewardGrant> Grants { get; set; } = new();
}

public sealed class CampaignRewardGrant
{
    public string Type { get; set; } = string.Empty;
    public int? Amount { get; set; }
    public int? Quantity { get; set; }
    // Stable per-champion slot for a captured card; keeps existing collection
    // instance IDs independent from rule evaluation and JSON array ordering.
    public int? CollectionSlot { get; set; }
}

public sealed class CampaignRewardCondition
{
    public string? Operator { get; set; }
    public List<CampaignRewardCondition>? Items { get; set; }
    public string? Type { get; set; }
    public string? TemplateGuid { get; set; }
}

public sealed record CampaignCaptureReward(string EncounterGuid, string RuleId,
    int CollectionSlot, IReadOnlyList<string> TemplateGuids);
