extern alias HexGame;
using System.Collections.Concurrent;
using Dingler.Server;
using Dingler.Game.Cards;
using Dingler.Game.GameObjects;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Mechanics.Transactions;
using HexGame::Game.Shared.Network.GameSession;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Games;

public class HexGameWrapper : IDisposable
{
	private readonly HexRulesEngine _gameSession;
	private readonly Dictionary<Player, Queue<(int eventId, byte[] eventData)>> _eventQueues;
	private readonly ConcurrentDictionary<Player, byte> _readyPlayers;
	private readonly Action<ulong>? _onMatchEnded;
	private readonly SessionManager _sessionManager;
	private readonly ILogger<HexGameWrapper>? _logger;
	private readonly CancellationToken _cancellationToken;
	private readonly ConcurrentDictionary<string, bool> _leaveChecks;
	private readonly Dictionary<Player, Dictionary<object, (int eventId, byte[] eventData)>> _eventHistory;
	private readonly ConcurrentQueue<UID> _pendingReconnects;
	private readonly ConcurrentDictionary<UID, string> _networkUsernamesByPlayer;
	private readonly ConcurrentDictionary<UID, byte> _missingNetworkSessionWarnings;
	private readonly ConcurrentDictionary<UID, byte> _firstNetworkBatchLogged;
	private readonly ConcurrentDictionary<UID, byte> _failedNetworkSendWarnings;

	public SessionStateEncounterData EncounterData => _gameSession.EncounterData;
	
	public Dictionary<string, UID> DeckIdsByPlayer
	{
		get
		{
			var dict = new Dictionary<string, UID>();

			foreach (var player in _gameSession.GetAllPlayers())
			{
				dict[player.m_ChampionCard.GetName()] = player.m_DeckID;
			}

			return dict;
		}
	}
	
	public ulong Id => _gameSession.m_SessionId.GetInstanceId();
	public HexGameWrapper(HexRulesEngine gameSession, CardVisibilityManager cardVisibilityManager,
		SessionManager sessionManager, CancellationToken cancellationToken = default, ILogger<HexGameWrapper>? logger = null,
		Action<ulong>? onMatchEnded = null)
	{
		_gameSession = gameSession;
		_sessionManager = sessionManager;
		_onMatchEnded = onMatchEnded;
		_eventQueues = new Dictionary<Player, Queue<(int eventId, byte[] eventData)>>();
		_readyPlayers = new ConcurrentDictionary<Player, byte>();
		_logger = logger;
		_gameSession.DispatchToPlayer += OnDispatchEventToPlayer;
		_gameSession.FlushReady += OnFlushReady;
		_cancellationToken = cancellationToken;
		_leaveChecks = new ConcurrentDictionary<string, bool>();
		_eventHistory = new Dictionary<Player, Dictionary<object, (int eventId, byte[] eventData)>>();
		_pendingReconnects = new ConcurrentQueue<UID>();
		_networkUsernamesByPlayer = new ConcurrentDictionary<UID, string>();
		_missingNetworkSessionWarnings = new ConcurrentDictionary<UID, byte>();
		_firstNetworkBatchLogged = new ConcurrentDictionary<UID, byte>();
		_failedNetworkSendWarnings = new ConcurrentDictionary<UID, byte>();
	}
	
	public bool IsGameEnded => _gameSession.IsGameEnded;

	// Frost Ring Arena: the run's deck (the reconnect reply needs it; tournaments take it from TournamentDecks).
	public bool IsArena => _gameSession.IsPvEArena();
	public ulong ArenaDeckInstanceId { get; set; }

	/// <summary>The player declined to rejoin a disconnected arena battle: it counts as a loss (design 03 D15-A).</summary>
	public void ForfeitArena(UID humanId)
	{
		if (!IsGameEnded)
			_gameSession.SubmitTransaction(QuitGameTransaction.Create(humanId, false, false));   // Create already initializes it
	}

	public Task<(UID, UID)> RunGameAsync()
	{
		return RunGameAsync(UID.Invalid);
	}
	
	public async Task<(UID, UID)> RunGameAsync(UID forcedFirstPlayer)
	{
		List<UID> results = new List<UID>();
		try
		{
			_gameSession.ForcedFirstPlayer = forcedFirstPlayer;
			results =await _gameSession.RunAsync(_cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// ignored
		}
		catch (Exception ex)
		{
			_logger?.LogError("Match {MatchId} run loop failed: {Exception}", Id, ex);
		}
		finally
		{
			_onMatchEnded?.Invoke(Id);
		}

		if (results.Count > 1)
			return (results[0], results[1]);

		return (UID.Invalid, UID.Invalid);
	}
	
	public SessionState GetSessionState(UID playerId)
	{
		var encounterData = _gameSession.m_EncounterData;
		if (encounterData?.TournamentDecks is { } decks)
		{
			encounterData = new SessionStateEncounterData
			{
				SessionFlags = encounterData.SessionFlags,
				TournamentID = encounterData.TournamentID,
				SeriesFormat = encounterData.SeriesFormat,
				SeriesMaxGames = encounterData.SeriesMaxGames,
				MatchPreviousWinners = encounterData.MatchPreviousWinners,
				FirstPlayer = encounterData.FirstPlayer,
				TournamentDecks = decks.Where(d => d.PlayerUID == playerId).ToList()
			};
		}

		var matchPlayerCount = _gameSession.GetAllPlayers().Count;

		return new SessionState
		{
			SessionId = _gameSession.m_SessionId,
			SessionName = _gameSession.m_SessionName,
			MinimumPlayerCount = matchPlayerCount,
			MaximumPlayerCount = matchPlayerCount,
			EncounterData = encounterData,
			JoinInsteadOfReconnect = false
		};
	}
	
	public void PlayerIsReadyForEvents(UID playerId)
	{
		var player = _gameSession.GetPlayer(playerId);
		if (player is null)
			return;

		_readyPlayers.TryAdd(player, 1);
		_gameSession.ProcessWork();
	}
	
	public bool TryAddPlayer(TrackedPlayer player)
	{
		_gameSession.AddPlayer(player);
		return true;
	}

	/// <summary>
	/// Bind a game-seat UID to the authenticated account name used by SessionManager.
	/// PvE champion display names are independent from login names, so network delivery
	/// must not assume that Player.m_ChampionCard.GetName() identifies the connection.
	/// </summary>
	public void BindNetworkSession(UID playerId, string username)
	{
		if (string.IsNullOrWhiteSpace(username))
			throw new ArgumentException("A network username is required.", nameof(username));

		_networkUsernamesByPlayer[playerId] = username;
		_missingNetworkSessionWarnings.TryRemove(playerId, out _);
	}
	
	public IReadOnlyList<string> GetPlayerNames()
	{
		return _gameSession.GetAllPlayers()
			.Select(p => p.m_ChampionCard?.GetName())
			.Where(name => !string.IsNullOrEmpty(name))
			.Cast<string>()
			.ToList();
	}
	
	public HexGame::Game.Shared.Network.LoadBalancer.ReadyForGameSetupResponseArgs BuildGameSetupResponse(UID playerId)
	{
		var player = _gameSession.GetPlayer(playerId);
		var opponents = player is null
			? new List<Player>()
			: _gameSession.GetOpponentsOfPlayer(player);

		var deckId = IsArena && ArenaDeckInstanceId != 0 ? new UID(UID.Type.Deck, ArenaDeckInstanceId) : UID.Invalid;
		if (_gameSession.m_EncounterData?.TournamentDecks is { } decks)
		{
			var deckEntry = decks.FirstOrDefault(d => d.PlayerUID == playerId);
			if (deckEntry?.PlayerDeck is { } deck)
				deckId = new UID(UID.Type.Deck, deck.Id);
		}

		return new HexGame::Game.Shared.Network.LoadBalancer.ReadyForGameSetupResponseArgs
		{
			SessionState = GetSessionState(playerId),
			DeckId = deckId,
			DeckTemplateId = ResourceId.Invalid,
			OpponentsInfo = opponents
				.Select(o => new PlayerState { PlayerId = o.m_PlayerId, PlayerPosition = o.m_PlayerPosition })
				.ToList(),
			TurnOrder = _gameSession.GetAllPlayersInTurnOrder().Select(p => p.m_PlayerId).ToList(),
			seedZ = 0,
			seedW = 0
		};
	}

	public void QueueTransaction(Transaction transaction)
	{
		transaction.Initialize();
		_gameSession.SubmitTransaction(transaction);
	}
	
	private void OnFlushReady()
	{
		ProcessPendingReconnects();

		foreach (var player in _gameSession.GetAllPlayers())
		{
			if (!_readyPlayers.TryGetValue(player, out _))
				continue;

			if (!_eventQueues.TryGetValue(player, out var queue))
			{
				queue = new Queue<(int eventId, byte[] data)>();
				_eventQueues[player] = queue;
			}
			
			if (!TryResolveNetworkSession(player, out var session))
				continue;

			var networkSessionEvent = new NetworkPacketSessionEventArgs
			{
				PlayerId = player.m_PlayerId,
				SessionId = _gameSession.m_SessionId
			};
			
			const int gameStartedClass = 1;
			const int playerUpdatedClass = 65;

			var queued = new List<(int eventId, byte[] eventData)>();
			while (queue.TryDequeue(out var e))
			{
				queued.Add(e);
			}

			if (queued.Count == 0)
				continue;
			
			foreach (var e in queued.Where(e => e.eventId == gameStartedClass))
			{
				networkSessionEvent.EventIds.Add(e.eventId);
				networkSessionEvent.EventData.Add(e.eventData);
			}

			foreach (var e in queued.Where(e => e.eventId == playerUpdatedClass))
			{
				networkSessionEvent.EventIds.Add(e.eventId);
				networkSessionEvent.EventData.Add(e.eventData);
			}

			foreach (var e in queued.Where(e => e.eventId != gameStartedClass && e.eventId != playerUpdatedClass))
			{
				networkSessionEvent.EventIds.Add(e.eventId);
				networkSessionEvent.EventData.Add(e.eventData);
			}

			try
			{
				var sessionEvent = new SessionSyncEventEventArgs
				{
					RoutingPlayerId = player.m_PlayerId,
					SessionArgs = networkSessionEvent
				};

				if (!session.TrySendMessageToClient(sessionEvent))
				{
					if (_failedNetworkSendWarnings.TryAdd(player.m_PlayerId, 0))
					{
						_logger?.LogWarning(
							"Match {MatchId}: network session for {User} rejected a session event batch for player {PlayerId}",
							Id, session.UserName, player.m_PlayerId);
					}
					continue;
				}

				_failedNetworkSendWarnings.TryRemove(player.m_PlayerId, out _);
				if (_firstNetworkBatchLogged.TryAdd(player.m_PlayerId, 0))
				{
					_logger?.LogInformation(
						"Match {MatchId}: sent first session event batch ({EventCount} events) to {User} for player {PlayerId}",
						Id, queued.Count, session.UserName, player.m_PlayerId);
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				_logger?.LogError("Failed to send session event batch to player {PlayerId}: {Exception}",
					player.m_PlayerId, ex);
			}
		}
	}

	public bool TryGetPlayerId(string username, out UID playerId)
	{
		foreach (var binding in _networkUsernamesByPlayer)
		{
			if (!string.Equals(binding.Value, username, StringComparison.Ordinal))
				continue;
			if (_gameSession.GetPlayer(binding.Key) is null)
				continue;

			playerId = binding.Key;
			return true;
		}

		var player = _gameSession.GetAllPlayers()
			.FirstOrDefault(p => p.m_ChampionCard?.GetName() == username);

		playerId = player?.m_PlayerId ?? UID.Invalid;
		return player is not null;
	}

	public void ReconnectPlayer(UID playerId)
	{
		_pendingReconnects.Enqueue(playerId);
		_gameSession.ProcessWork();
	}

	private void ProcessPendingReconnects()
	{
		while (_pendingReconnects.TryDequeue(out var playerId))
		{
			var player = _gameSession.GetPlayer(playerId);
			if (player is null)
				continue;

			if (!TryResolveNetworkSession(player, out var session))
				continue;

			var name = _networkUsernamesByPlayer.TryGetValue(player.m_PlayerId, out var boundUsername)
				? boundUsername
				: player.m_ChampionCard?.GetName() ?? player.m_PlayerId.ToString();

			_readyPlayers[player] = 1;

			if (_eventQueues.TryGetValue(player, out var queue))
				queue.Clear();

			foreach (var other in _gameSession.GetAllPlayers())
			{
				if (other.m_PlayerId == playerId)
					continue;

				session.TrySendMessageToClient(new PlayerAddedEventArgs
				{
					RoutingPlayerId = playerId,
					PlayerState = new PlayerState
					{
						PlayerId = other.m_PlayerId,
						PlayerPosition = other.m_PlayerPosition
					}
				});
			}

			var chainEvents = new List<(int eventId, byte[] eventData)>();
			var stateEvents = new List<(int eventId, byte[] eventData)>();

			if (_eventHistory.TryGetValue(player, out var recorded))
			{
				foreach (var entry in recorded)
				{
					if (TryGetChainAbilityEvent(entry.Key, out var abilityInstanceId))
					{
						if (_gameSession.Chain.ContainsAbility(abilityInstanceId))
							chainEvents.Add(entry.Value);
						continue;
					}

					stateEvents.Add(entry.Value);
				}
			}

			session.TrySendMessageToClient(new SessionSyncEventEventArgs
			{
				RoutingPlayerId = playerId,
				SessionArgs = BuildResyncPacket(player, stateEvents, _gameSession.BuildResyncEvents(player))
			});

			var priorityPlayer = _gameSession.GetPriorityPlayer();
			var activePlayer = _gameSession.GetActivePlayer();

			// These are sent as separate sync packets so the client applies them on
			// separate frames: the phase first (its handler clears the chain), then the
			// chain, then the priority green light. The pass button mode is decided once
			// when the priority context changes and must see a populated ChainView.
			SendSessionEvents(session, player, [
				new TurnPhaseUpdatedSessionEventArgs
				{
					SessionId = _gameSession.m_SessionId,
					ActivePlayerId = activePlayer?.m_PlayerId ?? UID.Invalid,
					PriorityPlayerId = priorityPlayer?.m_PlayerId ?? UID.Invalid,
					TurnPhase = _gameSession.CurrentTurnPhase,
					PriorityPlayerChessTimerElapsed = priorityPlayer is null ? 0 : (long)priorityPlayer.GetChessTimerElapsedTime().TotalSeconds
				}
			]);

			if (chainEvents.Count > 0)
			{
				SendSessionEvents(session, player,
					chainEvents.Select(e => SessionEventArgs.BuildArgs(e.eventId, e.eventData)));
			}

			if (priorityPlayer is not null)
			{
				SendSessionEvents(session, player, [
					new GreenLightSessionEventArgs
					{
						SessionId = _gameSession.m_SessionId,
						PlayerId = priorityPlayer.m_PlayerId,
						Context = _gameSession.GetPriorityContext()
					}
				]);

				if (priorityPlayer == player)
					_gameSession.SendPlayerOptions(player);
			}

			if (_eventQueues.TryGetValue(player, out var reconnectQueue))
			{
				reconnectQueue.Enqueue((53, new ReconnectDoneSessionEventArgs
				{
					SessionId = _gameSession.m_SessionId
				}.ToByteArray()));
			}

			_logger?.LogInformation("Player {Player} resynced into match {MatchId}", name, Id);
		}
	}

	private bool TryResolveNetworkSession(Player player, out SessionContext session)
	{
		if (_networkUsernamesByPlayer.TryGetValue(player.m_PlayerId, out var boundUsername)
		    && _sessionManager.TryGetUserSession(boundUsername, out session))
		{
			_missingNetworkSessionWarnings.TryRemove(player.m_PlayerId, out _);
			return true;
		}

		var championName = player.m_ChampionCard?.GetName();
		if (!string.IsNullOrEmpty(championName)
		    && _sessionManager.TryGetUserSession(championName, out session))
		{
			_missingNetworkSessionWarnings.TryRemove(player.m_PlayerId, out _);
			return true;
		}

		session = null!;
		if (_missingNetworkSessionWarnings.TryAdd(player.m_PlayerId, 0))
		{
			_logger?.LogWarning(
				"Match {MatchId}: no network session for player {PlayerId}; bound username={BoundUsername}, champion name={ChampionName}",
				Id, player.m_PlayerId, boundUsername, championName);
		}

		return false;
	}

	private void SendSessionEvents(SessionContext session, Player player, IEnumerable<SessionEventArgs> events)
	{
		var packet = new NetworkPacketSessionEventArgs
		{
			PlayerId = player.m_PlayerId,
			SessionId = _gameSession.m_SessionId
		};

		var any = false;
		foreach (var e in events)
		{
			packet.EventIds.Add(e.Class);
			packet.EventData.Add(e.ToByteArray());
			any = true;
		}

		if (!any)
			return;

		session.TrySendMessageToClient(new SessionSyncEventEventArgs
		{
			RoutingPlayerId = player.m_PlayerId,
			SessionArgs = packet
		});
	}

	private NetworkPacketSessionEventArgs BuildResyncPacket(Player player,
		IEnumerable<(int eventId, byte[] eventData)> recorded,
		IReadOnlyList<SessionEventArgs> live)
	{
		var packet = new NetworkPacketSessionEventArgs
		{
			PlayerId = player.m_PlayerId,
			SessionId = _gameSession.m_SessionId
		};

		var events = new List<(int eventId, byte[] eventData)>(recorded);
		foreach (var e in live)
			events.Add((e.Class, e.ToByteArray()));

		foreach (var e in events.OrderBy(e => ResyncOrderOf(e.eventId)))
		{
			packet.EventIds.Add(e.eventId);
			packet.EventData.Add(e.eventData);
		}

		return packet;
	}

	private static int ResyncOrderOf(int eventClass) => eventClass switch
	{
		ReconnectEventInfo.GAME_STARTED => 0,
		ReconnectEventInfo.PLAYER_UPDATED => 1,
		ReconnectEventInfo.CARD_UPDATED => 2,
		ReconnectEventInfo.DECK_CREATED => 3,
		ReconnectEventInfo.EQUIPMENT_SET => 3,
		ReconnectEventInfo.CHAMPION_CARD_PLAYED => 4,
		ReconnectEventInfo.RESOURCE_CARD_PLAYED => 5,
		ReconnectEventInfo.PLAYER_MULLIGANED_HAND => 6, 
		ReconnectEventInfo.PLAYER_ACCEPTED_HAND => 6, 
		ReconnectEventInfo.PLAYER_STATE_MODIFIED => 7,
		ReconnectEventInfo.TURN_PHASE_UPDATED => 8, 
		ReconnectEventInfo.GREEN_LIGHT => 9,
		ReconnectEventInfo.COMBAT_LISTING => 10,
		ReconnectEventInfo.PLAYER_OPTION_LIST => 11,
		ReconnectEventInfo.CHESS_TIMER_UPDATED => 12, 
		ReconnectEventInfo.ACTIVE_CHESS_TIMER => 12,
		_ => 13
	};

	private void OnDispatchEventToPlayer(Player player, SessionEventArgs args)
	{
		if (!_eventQueues.TryGetValue(player, out var queue))
		{
			queue = new Queue<(int eventId, byte[] eventData)>();
			_eventQueues[player] = queue;
		}

		var data = (args.Class, args.ToByteArray());
		queue.Enqueue(data);

		var key = ResyncKey(args);
		if (key is null)
			return;

		if (!_eventHistory.TryGetValue(player, out var history))
		{
			history = new Dictionary<object, (int eventId, byte[] eventData)>();
			_eventHistory[player] = history;
		}

		history[key] = data;
	}

	private static object? ResyncKey(SessionEventArgs args)
	{
		return args switch
		{
			GameStartedSessionEventArgs => (typeof(GameStartedSessionEventArgs), (object?)null),
			AbilityPushedOnChainSessionEventArgs chain => (typeof(AbilityPushedOnChainSessionEventArgs), (object?)chain.AbilityInstanceId),
			DeckCreatedSessionEventArgs d => (typeof(DeckCreatedSessionEventArgs), (object?)d.PlayerId),
			EquipmentSetSessionEventArgs e => (typeof(EquipmentSetSessionEventArgs), (object?)e.PlayerId),
			ChampionCardPlayedSessionEventArgs ch => (typeof(ChampionCardPlayedSessionEventArgs), (object?)ch.PlayerId),
			PlayerMulliganedHandSessionEventArgs m => (typeof(PlayerMulliganedHandSessionEventArgs), (object?)m.PlayerId),
			PlayerAcceptedStartingHandSessionEventArgs a => (typeof(PlayerAcceptedStartingHandSessionEventArgs), (object?)a.PlayerId),
			PlayerStateModifiedSessionEventArgs ps => (typeof(PlayerStateModifiedSessionEventArgs), (object?)ps.PlayerId),
			CombatListingSessionEventArgs => (typeof(CombatListingSessionEventArgs), (object?)null),
			ChessTimerUpdatedSessionEventArgs ct => (typeof(ChessTimerUpdatedSessionEventArgs), (object?)ct.PlayerId),
			ActiveChessTimerPlayerSessionEventArgs ac => (typeof(ActiveChessTimerPlayerSessionEventArgs), (object?)ac.PlayerId),
			_ => null
		};
	}

	private static bool TryGetChainAbilityEvent(object key, out long abilityInstanceId)
	{
		if (key is ValueTuple<Type, object> tuple &&
		    tuple.Item1 == typeof(AbilityPushedOnChainSessionEventArgs) &&
		    tuple.Item2 is long id)
		{
			abilityInstanceId = id;
			return true;
		}

		abilityInstanceId = 0;
		return false;
	}

	public void Dispose()
	{
		_gameSession.DispatchToPlayer -= OnDispatchEventToPlayer;
		_gameSession.FlushReady -= OnFlushReady;
		_gameSession.Dispose();
	}
}