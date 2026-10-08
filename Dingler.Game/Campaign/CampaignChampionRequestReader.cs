extern alias HexGame;
using System.Collections;
using System.Reflection;
using HexGame::Game.Shared;

namespace Dingler.Game.Campaign;

internal static class CampaignChampionRequestReader
{
    public static bool TryReadUInt64(object request, out ulong value, params string[] candidateNames)
    {
        foreach (var member in CandidateMembers(request.GetType(), candidateNames))
        {
            if (TryConvertUInt64(GetValue(member, request), out value))
                return true;
        }

        value = 0;
        return false;
    }

    public static bool TryReadTalentIds(object request, out List<string> talentIds)
    {
        string[] candidateNames =
        [
            "TalentIDs", "TalentIds", "ChampionTalentIDs", "ChampionTalentIds",
            "ChampionTalents", "Talents", "TalentIDsToSet", "TalentIdsToSet"
        ];

        foreach (var member in CandidateMembers(request.GetType(), candidateNames))
        {
            var raw = GetValue(member, request);
            if (raw is not IEnumerable enumerable || raw is string)
                continue;

            talentIds = new List<string>();
            foreach (var item in enumerable)
            {
                if (TryReadGuid(item, out var guid))
                    talentIds.Add(guid.ToString("D"));
            }
            return true;
        }

        talentIds = new List<string>();
        return false;
    }

    public static string DescribePublicMembers(object request) =>
        string.Join(", ", request.GetType()
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(static member => member.MemberType is MemberTypes.Property or MemberTypes.Field)
            .Select(static member => member.Name)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<MemberInfo> CandidateMembers(Type type, IReadOnlyCollection<string> candidateNames)
    {
        var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(static member => member.MemberType is MemberTypes.Property or MemberTypes.Field)
            .ToList();

        foreach (var candidate in candidateNames)
        {
            var exact = members.FirstOrDefault(member =>
                string.Equals(member.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                yield return exact;
        }

        // Client builds sometimes rename ID/Id fields. If an exact candidate was not enough,
        // compare names after removing punctuation/underscore differences.
        var normalizedCandidates = candidateNames.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            if (normalizedCandidates.Contains(Normalize(member.Name)))
                yield return member;
        }
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static object? GetValue(MemberInfo member, object instance) => member switch
    {
        PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(instance),
        FieldInfo field => field.GetValue(instance),
        _ => null,
    };

    private static bool TryConvertUInt64(object? raw, out ulong value)
    {
        switch (raw)
        {
            case null:
                value = 0;
                return false;
            case UID uid:
                value = uid.GetInstanceId();
                return true;
            case ulong v:
                value = v;
                return true;
            case long v when v >= 0:
                value = (ulong)v;
                return true;
            case uint v:
                value = v;
                return true;
            case int v when v >= 0:
                value = (ulong)v;
                return true;
            case ushort v:
                value = v;
                return true;
            case short v when v >= 0:
                value = (ulong)v;
                return true;
            case byte v:
                value = v;
                return true;
        }

        var type = raw.GetType();
        foreach (var name in new[] { "InstanceId", "InstanceID", "Id", "ID", "m_Id", "m_ID" })
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is not null && TryConvertUInt64(property.GetValue(raw), out value))
                return true;
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field is not null && TryConvertUInt64(field.GetValue(raw), out value))
                return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadGuid(object? raw, out Guid guid)
    {
        if (raw is null)
        {
            guid = Guid.Empty;
            return false;
        }

        if (raw is ResourceId resourceId)
        {
            guid = resourceId.m_Guid;
            return guid != Guid.Empty;
        }

        if (raw is Guid direct)
        {
            guid = direct;
            return guid != Guid.Empty;
        }

        var type = raw.GetType();
        foreach (var name in new[] { "m_Guid", "Guid", "guid" })
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property?.GetValue(raw) is Guid propertyGuid)
            {
                guid = propertyGuid;
                return guid != Guid.Empty;
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field?.GetValue(raw) is Guid fieldGuid)
            {
                guid = fieldGuid;
                return guid != Guid.Empty;
            }
        }

        return Guid.TryParse(raw.ToString(), out guid) && guid != Guid.Empty;
    }
}
