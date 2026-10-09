extern alias HexGame;

using System.Text.Json;
using System.Text.Json.Nodes;
using Dingler.Server;
using Microsoft.Extensions.Logging;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Campaign.Messages;

namespace Dingler.Game.Campaign;

/// <summary>
/// ServiceCampaign state machine for campaign bootstrap, authored conversation/encounter
/// progression and the handoff between campaign state and Dingler battle sessions.
/// </summary>
public sealed class CampaignService
{
    private readonly CampaignRunStore _store;
    private readonly CampaignOptions _options;
    private readonly ILogger<CampaignService>? _logger;

    public CampaignService(CampaignRunStore store, CampaignOptions options, ILogger<CampaignService>? logger = null)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    public byte[] HandleEnvelope(SessionContext context, byte[]? envelope)
    {
        using var document = JsonDocument.Parse(envelope is { Length: > 0 } ? envelope : "{}"u8.ToArray());
        var root = document.RootElement;
        var requestType = String(root, "RequestType");
        _logger?.LogInformation("Campaign: {user} request {request}", context.UserName, requestType);

        JsonNode response = requestType switch
        {
            "qcur4champ" => QueryCurrent(context, root),
            "getactive" => GetActive(context, root),
            "createcamp" => CreateCampaign(context, root),
            "getcampstate" => GetCampaignState(context, root),
            "startcamp" => StartCampaign(context, root),
            "getcampsum" => GetCampaignSummary(context, root),
            "locaction" => LocationAction(context, root),
            "sendevent" => SendEvent(context, root),
            "forfeit" => Forfeit(context, root),
            _ => Unsupported(root, requestType),
        };

        return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    private JsonNode QueryCurrent(SessionContext context, JsonElement root)
    {
        var championId = ULong(root, "ChampID");
        if (championId == 0)
            return CampaignStateFactory.BuildFailure(0, "ChampID is required");
        var record = _store.GetOrCreate(context.ProfileId, championId, _options.DefaultRace);
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode CreateCampaign(SessionContext context, JsonElement root)
    {
        // Only the starter PANORAMA is currently materialized. The client can still call
        // createcamp; returning the same idempotent record is safer than inventing a dungeon.
        return QueryCurrent(context, root);
    }

    private JsonNode GetActive(SessionContext context, JsonElement root)
    {
        var championId = ULong(root, "ChampID");
        if (championId == 0)
            return new JsonArray();

        var requestedType = Int(root, "CampType", 0);
        if (requestedType is not (0 or 6))
            return new JsonArray();

        var record = _store.GetOrCreate(context.ProfileId, championId, _options.DefaultRace);
        return new JsonArray { CampaignStateFactory.BuildTemplateInfo(record) };
    }

    private JsonNode GetCampaignState(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        return _store.TryGetByCampaignId(context.ProfileId, campaignId, out var record)
            ? CampaignStateFactory.BuildInputResponse(record)
            : CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");
    }

    private JsonNode StartCampaign(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");

        record.Started = true;
        record.State["Started"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        record.State["CurState"] = "EXPLORE";
        _store.Save(record);
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode GetCampaignSummary(SessionContext context, JsonElement root)
    {
        var result = new JsonArray();
        foreach (var campaignId in CampaignIds(root))
        {
            if (_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
                result.Add(CampaignStateFactory.BuildCampSummary(record));
        }
        return result;
    }

    private JsonNode LocationAction(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");

        var action = Int(root, "RAct", 0);
        var location = String(root, "Loc");
        if (action == 0 && !string.IsNullOrWhiteSpace(location))
        {
            record.State["ALoc"] = location;
            record.State["LastNode"] = location;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
        }
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode SendEvent(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");

        var eventName = String(root, "Event");
        var activeLocation = record.State["ALoc"]?.GetValue<string>();
        var eventParams = root.TryGetProperty("OParms", out var parameters)
            ? parameters.GetRawText()
            : "null";
        _logger?.LogInformation(
            "Campaign: {user} sendevent camp={campaign} event={event} active={active} params={params}",
            context.UserName,
            campaignId,
            eventName,
            activeLocation,
            eventParams);

        if (eventName == "choice_battle_yes")
            SetTutorialBattleUnlocked(record, true);
        else if (eventName == "choice_battle_no")
            SetTutorialBattleUnlocked(record, false);
        else if (eventName == "conv_done")
            CompleteConversation(record);
        else if (eventName == "enc_cancel")
        {
            var cfg = CampaignStateFactory.Race(record.Race);
            var active = record.State["ALoc"]?.GetValue<string>();
            var location = string.IsNullOrWhiteSpace(active)
                ? null
                : FindLocation(record.State, active);
            var conversationId = location?["conversationId"]?.GetValue<string>();
            var isBattleOutcomeConversation = location?["type"]?.GetValue<string>() == "Convo"
                && (string.Equals(conversationId, cfg.TrainingSuccessConversation, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(conversationId, cfg.TrainingFailConversation, StringComparison.OrdinalIgnoreCase));

            if (isBattleOutcomeConversation)
            {
                _logger?.LogInformation(
                    "Campaign: ignored stale enc_cancel while authored training result conversation {conversation} is active",
                    conversationId);
            }
            else
            {
                record.State["ALoc"] = null;
                record.State["CurState"] = "EXPLORE";
                _store.Save(record);
            }
        }
        else if (eventName == "start")
        {
            QueueGameStarted(context, record);
        }
        return CampaignStateFactory.BuildInputResponse(record);
    }


    /// <summary>
    /// Apply the authoritative battle result to campaign state and push the updated
    /// GameplayState back through ServiceCampaign. Battle simulation stays in the game
    /// layer; this method owns the campaign progression consequence.
    /// </summary>
    public bool ApplyBattleResult(
        SessionContext context,
        ulong campaignId,
        string encounterGuid,
        bool won)
    {
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
        {
            _logger?.LogWarning(
                "Campaign: battle result ignored; campaign {campaign} was not found for {user}",
                campaignId, context.UserName);
            return false;
        }

        var activeEncounter = record.State["ActiveEncounterGuid"]?.GetValue<string>() ?? string.Empty;
        if (!string.Equals(activeEncounter, encounterGuid, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning(
                "Campaign: battle result ignored for {user}; active encounter {active} does not match finished encounter {finished}",
                context.UserName, activeEncounter, encounterGuid);
            return false;
        }

        var cfg = CampaignStateFactory.Race(record.Race);
        if (!string.Equals(encounterGuid, cfg.TrainingEncounter, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning(
                "Campaign: battle result for encounter {encounter} has no implemented campaign progression rule",
                encounterGuid);
            return false;
        }

        ApplyTrainingBattleResult(record, cfg, won);
        _store.Save(record);

        var notifyEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = record.CampaignId,
            ["RequestType"] = "gameendnotify",
            ["ChampID"] = record.ChampionId,
            ["GameState"] = record.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["WinLose"] = won,
        };

        var sent = context.TrySendMessageToClient(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(notifyEnvelope),
        });

        if (sent)
        {
            _logger?.LogInformation(
                "Campaign: pushed gameendnotify camp={campaign} encounter={encounter} won={won} active={active}",
                record.CampaignId, encounterGuid, won, record.State["ALoc"]?.ToString());
        }
        else
        {
            _logger?.LogWarning(
                "Campaign: could not queue gameendnotify for {user} after campaign {campaign} battle",
                context.UserName, record.CampaignId);
        }

        return sent;
    }

    private static void ApplyTrainingBattleResult(
        CampaignRunRecord record,
        CampaignRaceConfig cfg,
        bool won)
    {
        var trainer = FindLocation(record.State, cfg.TrainerNpc);
        if (trainer is null)
        {
            if (record.State["VisLocs"] is not JsonArray locations)
            {
                locations = new JsonArray();
                record.State["VisLocs"] = locations;
            }

            var location = CampaignStateFactory.ConversationLocation(
                cfg.TrainerNpc,
                won ? cfg.TrainingSuccessConversation : cfg.TrainingFailConversation);
            locations.Add(location);
            trainer = location["Data"] as JsonObject;
        }

        if (trainer is not null)
        {
            trainer["type"] = "Convo";
            trainer["conversationId"] = won
                ? cfg.TrainingSuccessConversation
                : cfg.TrainingFailConversation;
            trainer["encounter"] = null;
            trainer["completed"] = false;
            trainer["enabled"] = true;
            trainer["visible"] = true;
            trainer["repeatable"] = false;
            trainer["autostart"] = false;
        }

        record.State["ALoc"] = cfg.TrainerNpc;
        record.State["LastNode"] = cfg.TrainerNpc;
        record.State["CurState"] = "EXPLORE";
        record.State.Remove("ActiveEncounterGuid");

        if (won)
        {
            record.State["TrainingVictoryPending"] = true;
            record.State["TutorialDone"] = true;
            record.State["Wins"] = StateInt(record.State, "Wins") + 1;
            EnsureQuestGiverLocation(record.State, cfg);
        }
        else
        {
            record.State.Remove("TrainingVictoryPending");
            record.State["Losses"] = StateInt(record.State, "Losses") + 1;
        }
    }

    private static void EnsureQuestGiverLocation(JsonObject state, CampaignRaceConfig cfg)
    {
        if (FindLocation(state, cfg.QuestNpc) is not null)
            return;

        if (state["VisLocs"] is not JsonArray locations)
        {
            locations = new JsonArray();
            state["VisLocs"] = locations;
        }

        locations.Add(CampaignStateFactory.ConversationLocation(
            cfg.QuestNpc, cfg.QuestConversation, giveQuest: true));
    }

    private static JsonObject? FindLocation(JsonObject state, string nameOrNode)
    {
        if (state["VisLocs"] is not JsonArray locations)
            return null;

        foreach (var item in locations.OfType<JsonObject>())
        {
            if (item["Data"] is not JsonObject data)
                continue;

            var node = data["node"]?.GetValue<string>();
            var name = data["name"]?.GetValue<string>();
            if (string.Equals(node, nameOrNode, StringComparison.Ordinal)
                || string.Equals(name, nameOrNode, StringComparison.Ordinal))
                return data;
        }

        return null;
    }

    private static int StateInt(JsonObject state, string name) =>
        int.TryParse(state[name]?.ToString(), out var value) ? value : 0;


    private void QueueGameStarted(SessionContext context, CampaignRunRecord record)
    {
        var cfg = CampaignStateFactory.Race(record.Race);
        var active = record.State["ALoc"]?.GetValue<string>();
        var encounterGuid = ActiveEncounter(record, active);

        // The starter panorama can recover the trainer encounter from its race metadata
        // when the active location itself no longer carries an encounter reference.
        // Do not invent encounters for unrelated locations.
        if (string.IsNullOrWhiteSpace(encounterGuid)
            && string.Equals(active, cfg.TrainerNpc, StringComparison.Ordinal))
        {
            encounterGuid = cfg.TrainingEncounter;
        }

        if (string.IsNullOrWhiteSpace(encounterGuid) || !Guid.TryParse(encounterGuid, out _))
        {
            _logger?.LogWarning(
                "Campaign: {user} start ignored; active location {active} has no valid encounter",
                context.UserName, active);
            return;
        }

        var encounter = new SessionStateEncounterData
        {
            SceneTemplateId = new ResourceId(encounterGuid),
            SessionFlags = ESessionFlags.IsEncounter | ESessionFlags.IsPvE,
            MatchPreviousWinners = new List<ulong>(),
            FirstPlayer = UID.Invalid,
        };

        // The launch notification intentionally carries an unassigned session id.
        // The client's subsequent StartEncounter request asks the LoadBalancer for
        // the authoritative session, matching the original campaign flow.
        var sessionName = $"camp_{record.CampaignId}";
        var sessionState = new SessionState
        {
            SessionId = UID.Invalid,
            SessionName = sessionName,
            MinimumPlayerCount = 1,
            MaximumPlayerCount = 2,
            EncounterData = encounter,
            JoinInsteadOfReconnect = false,
        };

        var sessionBytes = context.Encoder.Encode(sessionState);
        var deckUid64 = record.LastDeckId == 0
            ? 0UL
            : (record.LastDeckId << 8) | Convert.ToUInt64(UID.Type.Deck);

        var notifyEnvelope = new JsonObject
        {
            ["ReckID"] = context.ProfileId,
            ["CampID"] = record.CampaignId,
            ["RequestType"] = "gamestarted",
            ["GameSession"] = Convert.ToBase64String(sessionBytes),
            ["DeckID"] = new JsonObject
            {
                ["m_UID64"] = deckUid64,
            },
        };

        record.State["ActiveEncounterGuid"] = encounterGuid;
        _store.Save(record);

        // Unsolicited campaign notifications are CampSysGeneral.Request, not
        // Response. Queue it behind the normal sendevent reply so reqid=0 is
        // observed in the same order as the original service.
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(notifyEnvelope),
        });

        _logger?.LogInformation(
            "Campaign: queued gamestarted camp={campaign} session={session} encounter={encounter} deck={deck} sessionBytes={bytes}",
            record.CampaignId, sessionName, encounterGuid, record.LastDeckId, sessionBytes.Length);
    }

    private static string? ActiveEncounter(CampaignRunRecord record, string? active)
    {
        if (string.IsNullOrWhiteSpace(active) || record.State["VisLocs"] is not JsonArray locations)
            return null;

        foreach (var item in locations.OfType<JsonObject>())
        {
            if (item["Data"] is not JsonObject data)
                continue;

            var node = data["node"]?.GetValue<string>();
            var name = data["name"]?.GetValue<string>();
            if (!string.Equals(node, active, StringComparison.Ordinal)
                && !string.Equals(name, active, StringComparison.Ordinal))
                continue;

            return data["encounter"]?.GetValue<string>();
        }

        return null;
    }

    private void SetTutorialBattleUnlocked(CampaignRunRecord record, bool unlocked)
    {
        if (record.State["PublicState"] is not JsonObject publicState)
        {
            publicState = new JsonObject();
            record.State["PublicState"] = publicState;
        }

        if (publicState["Data"] is not JsonObject data)
        {
            data = new JsonObject();
            publicState["Data"] = data;
        }

        data["RaceTutorialBattleUnlocked"] = unlocked;
        _store.Save(record);
    }

    private void CompleteConversation(CampaignRunRecord record)
    {
        var cfg = CampaignStateFactory.Race(record.Race);
        var active = record.State["ALoc"]?.GetValue<string>();

        if (string.Equals(active, cfg.IntroNpc, StringComparison.Ordinal))
        {
            if (record.State["VisLocs"] is JsonArray locations)
            {
                foreach (var item in locations.OfType<JsonObject>())
                {
                    var data = item["Data"] as JsonObject;
                    if (data?["node"]?.GetValue<string>() == cfg.IntroNpc)
                    {
                        data["completed"] = true;
                        data["autostart"] = false;
                    }
                }

                var trainerAlreadyPresent = locations
                    .OfType<JsonObject>()
                    .Any(item => (item["Data"] as JsonObject)?["node"]?.GetValue<string>() == cfg.TrainerNpc);
                if (!trainerAlreadyPresent)
                    locations.Add(CampaignStateFactory.ConversationLocation(cfg.TrainerNpc, cfg.BattleConversation, giveQuest: true));
            }

            // After the intro the player returns to panorama explore mode and must
            // select the trainer rather than auto-launching their conversation.
            record.State["ALoc"] = null;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
            return;
        }

        if (string.Equals(active, cfg.TrainerNpc, StringComparison.Ordinal))
        {
            var victoryPending = record.State["TrainingVictoryPending"]?.GetValue<bool>() == true;
            if (victoryPending)
            {
                record.State.Remove("TrainingVictoryPending");
                var trainer = FindLocation(record.State, cfg.TrainerNpc);
                if (trainer is not null)
                {
                    trainer["completed"] = true;
                    trainer["visible"] = false;
                    trainer["enabled"] = false;
                    trainer["autostart"] = false;
                }

                EnsureQuestGiverLocation(record.State, cfg);
                record.State["ALoc"] = null;
                record.State["CurState"] = "EXPLORE";
                _logger?.LogInformation(
                    "Campaign: training victory conversation completed; trainer {trainer} hidden and quest giver {quest} exposed",
                    cfg.TrainerNpc, cfg.QuestNpc);
            }
            else
            {
                var battleUnlocked = record.State["PublicState"] is JsonObject publicState
                    && publicState["Data"] is JsonObject publicData
                    && publicData["RaceTutorialBattleUnlocked"]?.GetValue<bool>() == true;

                if (battleUnlocked && record.State["VisLocs"] is JsonArray locations)
                {
                    foreach (var item in locations.OfType<JsonObject>())
                    {
                        var data = item["Data"] as JsonObject;
                        var node = data?["node"]?.GetValue<string>();
                        var name = data?["name"]?.GetValue<string>();
                        if (!string.Equals(node, cfg.TrainerNpc, StringComparison.Ordinal)
                            && !string.Equals(name, cfg.TrainerNpc, StringComparison.Ordinal))
                            continue;

                        // Client ProcessStateChange can auto-trigger the encounter only when
                        // ALoc still points at the bound trainer NPC. Keep the encounter on
                        // the trainer's own location instead of moving to TrainingNode.
                        data!["type"] = "Encounter";
                        data["encounter"] = cfg.TrainingEncounter;
                        data["conversationId"] = null;
                        data["completed"] = false;
                        data["enabled"] = true;
                        data["visible"] = true;
                        data["autostart"] = false;
                        break;
                    }

                    record.State["ALoc"] = cfg.TrainerNpc;
                    record.State["LastNode"] = cfg.TrainerNpc;
                    record.State["CurState"] = "EXPLORE";
                    _logger?.LogInformation(
                        "Campaign: trainer handoff -> encounter {encounter} on {trainer}",
                        cfg.TrainingEncounter,
                        cfg.TrainerNpc);
                }
                else
                {
                    // The player declined the spar, or the authored choice event was not
                    // received. Leave the trainer conversation available for another try.
                    record.State["ALoc"] = null;
                    record.State["CurState"] = "EXPLORE";
                }
            }

            _store.Save(record);
            return;
        }

        // Conversations without an authored transition currently close back to panorama
        // explore mode without changing other campaign state.
        if (!string.IsNullOrWhiteSpace(active))
        {
            record.State["ALoc"] = null;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
        }
    }

    private JsonNode Forfeit(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");
        record.State["Finished"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        record.State["FinishReason"] = "Forfeit";
        record.State["CurState"] = "FINISHED";
        _store.Save(record);
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private static JsonNode Unsupported(JsonElement root, string requestType)
    {
        var campaignId = ULong(root, "CampID");
        return CampaignStateFactory.BuildFailure(campaignId, $"Unsupported campaign request '{requestType}'");
    }

    private static IEnumerable<ulong> CampaignIds(JsonElement root)
    {
        if (root.TryGetProperty("CampIDs", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in many.EnumerateArray())
                if (TryULong(value, out var id)) yield return id;
            yield break;
        }
        var one = ULong(root, "CampID");
        if (one != 0) yield return one;
    }

    private static string String(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static ulong ULong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return 0;
        return TryULong(value, out var parsed) ? parsed : 0;
    }

    private static bool TryULong(JsonElement value, out ulong parsed)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out parsed)) return true;
        if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out parsed)) return true;
        parsed = 0;
        return false;
    }

    private static int Int(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)) return parsed;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed)) return parsed;
        return fallback;
    }
}
