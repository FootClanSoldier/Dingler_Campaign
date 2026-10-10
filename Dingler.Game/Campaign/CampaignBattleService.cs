extern alias HexGame;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Dingler.Game.Arena;
using Dingler.Game.Games;
using Dingler.Server;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
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
    private readonly ILogger<CampaignBattleService>? _logger;
    private readonly ConcurrentDictionary<ulong, PendingBattle> _pending = new();
    private readonly ConcurrentDictionary<ulong, JoinedBattle> _joined = new();
    private readonly ConcurrentDictionary<ulong, ActiveBattle> _active = new();

    public CampaignBattleService(
        CampaignRunStore store,
        CampaignOptions options,
        CampaignService campaignService,
        ILogger<CampaignBattleService>? logger = null)
    {
        _store = store;
        _options = options;
        _campaignService = campaignService;
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
            var champion = CampaignBootstrapChampion.Create(
                context.ProfileId, _options, run.Race, run.CampaignId, run.LastDeckId, run.ChampionTalents);
            active.Game = games.CreateCampaignGame(
                pending.GameId, pending.SessionState.SessionName, pending.SessionState.EncounterData,
                joined.Human, context.Decks[run.LastDeckId], champion, context.UserName!,
                selfTurnPhases, opponentTurnPhases, joined.Ai,
                (engine, winners, losers) => OnGameEnded(context.ProfileId, context.UserName!, active, engine, winners));
        }
        catch (Exception ex)
        {
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
        _active.TryRemove(new KeyValuePair<ulong, ActiveBattle>(profileId, battle));
        _joined.TryRemove(profileId, out _);
        var won = winners.Contains(battle.Joined.Human);
        _logger?.LogInformation(
            "Campaign battle: {user} battle {game} ended; won={won}",
            userName, battle.Joined.Pending.GameId, won);

        _campaignService.ApplyBattleResult(
            battle.Joined.Session,
            battle.Joined.Pending.CampaignId,
            battle.Joined.Pending.EncounterGuid,
            won);
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
