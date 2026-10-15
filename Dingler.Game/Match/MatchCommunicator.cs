extern alias HexGame;
using Dingler.Server;
using Dingler.Game.Games;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Network.Tournaments;

namespace Dingler.Game.Match;

public class MatchCommunicator
{
	private readonly SessionManager _sessionManager;
	
	public MatchCommunicator(SessionManager sessionManager)
	{
		_sessionManager = sessionManager;
	}

	public void RegisterEvents(TournamentMatch match)
	{
		match.MatchStarted += OnMatchStarted;
		match.MatchEnded += OnMatchEnded;
		match.GameStarted += OnGameStarted;
		match.GameEnded += OnGameEnded;
		match.PlayerLeftToSideboard += OnPlayerLeftToSideboard;
		match.PlayerLeftToMainLobby += OnPlayerLeftToMainLobby;
		match.PlayerForfeited += OnPlayerForfeited;
	}

	public void UnregisterEvents(TournamentMatch match)
	{
		match.MatchStarted -= OnMatchStarted;
		match.MatchEnded -= OnMatchEnded;
		match.GameStarted -= OnGameStarted;
		match.GameEnded -= OnGameEnded;
		match.PlayerLeftToSideboard -= OnPlayerLeftToSideboard;
		match.PlayerLeftToMainLobby -= OnPlayerLeftToMainLobby;
		match.PlayerForfeited -= OnPlayerForfeited;
	}

	private void OnMatchStarted(TournamentMatch tournamentMatch)
	{
		
	}
	
	private void OnMatchEnded(TournamentMatch tournamentMatch)
	{
		// Send both players back to the tournament lobby to wait for the next round (non-virtual only;
		// OnPlayerLeftToMainLobby already skips virtual tournaments).
		foreach (var player in tournamentMatch.GetPlayersInMatch())
			OnPlayerLeftToMainLobby(tournamentMatch, player);
	}
	
	private void OnGameStarted(TournamentMatch tournamentMatch, HexGameWrapper game)
	{
		var decks = game.DeckIdsByPlayer;
		foreach (var player in tournamentMatch.GetPlayersInMatch())
		{
			if (!_sessionManager.TryGetUserSession(player, out var context))
				continue;
			
			if (!decks.TryGetValue(player, out var deckId))
				continue;

			var sessionState = new SessionState()
			{
				SessionId = new UID(UID.Type.AuthoritativeSession, game.Id),
				SessionName = $"game-{game.Id}",
				MinimumPlayerCount = 2,
				MaximumPlayerCount = 2,
				JoinInsteadOfReconnect = false,
				EncounterData = game.EncounterData
			};
			
			context.TrySendMessageToClient(new TournamentSessionStartEventArgs(sessionState, deckId, false));
		}
	}
	
	private void OnGameEnded(TournamentMatch tournamentMatch, HexGameWrapper game)
	{
		
	}
	
	private void OnPlayerLeftToSideboard(TournamentMatch tournamentMatch, string username)
	{
		if (!_sessionManager.TryGetUserSession(username, out var sessionContext)) 
			return;
		
		if (!tournamentMatch.TournamentPairing.Decks.TryGetValue(username, out var deck)) 
			return;
		
		var sendToSideboardEventArgs = new GotoSideboardingEventArgs(deck, tournamentMatch.TournamentInfo, 
			Math.Max((tournamentMatch.NextGameTime - DateTime.UtcNow).Seconds, 0));

		sessionContext.TrySendMessageToClient(sendToSideboardEventArgs);
		
	}

	private void OnPlayerLeftToMainLobby(TournamentMatch tournamentMatch, string username)
	{
		if (tournamentMatch.TournamentInfo.IsVirtual())
			return;
		
		if (!_sessionManager.TryGetUserSession(username, out var context))
			return;

		var sendToTournamentLobbyEventArgs = new GotoLobbyEventArgs()
		{
			TournamentID = tournamentMatch.TournamentInfo.TournamentID
		};

		context.TrySendMessageToClient(sendToTournamentLobbyEventArgs);
	}

	private void OnPlayerForfeited(TournamentMatch tournamentMatch, string forfeiter)
	{

	}
}