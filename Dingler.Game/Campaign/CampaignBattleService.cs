extern alias HexGame;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Dingler.Game.Arena;
using Dingler.Game.Games;
using Dingler.Server;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Game;
using HexGame::Game.Shared.Domain;
using Dingler.Game.Services;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Campaign;

/// <summary>
/// Campaign-side LoadBalancer bridge. Campaign state decides which encounter is active;
/// the shared Dingler battle/session infrastructure decides how that encounter is played.
/// This service owns campaign battle reservations and active battle state, and remains
/// separate from Frost Ring's ArenaBattleService.
/// </summary>
public sealed class CampaignBattleService
{
    public sealed record PendingBattle(
        ulong GameId,
        ulong CampaignId,
        ulong ChampionId,
        string EncounterGuid,
        string? MercenaryItemGuid,
        SessionState SessionState,
        string? Refusal);

    /// <summary>
    /// A campaign session that has passed JoinSession. The retained join data binds the
    /// campaign player, AI seat, deck and turn-stop preferences to the active engine.
    /// </summary>
    public sealed class JoinedBattle
    {
        public required PendingBattle Pending { get; init; }
        public required UID Human { get; init; }
        public required UID Ai { get; init; }
        public required ulong DeckId { get; init; }
        public required SessionContext Session { get; init; }
        public List<ETurnPhases>? SelfTurnPhases { get; init; }
        public List<ETurnPhases>? OpponentTurnPhases { get; init; }
    }

    public sealed class ActiveBattle
    {
        public required JoinedBattle Joined { get; init; }
        public HexGameWrapper? Game { get; set; }
        public int ReadyApplied;
    }

    private readonly CampaignRunStore _store;
    private readonly CampaignOptions _options;
    private readonly CampaignService _campaignService;
    private readonly CollectionCacheService _collection;
    private readonly ILogger<CampaignBattleService>? _logger;
    private readonly ConcurrentDictionary<ulong, PendingBattle> _pending = new();
    private readonly ConcurrentDictionary<ulong, JoinedBattle> _joined = new();
    private readonly ConcurrentDictionary<ulong, ActiveBattle> _active = new();

    public CampaignBattleService(
        CampaignRunStore store,
        CampaignOptions options,
        CampaignService campaignService,
        CollectionCacheService collection,
        ILogger<CampaignBattleService>? logger = null)
    {
        _store = store;
        _options = options;
        _campaignService = campaignService;
        _collection = collection;
        _logger = logger;
    }

    /// <summary>
    /// StartEncounter (22017): reserve an authoritative session for the campaign encounter
    /// selected by ServiceCampaign. The client-provided EncounterData is retained in the
    /// returned SessionState after being checked against the persisted campaign state.
    /// JoinSession consumes this reservation before the campaign game engine is created.
    /// </summary>
    public PendingBattle Reserve(
        SessionContext context,
        GameManager games,
        string sessionName,
        SessionStateEncounterData encounterData)
    {
        string? refusal = null;
        CampaignRunRecord? run = null;
        ulong campaignId = 0;
        string encounterGuid = string.Empty;

        if (!TryCampaignId(sessionName, out campaignId))
            refusal = $"invalid campaign session name '{sessionName}'";
        else if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out run))
            refusal = $"campaign {campaignId} was not found for this player";
        else if (!_store.TryGetOwnedChampion(context.ProfileId, run.ChampionId,
                     out _))
            refusal = "campaign champion is not owned by this player";
        else if (!run.Started)
            refusal = $"campaign {campaignId} has not been started";
        else if (games.TryGetGameForPlayer(context.UserName!, out _))
            refusal = "the player's previous battle is still running";
        else
        {
            encounterGuid = run.State["ActiveEncounterGuid"]?.GetValue<string>() ?? string.Empty;
            if (!Guid.TryParse(encounterGuid, out var activeEncounter))
                refusal = "campaign has no active encounter";
            else if (encounterData.SceneTemplateId.m_Guid != activeEncounter)
                refusal = $"client encounter {encounterData.SceneTemplateId.m_Guid} does not match active encounter {activeEncounter}";
            else if (run.LastDeckId == 0)
                refusal = "campaign champion has no selected deck";
        }

        var gameId = games.NextGameId();
        var state = new SessionState
        {
            SessionId = new UID(UID.Type.AuthoritativeSession, gameId),
            SessionName = sessionName,
            MinimumPlayerCount = 1,
            MaximumPlayerCount = 2,
            EncounterData = encounterData,
            JoinInsteadOfReconnect = false,
        };

        var pending = new PendingBattle(
            gameId,
            campaignId,
            run?.ChampionId ?? 0,
            encounterGuid,
            run?.State["BattleMercenaryItem"]?.ToString(),
            state,
            refusal);
        _pending[context.ProfileId] = pending;

        if (refusal is null)
        {
            _logger?.LogInformation(
                "Campaign battle: {user} reserved battle {game} for campaign {campaign}, encounter {encounter}",
                context.UserName, gameId, campaignId, encounterGuid);
        }
        else
        {
            _logger?.LogWarning(
                "Campaign battle: {user} reservation {game} deferred refusal to JoinSession: {reason}",
                context.UserName, gameId, refusal);
        }

        return pending;
    }

    public bool HasChampionBattle(ulong profileId, ulong championId) =>
        (_pending.TryGetValue(profileId, out var pending) && pending.ChampionId == championId) ||
        (_joined.TryGetValue(profileId, out var joined) && joined.Pending.ChampionId == championId) ||
        (_active.TryGetValue(profileId, out var active) && active.Joined.Pending.ChampionId == championId);

    public bool TryGetPending(SessionContext context, out PendingBattle pending) =>
        _pending.TryGetValue(context.ProfileId, out pending!);

    /// <summary>
    /// JoinSession (22021): consume the reservation, bind the player + campaign AI seats,
    /// and create the authoritative campaign game through GameManager.
    /// </summary>
    public bool TryJoin(
        SessionContext context,
        GameManager games,
        ulong sessionInstanceId,
        ulong deckId,
        List<ETurnPhases>? selfTurnPhases,
        List<ETurnPhases>? opponentTurnPhases,
        out JoinedBattle joined,
        out string reason)
    {
        joined = null!;

        if (!_pending.TryRemove(context.ProfileId, out var pending) || pending.GameId != sessionInstanceId)
            return Fail("no reserved campaign battle for this session", out reason);
        if (pending.Refusal is not null)
            return Fail(pending.Refusal, out reason);
        if (!_store.TryGetByCampaignId(context.ProfileId, pending.CampaignId, out var run))
            return Fail("the campaign is gone", out reason);
        if (!_store.TryGetOwnedChampion(context.ProfileId, run.ChampionId,
                out var savedChampion))
            return Fail("campaign champion is not owned by this player", out reason);
        if (!run.Started || run.ChampionId != pending.ChampionId)
            return Fail("the campaign changed since the battle was reserved", out reason);

        var activeEncounter = run.State["ActiveEncounterGuid"]?.GetValue<string>() ?? string.Empty;
        if (!string.Equals(activeEncounter, pending.EncounterGuid, StringComparison.OrdinalIgnoreCase))
            return Fail("the active encounter changed since the battle was reserved", out reason);
        if (run.LastDeckId == 0)
            return Fail("campaign champion has no selected deck", out reason);
        if (deckId != run.LastDeckId)
            return Fail($"deck {deckId} is not the campaign champion's deck {run.LastDeckId}", out reason);
        if (!context.Decks.ContainsKey(run.LastDeckId))
            return Fail($"deck {run.LastDeckId} not found in the player's decks", out reason);

        var manager = HexGame::Singleton<TemplateManager>.Instance;
        var partyTemplates = new List<ResourceId>();
        var partyItems = new Dictionary<Guid, (ResourceId Template, CampaignMercenaryPartyMember Member)>();
        foreach (var member in savedChampion.PartyMembers)
        {
            if (!Guid.TryParse(member.MercenaryItemGuid, out var itemGuid)
                || itemGuid == Guid.Empty
                || !_collection.IsOwnedMercenary(context.ProfileId, new ResourceId(itemGuid.ToString("D")))
                || manager.GetInventoryItem(new ResourceId(itemGuid.ToString("D"))) is not InventoryMercenaryData inventory
                || inventory.MercenaryTemplate is null)
                continue;
            var templateId = inventory.MercenaryTemplate.Id;
            if (partyItems.TryAdd(itemGuid, (templateId, member)))
                partyTemplates.Add(templateId);
        }

        var champion = CampaignChampionProjection.CreateFromSaved(savedChampion);
        deck_bits gameDeck = context.Decks[run.LastDeckId];
        if (!string.IsNullOrEmpty(pending.MercenaryItemGuid))
        {
            if (!string.Equals(run.State["BattleMercenaryItem"]?.ToString(),
                    pending.MercenaryItemGuid, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(pending.MercenaryItemGuid, out var requested)
                || !partyItems.TryGetValue(requested, out var selected)
                || selected.Member.DeckTemplateId == 0
                || !_store.TryGetProfileDeck(context.ProfileId, selected.Member.DeckTemplateId, out var savedDeck)
                || !string.Equals(savedDeck.ChampionTemplateGuid, selected.Template.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                return Fail("selected mercenary or its saved deck is no longer valid", out reason);
            try
            {
                var template = HexGame::Game.Shared.Profile.ProfileDeckTemplate.FromBytes(savedDeck.Data);
                if (template.Champ != selected.Template || !template.CardDesc.Any())
                    return Fail("selected mercenary deck does not match its mercenary", out reason);
                gameDeck = template.AsFakeDeck();
                champion.MercID = selected.Template;
                _logger?.LogInformation(
                    "Campaign battle: {user} selected mercenary {merc} using profile deck {deck}",
                    context.UserName, selected.Template, selected.Member.DeckTemplateId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Campaign battle: malformed saved mercenary deck");
                return Fail("selected mercenary deck could not be loaded", out reason);
            }
        }

        joined = new JoinedBattle
        {
            Pending = pending,
            Human = ArenaBattleService.HumanId(context),
            Ai = new UID(UID.Type.ServiceAI, pending.GameId),
            DeckId = run.LastDeckId,
            Session = context,
            SelfTurnPhases = selfTurnPhases,
            OpponentTurnPhases = opponentTurnPhases,
        };
        _joined[context.ProfileId] = joined;

        var active = new ActiveBattle { Joined = joined };
        _active[context.ProfileId] = active;
        try
        {
            active.Game = games.CreateCampaignGame(
                pending.GameId, pending.SessionState.SessionName, pending.SessionState.EncounterData,
                joined.Human, gameDeck, champion, partyTemplates, context.UserName!,
                selfTurnPhases, opponentTurnPhases, joined.Ai,
                (engine, winners, losers) => OnGameEnded(context.ProfileId, context.UserName!, active, engine, winners));
        }
        catch (Exception ex)
        {
            // Preserve the authored scene and persist a side-by-side HEX template
            // snapshot before returning the normal, retryable JoinSession refusal.
            CampaignEncounterDiagnostics.Capture(
                _options.StorePath, pending.CampaignId, pending.EncounterGuid, run.Race,
                run.LastDeckId, pending.SessionState.EncounterData, ex, _logger);
            _active.TryRemove(new KeyValuePair<ulong, ActiveBattle>(context.ProfileId, active));
            _joined.TryRemove(context.ProfileId, out _);
            return Fail("the campaign battle engine could not start: " + ex.Message, out reason);
        }

        _logger?.LogInformation(
            "Campaign battle: {user} joined battle {game} for campaign {campaign}; engine created, waiting for ReadyToStartGame",
            context.UserName, pending.GameId, pending.CampaignId);

        reason = string.Empty;
        return true;
    }

    public bool TryGetJoined(SessionContext context, out JoinedBattle joined) =>
        _joined.TryGetValue(context.ProfileId, out joined!);

    public bool TryGetActive(SessionContext context, out ActiveBattle battle) =>
        _active.TryGetValue(context.ProfileId, out battle!);

    private void OnGameEnded(
        ulong profileId,
        string userName,
        ActiveBattle battle,
        HexRulesEngine engine,
        List<UID> winners)
    {
        if (!_active.TryRemove(new KeyValuePair<ulong, ActiveBattle>(profileId, battle)))
            return;

        _joined.TryRemove(profileId, out _);
        var won = winners.Contains(battle.Joined.Human);
        var capturedTemplates = engine.ReadCampaignTamingAtGameEnd(battle.Joined.Human);
        _logger?.LogInformation(
            "Campaign battle: {user} battle {game} ended; won={won}",
            userName, battle.Joined.Pending.GameId, won);

        _campaignService.ApplyBattleResult(
            battle.Joined.Session,
            battle.Joined.Pending.CampaignId,
            battle.Joined.Pending.EncounterGuid,
            won,
            capturedTemplates);
    }

    private static bool Fail(string why, out string reason)
    {
        reason = why;
        return false;
    }

    private static bool TryCampaignId(string sessionName, out ulong campaignId)
    {
        const string prefix = "camp_";
        campaignId = 0;
        return sessionName.StartsWith(prefix, StringComparison.Ordinal)
               && ulong.TryParse(sessionName.AsSpan(prefix.Length), out campaignId)
               && campaignId != 0;
    }
}
