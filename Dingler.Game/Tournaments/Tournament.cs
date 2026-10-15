extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Systems;
using Dingler.Game.Games;
using Dingler.Game.Match;
using Dingler.Game.Protocol.Chat;
using Dingler.Game.States;
using Dingler.Game.Tournaments.Registration;
using Dingler.Game.Tournaments.StartCondition;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Tournaments;
using HexGame::Game.Shared.TournamentSystemMkII;

namespace Dingler.Game.Tournaments;

extern alias HexGame;

public sealed class Tournament : IRegisterable, IJoinable, IDisposable
{
	public ulong Id { get; }
	public bool IsRunning { get; init; }

	public event Action<Tournament, TournamentRoomState, string>? PlayerRegistered;
	public event Action<Tournament, TournamentRoomState, string>? PlayerDeregistered;
	public event Action<Tournament, TournamentRoomState, string, bool>? PlayerRequestsFullUpdate;
	public event Action<Tournament, TournamentRoomState, string, bool>? PlayerJoined;
	public event Action<Tournament, TournamentRoomState, string, bool>? PlayerLeft;
	public event Action<Tournament, TournamentRoomState, int>? RoundStart;
	public event Action<Tournament, TournamentRoomState, int>? RoundEnd;
	public event Action<Tournament, TournamentRoomState, ulong>? GameStart;
	public event Action<Tournament, TournamentRoomState, int>? GameEnd;
	public event Action<Tournament, TournamentRoomState, List<TournamentPairing>>? Pairings;
	public event Action<Tournament, TournamentRoomState>? TournamentStart;
	public event Action<Tournament, TournamentRoomState>? TournamentComplete;
	public event Action<Tournament, TournamentRoomState, string>? SendPlayerToSideboard;
	public event Action<Tournament, TournamentRoomState, string>? SendPlayerToMainLobby;
	public event Action<Tournament, TournamentRoomState, string>? SendPlayerToTournamentLobby;
	public event Action<TournamentMatch?, string>? PlayerForfeits;
	public event Action<Tournament, TournamentRoomState>? StateChanged;
	public event Action<Tournament>? Cleanup;
	public bool IsFull { get; private set; }
	public bool IsFinished { get; private set; }
	// It's confusing. I need to redo this. This is checking if it's a waiting room's internal tournament
	// I might have been high
	public bool IsWaitingRoom { get; private set; }
	
	private readonly Actor<TournamentRoomState> _actor;
	private readonly SemaphoreSlim _semaphoreSlim;
	private readonly IStartCondition _startCondition;
	private readonly CancellationToken _tournamentToken;
	private readonly MatchManager _matchManager;
	private readonly IReadOnlyList<ITournamentPhase> _phases;
	private readonly DeckBuildingPhase? _deckBuildingPhase;

	private const int PointsPerMatchWin = 1;

	private ulong _numberOfGamesPlayed = 0;

// Don't call this normally. Use TournamentManager.CreateTournament
	public Tournament(ulong id, IStartCondition startCondition, SessionManager sessionManager, GameManager gameManager,
		TournamentSettings tournamentSettings, GameSettings gameSettings, CancellationToken tournamentToken,
		IReadOnlyList<ITournamentPhase>? phases = null, bool isWaitingRoom = false)
	{
		Id = id;
		var tournamentInfo = new TournamentInfo()
		{
			TournamentID = id,
			MaxEntrants = tournamentSettings.MaxPlayers,
			MinEntrants = tournamentSettings.MinPlayers,
			Format = tournamentSettings.TournamentFormat,
			Style = tournamentSettings.TournamentStyle,
			Games = new List<TournamentGameInfo>(),
			linkedTournament = null,
			Players = new List<TournamentPlayerInfo>(),
			ResgistrationOpenTime = DateTime.MinValue.Ticks,
			State = TournamentState.WaitForStart,
			TournamentEndTime = DateTime.MaxValue.Ticks,
			TournamentStartTime = DateTime.MinValue.Ticks,
			TournamentStatus = ETournamentStatus.WaitingForPlayers,
			tournamentDescription = new TournamentDesc(Id, tournamentSettings.MinPlayers, tournamentSettings.MaxPlayers,
				tournamentSettings.Description, new List<string>().ToArray(), 0, 0, 0)
			{
				RoomType = "",
				TournamentState = TournamentState.WaitForStart,
				Format = tournamentSettings.TournamentFormat,
				Style = tournamentSettings.TournamentStyle,
				tournamentRewards = new TournamentRewardCollection(),
				tournamentFees = new Dictionary<int, TournamentEntryInfo>()
			}
		};
		_actor = new Actor<TournamentRoomState>(new TournamentRoomState(tournamentInfo, gameSettings));
		_semaphoreSlim = new SemaphoreSlim(1, 1);
		_startCondition = startCondition;
		IsWaitingRoom = isWaitingRoom;
		_tournamentToken = tournamentToken;
		_matchManager = new MatchManager(gameManager, gameSettings, _actor.State.TournamentInfo, sessionManager);
		_phases = phases ?? Array.Empty<ITournamentPhase>();
		DraftPhase = _phases.OfType<DraftPhase>().FirstOrDefault();
		_deckBuildingPhase = _phases.OfType<DeckBuildingPhase>().FirstOrDefault();
	}
	
	public Task<RegistrationResult> RegisterAsync(string username, deck_bits deck, UID playerUid)
	{
		return _actor.ScheduleWork(state =>
		{
			if (state.TournamentInfo.State != TournamentState.WaitForStart)
				return RegistrationResult.Fail("Tournament is not accepting entrants.");
			
			if (state.TournamentInfo.Players.Count >= state.TournamentInfo.MaxEntrants)
				return RegistrationResult.Fail("Tournament is full");

			if (state.TournamentInfo.Players.Any(p => p.Name.Equals(username)))
				return RegistrationResult.Fail("Player is already registered");
			
			state.TournamentInfo.Players.Add(new TournamentPlayerInfo()
			{
				Name = username,
				DeckHash = deck.Id.ToString(),
				Status = ETournamentPlayerStatus.WaitingForTournamentStart,
				State = PlayerStates.WaitingForTournamentStart,
				EliminationReason = ETournamentPlayerEliminationReason.TPE_NotEliminated,
				ElimintationRound = 0,
				PlayerID = (ulong)(state.TournamentInfo.Players.Count + 1),
				GWR = 0,
				Losses = 0,
				Wins = 0,
				OMWR = 0,
				OOMWR = 0,
				Points = 0,
				PlayerUID = playerUid
			});
			state.BaseDeckInfo[username] = deck;
			state.DeckInfo[username] = deck;
			var result = RegistrationResult.Success();

			result.TournamentId = Id;
			IsFull = state.TournamentInfo.Players.Count == state.TournamentInfo.MinEntrants;
			PlayerRegistered?.Invoke(this, state, username);
			return result;
		});
	}

	public Task<bool> IsInTournamentAsync(string username)
	{
		return _actor.ScheduleWork(state =>
		{
			return state.TournamentInfo.Players.Any(p => p.Name.Equals(username));
		});
	}
	
	public Task<RegistrationResult> DropAsync(string username)
	{
		return _actor.ScheduleWork(state =>
		{
			if (!state.TournamentInfo.State.In(TournamentState.WaitForStart))
			{
				return RegistrationResult.Fail("Tournament has already started");
			}
			
			var players = state.TournamentInfo.Players;
			if (!players.Any(p => p.Name.Equals(username)))
				return RegistrationResult.Fail("Player is not registered to tournament");

			for (int i = 0; i < players.Count; i++)
			{
				if (players[i].Name.Equals(username))
				{
					players.RemoveAt(i);
					var result = RegistrationResult.Success();
					result.TournamentId = Id;
					return result;
				}
			}

			return RegistrationResult.Fail("Could not  find player");
		});
	}

	// Not actually starting. Just sets it to wait to start.
	public async Task LaunchAsync(CancellationToken token)
	{
		_ = _actor.RunAsync(token);
		await _startCondition.WaitForStartAsync(this, token);

		await _actor.ScheduleWork(state =>
		{
			if (state.TournamentInfo.State != TournamentState.WaitForStart)
				return;
			
			if (state.TournamentInfo.Players.Count < state.TournamentInfo.MinEntrants)
				return;

			state.TournamentInfo.State = TournamentState.PlayGames.WaitForRoundToFinish;
			state.TournamentInfo.TournamentStatus = ETournamentStatus.InProgress;

			state.TournamentInfo.NumberOfRounds =
				CalculateNumberOfRounds(state.TournamentInfo.Style, state.TournamentInfo.Players.Count);

			foreach (var player in state.TournamentInfo.Players)
			{
				player.Status = ETournamentPlayerStatus.WaitingForNewRound;
				player.State = PlayerStates.WaitingForNewRound;
			}

			state.CurrentRound = 0;
			state.VersionNumber++;
			TournamentStart?.Invoke(this, state);
		});

		foreach (var phase in _phases)
			await phase.RunAsync(this, token);

		await _actor.ScheduleWork(state => TournamentComplete?.Invoke(this, state));
		_actor.Finish();
		Cleanup?.Invoke(this);
	}
	
	private int CalculateNumberOfRounds(ETournamentStyle style, int playerCount)
	{
		if (style.HasFlag(ETournamentStyle.Swiss))
		{
			var swissRounds = (int)Math.Ceiling(Math.Log(Math.Max(2, playerCount), 2.0)) + 1;
			return Math.Min(swissRounds, 12);
		}

		if (playerCount <= 1)
			return 1;

		return (int)Math.Ceiling(Math.Log(playerCount, 2.0));
	}

	// The pairing/game/elimination loop, run by the GamePhase. Lives here because it owns the actor and match
	// manager and raises the tournament events.
	internal async Task RunGamePhaseAsync(CancellationToken token)
	{
		var numberOfRounds = await _actor.ScheduleWork(state => state.TournamentInfo.NumberOfRounds);
		Dictionary<string, HashSet<string>> previousOpponents = new();

		// Round 1: pairings and games start immediately (no pre-round pause).
		var roundMatches = await GeneratePairingsAsync(previousOpponents);
		var games = await StartRoundGamesAsync(roundMatches, 1);
		await Task.WhenAll(games);
		await UpdateStandingsAsync(previousOpponents);

		for (int i = 2; i <= numberOfRounds; i++)
		{
			// Pair immediately and broadcast the new pairings, then pause to review standings, then start games.
			roundMatches = await GeneratePairingsAsync(previousOpponents);
			await Task.Delay(TimeSpan.FromSeconds(30), token);

			games = await StartRoundGamesAsync(roundMatches, i);
			await Task.WhenAll(games);
			await UpdateStandingsAsync(previousOpponents);
		}
	}

	private sealed record RoundMatch(TournamentPairing Pairing, TournamentGameInfo? GameInfo);

	// Computes final standings after the last round: tiebreakers (OMWR, OOMWR, GWR) and ranks.
	private Task UpdateStandingsAsync(Dictionary<string, HashSet<string>> previousOpponents)
	{
		return _actor.ScheduleWork(state =>
		{
			var players = state.TournamentInfo.Players;

			double MatchWinRate(TournamentPlayerInfo p) =>
				p.Wins + p.Losses == 0 ? 0 : (double)p.Wins / (p.Wins + p.Losses);

			var byName = players.ToDictionary(p => p.Name);

			// OMWR: average of each opponent's match win rate (byes are already excluded from previousOpponents).
			foreach (var p in players)
			{
				if (!previousOpponents.TryGetValue(p.Name, out var opponents) || opponents.Count == 0)
				{
					p.OMWR = 0;
					continue;
				}

				p.OMWR = opponents.Average(o => MatchWinRate(byName[o]));
			}

			// OOMWR: average of each opponent's OMWR.
			foreach (var p in players)
			{
				if (!previousOpponents.TryGetValue(p.Name, out var opponents) || opponents.Count == 0)
				{
					p.OOMWR = 0;
					continue;
				}

				p.OOMWR = opponents.Average(o => byName[o].OMWR);
			}

			// GWR: game wins / games played, derived from the recorded per-game winners.
			var gameWins = new Dictionary<string, int>();
			var gamesPlayed = new Dictionary<string, int>();
			foreach (var game in state.TournamentInfo.Games)
			{
				var p1 = players.FirstOrDefault(p => p.PlayerID == game.Player1ID);
				var p2 = players.FirstOrDefault(p => p.PlayerID == game.Player2ID);
				if (p1 is null || p2 is null)
					continue;

				foreach (var winnerId in new[] { game.Game1Winner, game.Game2Winner, game.Game3Winner })
				{
					if (winnerId == 0)
						continue;

					gamesPlayed[p1.Name] = gamesPlayed.GetValueOrDefault(p1.Name) + 1;
					gamesPlayed[p2.Name] = gamesPlayed.GetValueOrDefault(p2.Name) + 1;

					var winnerName = p1.PlayerID == winnerId ? p1.Name : p2.Name;
					gameWins[winnerName] = gameWins.GetValueOrDefault(winnerName) + 1;
				}
			}

			foreach (var p in players)
			{
				var played = gamesPlayed.GetValueOrDefault(p.Name);
				p.GWR = played == 0 ? 0 : (double)gameWins.GetValueOrDefault(p.Name) / played;
			}

			var ranked = players
				.OrderByDescending(p => p.Points)
				.ThenByDescending(p => p.OMWR)
				.ThenByDescending(p => p.OOMWR)
				.ThenByDescending(p => p.GWR)
				.ThenBy(p => p.Name, StringComparer.Ordinal)
				.ToList();

			for (var i = 0; i < ranked.Count; i++)
				ranked[i].Rank = i + 1;

			StateChanged?.Invoke(this, state);
		});
	}

	internal Task SetAllPlayersAsync(ETournamentPlayerStatus status, PlayerStates playerState)
	{
		return ScheduleWork(state =>
		{
			foreach (var player in state.TournamentInfo.Players)
			{
				player.Status = status;
				player.State = playerState;
			}
		});
	}

	private Task<List<RoundMatch>> GeneratePairingsAsync(Dictionary<string, HashSet<string>> previousOpponents)
	{
		return _actor.ScheduleWork(state =>
		{
			var round = state.CurrentRound + 1;
			state.TournamentInfo.State = TournamentState.PlayGames.StartNextRound.Pairing;

			var activePlayers = state.TournamentInfo.Players
				.Where(p => p.EliminationReason == ETournamentPlayerEliminationReason.TPE_NotEliminated).ToList();
			var pairings = PairingGenerator.GeneratePairings(activePlayers, state.DeckInfo, round, state.TournamentInfo.Style, previousOpponents);

			var roundMatches = new List<RoundMatch>();

			foreach (var pairing in pairings)
			{
				if (pairing.Bye)
				{
					pairing.Player1.Wins++;
					pairing.Player1.Points += PointsPerMatchWin;
					roundMatches.Add(new RoundMatch(pairing, null));
					continue;
				}

				var gameInfo = new TournamentGameInfo
				{
					ID = ++_numberOfGamesPlayed,
					MatchID = (uint)_numberOfGamesPlayed,
					RoundID = (uint)round,
					Player1ID = pairing.Player1.PlayerID,
					Player2ID = pairing.Player2.PlayerID,
					State = MatchState.PlayGame.WaitingForSession,
					Status = ETournamentGameStatus.Starting,
					StartTime = DateTime.UtcNow.Ticks,
					EndTime = 0,
					Game1Winner = 0,
					Game2Winner = 0,
					Game3Winner = 0
				};

				state.TournamentInfo.Games.Add(gameInfo);
				roundMatches.Add(new RoundMatch(pairing, gameInfo));
			}

			// Broadcast the pairings (including the newly-created matches) immediately, before the pause.
			Pairings?.Invoke(this, state, pairings);

			return roundMatches;
		});
	}

	private Task<List<Task>> StartRoundGamesAsync(List<RoundMatch> roundMatches, int roundNumber)
	{
		return _actor.ScheduleWork(state =>
		{
			state.TournamentInfo.State = TournamentState.PlayGames.WaitForRoundToFinish;

			var gameTasks = new List<Task>();

			foreach (var roundMatch in roundMatches)
			{
				if (roundMatch.GameInfo is null)
					continue;

				gameTasks.Add(StartMatch(roundMatch.Pairing, roundMatch.GameInfo, roundNumber));
			}

			state.CurrentRound = roundNumber;

			RoundStart?.Invoke(this, state, state.CurrentRound);

			return gameTasks;
		});
	}
	
	private async Task StartMatch(TournamentPairing pairing, TournamentGameInfo gameInfo, int round)
	{
		var match = _matchManager.CreateMatch(pairing);

		await ScheduleWork(state =>
		{
			SetPairingPlayerState(state, pairing, ETournamentPlayerStatus.InGame, PlayerStates.InGame);
			StateChanged?.Invoke(this, state);
		});

		// Record per-game winners and keep player status/state in sync with the match phase as it progresses. Each
		// change is queued to the actor (serialized + broadcast) and awaited before the standings update below.
		var pendingUpdates = new List<Task>();
		var gamesCompleted = 0;

		void OnGameCompleted(TournamentMatch m, UID winner)
		{
			var gameIndex = gamesCompleted++;
			var winnerPlayerId = winner == pairing.Player1.PlayerUID
				? pairing.Player1.PlayerID
				: pairing.Player2.PlayerID;

			pendingUpdates.Add(ScheduleWork(state =>
			{
				switch (gameIndex)
				{
					case 0: gameInfo.Game1Winner = winnerPlayerId; break;
					case 1: gameInfo.Game2Winner = winnerPlayerId; break;
					default: gameInfo.Game3Winner = winnerPlayerId; break;
				}

				StateChanged?.Invoke(this, state);
			}));
		}

		void OnPhaseChanged(TournamentMatch m, MatchPhase phase)
		{
			pendingUpdates.Add(ScheduleWork(state =>
			{
				switch (phase)
				{
					case MatchPhase.PlayingGame:
						SetPairingPlayerState(state, pairing, ETournamentPlayerStatus.InGame, PlayerStates.InGame);
						break;
					case MatchPhase.Sideboard:
						SetPairingPlayerState(state, pairing, ETournamentPlayerStatus.Sideboarding, PlayerStates.Sideboarding);
						break;
					case MatchPhase.Complete:
						gameInfo.State = MatchState.Complete;
						break;
				}

				StateChanged?.Invoke(this, state);
			}));
		}

		match.GameCompleted += OnGameCompleted;
		match.PhaseChanged += OnPhaseChanged;

		try
		{
			var result = await match.StartMatchAsync(_tournamentToken);
			await Task.WhenAll(pendingUpdates);

			if (result.Count == 0)
				return;

			var player1Wins = result.Count(u => u == pairing.Player1.PlayerUID);
			var player2Wins = result.Count(u => u == pairing.Player2.PlayerUID);
			if (player1Wins + player2Wins == 0)
				return;

			var winnerName = player1Wins > player2Wins ? pairing.Player1.Name : pairing.Player2.Name;
			var loserName = winnerName == pairing.Player1.Name ? pairing.Player2.Name : pairing.Player1.Name;

			await ScheduleWork(state =>
			{
				var winner = state.TournamentInfo.Players.First(p => p.Name == winnerName);
				var loser = state.TournamentInfo.Players.First(p => p.Name == loserName);

				winner.Wins++;
				winner.Points += PointsPerMatchWin;
				winner.Status = ETournamentPlayerStatus.WaitingForNewRound;
				winner.State = PlayerStates.WaitingForNewRound;

				loser.Losses++;

				if (state.TournamentInfo.Style.HasFlag(ETournamentStyle.Swiss))
				{
					loser.Status = ETournamentPlayerStatus.WaitingForNewRound;
					loser.State = PlayerStates.WaitingForNewRound;
				}
				else
				{
					loser.Status = ETournamentPlayerStatus.Eliminated;
					loser.State = PlayerStates.Eliminated;
					loser.EliminationReason = ETournamentPlayerEliminationReason.TPE_LostMatch_SingleElim;
					loser.ElimintationRound = round;
				}

				StateChanged?.Invoke(this, state);
			});
		}
		finally
		{
			match.GameCompleted -= OnGameCompleted;
			match.PhaseChanged -= OnPhaseChanged;
		}
	}

	private static void SetPairingPlayerState(TournamentRoomState state, TournamentPairing pairing,
		ETournamentPlayerStatus status, PlayerStates playerState)
	{
		foreach (var name in new[] { pairing.Player1.Name, pairing.Player2.Name })
		{
			var player = state.TournamentInfo.Players.FirstOrDefault(p => p.Name == name);
			if (player is null)
				continue;

			player.Status = status;
			player.State = playerState;
		}
	}

	private ulong GetPlayerIndex(TournamentRoomState state, string username)
	{
		return (ulong)state.TournamentInfo.Players.FindIndex(p => p.Name == username);
	}
	
	public Task<bool> TryJoinTournament(SessionContext context, bool isWaitingRoom = false)
	{
		return _actor.ScheduleWork(state =>
		{
			var username = context.UserName!;

			if (!state.TournamentInfo.Players.Any(p => p.Name.Equals(username)))
				return false;

			if (!state.DeckInfo.TryGetValue(username, out _))
				return false;

			state.WaitingRoomVersion++;
			PlayerJoined?.Invoke(this, state, username, isWaitingRoom);

			return true;
		});
	}
	
	public Task SendFullUpdateAsync(SessionContext context, bool isWaitingRoomRequest, CancellationToken token)
	{
		// Hex has different deltas depending on the room... Ughhhhhhhhhhhhhhhhhhhhhhh. We could have combined
		// them into one but noooooooooooo. I have to constantly know if the room I'm in is a "WaitingRoom" 
		return _actor.ScheduleWork(state => PlayerRequestsFullUpdate?.Invoke(this, state, context.UserName!, isWaitingRoomRequest));
	}

	public Task<TournamentInfo> GetInfoAsync()
	{
		return _actor.ScheduleWork(state => state.TournamentInfo);
	}

	public Task ReconnectAsync(SessionContext context)
	{
		return SendFullUpdateAsync(context, IsWaitingRoom, CancellationToken.None);
	}

	public void Dispose()
	{
		_semaphoreSlim.Dispose();
	}

	// isFullJoin is when hex has you join {roomName}_full. It's meant to say "send me the full room state". Players
	// need that before they can accept deltas.
	public async Task<bool> TryJoinAsync(SessionContext context, TournamentJoinChatRequest request, CancellationToken token = default)
	{
		if (request.IsFullStateRequest)
		{
			await SendFullUpdateAsync(context, request.IsWaitingRoomRequest, token);
			return true;
		}
		
		return await TryJoinTournament(context, request.IsWaitingRoomRequest);
		
	}

	public void UpdateDeck(string username, deck_bits deck)
	{
		// Limited formats build their deck in the deck-building phase before any match exists.
		if (_deckBuildingPhase?.SubmitDeck(username, deck) == true)
			return;

		if (!_matchManager.TryGetMatchForPlayer(username, out var match))
			return;
		
		match.DeckSubmitted(username, deck);
	}

	internal DraftPhase? DraftPhase { get; }

	internal Task ScheduleWork(Action<TournamentRoomState> work) => _actor.ScheduleWork(work);

	internal Task<TResult> ScheduleWork<TResult>(Func<TournamentRoomState, TResult> work) => _actor.ScheduleWork(work);

	public void HandlePlayerWantsToLeave(string username)
	{
		if (!_matchManager.TryGetMatchForPlayer(username, out var match))
			return;
		
		match.HandleTransfer(username);
	}

	public bool TryForfeitMatch(string username)
	{
		_matchManager.TryGetMatchForPlayer(username, out var match);

		match?.PlayerForfeitsMatch(username);
		
		PlayerForfeits?.Invoke(match, username);

		return true;
	}
	
}