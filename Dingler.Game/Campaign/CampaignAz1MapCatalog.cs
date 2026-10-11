using System.Text.Json;

namespace Dingler.Game.Campaign;

/// <summary>
/// Read-only AZ1 map topology extracted from the original HEX client. Authored
/// locations, Unity map objects and paths are independent: a logical location
/// may share its physical map object with another authored state variant.
/// </summary>
public static class CampaignAz1MapCatalog
{
    public sealed class AuthoredLocation
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string PhysicalId { get; set; } = string.Empty;
        public bool Direct { get; set; }
    }

    public sealed class VisualNode
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    public sealed class MapPath
    {
        public string Id { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
    }

    private sealed class MapData
    {
        public string CampaignId { get; set; } = string.Empty;
        public AuthoredLocation[] Authored { get; set; } = [];
        public VisualNode[] Visual { get; set; } = [];
        public MapPath[] Paths { get; set; } = [];
    }

    private static readonly MapData Data = ReadCatalog();
    private static readonly IReadOnlyDictionary<string, MapPath> PathIndex =
        Data.Paths.ToDictionary(path => path.Id, StringComparer.Ordinal);

    public static IReadOnlyList<AuthoredLocation> AuthoredLocations => Data.Authored;
    public static IReadOnlyList<VisualNode> VisualNodes => Data.Visual;
    public static IReadOnlyList<MapPath> Paths => Data.Paths;

    // Variant-family bindings for Node019/023/026 are inferred from authored
    // records, not observed runtime activation. They are catalogued but not
    // projected to GameplayState until a concrete logical state is confirmed.
    public static IEnumerable<AuthoredLocation> DirectLocations =>
        Data.Authored.Where(location => location.Direct);

    public static bool TryGetPath(string? path, out MapPath edge)
    {
        if (path is not null && PathIndex.TryGetValue(path, out var found))
        {
            edge = found;
            return true;
        }
        edge = null!;
        return false;
    }

    public static bool AreNeighbors(string? from, string? to) =>
        from is not null && to is not null && Data.Paths.Any(path =>
            (string.Equals(path.From, from, StringComparison.Ordinal)
             && string.Equals(path.To, to, StringComparison.Ordinal))
            || (string.Equals(path.From, to, StringComparison.Ordinal)
                && string.Equals(path.To, from, StringComparison.Ordinal)));

    private static MapData ReadCatalog()
    {
        using var stream = typeof(CampaignAz1MapCatalog).Assembly.GetManifestResourceStream(
            "Dingler.Game.Campaign.Definitions.Az1Map.json")
            ?? throw new InvalidOperationException("Embedded AZ1 map catalog is missing");
        var map = JsonSerializer.Deserialize<MapData>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("AZ1 map catalog is invalid");

        var visuals = map.Visual.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (map.CampaignId != CampaignAz1Config.CampaignTemplateId
            || map.Authored.Length != 85 || map.Visual.Length != 79 || map.Paths.Length != 83
            || visuals.Count != 79
            || map.Authored.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != 85
            || map.Paths.Select(path => path.Id).Distinct(StringComparer.Ordinal).Count() != 83
            || map.Authored.Any(node => !visuals.Contains(node.PhysicalId))
            || map.Paths.Any(path => !visuals.Contains(path.From) || !visuals.Contains(path.To)))
            throw new InvalidOperationException("AZ1 original-client map catalog failed integrity checks");

        return map;
    }
}
