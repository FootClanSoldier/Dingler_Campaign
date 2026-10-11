extern alias HexGame;

using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Campaign;

/// <summary>
/// Read-only reference audit of original HEX encounter dependencies. This intentionally
/// reports unresolved runtime references rather than replacing authored templates.
/// </summary>
internal static class CampaignEncounterDependencyDiagnostics
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static JsonObject Compare(object manager, object? active, object? comparison)
    {
        try
        {
            var references = BuildIndex(manager);
            return new JsonObject
            {
                ["indexedTemplateObjects"] = references.Count,
                ["active"] = DescribeScene(active, references),
                ["workingComparison"] = DescribeScene(comparison, references),
                ["note"] = "Runtime template lookup presence is evidence only. Missing index entries can reflect an unexposed lookup; neither modifiers nor deck references have been proven to cause InitEncounter's null reference."
            };
        }
        catch (Exception error)
        {
            return new JsonObject { ["inspectionError"] = error.ToString() };
        }
    }

    private static JsonNode? DescribeScene(object? scene, Dictionary<Guid, List<object>> index)
    {
        if (scene is null) return null;
        var players = new JsonArray();
        foreach (var player in Items(Get(scene, "m_EncounterPlayers")).Take(4))
        {
            var deckId = ReadGuid(Get(player, "m_EncounterDeck"));
            var deck = Lookup(index, deckId);
            var authoredDeck = GetTemplates(index, deckId, "EncounterDeck").FirstOrDefault();
            var deckTemplateId = ReadGuid(Get(authoredDeck, "m_DeckTemplateId"));
            var deckTemplate = GetTemplates(index, deckTemplateId, "DeckTemplate").FirstOrDefault();
            var startMods = new JsonArray();
            foreach (var group in Items(Get(player, "m_EncounterMods")).Take(24))
            {
                var effects = new JsonArray();
                foreach (var effect in Items(Get(group, "m_Effects")).Take(32))
                {
                    var cardId = ReadGuid(Get(effect, "m_CardId"));
                    effects.Add(new JsonObject
                    {
                        ["effectType"] = effect.GetType().FullName,
                        ["cardId"] = cardId?.ToString(),
                        ["cardTemplateMatches"] = Matches(index, cardId, "CardTemplate"),
                        ["target"] = Get(effect, "m_ModTargetPlayer")?.ToString(),
                        ["round"] = Get(effect, "m_RoundToApply")?.ToString()
                    });
                }
                startMods.Add(new JsonObject
                {
                    ["groupType"] = group.GetType().FullName,
                    ["effects"] = effects
                });
            }
            players.Add(new JsonObject
            {
                ["isAi"] = Get(player, "m_IsAI")?.ToString(),
                ["team"] = Get(player, "m_Team")?.ToString(),
                ["encounterDeckId"] = deckId?.ToString(),
                ["encounterDeckReferences"] = deck,
                ["deckTemplateId"] = deckTemplateId?.ToString(),
                ["deckTemplateReferences"] = Lookup(index, deckTemplateId),
                ["deckTemplateChampionId"] = ReadGuid(Get(deckTemplate, "m_ChampionId"))?.ToString(),
                ["deckResourceCount"] = Count(Get(deckTemplate, "m_DeckResources")),
                ["encounterDeckOrderCount"] = Count(Get(authoredDeck, "m_DeckOrder")),
                ["encounterStartMods"] = startMods
            });
        }
        var sceneMods = new JsonArray();
        foreach (var mod in Items(Get(scene, "m_Mods")).Take(32))
        {
            var refs = new JsonArray();
            foreach (var id in Items(Get(mod, "m_Mods")).Take(64))
            {
                var guid = ReadGuid(id);
                refs.Add(new JsonObject
                {
                    ["id"] = guid?.ToString() ?? id.ToString(),
                    ["runtimeTemplateReferences"] = Lookup(index, guid)
                });
            }
            sceneMods.Add(new JsonObject
            {
                ["title"] = Get(mod, "m_Title")?.ToString(),
                ["description"] = Get(mod, "m_Description")?.ToString(),
                ["references"] = refs
            });
        }
        return new JsonObject
        {
            ["sceneId"] = ReadGuid(Get(scene, "m_Id"))?.ToString(),
            ["sceneName"] = Get(scene, "m_Name")?.ToString(),
            ["gameboard"] = Get(scene, "m_Gameboard")?.ToString(),
            ["players"] = players,
            ["sceneMods"] = sceneMods
        };
    }

    private static Dictionary<Guid, List<object>> BuildIndex(object manager)
    {
        var index = new Dictionary<Guid, List<object>>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var inspected = 0;
        foreach (var member in manager.GetType().GetMembers(Members))
        {
            // Only original template lookup collections; avoid invoking unrelated
            // TemplateManager methods or traversing gameplay/session state.
            if (!member.Name.Contains("Deck", StringComparison.OrdinalIgnoreCase)
                && !member.Name.Contains("Card", StringComparison.OrdinalIgnoreCase)
                && !member.Name.Contains("Champion", StringComparison.OrdinalIgnoreCase)
                && !member.Name.Contains("Mod", StringComparison.OrdinalIgnoreCase))
                continue;
            object? collection;
            try
            {
                collection = member switch
                {
                    FieldInfo field => field.GetValue(manager),
                    PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(manager),
                    _ => null,
                };
            }
            catch { continue; }
            foreach (var template in Items(collection))
            {
                if (!seen.Add(template)) continue;
                if (++inspected > 100000) return index;
                var id = ReadGuid(Get(template, "m_Id", "Id"));
                if (id is null || id == Guid.Empty) continue;
                if (!index.TryGetValue(id.Value, out var owners))
                    index[id.Value] = owners = new List<object>();
                owners.Add(template);
            }
        }
        return index;
    }

    private static JsonArray Lookup(Dictionary<Guid, List<object>> index, Guid? id)
    {
        var result = new JsonArray();
        if (id is not null && id != Guid.Empty && index.TryGetValue(id.Value, out var owners))
            foreach (var owner in owners) result.Add(owner.GetType().Name);
        return result;
    }

    private static int Matches(Dictionary<Guid, List<object>> index, Guid? id, string category) =>
        id is not null && index.TryGetValue(id.Value, out var owners)
            ? owners.Count(owner => owner.GetType().Name.Equals(category, StringComparison.Ordinal)) : 0;

    private static IEnumerable<object> GetTemplates(Dictionary<Guid, List<object>> index, Guid? id, string category) =>
        id is not null && index.TryGetValue(id.Value, out var templates)
            ? templates.Where(template => template.GetType().Name.Equals(category, StringComparison.Ordinal))
            : Enumerable.Empty<object>();

    private static int? Count(object? value) =>
        value is ICollection list ? list.Count : value is IEnumerable seq
            ? seq.Cast<object>().Take(2049).Count() : null;

    private static Guid? ReadGuid(object? value)
    {
        if (value is null) return null;
        if (value is Guid direct) return direct;
        if (Guid.TryParse(value.ToString(), out var parsed)) return parsed;
        var nested = Get(value, "m_Guid");
        return nested is Guid guid ? guid : Guid.TryParse(nested?.ToString(), out parsed) ? parsed : null;
    }

    private static object? Get(object? source, params string[] names)
    {
        if (source is null) return null;
        foreach (var name in names)
            for (var type = source.GetType(); type is not null; type = type.BaseType)
                try
                {
                    var field = type.GetField(name, Members | BindingFlags.DeclaredOnly);
                    if (field is not null) return field.GetValue(source);
                    var property = type.GetProperty(name, Members | BindingFlags.DeclaredOnly);
                    if (property is not null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(source);
                }
                catch { /* Some client template accessors are not usable outside gameplay. */ }
        return null;
    }

    private static IEnumerable<object> Items(object? source)
    {
        if (source is null || source is string) yield break;
        if (source is IDictionary dictionary)
        {
            foreach (DictionaryEntry item in dictionary)
                if (item.Value is not null) yield return item.Value;
            yield break;
        }
        var lookup = Get(source, "m_Lookup");
        if (lookup is IDictionary inner)
        {
            foreach (DictionaryEntry item in inner)
                if (item.Value is not null) yield return item.Value;
            yield break;
        }
        if (source is IEnumerable list)
            foreach (var item in list)
                if (item is not null) yield return item;
    }
}
