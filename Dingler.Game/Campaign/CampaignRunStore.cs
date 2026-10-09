using System.Text.Json;
using System.Text.Json.Nodes;
using Dingler.Game.Protocol;

namespace Dingler.Game.Campaign;

/// <summary>
/// Campaign persistence currently uses one small JSON file per profile/champion.
/// This mirrors the proven ArenaRunStore pattern and avoids an EF migration while
/// the client contract is still being validated.
/// </summary>
public sealed class CampaignRunStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly object _gate = new();
    private readonly Dictionary<(ulong ProfileId, ulong ChampionId), CampaignRunRecord?> _cache = new();
    private ulong _lastCampaignId;

    public CampaignRunStore(string folder)
    {
        _folder = folder;
    }

    public CampaignRunRecord GetOrCreate(ulong profileId, ulong championId, int race)
    {
        lock (_gate)
        {
            var key = (profileId, championId);
            if (!_cache.TryGetValue(key, out var record))
                _cache[key] = record = Load(profileId, championId);

            if (record is not null)
            {
                if (EnsureIdentityLocked(record, profileId, championId))
                {
                    StaticLogger.LogWarning(
                        "Campaign: repaired persisted campaign identity for profile {profile}, champion {champion}, campaign {campaign}",
                        profileId, championId, record.CampaignId);
                    SaveLocked(record);
                }
                return record;
            }

            record = new CampaignRunRecord
            {
                ProfileId = profileId,
                ChampionId = championId,
                CampaignId = NewCampaignIdLocked(),
                Race = race is >= 1 and <= 8 ? race : 1,
                CampaignType = "PANORAMA",
                TemplateName = "AZ1",
            };
            record.State = CampaignStateFactory.CreateStarterPanoramaState(record);
            SaveLocked(record);
            return record;
        }
    }

    public bool TryGet(ulong profileId, ulong championId, out CampaignRunRecord record)
    {
        lock (_gate)
        {
            var key = (profileId, championId);
            if (!_cache.TryGetValue(key, out var cached))
                _cache[key] = cached = Load(profileId, championId);
            record = cached!;
            return cached is not null;
        }
    }

    public bool TryGetByCampaignId(ulong profileId, ulong campaignId, out CampaignRunRecord record)
    {
        lock (_gate)
        {
            foreach (var cached in _cache.Values)
            {
                if (cached is not null && cached.ProfileId == profileId && cached.CampaignId == campaignId)
                {
                    record = cached;
                    return true;
                }
            }

            if (Directory.Exists(_folder))
            {
                foreach (var path in Directory.EnumerateFiles(_folder, $"campaign-{profileId}-*.json"))
                {
                    var loaded = LoadPath(path);
                    if (loaded is null)
                        continue;
                    _cache[(loaded.ProfileId, loaded.ChampionId)] = loaded;
                    if (loaded.CampaignId == campaignId)
                    {
                        record = loaded;
                        return true;
                    }
                }
            }

            record = null!;
            return false;
        }
    }

    public void Save(CampaignRunRecord record)
    {
        lock (_gate)
            SaveLocked(record);
    }

    public void SetRace(ulong profileId, ulong championId, int race)
    {
        if (race is < 1 or > 8)
            return;
        lock (_gate)
        {
            if (!TryGet(profileId, championId, out var record))
                return;
            record.Race = race;
            SaveLocked(record);
        }
    }

    public CampaignRunRecord SetChampionDeck(ulong profileId, ulong championId, int race, ulong deckId)
    {
        lock (_gate)
        {
            var record = GetOrCreate(profileId, championId, race);
            record.LastDeckId = deckId;
            SaveLocked(record);
            return record;
        }
    }

    public CampaignRunRecord SetChampionTalents(ulong profileId, ulong championId, int race, IEnumerable<string> talents)
    {
        lock (_gate)
        {
            var record = GetOrCreate(profileId, championId, race);
            record.ChampionTalents = talents
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            SaveLocked(record);
            return record;
        }
    }

    public void Delete(CampaignRunRecord record)
    {
        lock (_gate)
        {
            var path = PathFor(record.ProfileId, record.ChampionId);
            if (File.Exists(path)) File.Delete(path);
            _cache[(record.ProfileId, record.ChampionId)] = null;
        }
    }

    private void SaveLocked(CampaignRunRecord record)
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor(record.ProfileId, record.ChampionId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
        File.Move(tmp, path, overwrite: true);
        _cache[(record.ProfileId, record.ChampionId)] = record;
        if (record.CampaignId > _lastCampaignId) _lastCampaignId = record.CampaignId;
    }

    private CampaignRunRecord? Load(ulong profileId, ulong championId) =>
        LoadPath(PathFor(profileId, championId));

    private CampaignRunRecord? LoadPath(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<CampaignRunRecord>(File.ReadAllText(path));
            if (record is null || record.CampaignId == 0 || record.ChampionId == 0 || record.State.Count == 0)
                throw new InvalidDataException("campaign record is incomplete");
            if (record.CampaignId > _lastCampaignId) _lastCampaignId = record.CampaignId;
            return record;
        }
        catch (Exception ex)
        {
            StaticLogger.LogError("Campaign: file {path} is damaged ({error}); set aside as .corrupt", path, ex.Message);
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch { /* leave it */ }
            return null;
        }
    }

    private static bool EnsureIdentityLocked(CampaignRunRecord record, ulong profileId, ulong championId)
    {
        var changed = false;

        if (record.ProfileId != profileId)
        {
            record.ProfileId = profileId;
            changed = true;
        }

        if (record.ChampionId != championId)
        {
            record.ChampionId = championId;
            changed = true;
        }

        if (!StateUInt64Equals(record.State["CampID"], record.CampaignId))
        {
            record.State["CampID"] = record.CampaignId;
            changed = true;
        }

        if (!StateUInt64Equals(record.State["ChampID"], championId))
        {
            record.State["ChampID"] = championId;
            changed = true;
        }

        return changed;
    }

    private static bool StateUInt64Equals(JsonNode? node, ulong expected) =>
        node is not null && ulong.TryParse(node.ToString(), out var actual) && actual == expected;

    private ulong NewCampaignIdLocked()
    {
        var id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (id <= _lastCampaignId) id = _lastCampaignId + 1;
        return _lastCampaignId = id;
    }

    private string PathFor(ulong profileId, ulong championId) =>
        Path.Combine(_folder, $"campaign-{profileId}-{championId}.json");
}
