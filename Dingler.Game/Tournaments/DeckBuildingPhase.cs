extern alias HexGame;
using Dingler.Data.Repositories;
using Dingler.Game.Draft;
using Dingler.Server;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network.Tournaments;
using HexGame::Game.Shared.Tournaments;
using HexGame::Game.Shared.TournamentSystemMkII;
using HexGame::Reckoning.Game;
using DraftCollectionEntity = Dingler.Data.Entities.GameData.DraftCollection;

namespace Dingler.Game.Tournaments;

public sealed class DeckBuildingPhase : ITournamentPhase
{
	private const int DefaultDeckBuildSeconds = 10;

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

		await tournament.SetAllPlayersAsync(ETournamentPlayerStatus.DeckBuilding, PlayerStates.Deckbuilding);

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

			// Bots never open the deck builder; give them a playable deck so they can be paired and play (or concede).
			if (DraftBots.IsBot(player.Name))
			{
				SubmitDeck(player.Name, BuildBotDeck(poolDeck, player.PlayerUID.GetInstanceId()));
				continue;
			}

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
				await tournament.DropFromDeckBuildingAsync(player.Name);
				continue;
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

		await tournament.SetAllPlayersAsync(ETournamentPlayerStatus.WaitingForNewRound, PlayerStates.WaitingForNewRound);
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

	private static deck_bits BuildBotDeck(deck_bits poolDeck, ulong profileId)
	{
		// A test deck: 40 basic shards and a valid champion. Any valid deck would do; this one is trivial to build.
		// Note: basic shards gain their threshold via an ability (not m_Threshold), so CardShards is Colorless for
		// them -- match on IsBasicResource() instead of a shard color.
		var shards = poolDeck.CardsInSideboard
			.Where(c => c.Template is { } template && template.IsBasicResource())
			.Take(40)
			.ToList();

		var championId = HexGame::Singleton<TemplateManager>.Instance.Champions.GetKeys().FirstOrDefault();

		return new deck_bits
		{
			Id = 0,
			player_id = profileId,
			PVPChampionId = championId,
			CardsInDeck = shards,
			CardsInSideboard = new List<card_instance_bits>()
		};
	}

	private void SendTo(string username, object message)
	{
		if (_sessionManager.TryGetUserSession(username, out var context))
			context.TrySendMessageToClient(message);
	}
}
