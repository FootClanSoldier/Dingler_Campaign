using System.Text.Json;
using System.Text.Json.Nodes;
using Dingler.Game.Protocol;

namespace Dingler.Game.Campaign;

public sealed record CampaignHandoffResult(
    CampaignRunRecord Panorama,
    CampaignRunRecord Quest,
    CampaignRunRecord Dungeon,
    bool QuestCreated,
    bool DungeonCreated);

public sealed record CampaignCompletionHandoffResult(
    CampaignRunRecord Panorama,
    CampaignRunRecord Quest,
    CampaignRunRecord Dungeon);

public sealed record CampaignQuestTurnInResult(
    CampaignRunRecord Panorama,
    CampaignRunRecord Quest);

/// <summary>
/// Campaign persistence uses one versioned JSON aggregate per profile/champion.
/// A champion owns deck/talent selection once while PANORAMA, QUEST and DUNGEON
/// remain independent campaign instances with stable CampID values.
/// </summary>
public sealed class CampaignRunStore
{
    private const int CurrentFormatVersion = 2;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly object _gate = new();
    private readonly Dictionary<(ulong ProfileId, ulong ChampionId), CampaignChampionRun?> _cache = new();
    private ulong _lastCampaignId;
    private bool _campaignIdsInitialized;

    public CampaignRunStore(string folder)
    {
        _folder = folder;
    }

    public CampaignRunRecord GetOrCreate(ulong profileId, ulong championId, int race)
    {
        lock (_gate)
        {
            var champion = GetOrCreateChampionLocked(profileId, championId, race);
            return SelectCurrentLocked(champion, saveIfChanged: true);
        }
    }

    public bool TryGet(ulong profileId, ulong championId, out CampaignRunRecord record)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            if (champion is null)
            {
                record = null!;
                return false;
            }

            record = SelectCurrentLocked(champion, saveIfChanged: true);
            return true;
        }
    }

    public IReadOnlyList<CampaignRunRecord> GetActive(
        ulong profileId,
        ulong championId,
        int race,
        int campaignType,
        string? templateName = null)
    {
        lock (_gate)
        {
            if (campaignType is not (0 or 1 or 4 or 6))
                return Array.Empty<CampaignRunRecord>();

            var champion = GetOrCreateChampionLocked(profileId, championId, race);
            IEnumerable<CampaignRunRecord> records = champion.Campaigns.Where(record => !IsFinished(record));

            if (campaignType != 0)
            {
                var typeName = campaignType switch
                {
                    1 => "DUNGEON",
                    4 => "QUEST",
                    6 => "PANORAMA",
                    _ => string.Empty,
                };
                records = records.Where(record =>
                    string.Equals(record.CampaignType, typeName, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(templateName))
            {
                records = records.Where(record =>
                    string.Equals(record.TemplateName, templateName, StringComparison.OrdinalIgnoreCase));
            }

            return records.OrderBy(record => record.CampaignId).ToArray();
        }
    }

    public bool TryGetByCampaignId(ulong profileId, ulong campaignId, out CampaignRunRecord record)
    {
        lock (_gate)
        {
            foreach (var champion in _cache.Values)
            {
                if (champion is null || champion.ProfileId != profileId)
                    continue;

                var cached = champion.Campaigns.FirstOrDefault(candidate => candidate.CampaignId == campaignId);
                if (cached is not null)
                {
                    AttachOwnerLocked(champion, cached);
                    record = cached;
                    return true;
                }
            }

            if (Directory.Exists(_folder))
            {
                foreach (var path in Directory.EnumerateFiles(_folder, $"campaign-{profileId}-*.json"))
                {
                    var loaded = LoadPathLocked(path);
                    if (loaded is null)
                        continue;

                    _cache[(loaded.ProfileId, loaded.ChampionId)] = loaded;
                    var candidate = loaded.Campaigns.FirstOrDefault(item => item.CampaignId == campaignId);
                    if (candidate is not null)
                    {
                        AttachOwnerLocked(loaded, candidate);
                        record = candidate;
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
        {
            var champion = LoadCachedLocked(record.ProfileId, record.ChampionId);
            if (champion is null)
                throw new InvalidOperationException($"Campaign owner {record.ProfileId}/{record.ChampionId} is not loaded");

            var persisted = champion.Campaigns.FirstOrDefault(candidate => candidate.CampaignId == record.CampaignId);
            if (persisted is null)
                throw new InvalidOperationException($"Campaign {record.CampaignId} is not owned by champion {record.ChampionId}");

            if (!ReferenceEquals(persisted, record))
            {
                var index = champion.Campaigns.IndexOf(persisted);
                champion.Campaigns[index] = record;
            }

            NormalizeRecordIdentityLocked(champion, record);
            SaveLocked(champion);
        }
    }

    public void SetRace(ulong profileId, ulong championId, int race)
    {
        if (race is < 1 or > 8)
            return;

        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            if (champion is null)
                return;
            champion.Race = race;
            SaveLocked(champion);
        }
    }

    public CampaignRunRecord SetChampionDeck(ulong profileId, ulong championId, int race, ulong deckId)
    {
        lock (_gate)
        {
            var champion = GetOrCreateChampionLocked(profileId, championId, race);
            champion.LastDeckId = deckId;
            SaveLocked(champion);
            return SelectCurrentLocked(champion, saveIfChanged: false);
        }
    }

    public CampaignRunRecord SetChampionTalents(
        ulong profileId,
        ulong championId,
        int race,
        IEnumerable<string> talents)
    {
        lock (_gate)
        {
            var champion = GetOrCreateChampionLocked(profileId, championId, race);
            champion.ChampionTalents = talents
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            SaveLocked(champion);
            return SelectCurrentLocked(champion, saveIfChanged: false);
        }
    }

    /// <summary>
    /// Atomically updates a scene campaign, ensures one QUEST and one DUNGEON child,
    /// and makes the dungeon the champion's current rendered campaign. The factories
    /// are only called when their exact type/template identity does not already exist.
    /// </summary>
    public CampaignHandoffResult ApplySceneHandoff(
        CampaignRunRecord panorama,
        string questTemplateName,
        Func<ulong, CampaignRunRecord> questFactory,
        string dungeonTemplateName,
        Func<ulong, CampaignRunRecord> dungeonFactory,
        Action<CampaignRunRecord> updatePanorama)
    {
        lock (_gate)
        {
            var champion = GetOrCreateChampionLocked(panorama.ProfileId, panorama.ChampionId, panorama.Race);
            var persistedPanorama = champion.Campaigns.FirstOrDefault(record =>
                record.CampaignId == panorama.CampaignId
                && string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase));
            if (persistedPanorama is null)
                throw new InvalidOperationException($"Panorama campaign {panorama.CampaignId} is not owned by champion {panorama.ChampionId}");

            updatePanorama(persistedPanorama);
            NormalizeRecordIdentityLocked(champion, persistedPanorama);

            var quest = champion.Campaigns.FirstOrDefault(record =>
                string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.TemplateName, questTemplateName, StringComparison.OrdinalIgnoreCase));
            var questCreated = quest is null;
            if (quest is null)
            {
                quest = questFactory(NewCampaignIdLocked());
                quest.CampaignType = "QUEST";
                quest.TemplateName = questTemplateName;
                NormalizeRecordIdentityLocked(champion, quest);
                champion.Campaigns.Add(quest);
            }

            var dungeon = champion.Campaigns.FirstOrDefault(record =>
                string.Equals(record.CampaignType, "DUNGEON", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.TemplateName, dungeonTemplateName, StringComparison.OrdinalIgnoreCase));
            var dungeonCreated = dungeon is null;
            if (dungeon is null)
            {
                dungeon = dungeonFactory(NewCampaignIdLocked());
                dungeon.CampaignType = "DUNGEON";
                dungeon.TemplateName = dungeonTemplateName;
                NormalizeRecordIdentityLocked(champion, dungeon);
                champion.Campaigns.Add(dungeon);
            }

            champion.CurrentCampaignId = dungeon.CampaignId;
            SaveLocked(champion);
            return new CampaignHandoffResult(
                persistedPanorama, quest, dungeon, questCreated, dungeonCreated);
        }
    }

    /// <summary>
    /// Atomically completes the active Crayburn dungeon, advances its linked
    /// journal quest and makes the retained panorama the champion's current scene.
    /// All three campaign records keep their existing stable CampID values.
    /// </summary>
    public CampaignCompletionHandoffResult ApplyCrayburnCompletion(
        CampaignRunRecord dungeon,
        string questTemplateName,
        Action<CampaignRunRecord> updateDungeon,
        Action<CampaignRunRecord> updateQuest,
        Action<CampaignRunRecord> updatePanorama)
    {
        lock (_gate)
        {
            var champion = GetOrCreateChampionLocked(dungeon.ProfileId, dungeon.ChampionId, dungeon.Race);
            var persistedDungeon = champion.Campaigns.FirstOrDefault(record =>
                record.CampaignId == dungeon.CampaignId
                && string.Equals(record.CampaignType, "DUNGEON", StringComparison.OrdinalIgnoreCase));
            if (persistedDungeon is null)
                throw new InvalidOperationException($"Dungeon campaign {dungeon.CampaignId} is not owned by champion {dungeon.ChampionId}");

            var quest = champion.Campaigns.FirstOrDefault(record =>
                string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.TemplateName, questTemplateName, StringComparison.OrdinalIgnoreCase));
            if (quest is null)
                throw new InvalidOperationException($"Quest campaign '{questTemplateName}' is not owned by champion {dungeon.ChampionId}");

            var panoramas = champion.Campaigns
                .Where(record => string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.CampaignId)
                .ToArray();
            var panorama = panoramas.FirstOrDefault(record =>
                record.State["PublicState"] is JsonObject publicState
                && publicState["Data"] is JsonObject data
                && data["IsStarterPano"]?.GetValue<bool>() == true)
                ?? panoramas.FirstOrDefault();
            if (panorama is null)
                throw new InvalidOperationException($"Champion {dungeon.ChampionId} has no panorama campaign for the Crayburn return handoff");

            updateDungeon(persistedDungeon);
            updateQuest(quest);
            updatePanorama(panorama);

            NormalizeRecordIdentityLocked(champion, persistedDungeon);
            NormalizeRecordIdentityLocked(champion, quest);
            NormalizeRecordIdentityLocked(champion, panorama);

            champion.CurrentCampaignId = panorama.CampaignId;
            SaveLocked(champion);
            return new CampaignCompletionHandoffResult(panorama, quest, persistedDungeon);
        }
    }

    /// <summary>
    /// Finishes the linked quest and its panorama report node in one persisted aggregate.
    /// A completed quest or a stale/mismatched conversation cannot claim the turn-in twice.
    /// </summary>
    public CampaignQuestTurnInResult? TryApplyCrayburnQuestTurnIn(
        CampaignRunRecord panorama,
        string questTemplateName,
        Func<CampaignRunRecord, bool> questReady,
        Func<CampaignRunRecord, bool> panoramaReady,
        Action<CampaignRunRecord> finishQuest,
        Action<CampaignRunRecord> finishPanorama)
    {
        lock (_gate)
        {
            var champion = GetOrCreateChampionLocked(panorama.ProfileId, panorama.ChampionId, panorama.Race);
            var persistedPanorama = champion.Campaigns.FirstOrDefault(record =>
                record.CampaignId == panorama.CampaignId
                && string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase));
            var quest = champion.Campaigns.FirstOrDefault(record =>
                string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.TemplateName, questTemplateName, StringComparison.OrdinalIgnoreCase));

            if (persistedPanorama is null || quest is null || IsFinished(quest)
                || !panoramaReady(persistedPanorama) || !questReady(quest))
                return null;

            finishQuest(quest);
            finishPanorama(persistedPanorama);
            NormalizeRecordIdentityLocked(champion, quest);
            NormalizeRecordIdentityLocked(champion, persistedPanorama);
            champion.CurrentCampaignId = persistedPanorama.CampaignId;
            SaveLocked(champion);
            return new CampaignQuestTurnInResult(persistedPanorama, quest);
        }
    }

    public void Delete(CampaignRunRecord record)
    {
        lock (_gate)
        {
            var path = PathFor(record.ProfileId, record.ChampionId);
            if (File.Exists(path))
                File.Delete(path);
            _cache[(record.ProfileId, record.ChampionId)] = null;
        }
    }

    private CampaignChampionRun GetOrCreateChampionLocked(ulong profileId, ulong championId, int race)
    {
        var champion = LoadCachedLocked(profileId, championId);
        if (champion is not null)
            return champion;

        champion = new CampaignChampionRun
        {
            FormatVersion = CurrentFormatVersion,
            ProfileId = profileId,
            ChampionId = championId,
            Race = race is >= 1 and <= 8 ? race : 1,
        };

        var panorama = new CampaignRunRecord
        {
            CampaignId = NewCampaignIdLocked(),
            CampaignType = "PANORAMA",
            TemplateName = "AZ1",
        };
        AttachOwnerLocked(champion, panorama);
        panorama.State = CampaignStateFactory.CreateStarterPanoramaState(panorama);
        champion.Campaigns.Add(panorama);
        champion.CurrentCampaignId = panorama.CampaignId;
        SaveLocked(champion);
        return champion;
    }

    private CampaignChampionRun? LoadCachedLocked(ulong profileId, ulong championId)
    {
        var key = (profileId, championId);
        if (!_cache.TryGetValue(key, out var champion))
        {
            champion = LoadPathLocked(PathFor(profileId, championId));
            _cache[key] = champion;
        }

        if (champion is null)
            return null;

        if (EnsureAggregateIdentityLocked(champion, profileId, championId))
        {
            StaticLogger.LogWarning(
                "Campaign: repaired persisted campaign aggregate identity for profile {profile}, champion {champion}",
                profileId, championId);
            SaveLocked(champion);
        }
        else
        {
            AttachOwnerLocked(champion);
        }

        return champion;
    }

    private CampaignRunRecord SelectCurrentLocked(CampaignChampionRun champion, bool saveIfChanged)
    {
        var current = champion.Campaigns.FirstOrDefault(record =>
            record.CampaignId == champion.CurrentCampaignId
            && !IsFinished(record)
            && !string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase));

        current ??= champion.Campaigns
            .Where(record => !IsFinished(record)
                             && record.Started
                             && string.Equals(record.CampaignType, "DUNGEON", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.CampaignId)
            .FirstOrDefault();

        current ??= champion.Campaigns
            .Where(record => !IsFinished(record)
                             && string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.CampaignId)
            .FirstOrDefault();

        current ??= champion.Campaigns
            .Where(record => !IsFinished(record)
                             && !string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.CampaignId)
            .FirstOrDefault();

        current ??= champion.Campaigns
            .Where(record => !string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.CampaignId)
            .FirstOrDefault();

        if (current is null)
        {
            current = new CampaignRunRecord
            {
                CampaignId = NewCampaignIdLocked(),
                CampaignType = "PANORAMA",
                TemplateName = "AZ1",
            };
            AttachOwnerLocked(champion, current);
            current.State = CampaignStateFactory.CreateStarterPanoramaState(current);
            champion.Campaigns.Add(current);
        }

        AttachOwnerLocked(champion, current);
        if (champion.CurrentCampaignId != current.CampaignId)
        {
            champion.CurrentCampaignId = current.CampaignId;
            if (saveIfChanged)
                SaveLocked(champion);
        }

        return current;
    }

    private void SaveLocked(CampaignChampionRun champion)
    {
        Directory.CreateDirectory(_folder);
        champion.FormatVersion = CurrentFormatVersion;
        AttachOwnerLocked(champion);
        foreach (var record in champion.Campaigns)
            NormalizeRecordIdentityLocked(champion, record);

        var path = PathFor(champion.ProfileId, champion.ChampionId);
        WriteAtomic(path, JsonSerializer.Serialize(champion, Json));
        _cache[(champion.ProfileId, champion.ChampionId)] = champion;
        foreach (var record in champion.Campaigns)
            if (record.CampaignId > _lastCampaignId)
                _lastCampaignId = record.CampaignId;
    }

    private CampaignChampionRun? LoadPathLocked(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            CampaignChampionRun champion;

            if (document.RootElement.TryGetProperty("Campaigns", out var campaigns)
                && campaigns.ValueKind == JsonValueKind.Array)
            {
                champion = JsonSerializer.Deserialize<CampaignChampionRun>(text)
                           ?? throw new InvalidDataException("campaign aggregate could not be parsed");
            }
            else
            {
                var legacy = JsonSerializer.Deserialize<LegacyCampaignRunRecord>(text)
                             ?? throw new InvalidDataException("legacy campaign record could not be parsed");
                if (legacy.CampaignId == 0 || legacy.ChampionId == 0 || legacy.State.Count == 0)
                    throw new InvalidDataException("legacy campaign record is incomplete");

                champion = new CampaignChampionRun
                {
                    FormatVersion = CurrentFormatVersion,
                    ProfileId = legacy.ProfileId,
                    ChampionId = legacy.ChampionId,
                    Race = legacy.Race is >= 1 and <= 8 ? legacy.Race : 1,
                    LastDeckId = legacy.LastDeckId,
                    ChampionTalents = legacy.ChampionTalents ?? new List<string>(),
                    CurrentCampaignId = legacy.CampaignId,
                    Campaigns =
                    [
                        new CampaignRunRecord
                        {
                            CampaignId = legacy.CampaignId,
                            CampaignType = string.IsNullOrWhiteSpace(legacy.CampaignType) ? "PANORAMA" : legacy.CampaignType,
                            TemplateName = string.IsNullOrWhiteSpace(legacy.TemplateName) ? "AZ1" : legacy.TemplateName,
                            Started = legacy.Started,
                            State = legacy.State,
                        },
                    ],
                };

                AttachOwnerLocked(champion);
                WriteAtomic(path, JsonSerializer.Serialize(champion, Json));
                StaticLogger.LogInformation(
                    "Campaign: migrated legacy campaign save {path} to aggregate format v{version}",
                    path, CurrentFormatVersion);
            }

            champion.Campaigns ??= new List<CampaignRunRecord>();
            champion.ChampionTalents ??= new List<string>();
            if (champion.Campaigns.Count == 0 || champion.ChampionId == 0)
                throw new InvalidDataException("campaign aggregate is incomplete");

            champion.FormatVersion = CurrentFormatVersion;
            AttachOwnerLocked(champion);
            foreach (var record in champion.Campaigns)
            {
                NormalizeRecordIdentityLocked(champion, record);
                if (record.CampaignId > _lastCampaignId)
                    _lastCampaignId = record.CampaignId;
            }

            return champion;
        }
        catch (Exception ex)
        {
            StaticLogger.LogError("Campaign: file {path} is damaged ({error}); set aside as .corrupt", path, ex.Message);
            try
            {
                File.Move(path, path + ".corrupt", overwrite: true);
            }
            catch
            {
                // Leave the original file in place if it cannot be moved.
            }
            return null;
        }
    }

    private bool EnsureAggregateIdentityLocked(CampaignChampionRun champion, ulong profileId, ulong championId)
    {
        var changed = false;
        if (champion.ProfileId != profileId)
        {
            champion.ProfileId = profileId;
            changed = true;
        }
        if (champion.ChampionId != championId)
        {
            champion.ChampionId = championId;
            changed = true;
        }
        if (champion.Race is < 1 or > 8)
        {
            champion.Race = 1;
            changed = true;
        }

        foreach (var record in champion.Campaigns)
            changed |= NormalizeRecordIdentityLocked(champion, record);

        if (champion.CurrentCampaignId == 0
            || champion.Campaigns.All(record => record.CampaignId != champion.CurrentCampaignId))
        {
            champion.CurrentCampaignId = champion.Campaigns
                .FirstOrDefault(record => !string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase))
                ?.CampaignId ?? champion.Campaigns[0].CampaignId;
            changed = true;
        }

        AttachOwnerLocked(champion);
        return changed;
    }

    private static bool NormalizeRecordIdentityLocked(CampaignChampionRun champion, CampaignRunRecord record)
    {
        var changed = false;
        AttachOwnerLocked(champion, record);

        if (!StateUInt64Equals(record.State["CampID"], record.CampaignId))
        {
            record.State["CampID"] = record.CampaignId;
            changed = true;
        }
        if (!StateUInt64Equals(record.State["ChampID"], champion.ChampionId))
        {
            record.State["ChampID"] = champion.ChampionId;
            changed = true;
        }
        return changed;
    }

    private static void AttachOwnerLocked(CampaignChampionRun champion)
    {
        foreach (var record in champion.Campaigns)
            AttachOwnerLocked(champion, record);
    }

    private static void AttachOwnerLocked(CampaignChampionRun champion, CampaignRunRecord record)
    {
        record.ProfileId = champion.ProfileId;
        record.ChampionId = champion.ChampionId;
        record.Race = champion.Race;
        record.LastDeckId = champion.LastDeckId;
        record.ChampionTalents = champion.ChampionTalents;
    }

    private static bool StateUInt64Equals(JsonNode? node, ulong expected) =>
        node is not null && ulong.TryParse(node.ToString(), out var actual) && actual == expected;

    private static bool IsFinished(CampaignRunRecord record) =>
        record.State["Finished"] is JsonNode finished && !string.IsNullOrWhiteSpace(finished.ToString());

    private ulong NewCampaignIdLocked()
    {
        EnsureCampaignIdsInitializedLocked();
        var id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (id <= _lastCampaignId)
            id = _lastCampaignId + 1;
        return _lastCampaignId = id;
    }

    private void EnsureCampaignIdsInitializedLocked()
    {
        if (_campaignIdsInitialized)
            return;
        _campaignIdsInitialized = true;
        if (!Directory.Exists(_folder))
            return;

        foreach (var path in Directory.EnumerateFiles(_folder, "campaign-*-*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.TryGetProperty("Campaigns", out var campaigns) && campaigns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var campaign in campaigns.EnumerateArray())
                    {
                        if (campaign.TryGetProperty("CampaignId", out var idElement)
                            && idElement.TryGetUInt64(out var id)
                            && id > _lastCampaignId)
                            _lastCampaignId = id;
                    }
                }
                else if (root.TryGetProperty("CampaignId", out var legacyId)
                         && legacyId.TryGetUInt64(out var id)
                         && id > _lastCampaignId)
                {
                    _lastCampaignId = id;
                }
            }
            catch
            {
                // Normal load will report malformed files if/when the profile opens them.
            }
        }
    }

    private static void WriteAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    private string PathFor(ulong profileId, ulong championId) =>
        Path.Combine(_folder, $"campaign-{profileId}-{championId}.json");

    private sealed class LegacyCampaignRunRecord
    {
        public ulong ProfileId { get; set; }
        public ulong ChampionId { get; set; }
        public ulong CampaignId { get; set; }
        public int Race { get; set; }
        public string CampaignType { get; set; } = "PANORAMA";
        public string TemplateName { get; set; } = "AZ1";
        public bool Started { get; set; }
        public ulong LastDeckId { get; set; }
        public List<string>? ChampionTalents { get; set; }
        public JsonObject State { get; set; } = new();
    }
}
