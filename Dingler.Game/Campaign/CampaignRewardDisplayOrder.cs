extern alias HexGame;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Campaign;

/// <summary>
/// Orders campaign reward display actions by configured item kind and original
/// CardTemplate rarity. Unconfigured actions retain their positions.
/// </summary>
internal static class CampaignRewardDisplayOrder
{
    public sealed class DisplayOrderFile
    {
        public List<string> CardItemKinds { get; set; } = new();
        public List<DisplayRarityPriority> CardRarities { get; set; } = new();
        public int DefaultCardPriority { get; set; }
        public List<DisplayPriority> Priorities { get; set; } = new();
    }

    public sealed class DisplayPriority
    {
        public string ItemKind { get; set; } = string.Empty;
        public int Priority { get; set; }
    }

    public sealed class DisplayRarityPriority
    {
        public string Rarity { get; set; } = string.Empty;
        public int Priority { get; set; }
    }

    private sealed record PrioritySettings(
        IReadOnlyDictionary<string, int> ItemPriorities,
        IReadOnlyDictionary<ERarity, int> CardRarityPriorities,
        IReadOnlySet<string> CardItemKinds,
        int DefaultCardPriority);

    private static readonly PrioritySettings Settings = LoadPriorities();

    public static void Apply(JsonObject applied)
    {
        if (applied["Completed"] is not JsonArray completed || completed.Count < 2)
            return;

        // Resolve each action once. In particular, do not look up the card
        // template again while rebuilding the list after its nodes are detached.
        var entries = completed.Select((item, index) =>
            (Item: item, Index: index, Priority: PriorityOf(item))).ToArray();
        var ranked = entries
            .Where(entry => entry.Priority.HasValue)
            .OrderByDescending(entry => entry.Priority!.Value)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToArray();

        if (ranked.Length < 2)
            return;

        // JsonNode may only have one parent. Preserve the exact slots of any
        // unconfigured item kinds, and preserve relative order for ties.
        completed.Clear();
        var nextRanked = 0;
        foreach (var entry in entries)
            completed.Add(entry.Priority.HasValue ? ranked[nextRanked++] : entry.Item);
    }

    private static int? PriorityOf(JsonNode? item)
    {
        if (item is not JsonObject action)
            return null;
        var kind = action["ItemKind"]?.GetValue<string>();
        if (kind is null)
            return null;

        if (Settings.CardItemKinds.Contains(kind))
        {
            var templateId = action["ItemTemplate"]?.GetValue<string>();
            if (Guid.TryParse(templateId, out var id)
                && HexGame::Singleton<TemplateManager>.Instance.Cards
                    .TryGetValue(new ResourceId(id.ToString("D")), out var card)
                && Settings.CardRarityPriorities.TryGetValue(card.CardRarity, out var rarityPriority))
                return rarityPriority;
            // The original enum also has special types (Promo, PvE, Land).
            // Missing templates and nonstandard rarities stay ahead of currency.
            return Settings.DefaultCardPriority;
        }

        return Settings.ItemPriorities.TryGetValue(kind, out var priority)
            ? priority : null;
    }

    private static PrioritySettings LoadPriorities()
    {
        const string resource = "Dingler.Game.Campaign.Definitions.RewardDisplayOrder.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded campaign reward display order: {resource}");
        var file = JsonSerializer.Deserialize<DisplayOrderFile>(stream, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new InvalidDataException("Campaign reward display order is empty");

        if (file.Priorities is null || file.Priorities.Count == 0
            || file.CardItemKinds is null || file.CardItemKinds.Count == 0
            || file.CardRarities is null || file.CardRarities.Count == 0
            || file.DefaultCardPriority <= 0)
            throw new InvalidDataException("Incomplete campaign reward display order");

        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in file.CardItemKinds)
        {
            if (kind is not ("CARD" or "BOACARD") || !kinds.Add(kind))
                throw new InvalidDataException($"Invalid or duplicate campaign card display kind: {kind}");
        }

        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Priorities)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.ItemKind)
                || entry.ItemKind != entry.ItemKind.Trim()
                || entry.Priority <= 0 || kinds.Contains(entry.ItemKind)
                || !priorities.TryAdd(entry.ItemKind, entry.Priority))
                throw new InvalidDataException($"Invalid or duplicate campaign reward display priority: {entry?.ItemKind}");
        }

        var rarities = new Dictionary<ERarity, int>();
        foreach (var entry in file.CardRarities)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Rarity)
                || !Enum.TryParse<ERarity>(entry.Rarity, ignoreCase: false, out var rarity)
                || rarity is ERarity.Unknown or ERarity.Land
                || entry.Rarity != rarity.ToString()
                || entry.Priority <= 0 || !rarities.TryAdd(rarity, entry.Priority))
                throw new InvalidDataException($"Invalid or duplicate campaign card display rarity: {entry?.Rarity}");
        }

        return new PrioritySettings(priorities, rarities, kinds, file.DefaultCardPriority);
    }
}
