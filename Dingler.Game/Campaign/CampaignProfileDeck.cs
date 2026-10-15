using System.Text.Json;

namespace Dingler.Game.Campaign;

/// <summary>An original HEX profile-deck template owned by one campaign profile.</summary>
public sealed class CampaignProfileDeck
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ChampionTemplateGuid { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public sealed partial class CampaignRunStore
{
    /// <summary>Profile templates are account-owned, unlike the champion's campaign runs.</summary>
    public IReadOnlyList<CampaignProfileDeck> GetProfileDecks(ulong profileId)
    {
        lock (_gate)
            return ReadProfileDecksLocked(profileId).Select(CloneProfileDeck).ToArray();
    }

    public bool TryGetProfileDeck(ulong profileId, ulong deckId, out CampaignProfileDeck deck)
    {
        lock (_gate)
        {
            var found = ReadProfileDecksLocked(profileId).FirstOrDefault(item => item.Id == deckId);
            if (found is not null)
            {
                deck = CloneProfileDeck(found);
                return true;
            }
            deck = null!;
            return false;
        }
    }

    public CampaignProfileDeck SaveProfileDeck(ulong profileId, ulong existingId,
        string championTemplateGuid, string name, byte[] data, ulong editingChampionId = 0)
    {
        if (profileId == 0 || data.Length is 0 or > 131072 || string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Invalid profile deck template");
        lock (_gate)
        {
            var decks = ReadProfileDecksLocked(profileId);
            CampaignProfileDeck? record = null;
            if (existingId != 0)
            {
                record = decks.FirstOrDefault(item => item.Id == existingId)
                    ?? throw new InvalidOperationException("Profile deck does not belong to the player");
                if (!string.Equals(record.ChampionTemplateGuid, championTemplateGuid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cannot change the owner of a profile deck template");
                if (IsProfileDeckAssignedElsewhereLocked(profileId, existingId, editingChampionId))
                {
                    // Shared profile templates are copied on edit. The native save reply
                    // lets the client bind the new ID to its current champion's party.
                    record = null;
                }
            }
            if (record is null)
            {
                if (decks.Count >= 500)
                    throw new InvalidOperationException("Profile deck limit reached");
                record = new CampaignProfileDeck
                {
                    Id = NextProfileDeckIdLocked(profileId, decks),
                };
                decks.Add(record);
            }
            record.Name = name;
            record.ChampionTemplateGuid = championTemplateGuid;
            record.Data = data.ToArray();
            Directory.CreateDirectory(ProfileFolder(profileId));
            WriteAtomic(ProfileDeckPath(profileId), JsonSerializer.Serialize(decks, Json));
            return CloneProfileDeck(record);
        }
    }

    /// <summary>On champion deletion, keep only profile templates referenced by surviving parties.</summary>
    public IReadOnlyList<ulong> RemoveUnreferencedProfileDecks(ulong profileId, ulong deletedChampionId)
    {
        lock (_gate)
        {
            var deleted = LoadCachedLocked(profileId, deletedChampionId);
            if (deleted is not { IsPlayerCreated: true, IsDeleted: true } || deleted.ProfileId != profileId)
                throw new InvalidOperationException("Champion deletion has not been prepared");

            // Fail closed: an unreadable champion file must not cause its live decks to be deleted.
            var referenced = new HashSet<ulong>();
            var folder = ProfileFolder(profileId);
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.EnumerateFiles(folder, $"campaign-{profileId}-*.json"))
                {
                    var survivor = LoadPathLocked(path);
                    if (survivor is null || survivor.ProfileId != profileId)
                        throw new InvalidDataException($"Cannot verify campaign owner in {path}");
                    if (survivor is { IsPlayerCreated: true, IsDeleted: false })
                        referenced.UnionWith((survivor.PartyMembers ?? new List<CampaignMercenaryPartyMember>())
                            .Select(static member => member.DeckTemplateId).Where(static id => id != 0));
                }
            }
            var decks = ReadProfileDecksLocked(profileId);
            var removed = decks.Where(deck => !referenced.Contains(deck.Id)).Select(static deck => deck.Id).ToArray();
            if (removed.Length == 0)
                return removed;
            // Record the last used ID while the departing champion still exists.
            // Future saves must not recycle templates removed by this sweep.
            NextProfileDeckIdLocked(profileId, decks);
            decks.RemoveAll(deck => !referenced.Contains(deck.Id));
            WriteAtomic(ProfileDeckPath(profileId), JsonSerializer.Serialize(decks, Json));
            return removed;
        }
    }

    private ulong NextProfileDeckIdLocked(ulong profileId, IReadOnlyList<CampaignProfileDeck> decks)
    {
        // The sequence survives garbage collection, so a retired template ID is never
        // silently reassigned to a different deck still cached by the HEX client.
        var folder = ProfileFolder(profileId);
        var path = Path.Combine(folder, $"profile-deck-sequence-{profileId}.json");
        var highest = decks.Count == 0 ? 0UL : decks.Max(static deck => deck.Id);
        if (File.Exists(path))
        {
            highest = Math.Max(highest, JsonSerializer.Deserialize<ulong>(File.ReadAllText(path)));
        }
        else if (Directory.Exists(folder))
        {
            // Initialize the high watermark from the champion references when the
            // sequence is first created, before any unreferenced templates are removed.
            foreach (var championPath in Directory.EnumerateFiles(folder, $"campaign-{profileId}-*.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(championPath));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("ProfileId", out var owner) ||
                    !owner.TryGetUInt64(out var recordedProfile) || recordedProfile != profileId ||
                    !root.TryGetProperty("PartyMembers", out var party) || party.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var member in party.EnumerateArray())
                    if (member.ValueKind == JsonValueKind.Object &&
                        member.TryGetProperty("DeckTemplateId", out var selected) &&
                        selected.TryGetUInt64(out var id))
                        highest = Math.Max(highest, id);
            }
        }
        var next = checked(highest + 1);
        Directory.CreateDirectory(ProfileFolder(profileId));
        WriteAtomic(path, JsonSerializer.Serialize(next));
        return next;
    }

    private bool IsProfileDeckAssignedElsewhereLocked(ulong profileId, ulong deckId, ulong editingChampionId)
    {
        return EnumerateCreatedLocked(profileId).Any(champion =>
            (editingChampionId == 0 || champion.ChampionId != editingChampionId)
            && champion.PartyMembers.Any(member => member.DeckTemplateId == deckId));
    }

    private List<CampaignProfileDeck> ReadProfileDecksLocked(ulong profileId)
    {
        var path = ProfileDeckPath(profileId);
        if (!File.Exists(path))
            return new List<CampaignProfileDeck>();
        return JsonSerializer.Deserialize<List<CampaignProfileDeck>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Could not deserialize saved profile decks");
    }

    private string ProfileDeckPath(ulong profileId) =>
        Path.Combine(ProfileFolder(profileId), $"profile-decks-{profileId}.json");

    private static CampaignProfileDeck CloneProfileDeck(CampaignProfileDeck value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        ChampionTemplateGuid = value.ChampionTemplateGuid,
        Data = value.Data.ToArray(),
    };
}
