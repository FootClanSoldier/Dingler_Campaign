extern alias HexGame;
using Dingler.Game.Match;
using Dingler.Server;
using Dingler.Game.Protocol.Rooms.Models;
using Dingler.Game.States;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Network.Tournaments;
using HexGame::Game.Shared.Tournaments;
using HexGame::Game.Shared.TournamentSystemMkII;

namespace Dingler.Game.Tournaments;

public sealed class TournamentCommunicator : IDisposable
{
	private readonly SessionManager _sessionManager;
	public TournamentCommunicator(SessionManager sessionManager)
	{
		_sessionManager = sessionManager;
	}

	public void RegisterTournamentMethods(Tournament tournament)
	{
		tournament.PlayerRegistered += OnPlayerRegistered;
		tournament.PlayerDeregistered += OnPlayerDeRegistered;
		tournament.PlayerJoined += OnPlayerJoined;
		tournament.PlayerLeft += OnPlayerLeft;
		tournament.RoundStart += OnRoundStart;
		tournament.RoundEnd += OnRoundEnd;
		tournament.GameStart += OnGameStart;
		tournament.GameEnd += OnGameEnd;
		tournament.Pairings += OnPairings;
		tournament.TournamentStart += OnTournamentStart;
		tournament.TournamentComplete += OnTournamentComplete;
		tournament.SendPlayerToSideboard += OnSendPlayerToSideboard;
		tournament.PlayerRequestsFullUpdate += OnPlayerRequestsFullUpdate;
		tournament.PlayerForfeits += OnPlayerForfeits;
		tournament.StateChanged += OnStateChanged;
	}

	public void UnregisterTournamentMethods(Tournament tournament)
	{
		tournament.PlayerRegistered -= OnPlayerRegistered;
		tournament.PlayerDeregistered -= OnPlayerDeRegistered;
		tournament.PlayerJoined -= OnPlayerJoined;
		tournament.PlayerLeft -= OnPlayerLeft;
		tournament.RoundStart -= OnRoundStart;
		tournament.RoundEnd -= OnRoundEnd;
		tournament.GameStart -= OnGameStart;
		tournament.GameEnd -= OnGameEnd;
		tournament.Pairings -= OnPairings;
		tournament.TournamentStart -= OnTournamentStart;
		tournament.TournamentComplete -= OnTournamentComplete;
		tournament.SendPlayerToSideboard -= OnSendPlayerToSideboard;
		tournament.PlayerRequestsFullUpdate -= OnPlayerRequestsFullUpdate;
		tournament.PlayerForfeits -= OnPlayerForfeits;
		tournament.StateChanged -= OnStateChanged;
	}
	
	private void OnPlayerRegistered(Tournament tournament, TournamentRoomState state, string playerUsername)
	{
		
	}

	private void OnPlayerDeRegistered(Tournament tournament, TournamentRoomState state, string playerUsername)
	{
		
	}

	private void OnPlayerJoined(Tournament tournament, TournamentRoomState state, string username, bool isWaitingRoom)
	{
		var players = state.TournamentInfo.Players.Select(p => p.Name).ToList();
		
		if (isWaitingRoom && state.TournamentInfo.State == TournamentState.WaitForStart)
		{
			var roomUpdate = new RoomUpdate(state.WaitingRoomVersion, UpdateType.Partial,
				$"/tourn:waitingroom-{tournament.Id}/players", players);
			var roomData = new RoomData($"tourn:waitingroom-{tournament.Id}", "SERVER", roomUpdate);

			foreach (var player in players.Where(p => p != username).ToList())
			{
				if (_sessionManager.TryGetUserSession(player, out var playerContext))
					playerContext.TrySendMessageToClient(roomData);
			}
		}
		else if (state.TournamentInfo.State != TournamentState.WaitForStart &&
		         state.TournamentInfo.State != TournamentState.Complete &&
		         state.TournamentInfo.State != TournamentState.Canceled)
		{
			if (!state.DeckInfo.TryGetValue(username, out var deck))
				return;

			var deckId = new UID(UID.Type.Deck, deck.Id);
				
			var playerIndex = GetPlayerIndex(state, username);
			var myGame =
				state.TournamentInfo.Games.LastOrDefault(g => g.Player1ID == playerIndex || g.Player2ID == playerIndex);
				
			if (myGame is null)
				return;

			var sessionState = BuildSessionState(myGame);
				
			if (_sessionManager.TryGetUserSession(username, out var context))
				context.TrySendMessageToClient(new TournamentSessionStartEventArgs(sessionState, deckId, false));
		}
	}

	private void OnPlayerLeft(Tournament tournament, TournamentRoomState state, string username, bool isWaitingRoom)
	{
		
	}

	private void OnRoundStart(Tournament tournament, TournamentRoomState state, int roundId)
	{
		BroadcastRoomUpdate(tournament, state);
	}

	private void OnRoundEnd(Tournament tournament, TournamentRoomState state, int roundId)
	{
		
	}

	private void OnGameStart(Tournament tournament, TournamentRoomState state, ulong gameId)
	{
		foreach (var player in state.TournamentInfo.Players.Select(p => p.Name).ToList())
		{
			var playerIndex = GetPlayerIndex(state, player);
			var myGame =
				state.TournamentInfo.Games.LastOrDefault(g => g.Player1ID == playerIndex || g.Player2ID == playerIndex);
				
			if (myGame is null)
				continue;

			state.DeckInfo.TryGetValue(player, out var deck);
			var deckId = deck is not null ? new UID(UID.Type.Deck, deck.Id) : UID.Invalid;
			var sessionState = BuildSessionState(myGame);

			if (_sessionManager.TryGetUserSession(player, out var context))
				context.TrySendMessageToClient(new TournamentSessionStartEventArgs(sessionState, deckId, false));
		}
	}

	private void OnGameEnd(Tournament tournament, TournamentRoomState state, int gameId)
	{
		
	}

	private void OnPairings(Tournament tournament, TournamentRoomState state, List<TournamentPairing> pairings)
	{
		BroadcastRoomUpdate(tournament, state);
	}

	private void OnStateChanged(Tournament tournament, TournamentRoomState state)
	{
		BroadcastRoomUpdate(tournament, state);
	}

	private void BroadcastRoomUpdate(Tournament tournament, TournamentRoomState state)
	{
		var info = state.TournamentInfo;

		var playersDelta = new Dictionary<string, TournamentPlayerInfo>();
		for (var i = 0; i < info.Players.Count; i++)
		{
			info.Players[i].PlayerID = (ulong)(i + 1);
			playersDelta[(i + 1).ToString()] = info.Players[i];
		}

		var matchesDelta = new Dictionary<string, TournamentGameInfo>();
		for (var i = 0; i < info.Games.Count; i++)
			matchesDelta[i.ToString()] = info.Games[i];

		// Partial updates (not full): the client ignores full updates once it has caught up on a room.
		var updates = new List<RoomUpdate>
		{
			new(++state.VersionNumber, UpdateType.Partial, $"/tourn:tournament-{tournament.Id}/players", playersDelta),
			new(++state.VersionNumber, UpdateType.Partial, $"/tourn:tournament-{tournament.Id}/matches", matchesDelta),
		};

		var roomData = new RoomData($"tourn:tournament-{tournament.Id}", "SERVER", updates);

		foreach (var player in info.Players)
		{
			if (_sessionManager.TryGetUserSession(player.Name, out var context))
				context.TrySendMessageToClient(roomData);
		}
	}

	private void OnTournamentComplete(Tournament tournament, TournamentRoomState roomState)
	{
		
	}

	private void OnTournamentStart(Tournament tournament, TournamentRoomState state)
	{
		var players = state.TournamentInfo.Players.Select(p => p.Name).ToList();
		foreach (var player in players)
		{
			state.DeckInfo.TryGetValue(player, out var deck);
			if (!_sessionManager.TryGetUserSession(player, out var context))
				continue;
				
			context.TrySendMessageToClient(new TournamentPlayerJoinedEventArgs(tournament.Id, false, deck, null));
			
			context.TrySendMessageToClient(new TournamentStartedEventArgs(tournament.Id));
			
			var info = state.TournamentInfo;
			var roomUpdate = new RoomUpdate(state.VersionNumber, info);
			var roomData = new RoomData($"tourn:tournament-{tournament.Id}", "SERVER", roomUpdate);

			context.TrySendMessageToClient(roomData);
		}
	}

	private void OnSendPlayerToSideboard(Tournament tournament, TournamentRoomState state, string username)
	{

	}

	private void OnPlayerRequestsFullUpdate(Tournament tournament, TournamentRoomState state, string username,
		bool isWaitingRoom)
	{
		if (!_sessionManager.TryGetUserSession(username, out var context))
			return;
		
		if (isWaitingRoom)
		{
			var waitingRoomUpdate =
				new WaitingRoomUpdate(tournament.Id, state.TournamentInfo.Players.Select(p => p.Name).ToList());
			var roomUpdate = new RoomUpdate(state.WaitingRoomVersion, waitingRoomUpdate);
			var roomData = new RoomData($"tourn:waitingroom-{tournament.Id}", "SERVER", roomUpdate);

			context.TrySendMessageToClient(roomData);
		}
		else
		{
			var info = state.TournamentInfo;
			var roomUpdate = new RoomUpdate(state.VersionNumber, info);
			var roomData = new RoomData($"tourn:tournament-{tournament.Id}", "SERVER", roomUpdate);
			context.TrySendMessageToClient(roomData);
		}
	}
	
	private static SessionState BuildSessionState(TournamentGameInfo gameInfo)
	{
		return new SessionState
		{
			SessionId = new UID(UID.Type.AuthoritativeSession, gameInfo.ID),
			SessionName = $"game-{gameInfo.ID}",
			MinimumPlayerCount = 2,
			MaximumPlayerCount = 2,
			JoinInsteadOfReconnect = false
		};
	}
	
	private ulong GetPlayerIndex(TournamentRoomState state, string username)
	{
		return (ulong)state.TournamentInfo.Players.FindIndex(p => p.Name == username);
	}

	private void OnPlayerForfeits(TournamentMatch? match, string username)
	{
		var winner = match?.GetPlayersInMatch().FirstOrDefault(p => p != username);
		
		if (winner is null || !_sessionManager.TryGetUserSession(winner, out var session))
			return;

		session.TrySendMessageToClient(new GotoLobbyEventArgs(match!.TournamentInfo.TournamentID));
	}

	public void Dispose()
	{
		
	}
}