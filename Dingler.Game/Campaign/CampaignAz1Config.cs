namespace Dingler.Game.Campaign;

/// <summary>
/// The playable Howling Plains opening and its authenticated action gates.
/// Full authored-to-visual topology is held separately in CampaignAz1MapCatalog.
/// </summary>
public static class CampaignAz1Config
{
    public const string TemplateName = "AZ1";
    public const string CampaignTemplateId = "742c6fc1-77e5-4029-bf9e-4e8af387857a";
    public const string BackgroundPrefab = "campaign/azmap/azmap";
    public const string NodesPrefab = "campaign/az01/nodes";
    public const string StartingNode = "Node001";
    public const string NextNode = "Node002";
    public const string StartingTitle = "Into The Woods";
    public const string NextTitle = "North Feralroot Woods";
    public const string ThirdNode = "Node003";
    public const string ThirdTitle = "Dunnwood";
    public const string FourthNode = "Node004";
    public const string FourthTitle = "Sporemist Hollow";
    public const string OaksNode = "Node007";
    public const string OaksTitle = "The Road of Oaks";
    public const string SutherlandNode = "Node009";
    public const string SutherlandTitle = "The Sutherland";
    public const string OaksToSutherland = "Path_Node007_Node009";
    public const string DunnwoodToHollow = "Path_Node003_Node004";
    public const string DunnwoodToOaks = "Path_Node003_Node007";
    public const string OpeningPath = "Path_Node001_Node002";
    public const string SecondPath = "Path_Node002_Node003";

    public static string? OpeningNode(string? value) => value switch
    {
        StartingNode or StartingTitle => StartingNode,
        NextNode or NextTitle => NextNode,
        ThirdNode or ThirdTitle => ThirdNode,
        FourthNode or FourthTitle => FourthNode,
        OaksNode or OaksTitle => OaksNode,
        SutherlandNode or SutherlandTitle => SutherlandNode,
        _ => null,
    };

    public sealed record MapEdge(string Path, string FromNode, string ToNode);

    // Node metadata lives in the original SceneData and TemplateManager;
    // these are the verified opening path identifiers from the Unity map.
    private static readonly MapEdge[] OpeningEdges =
    [
        new(OpeningPath, StartingNode, NextNode),
        new(SecondPath, NextNode, ThirdNode),
        new(DunnwoodToHollow, ThirdNode, FourthNode),
        new(DunnwoodToOaks, ThirdNode, OaksNode),
        new(OaksToSutherland, OaksNode, SutherlandNode),
    ];

    public static bool TryGetOpeningEdge(string? path, out MapEdge edge)
    {
        foreach (var candidate in OpeningEdges)
        {
            if (string.Equals(path, candidate.Path, StringComparison.Ordinal)
                || string.Equals(path, $"Path_{candidate.ToNode}_{candidate.FromNode}", StringComparison.Ordinal))
            {
                edge = candidate;
                return true;
            }
        }
        edge = null!;
        return false;
    }

    public static bool AreOpeningNeighbors(string? a, string? b) =>
        OpeningEdges.Any(edge =>
            (string.Equals(edge.FromNode, a, StringComparison.Ordinal)
             && string.Equals(edge.ToNode, b, StringComparison.Ordinal))
            || (string.Equals(edge.FromNode, b, StringComparison.Ordinal)
                && string.Equals(edge.ToNode, a, StringComparison.Ordinal)));

    public static bool IsOpeningPath(string? path) => TryGetOpeningEdge(path, out _);
}
