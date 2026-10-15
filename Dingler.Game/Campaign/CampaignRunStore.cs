using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
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

public sealed record CampaignAreaTravelResult(
    CampaignRunRecord Panorama,
    CampaignRunRecord Area,
    bool Created);

/// <summary>
/// Campaign persistence uses one versioned JSON aggregate per profile/champion.
/// A champion owns deck/talent selection once while PANORAMA, QUEST, DUNGEON and AREA
/// remain independent campaign instances with stable CampID values.
/// </summary>
public sealed partial class CampaignRunStore
{
    private const int CurrentFormatVersion = 3;
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

    /// <summary>
    /// Existing explicitly created champions only. Files without the creation marker are
    /// preserved but not included in a normal profile stream.
    /// </summary>
    public IReadOnlyList<CampaignChampionRun> GetCreatedChampions(ulong profileId)
    {
        lock (_gate)
            return EnumerateCreatedLocked(profileId).OrderBy(static champion => champion.ChampionId).ToArray();
    }

    public bool TryGetOwnedChampion(ulong profileId, ulong championId, out CampaignChampionRun champion)
    {
        lock (_gate)
        {
            var saved = LoadCachedLocked(profileId, championId);
            if (saved is { IsPlayerCreated: true, IsDeleted: false } && saved.ProfileId == profileId)
            {
                champion = saved;
                return true;
            }
            champion = null!;
            return false;
        }
    }

    /// <summary>Replace a created champion's party as one persisted aggregate update.</summary>
    public bool TrySaveChampionParty(ulong profileId, ulong championId,
        IReadOnlyList<CampaignMercenaryPartyMember> members)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            if (champion is not { IsPlayerCreated: true, IsDeleted: false } || champion.ProfileId != profileId)
                return false;
            // Profile decks belong to the account. A champion's party stores its own
            // selected template ID; copying is deferred until a shared deck is edited.
            champion.PartyMembers = members.Select(member => new CampaignMercenaryPartyMember
            {
                MercenaryItemGuid = member.MercenaryItemGuid,
                DeckTemplateId = member.DeckTemplateId,
                Upgrade = member.Upgrade,
            }).ToList();
            SaveLocked(champion);
            return true;
        }
    }

    /// <summary>Allow resuming an interrupted deletion without reactivating the champion.</summary>
    public bool TryGetChampionForDeletion(ulong profileId, ulong championId, out CampaignChampionRun champion)
    {
        lock (_gate)
        {
            var saved = LoadCachedLocked(profileId, championId);
            if (saved is { IsPlayerCreated: true } && saved.ProfileId == profileId)
            {
                champion = saved;
                return true;
            }
            champion = null!;
            return false;
        }
    }

    /// <summary>Hide a champion until its deck cleanup and file removal finish.</summary>
    public bool TryDeleteChampion(ulong profileId, ulong championId, out CampaignChampionRun deleted)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            if (champion is not { IsPlayerCreated: true } || champion.ProfileId != profileId)
            {
                deleted = null!;
                return false;
            }
            if (champion.IsDeleted)
            {
                deleted = champion;
                return true;
            }
            champion.IsDeleted = true;
            try
            {
                SaveLocked(champion);
            }
            catch
            {
                champion.IsDeleted = false;
                throw;
            }
            deleted = champion;
            return true;
        }
    }

    /// <summary>Finish a deletion after all owned-deck cleanup has succeeded.</summary>
    public void PermanentlyDeleteChampion(ulong profileId, ulong championId)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            if (champion is not { IsPlayerCreated: true, IsDeleted: true } || champion.ProfileId != profileId)
                throw new InvalidOperationException("Champion deletion has not been prepared");
            File.Delete(PathFor(profileId, championId));
            _cache.Remove((profileId, championId));
        }
    }

    /// <summary>Create a player-owned champion and its starter PANORAMA as one JSON aggregate.</summary>
    public CampaignChampionRun CreateChampion(ulong profileId, string name, int race, int championClass,
        int gender, string petName)
    {
        lock (_gate)
        {
            var existing = EnumerateCreatedLocked(profileId);
            if (existing.Count >= 20)
                throw new InvalidOperationException("Champion limit reached (20)");
            if (existing.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A champion with this name already exists", nameof(name));

            ulong championId;
            Span<byte> bytes = stackalloc byte[8];
            do
            {
                RandomNumberGenerator.Fill(bytes);
                // HEX UID stores its type in the low byte and the instance ID in 56 bits.
                // Keep player-created IDs in the upper half of that valid instance range.
                championId = (BitConverter.ToUInt64(bytes) & 0x007FFFFFFFFFFFFFUL) | (1UL << 55);
            }
            while (File.Exists(PathFor(profileId, championId)) ||
                   _cache.ContainsKey((profileId, championId)));

            var champion = new CampaignChampionRun
            {
                ProfileId = profileId,
                ChampionId = championId,
                IsPlayerCreated = true,
                Name = name,
                Race = race,
                ChampionClass = championClass,
                Gender = gender,
                PetName = petName,
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
    }

    /// <summary>Only rollback a fresh, unplayed creation if deck provisioning fails.</summary>
    public void RollbackUninitializedChampion(ulong profileId, ulong championId)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            if (champion is not { IsPlayerCreated: true, LastDeckId: 0 } ||
                champion.Campaigns.Count != 1 || champion.Campaigns[0].Started)
                return;
            var path = PathFor(profileId, championId);
            if (File.Exists(path))
                File.Delete(path);
            _cache.Remove((profileId, championId));
        }
    }

    private List<CampaignChampionRun> EnumerateCreatedLocked(ulong profileId)
    {
        var result = new List<CampaignChampionRun>();
        var folder = ProfileFolder(profileId);
        if (!Directory.Exists(folder))
            return result;
        foreach (var path in Directory.EnumerateFiles(folder, $"campaign-{profileId}-*.json"))
        {
            // Avoid migrating/rewriting the dormant legacy saves merely to list heroes.
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("IsPlayerCreated", out var marker) ||
                    marker.ValueKind != JsonValueKind.True ||
                    !root.TryGetProperty("ChampionId", out var idValue) ||
                    !idValue.TryGetUInt64(out var id))
                    continue;
                var champion = LoadCachedLocked(profileId, id);
                if (champion is { IsPlayerCreated: true, IsDeleted: false } && champion.ProfileId == profileId)
                    result.Add(champion);
            }
            catch (Exception ex)
            {
                StaticLogger.LogWarning("Campaign: could not enumerate champion file {path}: {error}", path, ex.Message);
            }
        }
        return result;
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
            if (champion is null || champion.IsDeleted)
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
            if (campaignType is not (0 or 1 or 2 or 4 or 6))
                return Array.Empty<CampaignRunRecord>();

            var champion = GetOrCreateChampionLocked(profileId, championId, race);
            IEnumerable<CampaignRunRecord> records = champion.Campaigns.Where(record => !IsFinished(record));

            if (campaignType != 0)
            {
                var typeName = campaignType switch
                {
                    1 => "DUNGEON",
                    2 => "AREA",
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
                if (champion is null || champion.IsDeleted || champion.ProfileId != profileId)
                    continue;

                var cached = champion.Campaigns.FirstOrDefault(candidate => candidate.CampaignId == campaignId);
                if (cached is not null)
                {
                    AttachOwnerLocked(champion, cached);
                    record = cached;
                    return true;
                }
            }

            var folder = ProfileFolder(profileId);
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.EnumerateFiles(folder, $"campaign-{profileId}-*.json"))
                {
                    var loaded = LoadPathLocked(path);
                    if (loaded is null || loaded.IsDeleted || loaded.ProfileId != profileId)
                        continue;

                    _cache[(loaded.ProfileId, loaded.ChampionId)] = loaded;
                    var candidate = loaded.Campaigns.FirstOrDefault(item => item.CampaignId == campaignId);
                    if (candidate is not null)
                    {
                        AttachOwnerLocked(loaded, candidate);
                        if (RepairCompletedCrayburnTravelLocked(loaded))
                        {
                            SaveLocked(loaded);
                            StaticLogger.LogInformation(
                                "Campaign: restored post-Crayburn travel conversation for champion {champion}",
                                loaded.ChampionId);
                        }
                        record = candidate;
                        return true;
                    }
                }
            }

            record = null!;
            return false;
        }
    }

    /// <summary>
    /// Read an existing faction quest, including a finished journal entry.
    /// Looking up conversation prerequisites must never create a quest.
    /// </summary>
    public bool TryGetQuest(ulong profileId, ulong championId, string script, out CampaignRunRecord quest)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            var found = champion is { IsDeleted: false } ? champion.Campaigns.FirstOrDefault(record =>
                string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.TemplateName, script, StringComparison.OrdinalIgnoreCase)) : null;
            if (found is null)
            {
                quest = null!;
                return false;
            }
            AttachOwnerLocked(champion!, found);
            quest = found;
            return true;
        }
    }

    public void Save(CampaignRunRecord record, JsonObject? appliedRewards = null)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(record.ProfileId, record.ChampionId);
            if (champion is null || champion.IsDeleted)
                throw new InvalidOperationException($"Campaign owner {record.ProfileId}/{record.ChampionId} is not active");

            var persisted = champion.Campaigns.FirstOrDefault(candidate => candidate.CampaignId == record.CampaignId);
            if (persisted is null)
                throw new InvalidOperationException($"Campaign {record.CampaignId} is not owned by champion {record.ChampionId}");

            if (!ReferenceEquals(persisted, record))
            {
                var index = champion.Campaigns.IndexOf(persisted);
                champion.Campaigns[index] = record;
            }

            NormalizeRecordIdentityLocked(champion, record);
            if (appliedRewards is not null)
                CampaignChampionProgression.ApplyGrantedXp(champion, appliedRewards);
            SaveLocked(champion);
        }
    }

    /// <summary>
    /// Consume an active Shroom Haus offer once and persist the selected card
    /// with its AREA state. The profile stream later projects this receipt as
    /// a collection instance; a repeated client event cannot claim it twice.
    /// </summary>
    public bool TryClaimAz1ShroomReward(CampaignRunRecord area, string choice)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(area.ProfileId, area.ChampionId);
            var saved = champion?.Campaigns.FirstOrDefault(candidate =>
                candidate.CampaignId == area.CampaignId && candidate.CampaignType == "AREA"
                && candidate.TemplateName == CampaignAz1Config.TemplateName);
            if (champion is { IsDeleted: true } || saved is null || !CampaignShroomRewardCatalog.IsChoice(choice)
                || !string.Equals(saved.State["ALoc"]?.ToString(), CampaignAz1Config.FourthTitle, StringComparison.Ordinal))
                return false;
            var location = (saved.State["VisLocs"] as JsonArray)?.OfType<JsonObject>()
                .Select(static loc => loc["Data"] as JsonObject)
                .FirstOrDefault(loc => loc?["node"]?.ToString() == CampaignAz1Config.FourthNode);
            if (location is null || location["enabled"]?.GetValue<bool>() != true
                || location["shroom_intro_completed"]?.GetValue<bool>() != true
                || location["completed"]?.GetValue<bool>() == true
                || location["type"]?.ToString() != "ShroomHaus"
                || (location["choices"] as JsonArray)?.Any(item =>
                    string.Equals(item?.ToString(), choice, StringComparison.OrdinalIgnoreCase)) != true)
                return false;

            location["shroom_reward_choice"] = choice.ToLowerInvariant();
            location["completed"] = true;
            location["type"] = "None";
            location["autostart"] = false;
            location["shroomhaus_ready"] = false;
            location["choices"] = null;
            saved.State["ALoc"] = null;
            saved.State["CurState"] = "EXPLORE";
            SaveLocked(champion!);
            return true;
        }
    }

    public void SetRace(ulong profileId, ulong championId, int race)
    {
        if (race is < 1 or > 8)
            return;

        lock (_gate)
        {
            var champion = LoadCachedLocked(profileId, championId);
            if (champion is null || champion.IsDeleted)
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
        Action<CampaignRunRecord, CampaignChampionRun> finishQuest,
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

            finishQuest(quest, champion);
            finishPanorama(persistedPanorama);
            NormalizeRecordIdentityLocked(champion, quest);
            NormalizeRecordIdentityLocked(champion, persistedPanorama);
            champion.CurrentCampaignId = persistedPanorama.CampaignId;
            SaveLocked(champion);
            return new CampaignQuestTurnInResult(persistedPanorama, quest);
        }
    }

    /// <summary>
    /// An explicit race travel choice creates or resumes one AREA without
    /// replacing the completed Crayburn quest or the retained starter panorama.
    /// Rejects stale/repeated events and unopened travel conversations.
    /// </summary>
    public CampaignAreaTravelResult? TryBeginAz1Travel(
        CampaignRunRecord panorama,
        Func<ulong, CampaignRunRecord> areaFactory)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(panorama.ProfileId, panorama.ChampionId);
            if (champion is null || champion.IsDeleted || champion.CurrentCampaignId != panorama.CampaignId)
                return null;

            var source = champion.Campaigns.FirstOrDefault(record =>
                record.CampaignId == panorama.CampaignId
                && string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase));
            var quest = champion.Campaigns.FirstOrDefault(record =>
                string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.TemplateName, CampaignCrayburnConfig.QuestScriptName,
                    StringComparison.OrdinalIgnoreCase));
            var cfg = CampaignStateFactory.Race(champion.Race);
            if (source is null || source.State["Finished"] is not null
                || source.State["PostCrayburnReport"]?.GetValue<bool>() != true
                || !string.Equals(source.State["ALoc"]?.ToString(), cfg.IntroNpc, StringComparison.Ordinal)
                || quest is null || !IsFinished(quest)
                || !string.Equals(quest.State["FinishReason"]?.ToString(), "Complete", StringComparison.OrdinalIgnoreCase))
                return null;

            var step2Done = (quest.State["VisLocs"] as JsonArray)?.OfType<JsonObject>()
                .Select(static item => item["Data"] as JsonObject)
                .Any(data => data is not null
                    && string.Equals(data["node"]?.ToString(), CampaignCrayburnConfig.QuestReportLocation, StringComparison.Ordinal)
                    && data["completed"]?.GetValue<bool>() == true) == true;
            var npcReady = (source.State["VisLocs"] as JsonArray)?.OfType<JsonObject>()
                .Select(static item => item["Data"] as JsonObject)
                .Any(data => data is not null
                    && string.Equals(data["node"]?.ToString(), cfg.IntroNpc, StringComparison.Ordinal)
                    && string.Equals(data["conversationId"]?.ToString(), cfg.TransitionConversation, StringComparison.OrdinalIgnoreCase)
                    && data["visible"]?.GetValue<bool>() == true
                    && data["enabled"]?.GetValue<bool>() == true) == true;
            if (!step2Done || !npcReady)
                return null;

            var area = champion.Campaigns.FirstOrDefault(record =>
                string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.TemplateName, CampaignAz1Config.TemplateName,
                    StringComparison.OrdinalIgnoreCase));
            var created = area is null;
            if (area is null)
            {
                area = areaFactory(NewCampaignIdLocked());
                area.CampaignType = "AREA";
                area.TemplateName = CampaignAz1Config.TemplateName;
                NormalizeRecordIdentityLocked(champion, area);
                champion.Campaigns.Add(area);
            }

            source.State["ALoc"] = null;
            source.State["CurState"] = "EXPLORE";
            champion.CurrentCampaignId = area.CampaignId;
            SaveLocked(champion);
            return new CampaignAreaTravelResult(source, area, created);
        }
    }

    /// <summary>
    /// Complete an authored AZ1 welcome and create any missing journal campaigns
    /// in one champion aggregate. Replay is safe for an older save whose welcome
    /// was completed before its QUEST children were implemented.
    /// </summary>
    public IReadOnlyList<CampaignRunRecord> EnsureAz1OpeningQuests(
        CampaignRunRecord area,
        IReadOnlyList<CampaignAz1AuthoredCatalog.Quest> authored,
        Action<CampaignRunRecord>? updateArea = null)
    {
        lock (_gate)
        {
            var champion = LoadCachedLocked(area.ProfileId, area.ChampionId);
            if (champion is null || champion.IsDeleted || champion.CurrentCampaignId != area.CampaignId)
                return Array.Empty<CampaignRunRecord>();
            var persisted = champion.Campaigns.FirstOrDefault(item => item.CampaignId == area.CampaignId
                && string.Equals(item.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.TemplateName, CampaignAz1Config.TemplateName, StringComparison.OrdinalIgnoreCase));
            if (persisted is null)
                return Array.Empty<CampaignRunRecord>();
            var location = (persisted.State["VisLocs"] as JsonArray)?.OfType<JsonObject>()
                .Select(static item => item["Data"] as JsonObject)
                .FirstOrDefault(item => item?["node"]?.ToString() == CampaignAz1Config.NextNode);
            if (location is null)
                return Array.Empty<CampaignRunRecord>();
            if (updateArea is null && location["completed"]?.GetValue<bool>() != true)
                return Array.Empty<CampaignRunRecord>();
            if (updateArea is not null && (location["completed"]?.GetValue<bool>() == true
                || !string.Equals(persisted.State["ALoc"]?.ToString(), CampaignAz1Config.NextTitle, StringComparison.Ordinal)))
                return Array.Empty<CampaignRunRecord>();
            updateArea?.Invoke(persisted);
            var created = new List<CampaignRunRecord>();
            foreach (var quest in authored)
            {
                if (champion.Campaigns.Any(item => string.Equals(item.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.TemplateName, quest.Script, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var child = new CampaignRunRecord
                {
                    CampaignId = NewCampaignIdLocked(),
                    CampaignType = "QUEST",
                    TemplateName = quest.Script,
                    Started = true,
                };
                NormalizeRecordIdentityLocked(champion, child);
                child.State = CampaignStateFactory.CreateAz1QuestState(child, quest);
                champion.Campaigns.Add(child);
                created.Add(child);
            }
            SaveLocked(champion);
            return created;
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
        if (champion is { IsDeleted: true })
            throw new InvalidOperationException("Deleted campaign champion cannot be reactivated");
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

        var repairedIdentity = EnsureAggregateIdentityLocked(champion, profileId, championId);
        var repairedTravel = RepairCompletedCrayburnTravelLocked(champion);
        if (repairedIdentity || repairedTravel)
        {
            if (repairedIdentity)
                StaticLogger.LogWarning(
                    "Campaign: repaired persisted campaign aggregate identity for profile {profile}, champion {champion}",
                    profileId, championId);
            if (repairedTravel)
                StaticLogger.LogInformation(
                    "Campaign: restored post-Crayburn travel conversation for champion {champion}",
                    championId);
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
        Directory.CreateDirectory(ProfileFolder(champion.ProfileId));
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
            champion.PartyMembers ??= new List<CampaignMercenaryPartyMember>();
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

    private static bool RepairCompletedCrayburnTravelLocked(CampaignChampionRun champion)
    {
        // Repair only an actually completed Crayburn report, never an untouched
        // tutorial, an unfinished quest, or a champion that has entered AREA.
        if (champion.Campaigns.Any(record =>
                string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)))
            return false;

        var quest = champion.Campaigns.FirstOrDefault(record =>
            string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.TemplateName, CampaignCrayburnConfig.QuestScriptName,
                StringComparison.OrdinalIgnoreCase));
        if (quest is null || quest.State["Finished"] is null
            || !string.Equals(quest.State["FinishReason"]?.ToString(), "Complete", StringComparison.OrdinalIgnoreCase))
            return false;

        var step2Completed = (quest.State["VisLocs"] as JsonArray)?.OfType<JsonObject>()
            .Select(static location => location["Data"] as JsonObject)
            .Any(data => data is not null
                && (string.Equals(data["node"]?.ToString(), CampaignCrayburnConfig.QuestReportLocation, StringComparison.Ordinal)
                    || string.Equals(data["name"]?.ToString(), CampaignCrayburnConfig.QuestReportLocation, StringComparison.Ordinal))
                && data["completed"]?.GetValue<bool>() == true) == true;
        if (!step2Completed)
            return false;

        var cfg = CampaignStateFactory.Race(champion.Race);
        var panorama = champion.Campaigns.FirstOrDefault(record =>
            string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase)
            && record.CampaignId == champion.CurrentCampaignId);
        if (panorama is null || panorama.State["Finished"] is not null)
            return false;

        var giverCompleted = (panorama.State["VisLocs"] as JsonArray)?.OfType<JsonObject>()
            .Select(static location => location["Data"] as JsonObject)
            .Any(data => data is not null
                && (string.Equals(data["node"]?.ToString(), cfg.QuestNpc, StringComparison.Ordinal)
                    || string.Equals(data["name"]?.ToString(), cfg.QuestNpc, StringComparison.Ordinal))
                && data["completed"]?.GetValue<bool>() == true) == true;
        return giverCompleted && CampaignStateFactory.PreparePostCrayburnTravel(panorama.State, cfg);
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

        foreach (var profileFolder in Directory.EnumerateDirectories(_folder))
        {
            if (!ulong.TryParse(Path.GetFileName(profileFolder), out var profileId))
                continue;

            foreach (var path in Directory.EnumerateFiles(profileFolder, $"campaign-{profileId}-*.json"))
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
    }

    private static void WriteAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    private string ProfileFolder(ulong profileId) =>
        Path.Combine(_folder, profileId.ToString());

    private string PathFor(ulong profileId, ulong championId) =>
        Path.Combine(ProfileFolder(profileId), $"campaign-{profileId}-{championId}.json");

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
