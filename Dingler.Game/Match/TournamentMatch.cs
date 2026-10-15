extern alias HexGame;
using System.Collections.Concurrent;
using Dingler.Game.Games;
using Dingler.Game.Tournaments;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Tournaments;

namespace Dingler.Game.Match;

public class TournamentMatch
{
	public ulong MatchId { get; }
	public MatchPhase Phase { get; private set; }
	public DateTime NextGameTime { get; private set; }
	public TournamentInfo TournamentInfo { get; }
	public bool IsMatchDecided { get; set; }
	public TournamentPairing TournamentPairing { get; }
	private readonly GameSettings _gameSettings;
	private readonly GameManager _gameManager;
	private TaskCompletionSource _tcs;
	private ConcurrentDictionary<string, byte> _readyPlayers;

	private readonly ConcurrentDictionary<string, bool> _requestedToLeave;
	private readonly Lock _sideboardLock;

	public event Action<TournamentMatch>? MatchStarted;
	public event Action<TournamentMatch, HexGameWrapper>? GameStarted;
	public event Action<TournamentMatch>? MatchEnded;
	public event Action<TournamentMatch, HexGameWrapper>? GameEnded;
	public event Action<TournamentMatch, string>? PlayerLeftToSideboard;
	public event Action<TournamentMatch, string>? PlayerLeftToMainLobby;
	public event Action<TournamentMatch, string>? PlayerForfeited;
	public event Action<TournamentMatch, UID>? GameCompleted;
	public event Action<TournamentMatch, MatchPhase>? PhaseChanged;
	

	public TournamentMatch(ulong id, GameManager gameManager, GameSettings gameSettings,
		TournamentPairing tournamentPairing, TournamentInfo tournamentInfo)
	{
		MatchId = id;
		Phase = MatchPhase.Pending;
		_gameManager = gameManager;
		_gameSettings = gameSettings;
		TournamentPairing = tournamentPairing;
		TournamentInfo = tournamentInfo;
		_tcs = new TaskCompletionSource();
		_sideboardLock = new Lock();
		_readyPlayers = new ConcurrentDictionary<string, byte>();
		_requestedToLeave = new ConcurrentDictionary<string, bool>
		{
			[tournamentPairing.Player1.Name] = false,
			[tournamentPairing.Player2.Name] = false
		};
	}

	public List<string> GetPlayersInMatch()
	{
		return
		[
			TournamentPairing.Player1.Name,
			TournamentPairing.Player2.Name
		];
	}

	
	public async Task<List<UID>> StartMatchAsync(CancellationToken token)
	{
		MatchStarted?.Invoke(this);
		var winners = new List<UID>();
		var forcedFirstPlayer = UID.Invalid;
		SessionStateEncounterData? encounterData = null;
		while (!IsMatchDecided)
		{
			HexGameWrapper game;
			if (encounterData is null)
			{
				game = _gameManager.CreateGameSession(TournamentInfo.TournamentID, TournamentPairing, _gameSettings.SeriesType,
					_gameSettings.SessionFlags, token);

				encounterData = game.EncounterData;
			}
			else
			{
				for (int i = 0; i < encounterData.TournamentDecks.Count; i++)
				{
					var playerName = encounterData.TournamentDecks[i].PlayerName;
					var updatedDeck = TournamentPairing.Decks[playerName];
					encounterData.TournamentDecks[i].PlayerDeck = updatedDeck;
				}

				encounterData.FirstPlayer = forcedFirstPlayer;
				game = _gameManager.CreateGameSession(TournamentInfo.TournamentID, TournamentPairing, encounterData, token);
			}
			
			lock (_sideboardLock)
			{
				Phase = MatchPhase.PlayingGame;
				_readyPlayers.Clear();
			}
			PhaseChanged?.Invoke(this, MatchPhase.PlayingGame);
			
			GameStarted?.Invoke(this, game);
			var result = await game.RunGameAsync(forcedFirstPlayer);
			NextGameTime = DateTime.UtcNow.AddMinutes(2);
			GameEnded?.Invoke(this, game);
			winners.Add(result.Item1);
			GameCompleted?.Invoke(this, result.Item1);

			if (winners.Count == 3 || (winners.Count == 2 && winners[0] == winners[1]))
				IsMatchDecided = true;

			if (!IsMatchDecided)
			{
				if (_tcs.Task.IsCompleted)
					_tcs = new TaskCompletionSource();

				lock (_sideboardLock)
				{
					Phase = MatchPhase.Sideboard;
				}
				PhaseChanged?.Invoke(this, MatchPhase.Sideboard);

				SubmitBotSideboards();

				await Task.WhenAny(_tcs.Task, Task.Delay(TimeSpan.FromMinutes(1), token));
				forcedFirstPlayer = result.Item2;
			}
			
			game.Dispose();
		}

		Phase = MatchPhase.Complete;
		PhaseChanged?.Invoke(this, MatchPhase.Complete);
		MatchEnded?.Invoke(this);
		return winners;
	}

	public void DeckSubmitted(string username, deck_bits deck)
	{
		lock (_sideboardLock)
		{
			if (Phase != MatchPhase.Sideboard)
				return;
		
			if (!TournamentPairing.Decks.ContainsKey(username))
				return;

			TournamentPairing.Decks[username] = deck;
			_readyPlayers[username] = 1;

			if (!_readyPlayers.ContainsKey(TournamentPairing.Player1.Name) ||
			    !_readyPlayers.ContainsKey(TournamentPairing.Player2.Name))
				return;
			
			_tcs.TrySetResult();
		}
	}

	// Bots never open the sideboard screen; auto-submit their existing deck so a game 2 can start immediately.
	private void SubmitBotSideboards()
	{
		foreach (var player in GetPlayersInMatch())
		{
			if (!DraftBots.IsBot(player))
				continue;

			if (TournamentPairing.Decks.TryGetValue(player, out var deck))
				DeckSubmitted(player, deck);
		}
	}

	public void HandleTransfer(string username)
	{
		if (!_requestedToLeave.TryGetValue(username, out var alreadyRequested))
			return;

		if (alreadyRequested)
		{
			_requestedToLeave[username] = false;

			if (Phase == MatchPhase.Sideboard)
			{
				PlayerLeftToSideboard?.Invoke(this, username);
			}
			else
			{
				PlayerLeftToMainLobby?.Invoke(this, username);
			}

			return;
		}

		_requestedToLeave[username] = true;
	}

	public void PlayerForfeitsMatch(string username)
	{
		lock (_sideboardLock)
		{
			if (IsMatchDecided)
				return;

			IsMatchDecided = true;
			if (Phase == MatchPhase.Sideboard)
				_tcs.TrySetResult();
		}

		PlayerForfeited?.Invoke(this, username);
	}
}