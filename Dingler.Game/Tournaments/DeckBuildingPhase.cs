extern alias HexGame;
using Dingler.Data.Repositories;
using Dingler.Game.Draft;
using Dingler.Server;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network.Tournaments;
using HexGame::Game.Shared.Tournaments;
using DraftCollectionEntity = Dingler.Data.Entities.GameData.DraftCollection;

namespace Dingler.Game.Tournaments;

public sealed class DeckBuildingPhase : ITournamentPhase
{
	private const int DefaultDeckBuildSeconds = 600;

	private readonly SessionManager _sessionManager;
	private readonly DraftCollectionRepository _repository;
	private readonly int _configId;
	private readonly int _deckBuildSeconds;

	private readonly Lock _gate = new();
	private readonly Dictionary<string, deck_bits> _submissions = new();
	private TaskCompletionSource _allSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _accepting;
	private int _playerCount;

	public DeckBuildingPhase(SessionManager sessionManager, DraftCollectionRepository repository, int configId,
		int deckBuildSeconds = DefaultDeckBuildSeconds)
	{
		_sessionManager = sessionManager;
		_repository = repository;
		_configId = configId;
		_deckBuildSeconds = deckBuildSeconds;
	}

	public async Task RunAsync(Tournament tournament, CancellationToken token)
	{
		var info = await tournament.GetInfoAsync();
		var players = info.Players.ToList();

		lock (_gate)
		{
			_accepting = true;
			_playerCount = players.Count;
			_submissions.Clear();
			_allSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		foreach (var player in players)
		{
			var poolDeck = await tournament.ScheduleWork(state =>
				state.DeckInfo.TryGetValue(player.Name, out var deck) ? deck : new deck_bits());

			SendTo(player.Name, new DeckConstructionStartedEventArgs(tournament.Id, info, poolDeck,
				_deckBuildSeconds, player.PlayerUID.GetInstanceId()));
		}

		await Task.WhenAny(_allSubmitted.Task, Task.Delay(TimeSpan.FromSeconds(_deckBuildSeconds), token));

		lock (_gate)
		{
			_accepting = false;
		}

		foreach (var player in players)
		{
			deck_bits finalDeck;
			if (_submissions.TryGetValue(player.Name, out var submitted))
			{
				finalDeck = submitted;
			}
			else
			{
				finalDeck = await tournament.ScheduleWork(state =>
					state.DeckInfo.TryGetValue(player.Name, out var deck) ? deck : new deck_bits());
			}

			await tournament.ScheduleWork(state =>
			{
				state.DeckInfo[player.Name] = finalDeck;
			});

			await _repository.UpsertAsync(new DraftCollectionEntity
			{
				TournamentId = _configId,
				PlayerProfileId = player.PlayerUID.GetInstanceId(),
				DeckJson = DraftCollection.SerializeDeck(finalDeck)
			});
		}
	}

	public bool SubmitDeck(string username, deck_bits deck)
	{
		lock (_gate)
		{
			if (!_accepting)
				return false;

			_submissions[username] = deck;

			if (_submissions.Count >= _playerCount)
				_allSubmitted.TrySetResult();

			return true;
		}
	}

	private void SendTo(string username, object message)
	{
		if (_sessionManager.TryGetUserSession(username, out var context))
			context.TrySendMessageToClient(message);
	}
}
