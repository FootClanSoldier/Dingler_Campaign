extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using Dingler.Game.Campaign;
using Dingler.Game.Games;
using HexGame::Game.Client.Network.LoadBalancer;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Network.LoadBalancer;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Arena;

// LoadBalancer PvE battle start. After the Battle button the client sends, in order:
// FindSession (22019), StartEncounter (22017), JoinSession (22021), ReadyToStartGame (22031).
// Frost Ring and Campaign share this protocol surface but keep separate battle services/state.

// 22019: must fail; the client then asks StartEncounter for a flag-128 encounter.
[Authenticated]
public sealed class FindSessionRequestHandler : IRequestHandler<FindSessionRequestArgs, FindSessionResponse>
{
	public FindSessionResponse HandleRequest(SessionContext context, FindSessionRequestArgs request) => new()
	{
		Success = false,
		RoutingPlayerId = ArenaBattleService.HumanId(context),
	};
}

// 22017: reserve a battle for the run's current fight and describe its session. Answer Success = true whenever
// possible: a failed StartEncounter leaves the client on its loading screen (refusals belong in JoinSession).
[Authenticated]
public sealed class StartEncounterRequestHandler : IRequestHandler<StartEncounterRequestArgs, StartEncounterResponse>
{
	private readonly ArenaBattleService _battles;
	private readonly CampaignBattleService _campaignBattles;
	private readonly GameManager _games;
	private readonly ILogger<StartEncounterRequestHandler>? _logger;

	public StartEncounterRequestHandler(ArenaBattleService battles, CampaignBattleService campaignBattles, GameManager games, ILogger<StartEncounterRequestHandler>? logger = null)
	{
		_battles = battles;
		_campaignBattles = campaignBattles;
		_games = games;
		_logger = logger;
	}

	public StartEncounterResponse HandleRequest(SessionContext context, StartEncounterRequestArgs request)
	{
		var human = ArenaBattleService.HumanId(context);
		try
		{
			var encounterData = request.EncounterData;
			var isArena = encounterData is not null
			              && (encounterData.SessionFlags & ESessionFlags.IsPvEArena) != 0;
			if (isArena)
			{
				var pending = _battles.Reserve(context, _games, request.SessionName ?? string.Empty);
				return Success(human, pending.SessionState);
			}

			var campaignFlags = ESessionFlags.IsEncounter | ESessionFlags.IsPvE;
			var isCampaign = encounterData is not null
			                 && (encounterData.SessionFlags & campaignFlags) == campaignFlags;
			if (isCampaign)
			{
				var pending = _campaignBattles.Reserve(
					context, _games, request.SessionName ?? string.Empty, encounterData!);
				return Success(human, pending.SessionState);
			}

			_logger?.LogWarning(
				"LoadBalancer: StartEncounter for {user} has unsupported encounter flags {flags}",
				context.UserName, encounterData?.SessionFlags);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: StartEncounter failed for {user}", context.UserName);
		}
		return new StartEncounterResponse { Success = false, RoutingPlayerId = human };
	}

	private static StartEncounterResponse Success(UID human, SessionState state) => new()
	{
		Success = true,
		RoutingPlayerId = human,
		SessionState = state,
		SessionID = state.SessionId,
		ServerID = UID.Invalid,   // never read by the client
	};
}

// 22021: join the reserved battle. Frost Ring and Campaign keep separate battle state,
// while this shared protocol handler routes the request to the service that owns the reservation.
[Authenticated]
public sealed class JoinSessionRequestHandler : IRequestHandler<JoinSessionRequestArgs, JoinSessionResponse>
{
	private readonly ArenaBattleService _battles;
	private readonly CampaignBattleService _campaignBattles;
	private readonly GameManager _games;
	private readonly ILogger<JoinSessionRequestHandler>? _logger;

	public JoinSessionRequestHandler(ArenaBattleService battles, CampaignBattleService campaignBattles, GameManager games, ILogger<JoinSessionRequestHandler>? logger = null)
	{
		_battles = battles;
		_campaignBattles = campaignBattles;
		_games = games;
		_logger = logger;
	}

	public JoinSessionResponse HandleRequest(SessionContext context, JoinSessionRequestArgs request)
	{
		var human = ArenaBattleService.HumanId(context);
		var sessionInstanceId = request.SessionId.GetInstanceId();
		try
		{
			// Campaign reservations are owned by CampaignBattleService. Only claim this request
			// when the pending campaign session id matches, so Frost Ring keeps its existing path.
			if (_campaignBattles.TryGetPending(context, out var campaignPending)
			    && campaignPending.GameId == sessionInstanceId)
			{
				if (_campaignBattles.TryJoin(
					    context, _games, sessionInstanceId, request.DeckID, request.SelfTurnPhases,
					    request.OpponentTurnPhases, out var campaignBattle, out var campaignReason))
				{
					return new JoinSessionResponse
					{
						Success = true,
						RoutingPlayerId = campaignBattle.Human,
						SessionState = campaignBattle.Pending.SessionState,
						SessionPlayers = new List<PlayerState>
						{
							new() { PlayerId = campaignBattle.Human, PlayerPosition = 0 },
							new() { PlayerId = campaignBattle.Ai, PlayerPosition = 1 },
						},
					};
				}

				_logger?.LogWarning(
					"Campaign battle: JoinSession refused for {user}: {reason}",
					context.UserName, campaignReason);
				return new JoinSessionResponse { Success = false, RoutingPlayerId = human };
			}

			if (_battles.TryStart(context, _games, sessionInstanceId, request.DeckID,
				    request.SelfTurnPhases, request.OpponentTurnPhases, out var pending, out var reason))
			{
				_battles.TryGetActive(context, out var battle);
				return new JoinSessionResponse
				{
					Success = true,
					RoutingPlayerId = human,
					SessionState = pending.SessionState,
					SessionPlayers = new List<PlayerState>
					{
						new() { PlayerId = human, PlayerPosition = 0 },
						new() { PlayerId = battle.Ai, PlayerPosition = 1 },
					},
				};
			}
			_logger?.LogWarning("Arena: JoinSession refused for {user}: {reason}", context.UserName, reason);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "LoadBalancer: JoinSession failed for {user}", context.UserName);
		}
		return new JoinSessionResponse { Success = false, RoutingPlayerId = human };
	}
}

// 22031: the client has added its own player and is ready. Tell it about the opponent (it has no other way to learn
// it), then let the game's events flow. The client expects no reply.
[Authenticated]
public sealed class ReadyToStartGameRequestHandler : IRequestHandler<ReadyToStartGameRequestArgs>
{
	private readonly ArenaBattleService _battles;
	private readonly CampaignBattleService _campaignBattles;
	private readonly ILogger<ReadyToStartGameRequestHandler>? _logger;

	public ReadyToStartGameRequestHandler(
		ArenaBattleService battles,
		CampaignBattleService campaignBattles,
		ILogger<ReadyToStartGameRequestHandler>? logger = null)
	{
		_battles = battles;
		_campaignBattles = campaignBattles;
		_logger = logger;
	}

	public void HandleRequest(SessionContext context, ReadyToStartGameRequestArgs request)
	{
		try
		{
			if (!request.IsReady)
				return;

			if (_campaignBattles.TryGetActive(context, out var campaignBattle))
			{
				if (campaignBattle.Game is null)
				{
					_logger?.LogWarning("Campaign battle: {user} is ready but the engine is missing", context.UserName);
					return;
				}
				if (Interlocked.Exchange(ref campaignBattle.ReadyApplied, 1) == 1)
					return;

				context.TrySendMessageToClient(new HexGame::Game.Shared.Network.GameSession.PlayerAddedEventArgs
				{
					RoutingPlayerId = campaignBattle.Joined.Human,
					PlayerState = new PlayerState { PlayerId = campaignBattle.Joined.Ai, PlayerPosition = 1 },
				});
				campaignBattle.Game.PlayerIsReadyForEvents(campaignBattle.Joined.Human);
				_logger?.LogInformation(
					"Campaign battle: {user} is ready; original HEX PvE engine is running battle {game}",
					context.UserName, campaignBattle.Joined.Pending.GameId);
				return;
			}

			if (!_battles.TryGetActive(context, out var battle) || battle.Game is null || battle.Applied != 0)
			{
				_logger?.LogWarning("Arena: {user} is ready but has no running battle", context.UserName);
				return;
			}

			context.TrySendMessageToClient(new HexGame::Game.Shared.Network.GameSession.PlayerAddedEventArgs
			{
				RoutingPlayerId = battle.Human,
				PlayerState = new PlayerState { PlayerId = battle.Ai, PlayerPosition = 1 },
			});
			battle.Game.PlayerIsReadyForEvents(battle.Human);
			_logger?.LogInformation("Arena: {user} is ready; battle running", context.UserName);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "LoadBalancer: ReadyToStartGame failed for {user}", context.UserName);
		}
	}
}
