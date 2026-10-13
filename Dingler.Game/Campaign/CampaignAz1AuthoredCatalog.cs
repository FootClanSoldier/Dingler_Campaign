extern alias HexGame;

using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using HexGame::Game.Shared;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Campaign;

/// <summary>
/// Resolves the opening AZ1 battle and journal objectives from the original
/// HEX templates loaded for this client installation. No replacement scene,
/// opponent, or quest GUID is synthesized when authored data is missing.
/// </summary>
public static class CampaignAz1AuthoredCatalog
{
    public sealed record Objective(string Node, string Title, string? Encounter, string? Conversation);
    public sealed record Quest(string Script, string TemplateId, string Title, IReadOnlyList<Objective> Objectives);

    private static readonly Regex DunnwoodSceneName = new(
        @"^AZ\s*1\s*-\s*NODE\s*0*3\s*-.*Wild\s*Cub",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DunnwoodConversationName = new(
        @"^AZ1\s*-\s*Node\s*0*3(?:\s+[A-Z])?\s*-",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static bool TryGetDunnwood(out string sceneGuid, out string preludeGuid)
    {
        sceneGuid = string.Empty;
        preludeGuid = string.Empty;
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var sceneMatches = EnumerateManagerTemplates(manager, "Scene")
            .Where(item => DunnwoodSceneName.IsMatch(Text(Member(item, "m_Name")))
                           && item.GetType().Name.Contains("EncounterScene", StringComparison.OrdinalIgnoreCase))
            .Select(item => Text(Member(item, "m_Id", "Id")))
            .Where(IsGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sceneMatches.Length != 1)
            return false;

        // The first-visit Feralroot Bear prelude is a separate authored
        // ConversationTemplate; do not confuse it with an outcome/repeat row.
        var preludeMatches = manager.ConversationTemplates.Values
            .Where(item => DunnwoodConversationName.IsMatch(item.m_Name ?? ""))
            .Where(item => (item.m_Name ?? "").Contains("Feralroot Bear", StringComparison.OrdinalIgnoreCase)
                           || (item.m_OwnerName ?? "").Contains("Feralroot Bear", StringComparison.OrdinalIgnoreCase))
            .Where(item => !(item.m_Name ?? "").Contains("Success", StringComparison.OrdinalIgnoreCase)
                           && !(item.m_Name ?? "").Contains("Fail", StringComparison.OrdinalIgnoreCase)
                           && !(item.m_Name ?? "").Contains("Repeat", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.m_Id.ToString())
            .Where(IsGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (preludeMatches.Length != 1)
            return false;

        sceneGuid = sceneMatches[0];
        preludeGuid = preludeMatches[0];
        return true;
    }

    private static readonly Regex SporemistSceneName = new(
        @"^AZ\s*1\s*-\s*NODE\s*0*4\b.*SHROOM\s*HAUS",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SporemistConversationName = new(
        @"^AZ1\s*-\s*Node\s*0*4\b(?:\s|\s*-)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Require unique original HEX scene and first-visit conversation identities
    /// before making the Shroom Haus map action available. The scene does not
    /// establish which collection cards its choice should award.
    /// </summary>
    /// <summary>
    /// Describe original candidate records when the Shroom Haus resolver
    /// cannot unambiguously bind a node. This does not mutate template data.
    /// </summary>
    public static string DescribeSporemistCandidates()
    {
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var scenes = EnumerateManagerTemplates(manager, "Scene")
            .Where(item => Regex.IsMatch(Text(Member(item, "m_Name")), @"NODE\s*0*4\b", RegexOptions.IgnoreCase))
            .Select(item => $"{item.GetType().Name}:{Text(Member(item, "m_Name"))} [{Text(Member(item, "m_Id", "Id"))}]")
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        var conversations = manager.ConversationTemplates.Values
            .Where(item => Regex.IsMatch(item.m_Name ?? "", @"Node\s*0*4\b", RegexOptions.IgnoreCase))
            .Select(item => $"{item.m_Name} [{item.m_Id}]")
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        return $"scenes=({string.Join("; ", scenes)}) conversations=({string.Join("; ", conversations)})";
    }

    public static bool TryGetSporemistIntro(out string sceneGuid, out string conversationGuid)
    {
        sceneGuid = string.Empty;
        conversationGuid = string.Empty;
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var sceneMatches = EnumerateManagerTemplates(manager, "Scene")
            .Where(item => SporemistSceneName.IsMatch(Text(Member(item, "m_Name"))))
            .Select(item => Text(Member(item, "m_Id", "Id")))
            .Where(IsGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sceneMatches.Length != 1)
            return false;

        var conversations = manager.ConversationTemplates.Values
            .Where(item => SporemistConversationName.IsMatch(item.m_Name ?? ""))
            .Where(item => !(item.m_Name ?? "").Contains("Success", StringComparison.OrdinalIgnoreCase)
                           && !(item.m_Name ?? "").Contains("Fail", StringComparison.OrdinalIgnoreCase)
                           && !(item.m_Name ?? "").Contains("Repeat", StringComparison.OrdinalIgnoreCase)
                           && !(item.m_Name ?? "").Contains("Quest End", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        // Only prefer the explicit first-visit variant when the authored
        // catalog contains multiple conversations for the same node.
        var first = conversations.Where(item => (item.m_Name ?? "").Contains("First", StringComparison.OrdinalIgnoreCase)
                                                || Regex.IsMatch(item.m_Name ?? "", @"\bStep\s*1\b", RegexOptions.IgnoreCase))
            .ToArray();
        if (first.Length != 0)
            conversations = first;
        var matches = conversations.Select(item => item.m_Id.ToString())
            .Where(IsGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length != 1)
            return false;

        sceneGuid = sceneMatches[0];
        conversationGuid = matches[0];
        return true;
    }

    // These IDs and exact roles are extracted from the original HEX 1.1.0.086
    // SceneData and ConversationTemplate records, not invented server outcomes.
    private const string GaalSceneId = "7656fe84-bc7a-4e2d-b8ae-2741164a7a26";
    private const string GaalFirstId = "9c05cb46-fde5-4d86-8d55-538d044ef048";
    private const string GaalRepeatId = "0675af02-23ae-4fe1-b860-a8688c30641e";
    private const string GaalFortuneId = "eb8968a1-a2d3-4a32-98bf-5cb4524a00bb";

    public static bool TryGetGaalCamp(out string first, out string repeat, out string hasFortune)
    {
        first = repeat = hasFortune = string.Empty;
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var scene = EnumerateManagerTemplates(manager, "Scene")
            .Where(item => string.Equals(Text(Member(item, "m_Id", "Id")), GaalSceneId,
                StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(Text(Member(item, "m_Name")),
                    @"^AZ\s*1\s*-\s*NODE\s*0*7\b.*GAAL\s*CAMP", RegexOptions.IgnoreCase))
            .Take(2).ToArray();
        if (scene.Length != 1)
            return false;
        var ids = new[] { GaalFirstId, GaalRepeatId, GaalFortuneId };
        var names = new[] { "First encounter", "Repeat encounters", "Player already has fortune" };
        for (var i = 0; i < ids.Length; i++)
        {
            if (!manager.ConversationTemplates.TryGetValue(new ResourceId(ids[i]), out var conversation)
                || !Regex.IsMatch(conversation.m_Name ?? string.Empty,
                    @"^AZ1\s*-\s*Node\s*0*7\b.*Milosh", RegexOptions.IgnoreCase)
                || !(conversation.m_Name ?? string.Empty).Contains(names[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        first = GaalFirstId;
        repeat = GaalRepeatId;
        hasFortune = GaalFortuneId;
        return true;
    }

    // The original client names this conversation set "Node R", while its
    // authored map location and Unity prefab path use the ID "Node00R".
    private const string ValeSceneId = "2f9a4008-a36b-4a49-805e-d61082c84c08";
    private const string ValeStep1Id = "b00ba245-aa98-4765-b171-c33610e456d1";
    private const string ValeStep2Id = "8433f72e-bd47-4978-bc02-360e34ddce57";
    private const string ValeStep3Id = "617819c9-bc9b-4788-a469-8117af28a613";

    public static bool TryGetValeOfOberon(out string step1, out string step2, out string step3)
    {
        step1 = step2 = step3 = string.Empty;
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var scenes = EnumerateManagerTemplates(manager, "Scene")
            .Where(item => string.Equals(Text(Member(item, "m_Id", "Id")), ValeSceneId,
                StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(Text(Member(item, "m_Name")),
                    @"^AZ\s*1\s*-\s*NODE\s*R\s*-\s*MASKED\s+ARMY\s*-\s*DIALOG$", RegexOptions.IgnoreCase))
            .Take(2).ToArray();
        if (scenes.Length != 1)
            return false;
        var ids = new[] { ValeStep1Id, ValeStep2Id, ValeStep3Id };
        var names = new[] { "Step 1", "Step 2 Player has faction quest", "Step 3 Player finished faction quest" };
        for (var i = 0; i < ids.Length; i++)
        {
            if (!manager.ConversationTemplates.TryGetValue(new ResourceId(ids[i]), out var conversation)
                || !Regex.IsMatch(conversation.m_Name ?? string.Empty,
                    @"^AZ1\s*-\s*Node\s*R\s*-\s*Masked\s+Army\s*-", RegexOptions.IgnoreCase)
                || !(conversation.m_Name ?? string.Empty).Contains(names[i], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(conversation.m_OwnerName, "Masked Sergeant", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        step1 = ValeStep1Id;
        step2 = ValeStep2Id;
        step3 = ValeStep3Id;
        return true;
    }

    // Identifiers independently extracted from the original HEX gamedata:
    // SceneData EncounterScene and Node 9 ConversationTemplate, respectively.
    private const string SutherlandSceneId = "ddf8a2da-d397-4c3b-9d32-729f1bc86ade";
    private const string SutherlandPreludeId = "316c47a8-de68-4ece-b482-86936f8099de";

    public static bool TryGetSutherland(out string sceneGuid, out string preludeGuid)
    {
        sceneGuid = preludeGuid = string.Empty;
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var scenes = EnumerateManagerTemplates(manager, "Scene")
            .Where(item => item.GetType().Name.Contains("EncounterScene", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Text(Member(item, "m_Id", "Id")), SutherlandSceneId,
                    StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(Text(Member(item, "m_Name")),
                    @"^AZ\s*1\s*-\s*NODE\s*0*9\b.*COCKATWICE\s+CHICK", RegexOptions.IgnoreCase))
            .Take(2).ToArray();
        if (scenes.Length != 1
            || !manager.ConversationTemplates.TryGetValue(new ResourceId(SutherlandPreludeId), out var prelude)
            || !Regex.IsMatch(prelude.m_Name ?? string.Empty,
                @"^AZ1\s*-\s*Node\s*0*9\b.*Cockatwice", RegexOptions.IgnoreCase))
            return false;

        sceneGuid = SutherlandSceneId;
        preludeGuid = SutherlandPreludeId;
        return true;
    }

    /// <summary>
    /// Accept only original HEX card templates actually named Chickatwice.
    /// The client's authored card index includes two template IDs under
    /// that name; the capture receipt comes from the original battle engine.
    /// </summary>
    public static bool HasCapturedChickatwice(IReadOnlyList<Guid>? capturedTemplates)
    {
        if (capturedTemplates is null || capturedTemplates.Count == 0)
            return false;
        var cards = HexGame::Singleton<TemplateManager>.Instance.Cards;
        foreach (var captured in capturedTemplates)
            if (cards.TryGetValue(new ResourceId(captured.ToString()), out var card)
                && string.Equals(Text(Member(card, "m_Name", "Name")), "Chickatwice",
                    StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool TryGetOpeningQuests(int race, out IReadOnlyList<Quest> quests)
    {
        var faction = race is >= 1 and <= 4
            ? "az01_ar_find_ambling_mesa" : "az01_uw_find_cave_in";
        var scripts = new[] { "az01_tamed", faction };
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var source = EnumerateManagerTemplates(manager, "Quest")
            .Where(item => item.GetType().Name.Equals("QuestTemplate", StringComparison.Ordinal))
            .ToList();
        var result = new List<Quest>();
        foreach (var script in scripts)
        {
            var matches = source.Where(item => string.Equals(Text(Member(item, "m_ScriptName")), script,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                quests = Array.Empty<Quest>();
                return false;
            }
            var template = matches[0];
            var templateId = Text(Member(template, "m_Id", "Id"));
            if (!IsGuid(templateId) || Member(template, "m_Objectives") is not IEnumerable objectives)
            {
                quests = Array.Empty<Quest>();
                return false;
            }
            var items = new List<Objective>();
            foreach (var objective in objectives)
            {
                if (objective is null)
                    continue;
                var node = Text(Member(objective, "m_QuestLocationId"));
                if (string.IsNullOrWhiteSpace(node) || items.Any(item => item.Node == node))
                {
                    quests = Array.Empty<Quest>();
                    return false;
                }
                var title = QualifiedTitle(Member(objective, "m_Title"), race);
                if (string.IsNullOrWhiteSpace(title))
                    title = Text(Member(objective, "m_TitleOld"));
                var encounter = Text(Member(objective, "m_EncounterId"));
                var conversation = Text(Member(objective, "m_ConversationIdOld"));
                items.Add(new Objective(node, string.IsNullOrWhiteSpace(title) ? node : title,
                    IsGuid(encounter) ? encounter : null, IsGuid(conversation) ? conversation : null));
            }
            if (items.Count == 0)
            {
                quests = Array.Empty<Quest>();
                return false;
            }
            var questTitle = Text(Member(template, "m_TitleOld"));
            if (string.IsNullOrWhiteSpace(questTitle))
                questTitle = QualifiedTitle(Member(template, "m_Title"), race);
            result.Add(new Quest(script, templateId, questTitle, items));
        }
        quests = result;
        return true;
    }

    private static string QualifiedTitle(object? qualified, int race)
    {
        if (qualified is not IEnumerable parts || qualified is string)
            return string.Empty;
        string fallback = string.Empty;
        var ardent = race is >= 1 and <= 4;
        foreach (var part in parts)
        {
            if (part is null)
                continue;
            var text = Text(Member(part, "m_Text"));
            if (string.IsNullOrWhiteSpace(text))
                continue;
            fallback = fallback.Length == 0 ? text : fallback;
            var qualifiers = Member(part, "m_Qualifiers") as IEnumerable;
            if (qualifiers is null)
                return text;
            var factions = qualifiers.Cast<object?>()
                .Where(q => q is not null)
                .Select(q => Text(Member(q!, "m_Faction")))
                .Where(q => q.Length > 0 && !q.Equals("Any", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (factions.Length == 0 || factions.Any(f => ardent
                    ? f is "Aria" or "Ardent" : f.Equals("Underworld", StringComparison.OrdinalIgnoreCase)))
                return text;
        }
        return fallback;
    }

    private static IEnumerable<object> EnumerateManagerTemplates(object manager, string kind)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var fields = manager.GetType().GetFields(Members)
            .Where(field => field.Name.Contains(kind, StringComparison.OrdinalIgnoreCase));
        foreach (var field in fields)
        {
            object? value;
            try { value = field.GetValue(manager); } catch { continue; }
            foreach (var item in EnumerateCollection(value))
                if (seen.Add(item)) yield return item;
        }
        var properties = manager.GetType().GetProperties(Members)
            .Where(property => property.Name.Contains(kind, StringComparison.OrdinalIgnoreCase)
                               && property.GetIndexParameters().Length == 0);
        foreach (var property in properties)
        {
            object? value;
            try { value = property.GetValue(manager); } catch { continue; }
            foreach (var item in EnumerateCollection(value))
                if (seen.Add(item)) yield return item;
        }
    }

    private static IEnumerable<object> EnumerateCollection(object? value)
    {
        if (value is null || value is string)
            yield break;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry item in dictionary)
                if (item.Value is not null) yield return item.Value;
            yield break;
        }
        // TemplateLookup<T> wraps its dictionary in m_Lookup.
        var inner = Member(value, "m_Lookup");
        if (inner is IDictionary wrapped)
        {
            foreach (DictionaryEntry item in wrapped)
                if (item.Value is not null) yield return item.Value;
            yield break;
        }
        if (value is IEnumerable enumerable)
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                var actual = item.GetType().IsGenericType && item.GetType().GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                    ? Member(item, "Value") : item;
                if (actual is not null) yield return actual;
            }
    }

    private static object? Member(object value, params string[] names)
    {
        foreach (var name in names)
        {
            for (var type = value.GetType(); type is not null; type = type.BaseType)
            {
                var field = type.GetField(name, Members | BindingFlags.DeclaredOnly);
                if (field is not null)
                {
                    try { return field.GetValue(value); } catch { }
                }
                var property = type.GetProperty(name, Members | BindingFlags.DeclaredOnly);
                if (property is not null && property.GetIndexParameters().Length == 0)
                {
                    try { return property.GetValue(value); } catch { }
                }
            }
        }
        return null;
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? string.Empty;
    private static bool IsGuid(string value) => Guid.TryParse(value, out var guid) && guid != Guid.Empty;
}
