extern alias HexGame;

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Campaign;

/// <summary>
/// Captures original HEX encounter metadata when a campaign battle cannot initialize.
/// No encounter data is modified and no replacement opponent is synthesized.
/// </summary>
internal static class CampaignEncounterDiagnostics
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Capture(
        string storePath,
        ulong campaignId,
        string sceneGuid,
        int race,
        ulong deckId,
        SessionStateEncounterData encounterData,
        Exception failure,
        ILogger? logger)
    {
        try
        {
            var manager = HexGame::Singleton<TemplateManager>.Instance;
            var scenes = ReadSceneTemplates(manager);
            var workingScene = CampaignCrayburnConfig.Race(race).CastleGateEncounter;
            var target = scenes.Where(item => HasId(item, sceneGuid)).ToList();
            var comparison = scenes.Where(item => HasId(item, workingScene)).ToList();
            var targetPlayers = target.Count == 1 ? ReadMember(target[0], "m_EncounterPlayers") : null;
            var comparisonPlayers = comparison.Count == 1 ? ReadMember(comparison[0], "m_EncounterPlayers") : null;
            var output = new JsonObject
            {
                ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
                ["failure"] = failure.ToString(),
                ["campId"] = campaignId.ToString(),
                ["race"] = race,
                ["selectedDeckId"] = deckId.ToString(),
                ["sessionData"] = new JsonObject
                {
                    ["sceneTemplateId"] = encounterData.SceneTemplateId.ToString(),
                    ["sessionFlags"] = encounterData.SessionFlags.ToString(),
                    ["firstPlayer"] = encounterData.FirstPlayer.ToString(),
                },
                ["sceneTemplateCount"] = scenes.Count,
                ["activeSceneMatches"] = target.Count,
                ["comparisonSceneMatches"] = comparison.Count,
                ["activeEncounterPlayers"] = CountItems(targetPlayers),
                ["comparisonEncounterPlayers"] = CountItems(comparisonPlayers),
                ["activeScene"] = target.Count == 1 ? Snapshot(target[0], 3) : null,
                ["workingCrayburnCastleGateSceneId"] = workingScene,
                ["workingCrayburnCastleGateScene"] = comparison.Count == 1 ? Snapshot(comparison[0], 3) : null,
                ["encounterDependencyAudit"] = CampaignEncounterDependencyDiagnostics.Compare(
                    manager, target.Count == 1 ? target[0] : null,
                    comparison.Count == 1 ? comparison[0] : null),
                ["originalInitEncounterIlReferences"] = CampaignEncounterIlDiagnostics.Inspect(),
                ["note"] = "Scene data and IL metadata are from installed original HEX assemblies. The Crayburn scene is a proven comparison, not a replacement opponent. The failing null operand is still unknown; no original rules or modifiers were changed.",
            };

            var diagnostics = Path.Combine(storePath, "diagnostics");
            Directory.CreateDirectory(diagnostics);
            var path = Path.Combine(diagnostics, $"campaign-encounter-failure-{campaignId}.json");
            File.WriteAllText(path, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            logger?.LogWarning(
                "Campaign encounter initialization failed: camp={Camp} scene={Scene} sceneMatches={SceneMatches} scenePlayers={ScenePlayers} comparison={Comparison} comparisonMatches={ComparisonMatches} comparisonPlayers={ComparisonPlayers}; original HEX metadata saved to {Path}",
                campaignId, sceneGuid, target.Count, CountItems(targetPlayers),
                workingScene, comparison.Count, CountItems(comparisonPlayers), Path.GetFullPath(path));
        }
        catch (Exception error)
        {
            logger?.LogWarning(error, "Campaign encounter diagnostic could not be written for {Scene}", sceneGuid);
        }
    }

    private static List<object> ReadSceneTemplates(object manager)
    {
        var result = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var member in manager.GetType().GetMembers(Members))
        {
            if (!member.Name.Contains("Scene", StringComparison.OrdinalIgnoreCase))
                continue;
            object? value;
            try
            {
                value = member switch
                {
                    FieldInfo field => field.GetValue(manager),
                    PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(manager),
                    _ => null,
                };
            }
            catch { continue; }

            foreach (var candidate in Values(value))
            {
                if (!candidate.GetType().Name.Contains("Scene", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seen.Add(candidate)) result.Add(candidate);
            }
        }
        return result;
    }

    private static IEnumerable<object> Values(object? container)
    {
        if (container is null || container is string)
            yield break;
        if (container is IDictionary dictionary)
        {
            foreach (DictionaryEntry item in dictionary)
                if (item.Value is not null) yield return item.Value;
            yield break;
        }
        var lookup = ReadMember(container, "m_Lookup");
        if (lookup is IDictionary inner)
        {
            foreach (DictionaryEntry item in inner)
                if (item.Value is not null) yield return item.Value;
            yield break;
        }
        if (container is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (item is not null) yield return item;
        }
    }

    private static bool HasId(object value, string sceneGuid) =>
        Guid.TryParse(ReadMember(value, "m_Id", "Id")?.ToString(), out var id) &&
        Guid.TryParse(sceneGuid, out var wanted) && id == wanted;

    private static object? ReadMember(object source, params string[] names)
    {
        foreach (var name in names)
        {
            for (var type = source.GetType(); type is not null; type = type.BaseType)
            {
                try
                {
                    var field = type.GetField(name, Members | BindingFlags.DeclaredOnly);
                    if (field is not null) return field.GetValue(source);
                    var property = type.GetProperty(name, Members | BindingFlags.DeclaredOnly);
                    if (property is not null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(source);
                }
                catch { /* Some original HEX getters are not safe outside an engine session. */ }
            }
        }
        return null;
    }

    private static int? CountItems(object? value)
    {
        if (value is ICollection collection) return collection.Count;
        if (value is IEnumerable sequence && value is not string) return sequence.Cast<object>().Take(33).Count();
        return null;
    }

    private static JsonNode? Snapshot(object? value, int remainingDepth) =>
        Snapshot(value, remainingDepth, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static JsonNode? Snapshot(object? value, int remainingDepth, HashSet<object> visited)
    {
        if (value is null) return null;
        if (value is string || value.GetType().IsPrimitive || value is Guid || value is Enum || value is decimal)
            return JsonValue.Create(Truncate(value.ToString()));
        if (remainingDepth == 0) return JsonValue.Create(Truncate(value.ToString()));
        if (!visited.Add(value)) return JsonValue.Create("<reference already shown>");
        if (value is IEnumerable items)
        {
            var array = new JsonArray();
            var count = 0;
            foreach (var item in items)
            {
                if (count++ == 16) { array.Add("<remaining entries omitted>"); break; }
                array.Add(Snapshot(item, remainingDepth - 1, visited));
            }
            return array;
        }
        var obj = new JsonObject { ["$type"] = value.GetType().FullName };
        var names = value.GetType().GetFields(Members)
            .Where(field => !field.IsStatic && field.Name.StartsWith("m_", StringComparison.Ordinal))
            .Select(field => field.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(90);
        foreach (var name in names)
            obj[name] = Snapshot(ReadMember(value, name), remainingDepth - 1, visited);
        return obj;
    }

    private static string Truncate(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length > 512 ? text[..512] : text;
}
