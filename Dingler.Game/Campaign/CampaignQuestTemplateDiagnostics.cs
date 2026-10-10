extern alias HexGame;

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dingler.Server.Abstractions;
using HexGame::Game.Shared;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Campaign;

/// <summary>
/// Captures the original HEX QuestTemplate data needed to identify the authored
/// Crayburn journal quest without inventing script names or objective ids.
/// </summary>
public sealed class CampaignQuestTemplateDiagnostics : IStartupService
{
    private static readonly string[] SearchTerms = ["crayburn", "cragburn", "crayborn"];

    private readonly CampaignOptions _options;
    private readonly ILogger<CampaignQuestTemplateDiagnostics>? _logger;

    public CampaignQuestTemplateDiagnostics(
        CampaignOptions options,
        ILogger<CampaignQuestTemplateDiagnostics>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public void Initialize()
    {
        try
        {
            DumpQuestTemplates();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Campaign QuestTemplate diagnostic failed");
        }
    }

    private void DumpQuestTemplates()
    {
        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var discovered = DiscoverQuestTemplates(manager);

        var summaries = new JsonArray();
        var matches = new JsonArray();
        foreach (var item in discovered.Templates)
        {
            var summary = BuildSummary(item.SourceMember, item.Template);
            summaries.Add(summary);

            if (IsCrayburnCandidate(item.Template))
                matches.Add(BuildDetailed(item.SourceMember, item.Template));
        }

        var root = new JsonObject
        {
            ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
            ["templateManagerType"] = manager.GetType().FullName,
            ["searchTerms"] = new JsonArray(SearchTerms.Select(term => (JsonNode?)JsonValue.Create(term)).ToArray()),
            ["collectionSources"] = new JsonArray(discovered.CollectionSources.Select(source => (JsonNode?)JsonValue.Create(source)).ToArray()),
            ["questTemplateCount"] = discovered.Templates.Count,
            ["matchCount"] = matches.Count,
            ["matches"] = matches,
            ["allQuestTemplates"] = summaries,
        };

        var diagnosticsPath = Path.Combine(_options.StorePath, "diagnostics");
        Directory.CreateDirectory(diagnosticsPath);
        var outputPath = Path.Combine(diagnosticsPath, "quest-templates-crayburn.json");
        File.WriteAllText(outputPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        _logger?.LogInformation(
            "Campaign QuestTemplate diagnostic scanned {Count} templates and found {Matches} Crayburn candidate(s); dump={Path}",
            discovered.Templates.Count,
            matches.Count,
            Path.GetFullPath(outputPath));

        foreach (var match in discovered.Templates.Where(t => IsCrayburnCandidate(t.Template)))
        {
            _logger?.LogInformation(
                "Campaign QuestTemplate match source={Source} id={Id} script={Script} name={Name} title={Title} objectives={Objectives}",
                match.SourceMember,
                ScalarText(ReadMember(match.Template, "m_Id", "Id")),
                ScalarText(ReadMember(match.Template, "m_ScriptName")),
                ScalarText(ReadMember(match.Template, "m_Name")),
                ScalarText(ReadMember(match.Template, "m_Title")),
                CountEnumerable(ReadMember(match.Template, "m_Objectives")));
        }
    }

    private static DiscoveryResult DiscoverQuestTemplates(object manager)
    {
        var templates = new List<DiscoveredTemplate>();
        var collectionSources = new List<string>();
        var seen = new HashSet<object>(ReferenceComparer.Instance);
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var managerType = manager.GetType();

        foreach (var field in managerType.GetFields(flags))
        {
            if (!LooksQuestRelated(field.Name, field.FieldType))
                continue;

            object? value;
            try
            {
                value = field.GetValue(manager);
            }
            catch
            {
                continue;
            }

            CollectTemplates($"field:{field.Name}", field.FieldType, value, templates, collectionSources, seen);
        }

        foreach (var property in managerType.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead ||
                !LooksQuestRelated(property.Name, property.PropertyType))
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(manager);
            }
            catch
            {
                continue;
            }

            CollectTemplates($"property:{property.Name}", property.PropertyType, value, templates, collectionSources, seen);
        }

        return new DiscoveryResult(templates, collectionSources.Distinct(StringComparer.Ordinal).ToList());
    }

    private static bool LooksQuestRelated(string memberName, Type memberType)
    {
        if (memberName.Contains("quest", StringComparison.OrdinalIgnoreCase) ||
            TypeLooksQuestRelated(memberType))
        {
            return true;
        }

        return memberType.IsGenericType &&
               memberType.GetGenericArguments().Any(TypeLooksQuestRelated);
    }

    private static bool TypeLooksQuestRelated(Type type) =>
        (type.FullName ?? type.Name).Contains("QuestTemplate", StringComparison.OrdinalIgnoreCase) ||
        type.Name.Contains("Quest", StringComparison.OrdinalIgnoreCase);

    private static void CollectTemplates(
        string sourceMember,
        Type declaredType,
        object? value,
        List<DiscoveredTemplate> templates,
        List<string> collectionSources,
        HashSet<object> seen)
    {
        if (value is null)
            return;

        collectionSources.Add($"{sourceMember} ({declaredType.FullName ?? declaredType.Name})");

        // TemplateManager exposes both Dictionary<ResourceId, QuestTemplate> and
        // TemplateLookup<QuestTemplate>. Treat containers before inspecting their
        // type arguments so a generic container is never mistaken for a template.
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is not null && IsQuestTemplate(entry.Value))
                    AddTemplate(sourceMember, entry.Value, templates, seen);
            }

            return;
        }

        if (IsQuestTemplate(value))
        {
            AddTemplate(sourceMember, value, templates, seen);
            return;
        }

        // TemplateLookup<T> wraps the authored dictionary in m_Lookup. Unwrap it
        // explicitly instead of relying on the wrapper being enumerable.
        var lookup = ReadMember(value, "m_Lookup");
        if (lookup is not null && !ReferenceEquals(lookup, value))
        {
            CollectTemplates(
                $"{sourceMember}.m_Lookup",
                lookup.GetType(),
                lookup,
                templates,
                collectionSources,
                seen);
            return;
        }

        if (value is not IEnumerable enumerable || value is string)
            return;

        foreach (var entry in enumerable)
        {
            if (entry is null)
                continue;

            if (IsQuestTemplate(entry))
            {
                AddTemplate(sourceMember, entry, templates, seen);
                continue;
            }

            var entryType = entry.GetType();
            if (!entryType.IsGenericType || entryType.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
                continue;

            var valueProperty = entryType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            var nested = valueProperty?.GetValue(entry);
            if (nested is not null && IsQuestTemplate(nested))
                AddTemplate(sourceMember, nested, templates, seen);
        }
    }

    private static bool IsQuestTemplate(object value) =>
        typeof(QuestTemplate).IsAssignableFrom(value.GetType());

    private static void AddTemplate(
        string sourceMember,
        object template,
        List<DiscoveredTemplate> templates,
        HashSet<object> seen)
    {
        if (!seen.Add(template))
            return;

        templates.Add(new DiscoveredTemplate(sourceMember, template));
    }

    private static JsonObject BuildSummary(string sourceMember, object template)
    {
        var objectives = ReadMember(template, "m_Objectives");
        return new JsonObject
        {
            ["sourceMember"] = sourceMember,
            ["runtimeType"] = template.GetType().FullName,
            ["id"] = ScalarText(ReadMember(template, "m_Id", "Id")),
            ["scriptName"] = ScalarText(ReadMember(template, "m_ScriptName")),
            ["name"] = ScalarText(ReadMember(template, "m_Name")),
            ["title"] = ScalarText(ReadMember(template, "m_Title")),
            ["titleOld"] = ScalarText(ReadMember(template, "m_TitleOld")),
            ["objectiveCount"] = CountEnumerable(objectives),
        };
    }

    private static JsonObject BuildDetailed(string sourceMember, object template)
    {
        var visited = new HashSet<object>(ReferenceComparer.Instance);
        return new JsonObject
        {
            ["sourceMember"] = sourceMember,
            ["runtimeType"] = template.GetType().FullName,
            ["m_Id"] = ToJsonNode(ReadMember(template, "m_Id", "Id"), 4, visited),
            ["m_ScriptName"] = ToJsonNode(ReadMember(template, "m_ScriptName"), 4, visited),
            ["m_Name"] = ToJsonNode(ReadMember(template, "m_Name"), 4, visited),
            ["m_Title"] = ToJsonNode(ReadMember(template, "m_Title"), 4, visited),
            ["m_TitleOld"] = ToJsonNode(ReadMember(template, "m_TitleOld"), 4, visited),
            ["m_Objectives"] = ToJsonNode(ReadMember(template, "m_Objectives"), 8, visited),
            ["authoredMembers"] = SerializeAuthoredMembers(template, 6, visited),
        };
    }

    private static bool IsCrayburnCandidate(object template)
    {
        var text = string.Join("\n",
            ScalarText(ReadMember(template, "m_ScriptName")),
            ScalarText(ReadMember(template, "m_Name")),
            ScalarText(ReadMember(template, "m_Title")),
            ScalarText(ReadMember(template, "m_TitleOld")));

        if (SearchTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return true;

        return ContainsSearchTerm(template, 4, new HashSet<object>(ReferenceComparer.Instance));
    }

    private static bool ContainsSearchTerm(object? value, int depth, HashSet<object> visited)
    {
        if (value is null || depth < 0)
            return false;

        if (value is string text)
            return SearchTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal || value is Guid || value is DateTime || value is DateTimeOffset)
            return SearchTerms.Any(term => ScalarText(value).Contains(term, StringComparison.OrdinalIgnoreCase));

        if (!type.IsValueType && !visited.Add(value))
            return false;

        try
        {
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ContainsSearchTerm(entry.Key, depth - 1, visited) ||
                        ContainsSearchTerm(entry.Value, depth - 1, visited))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (ContainsSearchTerm(item, depth - 1, visited))
                        return true;
                }

                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in type.GetFields(flags))
            {
                if (!ShouldDumpMember(field.Name))
                    continue;

                try
                {
                    if (ContainsSearchTerm(field.GetValue(value), depth - 1, visited))
                        return true;
                }
                catch
                {
                    // Continue with the remaining authored members.
                }
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    !ShouldDumpMember(property.Name))
                {
                    continue;
                }

                try
                {
                    if (ContainsSearchTerm(property.GetValue(value), depth - 1, visited))
                        return true;
                }
                catch
                {
                    // Continue with the remaining authored members.
                }
            }

            return SearchTerms.Any(term => ScalarText(value).Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (!type.IsValueType)
                visited.Remove(value);
        }
    }

    private static object? ReadMember(object instance, params string[] names)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();

        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field is not null)
            {
                try
                {
                    return field.GetValue(instance);
                }
                catch
                {
                    // Try the next compatible member name.
                }
            }

            var property = type.GetProperty(name, flags);
            if (property is null || property.GetIndexParameters().Length != 0 || !property.CanRead)
                continue;

            try
            {
                return property.GetValue(instance);
            }
            catch
            {
                // Try the next compatible member name.
            }
        }

        return null;
    }

    private static string ScalarText(object? value)
    {
        if (value is null)
            return string.Empty;

        try
        {
            return value.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int CountEnumerable(object? value)
    {
        if (value is null || value is string)
            return 0;

        if (value is ICollection collection)
            return collection.Count;

        if (value is not IEnumerable enumerable)
            return 0;

        var count = 0;
        foreach (var _ in enumerable)
            count++;
        return count;
    }

    private static JsonNode? ToJsonNode(object? value, int depth, HashSet<object> visited)
    {
        if (value is null)
            return null;

        if (depth <= 0)
            return JsonValue.Create(ScalarText(value));

        var type = value.GetType();
        if (value is string text)
            return JsonValue.Create(text);
        if (value is bool boolean)
            return JsonValue.Create(boolean);
        if (value is byte byteValue)
            return JsonValue.Create(byteValue);
        if (value is sbyte sbyteValue)
            return JsonValue.Create(sbyteValue);
        if (value is short shortValue)
            return JsonValue.Create(shortValue);
        if (value is ushort ushortValue)
            return JsonValue.Create(ushortValue);
        if (value is int intValue)
            return JsonValue.Create(intValue);
        if (value is uint uintValue)
            return JsonValue.Create(uintValue);
        if (value is long longValue)
            return JsonValue.Create(longValue);
        if (value is ulong ulongValue)
            return JsonValue.Create(ulongValue);
        if (value is float floatValue)
            return JsonValue.Create(floatValue);
        if (value is double doubleValue)
            return JsonValue.Create(doubleValue);
        if (value is decimal decimalValue)
            return JsonValue.Create(decimalValue);
        if (value is Guid guid)
            return JsonValue.Create(guid.ToString());
        if (value is DateTime dateTime)
            return JsonValue.Create(dateTime.ToString("O"));
        if (value is DateTimeOffset dateTimeOffset)
            return JsonValue.Create(dateTimeOffset.ToString("O"));
        if (type.IsEnum)
            return JsonValue.Create(value.ToString());

        var typeName = type.FullName ?? type.Name;
        if (typeName.Contains("ResourceId", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(ScalarText(value));

        if (!type.IsValueType && !visited.Add(value))
            return JsonValue.Create("<cycle>");

        try
        {
            if (value is IDictionary dictionary)
            {
                var result = new JsonObject();
                foreach (DictionaryEntry entry in dictionary)
                    result[ScalarText(entry.Key)] = ToJsonNode(entry.Value, depth - 1, visited);
                return result;
            }

            if (value is IEnumerable enumerable)
            {
                var result = new JsonArray();
                foreach (var item in enumerable)
                    result.Add(ToJsonNode(item, depth - 1, visited));
                return result;
            }

            return SerializeAuthoredMembers(value, depth - 1, visited);
        }
        finally
        {
            if (!type.IsValueType)
                visited.Remove(value);
        }
    }

    private static JsonObject SerializeAuthoredMembers(object instance, int depth, HashSet<object> visited)
    {
        var result = new JsonObject();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in type.GetFields(flags).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (!ShouldDumpMember(field.Name) || !names.Add(field.Name))
                continue;

            try
            {
                result[field.Name] = ToJsonNode(field.GetValue(instance), depth, visited);
            }
            catch (Exception ex)
            {
                result[field.Name] = $"<error:{ex.GetType().Name}>";
            }
        }

        foreach (var property in type.GetProperties(flags).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                !ShouldDumpMember(property.Name) || !names.Add(property.Name))
            {
                continue;
            }

            try
            {
                result[property.Name] = ToJsonNode(property.GetValue(instance), depth, visited);
            }
            catch (Exception ex)
            {
                result[property.Name] = $"<error:{ex.GetType().Name}>";
            }
        }

        if (result.Count == 0)
            result["value"] = ScalarText(instance);

        return result;
    }

    private static bool ShouldDumpMember(string name) =>
        name.StartsWith("m_", StringComparison.Ordinal) ||
        name.Equals("_t", StringComparison.Ordinal) ||
        name.Equals("Id", StringComparison.Ordinal) ||
        name.Equals("Name", StringComparison.Ordinal) ||
        name.Equals("Title", StringComparison.Ordinal) ||
        name.Equals("Type", StringComparison.Ordinal);

    private sealed record DiscoveredTemplate(string SourceMember, object Template);
    private sealed record DiscoveryResult(List<DiscoveredTemplate> Templates, List<string> CollectionSources);

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
