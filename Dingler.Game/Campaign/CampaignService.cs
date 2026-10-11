extern alias HexGame;

using System.Text.Json;
using System.Text.Json.Nodes;
using Dingler.Server;
using Dingler.Game.Services;
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
    // Original Dire Toad CardTemplate GUID, verified through the native card cache
    // in the successful Wild Cub capture log (2026-10-11).
    private static readonly Guid DunnwoodDireToadTemplate = new("c0bb008e-17c5-4e73-baf9-5cbd8f3eb6dc");

    private readonly CampaignRunStore _store;
    private readonly CampaignOptions _options;
    private readonly CollectionCacheService _collection;
    private readonly ILogger<CampaignService>? _logger;

    public CampaignService(CampaignRunStore store, CampaignOptions options,
        CollectionCacheService collection, ILogger<CampaignService>? logger = null)
    {
        _store = store;
        _options = options;
        _collection = collection;
        _logger = logger;
    }

    public byte[] HandleEnvelope(SessionContext context, byte[]? envelope)
    {
        using var document = JsonDocument.Parse(envelope is { Length: > 0 } ? envelope : "{}"u8.ToArray());
        var root = document.RootElement;
        var requestType = String(root, "RequestType");
        if (requestType == "locaction")
            _logger?.LogDebug("Campaign: {user} request {request}", context.UserName, requestType);
        else
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
        PrepareAz1Opening(record);
        BackfillAz1OpeningQuests(context, record);
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
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");
        PrepareAz1Opening(record);
        BackfillAz1OpeningQuests(context, record);
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode StartCampaign(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");

        PrepareAz1Opening(record);
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

        PrepareAz1Opening(record);
        var action = Int(root, "RAct", 0);
        var location = String(root, "Loc");
        if (action == 0 && string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase))
        {
            var node = CampaignAz1Config.OpeningNode(location);
            if (node is null || FindLocation(record.State, node)?["enabled"]?.GetValue<bool>() != true)
            {
                _logger?.LogWarning("Campaign: rejected AZ1 StartLoc {location} for campaign {campaign}", location, campaignId);
                return CampaignStateFactory.BuildFailure(campaignId, "Location is not available");
            }
            if (node == CampaignAz1Config.NextNode
                && !CampaignAz1OpeningCatalog.TryGetWelcome(record.Race, out _, out _)
                && FindLocation(record.State, node)?["completed"]?.GetValue<bool>() != true)
            {
                _logger?.LogWarning("Campaign: AZ1 Node002 has no verified {faction} welcome conversation in original HEX gamedata",
                    record.Race is >= 1 and <= 4 ? "Ardent" : "Underworld");
                return CampaignStateFactory.BuildFailure(campaignId, "Authored welcome conversation is unavailable");
            }
            var locationData = FindLocation(record.State, node)!;
            if (node == CampaignAz1Config.ThirdNode
                && locationData["completed"]?.GetValue<bool>() != true
                && !CampaignAz1AuthoredCatalog.TryGetDunnwood(out _, out _))
                return CampaignStateFactory.BuildFailure(campaignId, "Authored Dunnwood encounter is unavailable");
            if (node == CampaignAz1Config.SutherlandNode
                && !CampaignAz1AuthoredCatalog.TryGetSutherland(out _, out _))
                return CampaignStateFactory.BuildFailure(campaignId, "Authored Cockatwice encounter is unavailable");
            if (node == CampaignAz1Config.OaksNode
                && !CampaignAz1AuthoredCatalog.TryGetGaalCamp(out _, out _, out _))
                return CampaignStateFactory.BuildFailure(campaignId, "Authored Gaal Camp conversation is unavailable");
            if (node == CampaignAz1Config.FourthNode
                && !CampaignAz1AuthoredCatalog.TryGetSporemistIntro(out _, out _))
                return CampaignStateFactory.BuildFailure(campaignId, "Authored Sporemist Hollow conversation is unavailable");
            if (node == CampaignAz1Config.FourthNode
                && locationData["shroom_intro_completed"]?.GetValue<bool>() == true
                && locationData["completed"]?.GetValue<bool>() != true
                && !CampaignShroomRewardCatalog.TryGetVerifiedClientTemplates(out _))
                return CampaignStateFactory.BuildFailure(campaignId, "Shroom Haus choice templates are not available");
            var previousNode = CampaignAz1Config.OpeningNode(record.State["LastNode"]?.ToString());
            if (previousNode is not null && previousNode != node
                && !HasAz1OpenTravelRoute(record.State, previousNode, node))
                return CampaignStateFactory.BuildFailure(campaignId, "No unlocked route to location");
            // AREA's active location is identified by its authored display name.
            // LastNode and map visit/path tracking use the Unity map-node ID.
            var activeName = locationData["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(activeName))
                activeName = node;
            var visitedNodes = record.State["PublicState"]?["Data"]?["visited_nodes"] as JsonArray;
            var alreadyActive = string.Equals(record.State["ALoc"]?.ToString(), activeName, StringComparison.Ordinal)
                && string.Equals(record.State["LastNode"]?.ToString(), node, StringComparison.Ordinal)
                && string.Equals(record.State["CurState"]?.ToString(), "EXPLORE", StringComparison.Ordinal)
                && visitedNodes?.Any(item => string.Equals(item?.ToString(), node, StringComparison.Ordinal)) == true;
            record.State["ALoc"] = activeName;
            record.State["LastNode"] = node;
            record.State["CurState"] = "EXPLORE";
            AddVisitedNode(record.State, node);
            if (!alreadyActive)
            {
                _store.Save(record);
                _logger?.LogInformation(
                    "Campaign: AZ1 StartLoc camp={campaign} requested={requested} node={node} ALoc={active} LastNode={lastNode} type={type} autostart={autostart} conversation={conversation} response=InputResponse(success=true)",
                    campaignId, location, node, activeName, node,
                    locationData["type"]?.ToString(), locationData["autostart"]?.ToString(),
                    locationData["conversationId"]?.ToString());
            }
            else
            {
                _logger?.LogDebug("Campaign: repeated AZ1 StartLoc camp={campaign} node={node} ALoc={active}",
                    campaignId, node, activeName);
            }
            return CampaignStateFactory.BuildInputResponse(record);
        }
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

        PrepareAz1Opening(record);
        var eventName = String(root, "Event");
        var activeLocation = record.State["ALoc"]?.GetValue<string>();
        var eventParams = root.TryGetProperty("OParms", out var parameters)
            ? parameters.GetRawText()
            : "null";
        if (eventName == "empty_done")
            _logger?.LogDebug("Campaign: {user} sendevent camp={campaign} event={event} active={active}",
                context.UserName, campaignId, eventName, activeLocation);
        else
            _logger?.LogInformation(
                "Campaign: {user} sendevent camp={campaign} event={event} active={active} params={params}",
                context.UserName, campaignId, eventName, activeLocation, eventParams);

        if (eventName == "choice_battle_yes")
            SetTutorialBattleUnlocked(record, true);
        else if (eventName == "choice_battle_no")
            SetTutorialBattleUnlocked(record, false);
        else if (eventName == "conv_done")
            record = CompleteConversation(context, record);
        else if (eventName is "choice_go" or "choice_stay")
        {
            if (eventName == "choice_go")
            {
                var travel = _store.TryBeginAz1Travel(record, areaId =>
                {
                    var area = new CampaignRunRecord
                    {
                        CampaignId = areaId,
                        CampaignType = "AREA",
                        TemplateName = CampaignAz1Config.TemplateName,
                        Started = true,
                        ProfileId = record.ProfileId,
                        ChampionId = record.ChampionId,
                        Race = record.Race,
                    };
                    area.State = CampaignStateFactory.CreateAz1AreaState(
                        area, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                    if (CampaignAz1OpeningCatalog.TryGetWelcome(area.Race, out var welcomeId, out _))
                        CampaignStateFactory.PrepareAz1Welcome(area.State, welcomeId);
                    return area;
                });
                if (travel is not null)
                {
                    PrepareAz1Opening(travel.Area);
                    QueueAz1Travel(context, travel.Area);
                    _logger?.LogInformation(
                        "Campaign: AZ1 travel accepted champion={champion} panorama={panorama} area={area} created={created} node={node}; queued AREA transition=true",
                        travel.Area.ChampionId, travel.Panorama.CampaignId,
                        travel.Area.CampaignId, travel.Created, travel.Area.State["LastNode"]?.ToString());
                }
                else
                    _logger?.LogWarning("Campaign: ignored unauthorized or repeated AZ1 choice_go camp={campaign}", campaignId);
            }
            else
            {
                var cfg = CampaignStateFactory.Race(record.Race);
                var intro = FindLocation(record.State, cfg.IntroNpc);
                if (string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.State["ALoc"]?.ToString(), cfg.IntroNpc, StringComparison.Ordinal)
                    && string.Equals(intro?["conversationId"]?.ToString(), cfg.TransitionConversation, StringComparison.OrdinalIgnoreCase))
                    _logger?.LogInformation("Campaign: post-Crayburn choice_stay champion={champion}; retained panorama={panorama}", record.ChampionId, campaignId);
            }
        }
        else if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
                 && (eventName is "visit_path" or "visit_node"))
        {
            ApplyAz1OpeningTravel(record, eventName, root);
        }
        else if (eventName == "empty_done"
                 && string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase))
        {
            var empty = string.IsNullOrWhiteSpace(activeLocation) ? null : FindLocation(record.State, activeLocation);
            if (empty is not null && empty["type"]?.ToString() == "Empty"
                && !string.Equals(CampaignAz1Config.OpeningNode(activeLocation),
                    CampaignAz1Config.ThirdNode, StringComparison.Ordinal)
                && empty["completed"]?.GetValue<bool>() != true)
            {
                empty["completed"] = true;
                record.State["ALoc"] = null;
                record.State["CurState"] = "EXPLORE";
                _store.Save(record);
                QueueAz1OpeningProgress(context, record);
                _logger?.LogInformation("Campaign: completed empty AREA location {node}", empty["node"]?.ToString());
            }
            else if (empty is not null
                     && string.Equals(CampaignAz1Config.OpeningNode(activeLocation),
                         CampaignAz1Config.ThirdNode, StringComparison.Ordinal)
                     && empty["type"]?.ToString() != "Empty"
                     && record.State["Flags"]?["DunnwoodResyncSent"]?.GetValue<bool>() != true)
            {
                // An older client view may still be attempting the removed Empty
                // action after its persisted node was hydrated as authored content.
                if (record.State["Flags"] is not JsonObject flags)
                {
                    flags = new JsonObject();
                    record.State["Flags"] = flags;
                }
                flags["DunnwoodResyncSent"] = true;
                _store.Save(record);
                QueueAz1OpeningProgress(context, record);
                _logger?.LogWarning("Campaign: rejected stale Dunnwood empty_done; sent AREA state refresh");
            }
        }
        else if (eventName == "gaal_camp_accept"
                 && string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase))
        {
            var gaal = FindLocation(record.State, CampaignAz1Config.OaksNode);
            var activeGaal = string.Equals(CampaignAz1Config.OpeningNode(activeLocation),
                CampaignAz1Config.OaksNode, StringComparison.Ordinal);
            var verifiedDialogue = CampaignAz1AuthoredCatalog.TryGetGaalCamp(
                out var first, out var repeat, out _)
                && (string.Equals(gaal?["conversationId"]?.ToString(), first, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(gaal?["conversationId"]?.ToString(), repeat, StringComparison.OrdinalIgnoreCase));
            if (!activeGaal || gaal?["type"]?.ToString() != "Convo" || !verifiedDialogue
                || gaal["enabled"]?.GetValue<bool>() != true)
                return CampaignStateFactory.BuildFailure(campaignId, "No active Gaal Camp reading");

            // TODO: Temporary solution for Gaal Camp: accept the original 100 Gold
            // dialogue choice while persistent Gold ownership/debit is unavailable.
            // This acknowledges the choice ONLY; do not invent a Fortune card,
            // grant any benefit or alter a non-existent authoritative balance.
            // Replace with a validated balance debit and original Fortune flow.
            if (gaal["gaal_development_choice_pending"]?.GetValue<bool>() != true)
            {
                gaal["gaal_development_choice_pending"] = true;
                _store.Save(record);
            }
            _logger?.LogInformation("Campaign: Gaal Camp development payment bypass accepted (no Gold debit, no Fortune grant)");
        }
        else if (eventName == "display_done"
                 && string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase))
        {
            // No ModDisplay may be opened until a verified Fortune is stored;
            // stale acknowledgements have no effect on campaign progression.
            _logger?.LogDebug("Campaign: ignored Gaal display_done without an active Fortune display");
        }
        else if (eventName == "shroom_choice"
                 && string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("OParms", out var choiceParams)
                || choiceParams.ValueKind != JsonValueKind.Array || choiceParams.GetArrayLength() != 1
                || choiceParams[0].ValueKind != JsonValueKind.String)
                return CampaignStateFactory.BuildFailure(campaignId, "Invalid Shroom Haus choice payload");
            var choice = choiceParams[0].GetString();
            if (!CampaignShroomRewardCatalog.IsChoice(choice)
                || !CampaignShroomRewardCatalog.TryGetVerifiedClientTemplates(out _)
                || !_collection.TryGetShroomRewardInstanceId(record.Race, choice!, out var instanceId)
                || !_store.TryClaimAz1ShroomReward(record, choice!))
            {
                _logger?.LogWarning("Campaign: rejected Shroom Haus choice camp={campaign} selected={choice}", campaignId, choice);
                return CampaignStateFactory.BuildFailure(campaignId, "Shroom Haus offer is unavailable or already claimed");
            }
            _logger?.LogInformation("Campaign: Shroom Haus claimed camp={campaign} champion={champion} template={choice} instance={instance}",
                campaignId, record.ChampionId, choice, instanceId);
            return CampaignStateFactory.BuildInputResponse(record,
                appliedOverride: CampaignShroomRewardCatalog.BuildAppliedCard(choice!, instanceId));
        }
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
        bool won,
        IReadOnlyList<Guid>? capturedTemplates = null)
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

        CampaignRunRecord? tamedQuest = null;
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
        else if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
                 && CampaignAz1AuthoredCatalog.TryGetDunnwood(out var wildCubScene, out _)
                 && string.Equals(encounterGuid, wildCubScene, StringComparison.OrdinalIgnoreCase)
                 && FindLocation(record.State, CampaignAz1Config.ThirdNode) is JsonObject wildCub
                 && string.Equals(wildCub["encounter"]?.ToString(), encounterGuid, StringComparison.OrdinalIgnoreCase))
        {
            // Original Tame.Apply records a captured card on the human champion. A battle
            // win alone must not complete the authored "Tame Dire Toad and win match" objective.
            var tamedDireToad = won && capturedTemplates?.Contains(DunnwoodDireToadTemplate) == true;
            if (won)
                wildCub["bear_defeated"] = true;
            wildCub["completed"] = tamedDireToad;
            wildCub["repeatable"] = won && !tamedDireToad;
            wildCub["autostart"] = false;
            record.State["ALoc"] = null;
            record.State["LastNode"] = CampaignAz1Config.ThirdNode;
            record.State["CurState"] = "EXPLORE";
            record.State.Remove("ActiveEncounterGuid");
            record.State[won ? "Wins" : "Losses"] = StateInt(record.State, won ? "Wins" : "Losses") + 1;

            if (tamedDireToad)
                tamedQuest = AdvanceDunnwoodTamedQuest(context, record, encounterGuid);

            if (won)
            {
                var shroomReady = CampaignAz1AuthoredCatalog.TryGetSporemistIntro(out _, out var shroomScenePrelude);
                if (shroomReady)
                    CampaignStateFactory.PrepareAz1Sporemist(record.State, shroomScenePrelude);
                var gaalReady = CampaignAz1AuthoredCatalog.TryGetGaalCamp(out var first, out var repeat, out var fortune);
                if (gaalReady)
                    CampaignStateFactory.PrepareAz1GaalCamp(record.State, first, repeat, fortune);
                foreach (var node in new[] { CampaignAz1Config.FourthNode, CampaignAz1Config.OaksNode })
                {
                    var revealed = FindLocation(record.State, node);
                    if (revealed is null)
                        continue;
                    revealed["visible"] = true;
                    revealed["enabled"] = node == CampaignAz1Config.FourthNode ? shroomReady : gaalReady;
                }
            }
            _logger?.LogInformation(
                "Campaign: Dunnwood Wild Cub battle ended won={Won} capturedDireToad={Captured} verifiedTemplates={Count} retryable={Retryable}; adjacent nodes {Status}",
                won, tamedDireToad, capturedTemplates?.Count ?? 0, won && !tamedDireToad,
                won ? "both authored exits enabled when their client templates resolve" : "locked");
        }
        else if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
                 && CampaignAz1AuthoredCatalog.TryGetSutherland(out var sutherlandScene, out _)
                 && string.Equals(encounterGuid, sutherlandScene, StringComparison.OrdinalIgnoreCase)
                 && FindLocation(record.State, CampaignAz1Config.SutherlandNode) is JsonObject cockatwice
                 && string.Equals(cockatwice["encounter"]?.ToString(), encounterGuid, StringComparison.OrdinalIgnoreCase))
        {
            // The original az01_tamed Tame2 objective requires both a won
            // Cockatwice encounter and a native capture of Chickatwice.
            // A win without the objective receipt must remain retryable.
            var capturedChickatwice = won && CampaignAz1AuthoredCatalog.HasCapturedChickatwice(capturedTemplates);
            var completedTame2 = capturedChickatwice
                ? AdvanceSutherlandTamedQuest(context, record, encounterGuid)
                : null;
            if (completedTame2 is not null)
                tamedQuest = completedTame2;
            var completed = completedTame2 is not null;
            cockatwice["completed"] = completed;
            cockatwice["repeatable"] = won && !completed;
            cockatwice["autostart"] = false;
            if (completed)
            {
                cockatwice["type"] = "None";
                cockatwice["encounter"] = null;
            }
            record.State["ALoc"] = null;
            record.State["LastNode"] = CampaignAz1Config.SutherlandNode;
            record.State["CurState"] = "EXPLORE";
            record.State.Remove("ActiveEncounterGuid");
            record.State[won ? "Wins" : "Losses"] = StateInt(record.State, won ? "Wins" : "Losses") + 1;
            _logger?.LogInformation("Campaign: The Sutherland Cockatwice Chick battle ended won={Won} capturedChickatwice={Captured} completedTame2={Completed}; downstream exits remain gated", won, capturedChickatwice, completed);
        }
        else
        {
            _logger?.LogWarning(
                "Campaign: battle result for encounter {encounter} has no implemented campaign progression rule",
                encounterGuid);
            return false;
        }

        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.TemplateName, CampaignAz1Config.TemplateName, StringComparison.OrdinalIgnoreCase))
            CampaignStateFactory.SyncAz1OpeningExitLocks(record.State);
        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase))
            TraceAz1Map(record, true);

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

        if (tamedQuest is not null)
            SendTamedQuestProgress(context, tamedQuest);

        return sent;
    }

    /// <summary>
    /// Advance the authored Tame1 objective only when its linked encounter provided
    /// a verified capture. AREA and QUEST are records in one champion save aggregate;
    /// the caller persists both together when it saves the AREA record.
    /// </summary>
    private CampaignRunRecord? AdvanceDunnwoodTamedQuest(
        SessionContext context, CampaignRunRecord area, string encounterGuid)
    {
        var quest = _store.GetActive(context.ProfileId, area.ChampionId, area.Race, 4, "az01_tamed")
            .FirstOrDefault();
        if (quest is null)
        {
            _logger?.LogWarning("Campaign: verified Dire Toad capture for champion {Champion} has no active Tamed QUEST", area.ChampionId);
            return null;
        }

        var objective = FindLocation(quest.State, "Tame1");
        if (objective is null
            || !string.Equals(objective["encounter"]?.ToString(), encounterGuid, StringComparison.OrdinalIgnoreCase)
            || objective["completed"]?.GetValue<bool>() == true)
        {
            _logger?.LogWarning("Campaign: Tamed QUEST {Quest} has no pending Tame1 objective for encounter {Encounter}",
                quest.CampaignId, encounterGuid);
            return null;
        }

        objective["completed"] = true;
        var next = FindLocation(quest.State, "Tame2");
        if (next is not null)
        {
            quest.State["LastNode"] = "Tame2";
            next["enabled"] = true;
            next["visible"] = true;
        }
        var flags = quest.State["Flags"] as JsonObject;
        if (flags is null)
        {
            flags = new JsonObject();
            quest.State["Flags"] = flags;
        }
        flags["_quest_objective_idx"] = 1;
        _logger?.LogInformation("Campaign: Tamed QUEST {Quest} Tame1 completed for champion {Champion}; next=Tame2",
            quest.CampaignId, quest.ChampionId);
        return quest;
    }

    /// <summary>
    /// The second authored Tamed objective is tied to the Cockatwice Chick
    /// scene, not simply to having visited Node009 or won its encounter.
    /// Previous objectives must remain completed in the persisted journal.
    /// </summary>
    private CampaignRunRecord? AdvanceSutherlandTamedQuest(
        SessionContext context, CampaignRunRecord area, string encounterGuid)
    {
        var quest = _store.GetActive(context.ProfileId, area.ChampionId, area.Race, 4, "az01_tamed")
            .FirstOrDefault();
        if (quest is null || FindLocation(quest.State, "Tame1")?["completed"]?.GetValue<bool>() != true)
            return null;
        var objective = FindLocation(quest.State, "Tame2");
        if (objective is null
            || !string.Equals(objective["encounter"]?.ToString(), encounterGuid, StringComparison.OrdinalIgnoreCase)
            || objective["completed"]?.GetValue<bool>() == true)
            return null;
        objective["completed"] = true;
        var next = FindLocation(quest.State, "Tame3");
        if (next is not null)
        {
            quest.State["LastNode"] = "Tame3";
            next["enabled"] = true;
            next["visible"] = true;
        }
        var flags = quest.State["Flags"] as JsonObject;
        if (flags is null)
        {
            flags = new JsonObject();
            quest.State["Flags"] = flags;
        }
        flags["_quest_objective_idx"] = 2;
        _logger?.LogInformation("Campaign: Tamed QUEST {Quest} Tame2 completed for champion {Champion}; next=Tame3",
            quest.CampaignId, quest.ChampionId);
        return quest;
    }

    private void SendTamedQuestProgress(SessionContext context, CampaignRunRecord quest)
    {
        var envelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = quest.CampaignId,
            ["ChampID"] = quest.ChampionId,
            ["Reason"] = "quest_progress",
            ["TemplateType"] = "QUEST",
            ["Transition"] = false,
            ["State"] = quest.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        var sent = context.TrySendMessageToClient(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(envelope),
        });
        _logger?.LogInformation("Campaign: Tamed QUEST cmpupdate quest={Quest} sent={Sent}", quest.CampaignId, sent);
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
        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(active)
                || FindLocation(record.State, active)?["type"]?.ToString() != "Encounter"
                || FindLocation(record.State, active)?["completed"]?.GetValue<bool>() == true))
        {
            _logger?.LogWarning("Campaign: rejected AREA battle start at non-encounter location {active}", active);
            return;
        }

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
        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            && string.Equals(CampaignAz1Config.OpeningNode(record.State["ALoc"]?.ToString()),
                CampaignAz1Config.NextNode, StringComparison.Ordinal))
        {
            var welcome = FindLocation(record.State, CampaignAz1Config.NextNode);
            if (welcome is not null
                && welcome["completed"]?.GetValue<bool>() != true
                && welcome["type"]?.ToString() == "Convo"
                && CampaignAz1OpeningCatalog.TryGetWelcome(record.Race, out var verifiedId, out _)
                && string.Equals(welcome["conversationId"]?.ToString(), verifiedId, StringComparison.OrdinalIgnoreCase))
            {
                var authored = CampaignAz1AuthoredCatalog.TryGetOpeningQuests(record.Race, out var templates)
                    ? templates : Array.Empty<CampaignAz1AuthoredCatalog.Quest>();
                var spawned = _store.EnsureAz1OpeningQuests(record, authored, area =>
                {
                    var nodeTwo = FindLocation(area.State, CampaignAz1Config.NextNode)!;
                    nodeTwo["completed"] = true;
                    nodeTwo["autostart"] = false;
                    nodeTwo["givequest"] = false;
                    area.State["ALoc"] = null;
                    area.State["CurState"] = "EXPLORE";
                    var third = FindLocation(area.State, CampaignAz1Config.ThirdNode);
                    if (third is not null)
                    {
                        third["visible"] = true;
                        third["enabled"] = true;
                    }
                });
                QueueAz1QuestSpawns(context, record, spawned);
                QueueAz1OpeningProgress(context, record);
                _logger?.LogInformation(
                    "Campaign: AZ1 welcome {conversation} completed at Node002 for {faction}; Dunnwood revealed, new quests={quests}",
                    verifiedId, record.Race is >= 1 and <= 4 ? "Ardent" : "Underworld", spawned.Count);
                return record;
            }
            _logger?.LogWarning("Campaign: ignored AZ1 Node002 conv_done without the verified authored welcome conversation");
            return record;
        }

        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            && string.Equals(CampaignAz1Config.OpeningNode(record.State["ALoc"]?.ToString()),
                CampaignAz1Config.ThirdNode, StringComparison.Ordinal))
        {
            var wildCub = FindLocation(record.State, CampaignAz1Config.ThirdNode);
            if (wildCub is not null && wildCub["type"]?.ToString() == "Convo"
                && wildCub["pre_encounter_completed"]?.GetValue<bool>() != true
                && CampaignAz1AuthoredCatalog.TryGetDunnwood(out var scene, out var prelude)
                && string.Equals(wildCub["conversationId"]?.ToString(), prelude, StringComparison.OrdinalIgnoreCase))
            {
                wildCub["pre_encounter_completed"] = true;
                wildCub["type"] = "Encounter";
                wildCub["encounter"] = scene;
                wildCub["conversationId"] = null;
                wildCub["autostart"] = false;
                record.State["ALoc"] = null;
                record.State["CurState"] = "EXPLORE";
                _store.Save(record);
                QueueAz1OpeningProgress(context, record);
                _logger?.LogInformation("Campaign: Dunnwood Feralroot Bear prelude completed; Wild Cub encounter={scene} ready at Node003", scene);
            }
            return record;
        }

        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            && string.Equals(CampaignAz1Config.OpeningNode(record.State["ALoc"]?.ToString()),
                CampaignAz1Config.SutherlandNode, StringComparison.Ordinal))
        {
            var location = FindLocation(record.State, CampaignAz1Config.SutherlandNode);
            if (location?["type"]?.ToString() == "Convo"
                && location["pre_encounter_completed"]?.GetValue<bool>() != true
                && CampaignAz1AuthoredCatalog.TryGetSutherland(out var scene, out var prelude)
                && string.Equals(location["conversationId"]?.ToString(), prelude, StringComparison.OrdinalIgnoreCase))
            {
                location["pre_encounter_completed"] = true;
                CampaignStateFactory.PrepareAz1Sutherland(record.State, scene, prelude);
                record.State["ALoc"] = null;
                record.State["CurState"] = "EXPLORE";
                _store.Save(record);
                QueueAz1OpeningProgress(context, record, "az1_cockatwice_prelude");
                _logger?.LogInformation("Campaign: Cockatwice prelude completed; encounter={scene} ready at Node009", scene);
            }
            else
                _logger?.LogWarning("Campaign: ignored Node009 conv_done without authored Cockatwice prelude");
            return record;
        }

        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            && string.Equals(CampaignAz1Config.OpeningNode(record.State["ALoc"]?.ToString()),
                CampaignAz1Config.OaksNode, StringComparison.Ordinal))
        {
            var camp = FindLocation(record.State, CampaignAz1Config.OaksNode);
            if (camp is not null && camp["type"]?.ToString() == "Convo"
                && CampaignAz1AuthoredCatalog.TryGetGaalCamp(out var first, out var repeat, out var fortune)
                && new[] { first, repeat, fortune }.Contains(camp["conversationId"]?.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var developmentChoice = camp["gaal_development_choice_pending"]?.GetValue<bool>() == true;
                camp.Remove("gaal_development_choice_pending");
                camp["gaal_visits"] = (camp["gaal_visits"]?.GetValue<int>() ?? 0) + 1;
                CampaignStateFactory.PrepareAz1GaalCamp(record.State, first, repeat, fortune);
                if (CampaignAz1AuthoredCatalog.TryGetSutherland(out var sutherlandScene, out var sutherlandPrelude))
                    CampaignStateFactory.PrepareAz1Sutherland(record.State, sutherlandScene, sutherlandPrelude);
                if (developmentChoice)
                    _logger?.LogInformation("Campaign: Gaal Camp development choice completed without Fortune effect");
                record.State["ALoc"] = null;
                record.State["CurState"] = "EXPLORE";
                _store.Save(record);
                QueueAz1OpeningProgress(context, record, "az1_gaal_complete");
                _logger?.LogInformation("Campaign: Gaal Camp conversation closed; visits={visits}, Node009 available when authored content resolves", camp["gaal_visits"]);
            }
            else
                _logger?.LogWarning("Campaign: ignored Gaal Camp conv_done without verified active dialogue");
            return record;
        }

        if (string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            && string.Equals(CampaignAz1Config.OpeningNode(record.State["ALoc"]?.ToString()),
                CampaignAz1Config.FourthNode, StringComparison.Ordinal))
        {
            var hollow = FindLocation(record.State, CampaignAz1Config.FourthNode);
            if (hollow?["type"]?.ToString() == "Convo"
                && hollow["shroom_intro_completed"]?.GetValue<bool>() != true
                && CampaignAz1AuthoredCatalog.TryGetSporemistIntro(out _, out var shroomPrelude)
                && string.Equals(hollow["conversationId"]?.ToString(), shroomPrelude, StringComparison.OrdinalIgnoreCase))
            {
                hollow["shroom_intro_completed"] = true;
                CampaignStateFactory.PrepareAz1Sporemist(record.State, shroomPrelude);
                // Keep ALoc active so the original client's OnHandleStateChange
                // can open the Shroom Haus chooser from this very response.
                record.State["CurState"] = "EXPLORE";
                _store.Save(record);
                _logger?.LogInformation("Campaign: Sporemist Hollow intro completed; chooser type={type} choices={count}",
                    hollow["type"]?.ToString(), (hollow["choices"] as JsonArray)?.Count ?? 0);
            }
            else
                _logger?.LogWarning("Campaign: ignored Sporemist Hollow conv_done without original first-visit conversation");
            return record;
        }

        var cfg = CampaignStateFactory.Race(record.Race);
        var active = record.State["ALoc"]?.GetValue<string>();
        var isPanorama = string.Equals(record.CampaignType, "PANORAMA", StringComparison.OrdinalIgnoreCase);

        if (isPanorama && string.Equals(active, cfg.IntroNpc, StringComparison.Ordinal))
        {
            var intro = FindLocation(record.State, cfg.IntroNpc);
            if (record.State["PostCrayburnReport"]?.GetValue<bool>() == true
                && string.Equals(intro?["conversationId"]?.ToString(),
                    cfg.TransitionConversation, StringComparison.OrdinalIgnoreCase))
            {
                // The travel conversation is not the tutorial introduction. Keep
                // the NPC available if the player chooses to remain in the panorama.
                record.State["ALoc"] = null;
                record.State["CurState"] = "EXPLORE";
                _store.Save(record);
                _logger?.LogInformation(
                    "Campaign: {race} travel conversation {conversation} closed at {npc}; panorama retained",
                    cfg.Name, cfg.TransitionConversation, cfg.IntroNpc);
                return record;
            }

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
                    panorama => FinishCrayburnReportPanorama(panorama, cfg));
                if (turnIn is not null)
                {
                    QueueCrayburnQuestTurnIn(context, turnIn);
                    _logger?.LogInformation(
                        "Campaign: Crayburn report conversation {conversation} completed; quest={quest} Step2 finished; panorama={panorama} quest giver={npc}; travel npc={travelNpc} conversation={travelConversation}",
                        reportConversation, turnIn.Quest.CampaignId, turnIn.Panorama.CampaignId,
                        cfg.QuestNpc, cfg.IntroNpc, cfg.TransitionConversation);
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

    private static void FinishCrayburnReportPanorama(CampaignRunRecord panorama, CampaignRaceConfig cfg)
    {
        var giver = FindLocation(panorama.State, cfg.QuestNpc)!;
        giver["completed"] = true;
        giver["autostart"] = false;
        giver["turninquest"] = false;
        giver["enabled"] = false;
        panorama.State["ALoc"] = null;
        panorama.State["LastNode"] = cfg.QuestNpc;
        panorama.State["CurState"] = "EXPLORE";
        CampaignStateFactory.PreparePostCrayburnTravel(panorama.State, cfg);
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

    private void PrepareAz1Opening(CampaignRunRecord record)
    {
        if (!string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.TemplateName, CampaignAz1Config.TemplateName, StringComparison.OrdinalIgnoreCase))
            return;
        var mapChanged = CampaignStateFactory.EnsureAz1MapState(record.State);
        if (!CampaignAz1OpeningCatalog.TryGetWelcome(record.Race, out var conversationId, out var conversationName))
        {
            _logger?.LogWarning("Campaign: original HEX AZ1 Node002 {faction} welcome template was not uniquely resolved",
                record.Race is >= 1 and <= 4 ? "Ardent" : "Underworld");
            var locksChanged = CampaignStateFactory.SyncAz1OpeningExitLocks(record.State);
            if (mapChanged || locksChanged)
                _store.Save(record);
            TraceAz1Map(record, mapChanged || locksChanged);
            return;
        }
        var changed = mapChanged | CampaignStateFactory.PrepareAz1Welcome(record.State, conversationId);
        if (CampaignAz1AuthoredCatalog.TryGetDunnwood(out var scene, out var prelude))
        {
            var repairedDunnwood = CampaignStateFactory.PrepareAz1Dunnwood(record.State, scene, prelude);
            changed |= repairedDunnwood;
            if (repairedDunnwood)
                _logger?.LogInformation("Campaign: hydrated Dunnwood from HEX SceneData Wild Cub={scene} Feralroot Bear prelude={prelude}",
                    scene, prelude);
        }
        else
        {
            _logger?.LogWarning("Campaign: original HEX Dunnwood Wild Cub scene/prelude could not be uniquely resolved; encounter is unavailable");
        }
        var bear = FindLocation(record.State, CampaignAz1Config.ThirdNode);
        if (bear is not null && bear["bear_defeated"]?.GetValue<bool>() != true
            && CampaignStateFactory.HasWonAz1Bear(record.State))
        {
            bear["bear_defeated"] = true;
            changed = true;
        }
        if (CampaignAz1AuthoredCatalog.TryGetGaalCamp(out var gaalFirst, out var gaalRepeat, out var gaalFortune))
        {
            changed |= CampaignStateFactory.PrepareAz1GaalCamp(record.State, gaalFirst, gaalRepeat, gaalFortune);
        }
        else if (FindLocation(record.State, CampaignAz1Config.OaksNode) is JsonObject gaal)
        {
            if (gaal["enabled"]?.GetValue<bool>() == true)
            {
                gaal["enabled"] = false;
                changed = true;
            }
            if (CampaignStateFactory.HasWonAz1Bear(record.State))
                _logger?.LogWarning("Campaign: Gaal Camp Node007 disabled; original HEX scene/conversation templates not resolved");
        }
        if (CampaignAz1AuthoredCatalog.TryGetSutherland(out var sutherlandScene, out var sutherlandPrelude))
        {
            changed |= CampaignStateFactory.PrepareAz1Sutherland(record.State, sutherlandScene, sutherlandPrelude);
        }
        else if (FindLocation(record.State, CampaignAz1Config.SutherlandNode) is JsonObject sutherland)
        {
            if (sutherland["enabled"]?.GetValue<bool>() == true || sutherland["visible"]?.GetValue<bool>() == true)
            {
                sutherland["enabled"] = false;
                sutherland["visible"] = false;
                changed = true;
            }
            if ((FindLocation(record.State, CampaignAz1Config.OaksNode)?["gaal_visits"]?.GetValue<int>() ?? 0) > 0)
                _logger?.LogWarning("Campaign: Node009 unavailable; authored Cockatwice scene/conversation not resolved");
        }
        if (CampaignAz1AuthoredCatalog.TryGetSporemistIntro(out var shroomScene, out var shroomPrelude))
        {
            changed |= CampaignStateFactory.PrepareAz1Sporemist(record.State, shroomPrelude);
        }
        else if (FindLocation(record.State, CampaignAz1Config.ThirdNode)?["completed"]?.GetValue<bool>() == true)
        {
            // A repaired/older AREA must not retain an enabled action when
            // the corresponding native authored content cannot be resolved.
            var hollow = FindLocation(record.State, CampaignAz1Config.FourthNode);
            if (hollow?["enabled"]?.GetValue<bool>() == true)
            {
                hollow["enabled"] = false;
                changed = true;
            }
            _logger?.LogWarning("Campaign: Sporemist Hollow remains unavailable; original NODE 04 Shroom Haus scene/intro is not uniquely resolved; {candidates}",
                CampaignAz1AuthoredCatalog.DescribeSporemistCandidates());
        }
        if (CampaignStateFactory.HasWonAz1Bear(record.State))
            foreach (var node in new[] { CampaignAz1Config.FourthNode, CampaignAz1Config.OaksNode })
                if (FindLocation(record.State, node) is JsonObject loc && loc["visible"]?.GetValue<bool>() != true)
                {
                    loc["visible"] = true;
                    changed = true;
                }
        changed |= CampaignStateFactory.SyncAz1OpeningExitLocks(record.State);
        if (changed)
        {
            _store.Save(record);
            _logger?.LogInformation("Campaign: hydrated AZ1 opening for champion={champion} welcome={conversation} ({name})",
                record.ChampionId, conversationId, conversationName);
        }
        TraceAz1Map(record, changed);
    }

    private void BackfillAz1OpeningQuests(SessionContext context, CampaignRunRecord record)
    {
        if (!string.Equals(record.CampaignType, "AREA", StringComparison.OrdinalIgnoreCase)
            || FindLocation(record.State, CampaignAz1Config.NextNode)?["completed"]?.GetValue<bool>() != true)
            return;
        if (!CampaignAz1AuthoredCatalog.TryGetOpeningQuests(record.Race, out var templates))
            return;
        var spawned = _store.EnsureAz1OpeningQuests(record, templates);
        QueueAz1QuestSpawns(context, record, spawned);
    }

    private void QueueAz1QuestSpawns(SessionContext context, CampaignRunRecord area,
        IReadOnlyList<CampaignRunRecord> spawned)
    {
        foreach (var quest in spawned)
        {
            var envelope = new JsonObject
            {
                ["ReckID"] = 0,
                ["CampID"] = quest.CampaignId,
                ["ChampID"] = quest.ChampionId,
                ["TemplateType"] = "QUEST",
                ["TemplateName"] = quest.TemplateName,
                ["SpawnedBy"] = area.CampaignId,
                ["SpawnedByTemplate"] = CampaignAz1Config.TemplateName,
                ["Transition"] = false,
                ["ChildState"] = quest.State.DeepClone(),
                ["RequestType"] = "campspawn",
            };
            context.QueueMessageAfterResponse(new CampSysGeneral.Request
            {
                Envelope = JsonSerializer.SerializeToUtf8Bytes(envelope),
            });
            _logger?.LogInformation("Campaign: AZ1 quest spawned {script} CampID={quest} champion={champion}",
                quest.TemplateName, quest.CampaignId, quest.ChampionId);
        }
    }

    private void QueueAz1OpeningProgress(SessionContext context, CampaignRunRecord area,
        string reason = "az1_welcome_complete")
    {
        if (CampaignStateFactory.SyncAz1OpeningExitLocks(area.State))
            _store.Save(area);
        TraceAz1Map(area, true);
        var envelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = area.CampaignId,
            ["ChampID"] = area.ChampionId,
            ["Reason"] = reason,
            ["TemplateType"] = "AREA",
            ["Transition"] = false,
            ["State"] = area.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(envelope),
        });
    }

    private void QueueAz1Travel(SessionContext context, CampaignRunRecord area)
    {
        var envelope = new JsonObject
        {
            ["ReckID"] = 0,
            ["CampID"] = area.CampaignId,
            ["ChampID"] = area.ChampionId,
            ["Reason"] = "feralroot_travel",
            ["TemplateType"] = "AREA",
            ["Transition"] = true,
            ["State"] = area.State.DeepClone(),
            ["Applied"] = CampaignStateFactory.EmptyAppliedUpdates(),
            ["RequestType"] = "cmpupdate",
        };
        context.QueueMessageAfterResponse(new CampSysGeneral.Request
        {
            Envelope = JsonSerializer.SerializeToUtf8Bytes(envelope),
        });
    }

    private static bool HasAz1OpenTravelRoute(JsonObject state, string from, string to)
    {
        // The client may animate Node004 -> Node003 -> Node007 as a single
        // destination visit. Validate every intermediate edge against the
        // original map and the server's current locks; a visit_path event
        // never supplies authority to move or unlock a destination.
        if (from == to)
            return true;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in new[]
        {
            CampaignAz1Config.StartingNode, CampaignAz1Config.NextNode,
            CampaignAz1Config.ThirdNode, CampaignAz1Config.FourthNode,
            CampaignAz1Config.OaksNode, CampaignAz1Config.SutherlandNode,
        })
            if (FindLocation(state, node)?["enabled"]?.GetValue<bool>() == true)
                allowed.Add(node);
        if (!allowed.Contains(from) || !allowed.Contains(to))
            return false;

        var visited = (state["PublicState"]?["Data"]?["visited_nodes"] as JsonArray)?
            .Select(item => item?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var locked = (state["PublicState"]?["Data"]?["locked_paths"] as JsonArray)?
            .Select(item => item?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in CampaignAz1MapCatalog.Paths)
            {
                if (locked.Contains(edge.Id))
                    continue;
                var neighbor = edge.From == current ? edge.To
                    : edge.To == current ? edge.From : null;
                if (neighbor is null || !allowed.Contains(neighbor) || !seen.Add(neighbor))
                    continue;
                if (neighbor == to)
                    return true;
                // Intermediate locations must already have been visited, so
                // the pathfinder cannot skip an undiscovered encounter.
                if (visited.Contains(neighbor))
                    queue.Enqueue(neighbor);
            }
        }
        return false;
    }

    private void TraceAz1Map(CampaignRunRecord area, bool updated)
    {
        if (_logger is null || !_logger.IsEnabled(updated ? LogLevel.Information : LogLevel.Debug))
            return;
        var visible = new List<string>();
        var enabled = new List<string>();
        foreach (var location in (area.State["VisLocs"] as JsonArray)?.OfType<JsonObject>()
                     ?? Enumerable.Empty<JsonObject>())
        {
            var data = location["Data"] as JsonObject;
            var id = data?["node"]?.ToString();
            if (id is null)
                continue;
            if (data?["visible"]?.GetValue<bool>() == true)
                visible.Add(id);
            if (data?["enabled"]?.GetValue<bool>() == true)
                enabled.Add(id);
        }
        var publicData = area.State["PublicState"]?["Data"];
        var locked = (publicData?["locked_paths"] as JsonArray)?.Select(item => item?.ToString()).ToArray() ?? [];
        var visited = (publicData?["visited_nodes"] as JsonArray)?.Select(item => item?.ToString()).ToArray() ?? [];
        var paths = (publicData?["visited_paths"] as JsonArray)?.Select(item => item?.ToString()).ToArray() ?? [];
        _logger.Log(updated ? LogLevel.Information : LogLevel.Debug,
            "Campaign: AZ1 map camp={campaign} LastNode={last} ALoc={active} locations={count} visible=[{visible}] enabled=[{enabled}] visited_nodes={visitedCount} visited_paths={pathCount} locked_paths={lockCount}",
            area.CampaignId, area.State["LastNode"]?.ToString(), area.State["ALoc"]?.ToString(),
            (area.State["VisLocs"] as JsonArray)?.Count ?? 0, string.Join(",", visible),
            string.Join(",", enabled), visited.Length, paths.Length, locked.Length);
    }

    private void ApplyAz1OpeningTravel(CampaignRunRecord area, string eventName, JsonElement root)
    {
        if (eventName == "visit_path")
        {
            if (!root.TryGetProperty("OParms", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
                return;

            var paths = new List<string>();
            CollectPathNames(parameters, paths);
            var visited = area.State["PublicState"]?["Data"]?["visited_paths"] as JsonArray;
            if (visited is null)
                return;
            var changed = false;
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                // The original Unity path object is authoritative. Keep the
                // previously accepted reversed opening aliases for old clients.
                if (!CampaignAz1MapCatalog.TryGetPath(path, out var edge)
                    && (!CampaignAz1Config.TryGetOpeningEdge(path, out var opening)
                        || !CampaignAz1MapCatalog.TryGetPath(opening.Path, out edge)))
                {
                    _logger?.LogDebug("Campaign: ignored unknown AZ1 path {path}", path);
                    continue;
                }
                var locked = area.State["PublicState"]?["Data"]?["locked_paths"] as JsonArray;
                if (locked?.Any(item => string.Equals(item?.ToString(), edge.Id, StringComparison.Ordinal)) == true)
                {
                    _logger?.LogDebug("Campaign: ignored locked AZ1 path {path}", edge.Id);
                    continue;
                }
                var from = FindLocation(area.State, edge.From);
                var to = FindLocation(area.State, edge.To);
                var fromCompleted = edge.From == CampaignAz1Config.StartingNode
                    || from?["completed"]?.GetValue<bool>() == true
                    || (edge.From == CampaignAz1Config.ThirdNode && CampaignStateFactory.HasWonAz1Bear(area.State))
                    || (edge.From == CampaignAz1Config.OaksNode && (from?["gaal_visits"]?.GetValue<int>() ?? 0) > 0);
                var toCompleted = edge.To == CampaignAz1Config.StartingNode
                    || to?["completed"]?.GetValue<bool>() == true
                    || (edge.To == CampaignAz1Config.ThirdNode && CampaignStateFactory.HasWonAz1Bear(area.State))
                    || (edge.To == CampaignAz1Config.OaksNode && (to?["gaal_visits"]?.GetValue<int>() ?? 0) > 0);
                if (!((fromCompleted && to?["enabled"]?.GetValue<bool>() == true)
                      || (toCompleted && from?["enabled"]?.GetValue<bool>() == true)))
                {
                    _logger?.LogDebug("Campaign: ignored AZ1 path with unavailable endpoints {path}", edge.Id);
                    continue;
                }
                // visit_path may contain several animation segments. Recording
                // one never changes LastNode, ALoc, completion, or permissions.
                if (!visited.Any(item => string.Equals(item?.ToString(), edge.Id, StringComparison.Ordinal)))
                {
                    visited.Add(edge.Id);
                    changed = true;
                }
            }
            if (changed)
            {
                _store.Save(area);
                TraceAz1Map(area, true);
            }
            return;
        }

        if (!root.TryGetProperty("OParms", out var nodeParams) || nodeParams.ValueKind != JsonValueKind.Array)
            return;
        var raw = nodeParams.EnumerateArray().FirstOrDefault();
        var node = raw.ValueKind == JsonValueKind.String
            ? CampaignAz1Config.OpeningNode(raw.GetString()) : null;
        if (node is null || FindLocation(area.State, node)?["enabled"]?.GetValue<bool>() != true)
        {
            _logger?.LogInformation("Campaign: ignored AZ1 visit_node outside opening map: {node}", raw.ToString());
            return;
        }
        if (node == CampaignAz1Config.NextNode
            && !string.Equals(area.State["LastNode"]?.ToString(), CampaignAz1Config.StartingNode, StringComparison.Ordinal)
            && !string.Equals(area.State["LastNode"]?.ToString(), CampaignAz1Config.NextNode, StringComparison.Ordinal))
            return;
        if (node == CampaignAz1Config.ThirdNode
            && FindLocation(area.State, CampaignAz1Config.NextNode)?["completed"]?.GetValue<bool>() != true)
            return;
        if ((node == CampaignAz1Config.FourthNode || node == CampaignAz1Config.OaksNode)
            && !CampaignStateFactory.HasWonAz1Bear(area.State))
            return;
        if (node == CampaignAz1Config.SutherlandNode
            && (FindLocation(area.State, CampaignAz1Config.OaksNode)?["gaal_visits"]?.GetValue<int>() ?? 0) == 0)
            return;
        var current = CampaignAz1Config.OpeningNode(area.State["LastNode"]?.ToString());
        if (current is not null && current != node
            && !HasAz1OpenTravelRoute(area.State, current, node))
        {
            _logger?.LogInformation("Campaign: ignored AZ1 move without unlocked route {from} -> {to}", current, node);
            return;
        }
        area.State["LastNode"] = node;
        area.State["ALoc"] = null;
        area.State["CurState"] = "EXPLORE";
        AddVisitedNode(area.State, node);
        _store.Save(area);
        TraceAz1Map(area, true);
        _logger?.LogInformation("Campaign: AZ1 visited node camp={campaign} node={node}", area.CampaignId, node);
    }

    private static void CollectPathNames(JsonElement element, List<string> target)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            target.Add(element.GetString() ?? string.Empty);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                CollectPathNames(item, target);
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
