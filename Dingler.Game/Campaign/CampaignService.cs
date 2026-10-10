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

    private int RaceForChampion(ulong profileId, ulong championId)
    {
        if (CampaignBootstrapChampion.TryResolveChampionId(profileId, championId, out var rawId, out var race)
            && rawId == championId)
            return race;

        return _store.TryGet(profileId, championId, out var existing)
            ? existing.Race
            : _options.DefaultRace;
    }

    private JsonNode QueryCurrent(SessionContext context, JsonElement root)
    {
        var championId = ULong(root, "ChampID");
        if (championId == 0)
            return CampaignStateFactory.BuildFailure(0, "ChampID is required");
        var record = _store.GetOrCreate(context.ProfileId, championId, RaceForChampion(context.ProfileId, championId));
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode CreateCampaign(SessionContext context, JsonElement root)
    {
        var championId = ULong(root, "ChampID");
        if (championId == 0)
            return CampaignStateFactory.BuildFailure(0, "ChampID is required");

        var requestedType = Int(root, "CampType", 0);
        var templateName = RequestTemplateName(root);
        if (requestedType == 0 && string.IsNullOrWhiteSpace(templateName))
            return QueryCurrent(context, root);

        var existing = _store.GetActive(
            context.ProfileId, championId, RaceForChampion(context.ProfileId, championId), requestedType, templateName);
        return existing.Count > 0
            ? CampaignStateFactory.BuildInputResponse(existing[0])
            : CampaignStateFactory.BuildFailure(0, "Requested campaign is not available");
    }

    private JsonNode GetActive(SessionContext context, JsonElement root)
    {
        var championId = ULong(root, "ChampID");
        if (championId == 0)
            return new JsonArray();

        var requestedType = Int(root, "CampType", 0);
        var templateName = RequestTemplateName(root);
        var records = _store.GetActive(
            context.ProfileId, championId, RaceForChampion(context.ProfileId, championId), requestedType, templateName);
        var result = new JsonArray();
        foreach (var record in records)
            result.Add(CampaignStateFactory.BuildTemplateInfo(record));
        return result;
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

        if (!string.Equals(record.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase))
        {
            record.Started = true;
            if (record.State["Started"] is null)
                record.State["Started"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
        }
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
            record = CompleteConversation(context, record);
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
                    || string.Equals(conversationId, cfg.TrainingFailConversation, StringComparison.OrdinalIgnoreCase)
                    || IsCrayburnBattleOutcomeConversation(record, active, conversationId));

            if (isBattleOutcomeConversation)
            {
                _logger?.LogInformation(
                    "Campaign: ignored stale enc_cancel while authored battle result conversation {conversation} is active",
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
        if (string.Equals(encounterGuid, cfg.TrainingEncounter, StringComparison.OrdinalIgnoreCase))
        {
            ApplyTrainingBattleResult(record, cfg, won);
        }
        else if (IsCrayburnDungeon(record)
                 && CampaignCrayburnConfig.TryEncounter(record.Race, encounterGuid, out var crayburnEncounter))
        {
            if (!ApplyCrayburnBattleResult(record, crayburnEncounter, won))
            {
                _logger?.LogWarning(
                    "Campaign: Crayburn battle result for encounter {encounter} could not resolve node {node}",
                    encounterGuid,
                    crayburnEncounter.Node);
                return false;
            }

            _logger?.LogInformation(
                "Campaign: Crayburn encounter {encounter} ended won={won}; queued authored {result} conversation {conversation} at {node}",
                encounterGuid,
                won,
                won ? "success" : "defeat",
                won ? crayburnEncounter.SuccessConversation : crayburnEncounter.FailConversation,
                crayburnEncounter.Node);
        }
        else
        {
            _logger?.LogWarning(
                "Campaign: battle result for encounter {encounter} has no implemented campaign progression rule",
                encounterGuid);
            return false;
        }

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

    private static bool ApplyCrayburnBattleResult(
        CampaignRunRecord record,
        CampaignCrayburnEncounterConfig encounter,
        bool won)
    {
        var location = FindLocation(record.State, encounter.Node);
        if (location is null)
            return false;

        location["type"] = "Convo";
        location["conversationId"] = won
            ? encounter.SuccessConversation
            : encounter.FailConversation;
        location["encounter"] = null;
        location["completed"] = false;
        location["enabled"] = true;
        location["visible"] = true;
        location["repeatable"] = false;
        location["autostart"] = false;

        record.State["ALoc"] = encounter.Node;
        record.State["LastNode"] = encounter.Node;
        record.State["CurState"] = "EXPLORE";
        record.State.Remove("ActiveEncounterGuid");

        if (won)
            record.State["Wins"] = StateInt(record.State, "Wins") + 1;
        else
            record.State["Losses"] = StateInt(record.State, "Losses") + 1;

        return true;
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

    private CampaignRunRecord CompleteConversation(SessionContext context, CampaignRunRecord record)
    {
        var cfg = CampaignStateFactory.Race(record.Race);
        var active = record.State["ALoc"]?.GetValue<string>();
        var isPanorama = string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase);

        if (isPanorama && string.Equals(active, cfg.IntroNpc, StringComparison.Ordinal))
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

            record.State["ALoc"] = null;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
            return record;
        }

        if (isPanorama && string.Equals(active, cfg.TrainerNpc, StringComparison.Ordinal))
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
                    record.State["ALoc"] = null;
                    record.State["CurState"] = "EXPLORE";
                }
            }

            _store.Save(record);
            return record;
        }

        if (isPanorama && string.Equals(active, cfg.QuestNpc, StringComparison.Ordinal))
        {
            var questLocation = FindLocation(record.State, cfg.QuestNpc);
            var conversationId = questLocation?["conversationId"]?.GetValue<string>();
            var tutorialDone = record.State["TutorialDone"]?.GetValue<bool>() == true;
            var reportConversation = CampaignCrayburnConfig.Race(record.Race).ReportConversation;
            if (string.Equals(conversationId, reportConversation, StringComparison.OrdinalIgnoreCase)
                && questLocation?["turninquest"]?.GetValue<bool>() == true)
            {
                var turnIn = _store.TryApplyCrayburnQuestTurnIn(
                    record,
                    CampaignCrayburnConfig.QuestScriptName,
                    quest => CrayburnQuestReadyForReport(quest, reportConversation),
                    panorama => CrayburnPanoramaReadyForReport(panorama, cfg.QuestNpc, reportConversation),
                    FinishCrayburnQuest,
                    panorama => FinishCrayburnReportPanorama(panorama, cfg.QuestNpc));
                if (turnIn is not null)
                {
                    QueueCrayburnQuestTurnIn(context, turnIn);
                    _logger?.LogInformation(
                        "Campaign: Crayburn report conversation {conversation} completed; quest={quest} Step2 finished; panorama={panorama} quest giver={npc}",
                        reportConversation, turnIn.Quest.CampaignId, turnIn.Panorama.CampaignId, cfg.QuestNpc);
                    return turnIn.Panorama;
                }

                _logger?.LogWarning(
                    "Campaign: ignored Crayburn report at {npc} because the linked quest is not ready or was already completed",
                    cfg.QuestNpc);
            }

            if (tutorialDone
                && string.Equals(conversationId, cfg.QuestConversation, StringComparison.OrdinalIgnoreCase))
            {
                var startedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                var handoff = _store.ApplySceneHandoff(
                    record,
                    CampaignCrayburnConfig.QuestScriptName,
                    questId =>
                    {
                        var quest = new CampaignRunRecord
                        {
                            ProfileId = record.ProfileId,
                            ChampionId = record.ChampionId,
                            Race = record.Race,
                            CampaignId = questId,
                            CampaignType = "QUEST",
                            TemplateName = CampaignCrayburnConfig.QuestScriptName,
                            Started = true,
                        };
                        quest.State = CampaignStateFactory.CreateCrayburnQuestState(quest);
                        return quest;
                    },
                    CampaignCrayburnConfig.DungeonTemplateName,
                    dungeonId =>
                    {
                        var dungeon = new CampaignRunRecord
                        {
                            ProfileId = record.ProfileId,
                            ChampionId = record.ChampionId,
                            Race = record.Race,
                            CampaignId = dungeonId,
                            CampaignType = "DUNGEON",
                            TemplateName = CampaignCrayburnConfig.DungeonTemplateName,
                            Started = true,
                        };
                        dungeon.State = CampaignStateFactory.CreateCrayburnDungeonState(dungeon, startedUtc);
                        return dungeon;
                    },
                    panorama =>
                    {
                        var giver = FindLocation(panorama.State, cfg.QuestNpc);
                        if (giver is not null)
                        {
                            giver["completed"] = true;
                            giver["autostart"] = false;
                        }
                        panorama.State["ALoc"] = null;
                        panorama.State["LastNode"] = cfg.QuestNpc;
                        panorama.State["CurState"] = "EXPLORE";
                    });

                QueueCrayburnHandoff(context, handoff);
                _logger?.LogInformation(
                    "Campaign: quest giver {questNpc} completed; panorama={panorama} quest={quest} (created={questCreated}) dungeon={dungeon} (created={dungeonCreated}); queued Crayburn handoff",
                    cfg.QuestNpc,
                    handoff.Panorama.CampaignId,
                    handoff.Quest.CampaignId,
                    handoff.QuestCreated,
                    handoff.Dungeon.CampaignId,
                    handoff.DungeonCreated);
                return handoff.Panorama;
            }
        }

        if (string.Equals(record.CampaignType, "DUNGEON", StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.TemplateName, CampaignCrayburnConfig.DungeonTemplateName, StringComparison.OrdinalIgnoreCase)
            && CompleteCrayburnConversation(context, record, active))
        {
            return record;
        }

        // Conversation types whose progression is not implemented yet return to the
        // current campaign map without inventing the next authored state.
        if (!string.IsNullOrWhiteSpace(active))
        {
            record.State["ALoc"] = null;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
        }

        return record;
    }

    private bool CompleteCrayburnConversation(
        SessionContext context,
        CampaignRunRecord record,
        string? active)
    {
        if (string.IsNullOrWhiteSpace(active))
            return false;

        if (CampaignCrayburnConfig.TryEncounterNode(record.Race, active, out var battle))
        {
            var battleLocation = FindLocation(record.State, active);
            if (battleLocation is null
                || !string.Equals(battleLocation["type"]?.GetValue<string>(), "Convo", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var conversationId = battleLocation["conversationId"]?.GetValue<string>();
            if (string.Equals(conversationId, battle.FailConversation, StringComparison.OrdinalIgnoreCase))
            {
                RestoreCrayburnEncounterAfterDefeat(record, battle, battleLocation);
                _store.Save(record);
                _logger?.LogInformation(
                    "Campaign: Crayburn defeat conversation {conversation} completed; restored encounter {encounter} at {node}",
                    conversationId,
                    battle.Encounter,
                    battle.Node);
                return true;
            }

            if (!string.Equals(conversationId, battle.SuccessConversation, StringComparison.OrdinalIgnoreCase))
                return false;

            if (battle.NextNode is null)
            {
                CompleteCrayburnDungeon(context, record, battle, battleLocation);
                return true;
            }

            CompleteCrayburnNode(record, battle.Node, battle.NextNode, battleLocation);
            QueueCrayburnProgress(context, record, battle.Node, battle.NextNode);
            _logger?.LogInformation(
                "Campaign: Crayburn victory conversation {node} completed; revealed {next} in dungeon {campaign}",
                battle.Node,
                battle.NextNode,
                record.CampaignId);
            return true;
        }

        var cfg = CampaignCrayburnConfig.Race(record.Race);
        string? expectedConversation = null;
        string? nextNode = null;

        switch (active)
        {
            case "WatchTower":
                expectedConversation = cfg.WatchTowerConversation;
                nextNode = "Drawbridge";
                break;
            case "Drawbridge":
                expectedConversation = cfg.DrawbridgeConversation;
                nextNode = "CastleGate";
                break;
            case "InnerBailey":
                expectedConversation = cfg.InnerBaileyConversation;
                nextNode = "TowerGate";
                break;
            default:
                return false;
        }

        if (expectedConversation is null || nextNode is null)
            return false;

        var location = FindLocation(record.State, active);
        if (location is null
            || !string.Equals(location["type"]?.GetValue<string>(), "Convo", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                location["conversationId"]?.GetValue<string>(),
                expectedConversation,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        CompleteCrayburnNode(record, active, nextNode, location);
        QueueCrayburnProgress(context, record, active, nextNode);
        _logger?.LogInformation(
            "Campaign: Crayburn conversation {node} completed; revealed {next} in dungeon {campaign}",
            active,
            nextNode,
            record.CampaignId);
        return true;
    }

    private void CompleteCrayburnNode(
        CampaignRunRecord record,
        string active,
        string nextNode,
        JsonObject location)
    {
        location["completed"] = true;
        location["autostart"] = false;
        location["enabled"] = true;
        location["visible"] = true;

        // Entrance is a pass-through map node. By the time any authored castle
        // conversation closes, the client has already travelled beyond it.
        var entrance = FindLocation(record.State, "Entrance");
        if (entrance is not null)
            entrance["completed"] = true;

        AddVisitedNode(record.State, active);
        RevealCrayburnLocation(record.State, nextNode);
        record.State["ALoc"] = null;
        record.State["LastNode"] = active;
        record.State["CurState"] = "EXPLORE";
        record.State.Remove("ActiveEncounterGuid");
        _store.Save(record);
    }

    private void CompleteCrayburnDungeon(
        SessionContext context,
        CampaignRunRecord dungeon,
        CampaignCrayburnEncounterConfig battle,
        JsonObject battleLocation)
    {
        var finishedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var cfg = CampaignStateFactory.Race(dungeon.Race);
        var crayburn = CampaignCrayburnConfig.Race(dungeon.Race);

        var completion = _store.ApplyCrayburnCompletion(
            dungeon,
            CampaignCrayburnConfig.QuestScriptName,
            persistedDungeon =>
            {
                var finalLocation = FindLocation(persistedDungeon.State, battle.Node) ?? battleLocation;
                finalLocation["completed"] = true;
                finalLocation["autostart"] = false;
                finalLocation["enabled"] = true;
                finalLocation["visible"] = true;

                var entrance = FindLocation(persistedDungeon.State, "Entrance");
                if (entrance is not null)
                    entrance["completed"] = true;

                AddVisitedNode(persistedDungeon.State, battle.Node);
                persistedDungeon.State["ALoc"] = null;
                persistedDungeon.State["LastNode"] = battle.Node;
                persistedDungeon.State["CurState"] = "EXPLORE";
                persistedDungeon.State["Finished"] = finishedUtc;
                persistedDungeon.State["FinishReason"] = "Complete";
                persistedDungeon.State.Remove("ActiveEncounterGuid");
            },
            quest => AdvanceCrayburnQuestToReport(quest, crayburn.ReportConversation),
            panorama => PrepareCrayburnReportPanorama(
                panorama, cfg.QuestNpc, crayburn.ReportConversation));

        QueueCrayburnCompletion(context, completion);
        _logger?.LogInformation(
            "Campaign: Crayburn completed at {node}; dungeon={dungeon} quest={quest} advanced to {step} and panorama={panorama} prepared for report to {questNpc}",
            battle.Node,
            completion.Dungeon.CampaignId,
            completion.Quest.CampaignId,
            CampaignCrayburnConfig.QuestReportLocation,
            completion.Panorama.CampaignId,
            cfg.QuestNpc);
    }

    private static void AdvanceCrayburnQuestToReport(
        CampaignRunRecord quest,
        string reportConversation)
    {
        var step1 = FindLocation(quest.State, CampaignCrayburnConfig.QuestFirstLocation);
        if (step1 is not null)
        {
            step1["completed"] = true;
            step1["autostart"] = false;
        }

        var step2 = FindLocation(quest.State, CampaignCrayburnConfig.QuestReportLocation);
        if (step2 is null)
        {
            if (quest.State["VisLocs"] is not JsonArray locations)
            {
                locations = new JsonArray();
                quest.State["VisLocs"] = locations;
            }

            var location = CampaignStateFactory.ConversationLocation(
                CampaignCrayburnConfig.QuestReportLocation,
                reportConversation);
            locations.Add(location);
            step2 = location["Data"] as JsonObject;
        }

        if (step2 is not null)
        {
            step2["type"] = "Convo";
            step2["conversationId"] = reportConversation;
            step2["encounter"] = null;
            step2["completed"] = false;
            step2["enabled"] = true;
            step2["visible"] = true;
            step2["repeatable"] = false;
            step2["givequest"] = false;
            step2["turninquest"] = false;
            step2["autostart"] = false;
        }

        if (quest.State["LocNodes"] is not JsonArray locNodes)
        {
            locNodes = new JsonArray();
            quest.State["LocNodes"] = locNodes;
        }

        var step2NodePresent = locNodes
            .OfType<JsonObject>()
            .Any(item => string.Equals(
                (item["Name"]?.GetValue<string>()
                 ?? (item["Data"] as JsonObject)?["id"]?.GetValue<string>()),
                CampaignCrayburnConfig.QuestReportLocation,
                StringComparison.Ordinal));
        if (!step2NodePresent)
        {
            locNodes.Add(new JsonObject
            {
                ["Name"] = CampaignCrayburnConfig.QuestReportLocation,
                ["Data"] = new JsonObject
                {
                    ["id"] = CampaignCrayburnConfig.QuestReportLocation,
                    ["type"] = "DEFAULT",
                },
            });
        }

        quest.State["ALoc"] = CampaignCrayburnConfig.QuestReportLocation;
        quest.State["LastNode"] = CampaignCrayburnConfig.QuestReportLocation;
        quest.State["CurState"] = "EXPLORE";
        quest.State["Finished"] = null;
        quest.State["FinishReason"] = null;
    }

    private static void PrepareCrayburnReportPanorama(
        CampaignRunRecord panorama,
        string questNpc,
        string reportConversation)
    {
        var report = FindLocation(panorama.State, questNpc);
        if (report is null)
        {
            if (panorama.State["VisLocs"] is not JsonArray locations)
            {
                locations = new JsonArray();
                panorama.State["VisLocs"] = locations;
            }

            var location = CampaignStateFactory.ConversationLocation(questNpc, reportConversation);
            locations.Add(location);
            report = location["Data"] as JsonObject;
        }

        if (report is not null)
        {
            report["type"] = "Convo";
            report["conversationId"] = reportConversation;
            report["encounter"] = null;
            report["completed"] = false;
            report["enabled"] = true;
            report["visible"] = true;
            report["repeatable"] = false;
            report["givequest"] = false;
            report["turninquest"] = true;
            report["autostart"] = false;
        }

        panorama.State["ALoc"] = null;
        panorama.State["LastNode"] = questNpc;
        panorama.State["CurState"] = "EXPLORE";
    }

    private static bool CrayburnQuestReadyForReport(CampaignRunRecord quest, string conversation)
    {
        var step1 = FindLocation(quest.State, CampaignCrayburnConfig.QuestFirstLocation);
        var step2 = FindLocation(quest.State, CampaignCrayburnConfig.QuestReportLocation);
        return step1?["completed"]?.GetValue<bool>() == true
               && step2?["completed"]?.GetValue<bool>() == false
               && string.Equals(step2?["conversationId"]?.GetValue<string>(), conversation,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool CrayburnPanoramaReadyForReport(
        CampaignRunRecord panorama, string questNpc, string conversation)
    {
        var giver = FindLocation(panorama.State, questNpc);
        if (giver is null)
            return false;

        return string.Equals(panorama.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase)
               && string.Equals(panorama.State["ALoc"]?.GetValue<string>(), questNpc, StringComparison.Ordinal)
               && giver["turninquest"]?.GetValue<bool>() == true
               && giver["completed"]?.GetValue<bool>() == false
               && string.Equals(giver["conversationId"]?.GetValue<string>(), conversation,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void FinishCrayburnQuest(CampaignRunRecord quest)
    {
        var step2 = FindLocation(quest.State, CampaignCrayburnConfig.QuestReportLocation)!;
        step2["completed"] = true;
        step2["autostart"] = false;
        quest.State["ALoc"] = null;
        quest.State["LastNode"] = CampaignCrayburnConfig.QuestReportLocation;
        quest.State["CurState"] = "FINISHED";
        quest.State["Finished"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        quest.State["FinishReason"] = "Complete";
    }

    private static void FinishCrayburnReportPanorama(CampaignRunRecord panorama, string questNpc)
    {
        var giver = FindLocation(panorama.State, questNpc)!;
        giver["completed"] = true;
        giver["autostart"] = false;
        giver["turninquest"] = false;
        giver["enabled"] = false;
        panorama.State["ALoc"] = null;
        panorama.State["LastNode"] = questNpc;
        panorama.State["CurState"] = "EXPLORE";
    }

    private static void RestoreCrayburnEncounterAfterDefeat(
        CampaignRunRecord record,
        CampaignCrayburnEncounterConfig battle,
        JsonObject location)
    {
        location["type"] = "Encounter";
        location["conversationId"] = null;
        location["encounter"] = battle.Encounter;
        location["completed"] = false;
        location["enabled"] = true;
        location["visible"] = true;
        location["repeatable"] = false;
        location["autostart"] = false;

        record.State["ALoc"] = null;
        record.State["LastNode"] = battle.ReturnNode;
        record.State["CurState"] = "EXPLORE";
        record.State.Remove("ActiveEncounterGuid");
    }

    private static bool IsCrayburnDungeon(CampaignRunRecord record) =>
        string.Equals(record.CampaignType, "DUNGEON", StringComparison.OrdinalIgnoreCase)
        && string.Equals(record.TemplateName, CampaignCrayburnConfig.DungeonTemplateName, StringComparison.OrdinalIgnoreCase);

    private static bool IsCrayburnBattleOutcomeConversation(
        CampaignRunRecord record,
        string? active,
        string? conversationId)
    {
        if (!IsCrayburnDungeon(record)
            || string.IsNullOrWhiteSpace(active)
            || string.IsNullOrWhiteSpace(conversationId)
            || !CampaignCrayburnConfig.TryEncounterNode(record.Race, active, out var battle))
        {
            return false;
        }

        return string.Equals(conversationId, battle.SuccessConversation, StringComparison.OrdinalIgnoreCase)
            || string.Equals(conversationId, battle.FailConversation, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddVisitedNode(JsonObject state, string node)
    {
        if (state["PublicState"] is not JsonObject publicState)
        {
            publicState = new JsonObject();
            state["PublicState"] = publicState;
        }

        if (publicState["Data"] is not JsonObject data)
        {
            data = new JsonObject();
            publicState["Data"] = data;
        }

        if (data["visited_nodes"] is not JsonArray visited)
        {
            visited = new JsonArray();
            data["visited_nodes"] = visited;
        }

        foreach (var item in visited)
        {
            if (item is JsonValue value
                && value.TryGetValue<string>(out var existing)
                && string.Equals(existing, node, StringComparison.Ordinal))
            {
                return;
            }
        }

        visited.Add(node);
    }

    private static void RevealCrayburnLocation(JsonObject state, string node)
    {
        var location = FindLocation(state, node);
        if (location is null)
            return;

        location["enabled"] = true;
        location["visible"] = true;
        location["autostart"] = false;
    }

    private void QueueCrayburnProgress(
        SessionContext context,
        CampaignRunRecord dungeon,
        string completedNode,
        string revealedNode)
    {
        var updateEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = dungeon.CampaignId,
            ["ChampID"] = dungeon.ChampionId,
            ["Reason"] = "crayburn_travel",
            ["TemplateType"] = "DUNGEON",
            ["Transition"] = false,
            ["State"] = dungeon.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(updateEnvelope),
        });
        _logger?.LogInformation(
            "Campaign: queued Crayburn cmpupdate dungeon={dungeon} completed={completed} revealed={revealed}",
            dungeon.CampaignId,
            completedNode,
            revealedNode);
    }

    private void QueueCrayburnCompletion(
        SessionContext context,
        CampaignCompletionHandoffResult completion)
    {
        var dungeonEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = completion.Dungeon.CampaignId,
            ["ChampID"] = completion.Dungeon.ChampionId,
            ["Reason"] = "crayburn_travel",
            ["TemplateType"] = "DUNGEON",
            ["Transition"] = false,
            ["State"] = completion.Dungeon.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(dungeonEnvelope),
        });

        var questEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = completion.Quest.CampaignId,
            ["ChampID"] = completion.Quest.ChampionId,
            ["Reason"] = "quest_complete",
            ["TemplateType"] = "QUEST",
            ["Transition"] = false,
            ["State"] = completion.Quest.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(questEnvelope),
        });

        var panoramaEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = completion.Panorama.CampaignId,
            ["ChampID"] = completion.Panorama.ChampionId,
            ["Reason"] = "dungeon_complete",
            ["TemplateType"] = "PANORAMA",
            ["Transition"] = true,
            ["State"] = completion.Panorama.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(panoramaEnvelope),
        });

        _logger?.LogInformation(
            "Campaign: queued Crayburn completion updates dungeon={dungeon} quest={quest} panorama={panorama} transition=true",
            completion.Dungeon.CampaignId,
            completion.Quest.CampaignId,
            completion.Panorama.CampaignId);
    }

    private void QueueCrayburnQuestTurnIn(SessionContext context, CampaignQuestTurnInResult turnIn)
    {
        var questEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = turnIn.Quest.CampaignId,
            ["ChampID"] = turnIn.Quest.ChampionId,
            ["Reason"] = "quest_complete",
            ["TemplateType"] = "QUEST",
            ["Transition"] = false,
            ["State"] = turnIn.Quest.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(questEnvelope),
        });

        var panoramaEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = turnIn.Panorama.CampaignId,
            ["ChampID"] = turnIn.Panorama.ChampionId,
            ["Reason"] = "quest_complete",
            ["TemplateType"] = "PANORAMA",
            ["Transition"] = false,
            ["State"] = turnIn.Panorama.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(panoramaEnvelope),
        });
        _logger?.LogInformation(
            "Campaign: queued Crayburn report cmpupdates quest={quest} panorama={panorama} transition=false",
            turnIn.Quest.CampaignId, turnIn.Panorama.CampaignId);
    }

    private void QueueCrayburnHandoff(SessionContext context, CampaignHandoffResult handoff)
    {
        if (handoff.QuestCreated)
        {
            var spawnEnvelope = new JsonObject
            {
                ["ReckID"] = 0,
                ["CampID"] = handoff.Quest.CampaignId,
                ["ChampID"] = handoff.Quest.ChampionId,
                ["TemplateType"] = "QUEST",
                ["TemplateName"] = handoff.Quest.TemplateName,
                ["SpawnedBy"] = handoff.Panorama.CampaignId,
                ["SpawnedByTemplate"] = handoff.Panorama.TemplateName,
                ["Transition"] = false,
                ["ChildState"] = handoff.Quest.State.DeepClone(),
                ["RequestType"] = "campspawn",
            };
            context.QueueMessageAfterResponse(new CampSysGeneral.Request
            {
                Envelope = JsonSerializer.SerializeToUtf8Bytes(spawnEnvelope),
            });
            _logger?.LogInformation(
                "Campaign: queued campspawn quest={quest} template={template} spawnedBy={panorama}",
                handoff.Quest.CampaignId, handoff.Quest.TemplateName, handoff.Panorama.CampaignId);
        }

        var transitionEnvelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = handoff.Dungeon.CampaignId,
            ["ChampID"] = handoff.Dungeon.ChampionId,
            ["Reason"] = "quest_complete",
            ["TemplateType"] = "DUNGEON",
            ["Transition"] = true,
            ["State"] = handoff.Dungeon.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(transitionEnvelope),
        });
        _logger?.LogInformation(
            "Campaign: queued cmpupdate transition dungeon={dungeon} template={template} aloc={active}",
            handoff.Dungeon.CampaignId, handoff.Dungeon.TemplateName, handoff.Dungeon.State["ALoc"]?.ToString());
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

    private static string RequestTemplateName(JsonElement root)
    {
        var templateName = String(root, "TemplateName");
        return string.IsNullOrWhiteSpace(templateName) ? String(root, "Template") : templateName;
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
