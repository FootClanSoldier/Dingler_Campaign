extern alias HexGame;
using Dingler.Data.Repositories;
using Dingler.Game.Draft;
using Dingler.Server;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network.Tournaments;
using DraftCollectionEntity = Dingler.Data.Entities.GameData.DraftCollection;

namespace Dingler.Game.Tournaments;

public sealed class DraftPhase : ITournamentPhase
{
	private const int SecondsPerCardRemaining = 5;
	private const int BasicShardsPerType = 40;

	private readonly SessionManager _sessionManager;
	private readonly DraftCollectionRepository _repository;
	private readonly IReadOnlyList<int> _draftSetIds;
	private readonly int _configId;

	private readonly Lock _gate = new();
	private readonly List<DraftSeat> _seats = new();
	private readonly Dictionary<string, int> _seatIndexByName = new();
	private readonly HashSet<string> _autoPickers = new(StringComparer.Ordinal);
	private RoundState? _currentRound;
	private ulong _idCounter;
	private ulong _packIdCounter;

	public DraftPhase(SessionManager sessionManager, DraftCollectionRepository repository,
		IReadOnlyList<int> draftSetIds, int configId)
	{
		_sessionManager = sessionManager;
		_repository = repository;
		_draftSetIds = draftSetIds;
		_configId = configId;
	}

	public async Task RunAsync(Tournament tournament, CancellationToken token)
	{
		var info = await tournament.GetInfoAsync();
		var players = info.Players.ToList();
		if (players.Count == 0)
			return;

		var packGenerator = new PackGenerator();

		foreach (var player in players)
		{
			var seat = new DraftSeat(player.Name, player.PlayerUID.GetInstanceId());
			_seats.Add(seat);
			_seatIndexByName[seat.Name] = _seats.Count - 1;
		}

		foreach (var seat in _seats)
			SendTo(seat.Name, new DraftStartingEventArgs(tournament.Id, new List<card_instance_bits>()));

		for (var packIndex = 0; packIndex < _draftSetIds.Count; packIndex++)
		{
			var setNumber = _draftSetIds[packIndex];

			foreach (var seat in _seats)
				seat.CurrentPack.AddRange(DraftManager.MaterializePack(packGenerator, setNumber, ref _idCounter));

			var packSize = _seats[0].CurrentPack.Count;
			var moveRight = packIndex != 1;

			for (var pick = 0; pick < packSize; pick++)
				await RunPickRoundAsync(tournament, token, moveRight);
		}

		await FinalizeDraftAsync(tournament);
	}

	public void AddAutoPicker(string username) => _autoPickers.Add(username);

	public EDraftSelectCardError SelectCard(string username, ulong packId, ulong cardId)
	{
		lock (_gate)
		{
			if (_currentRound is null || !_seatIndexByName.TryGetValue(username, out var seatIndex))
				return EDraftSelectCardError.InvalidTournamentError;

			var seat = _seats[seatIndex];

			if (!_currentRound.PackIdsBySeat.TryGetValue(seat, out var expectedPackId) || expectedPackId != packId)
				return EDraftSelectCardError.InvalidCardSelectionError;

			if (_currentRound.Picks.ContainsKey(seat))
				return EDraftSelectCardError.InvalidCardSelectionError;

			if (!_currentRound.CardsBySeat[seat].Contains(cardId))
				return EDraftSelectCardError.InvalidCardSelectionError;

			_currentRound.Picks[seat] = seat.CurrentPack.First(c => c.Id == cardId);

			if (_currentRound.Picks.Count == _currentRound.ActiveSeats.Count)
				_currentRound.AllPicked.TrySetResult();

			return EDraftSelectCardError.Ok;
		}
	}

	private async Task RunPickRoundAsync(Tournament tournament, CancellationToken token, bool moveRight)
	{
		var activeSeats = _seats.Where(s => s.CurrentPack.Count > 0).ToList();
		if (activeSeats.Count == 0)
			return;

		var round = new RoundState(activeSeats);

		foreach (var seat in activeSeats)
		{
			round.PackIdsBySeat[seat] = ++_packIdCounter;
			round.CardsBySeat[seat] = seat.CurrentPack.Select(c => c.Id).ToHashSet();
		}

		lock (_gate)
		{
			_currentRound = round;

			foreach (var seat in activeSeats)
			{
				if (_autoPickers.Contains(seat.Name))
					round.Picks[seat] = DraftManager.PickRarest(seat.CurrentPack);
			}

			if (round.Picks.Count == round.ActiveSeats.Count)
				round.AllPicked.TrySetResult();
		}

		foreach (var seat in activeSeats)
		{
			var ticks = (int)(TimeSpan.TicksPerSecond * SecondsPerCardRemaining * seat.CurrentPack.Count);
			SendTo(seat.Name, new ReceiveDraftPackEventArgs(tournament.Id, round.PackIdsBySeat[seat], ticks, new List<card_instance_bits>(seat.CurrentPack)));
		}

		var maxTicks = activeSeats.Max(s => (long)TimeSpan.TicksPerSecond * SecondsPerCardRemaining * s.CurrentPack.Count);
		var timeout = Task.Delay(TimeSpan.FromTicks(maxTicks), token);

		await Task.WhenAny(round.AllPicked.Task, timeout);

		lock (_gate)
		{
			foreach (var seat in activeSeats)
			{
				if (!round.Picks.TryGetValue(seat, out var picked))
					picked = DraftManager.PickRarest(seat.CurrentPack);

				seat.CurrentPack.Remove(picked);
				seat.Collection.Add(picked);

				SendTo(seat.Name, new ReceiveSelectedCardEventArgs(tournament.Id, picked));
			}

			_currentRound = null;
		}

		RotatePacks(moveRight);
	}

	private void RotatePacks(bool moveRight)
	{
		var moved = new List<card_instance_bits>[_seats.Count];

		for (var i = 0; i < _seats.Count; i++)
		{
			var target = moveRight ? (i + 1) % _seats.Count : (i - 1 + _seats.Count) % _seats.Count;
			moved[target] = _seats[i].CurrentPack;
		}

		for (var i = 0; i < _seats.Count; i++)
			_seats[i].CurrentPack = moved[i];
	}

	private async Task FinalizeDraftAsync(Tournament tournament)
	{
		foreach (var seat in _seats)
		{
			var deck = DraftCollection.ToPoolDeck(seat.Collection, seat.ProfileId);

			// Every player gets 40 of each basic shard for deck building (basic resources aren't drafted).
			foreach (var shard in DraftManager.GetBasicShards(BasicShardsPerType, ref _idCounter))
				deck.CardsInSideboard.Add(shard);

			await tournament.ScheduleWork(state =>
			{
				state.DeckInfo[seat.Name] = deck;
			});

			await _repository.UpsertAsync(new DraftCollectionEntity
			{
				TournamentId = _configId,
				PlayerProfileId = seat.ProfileId,
				CardsJson = DraftCollection.SerializeCards(seat.Collection)
			});
		}
	}

	private void SendTo(string username, object message)
	{
		if (_sessionManager.TryGetUserSession(username, out var context))
			context.TrySendMessageToClient(message);
	}

	private sealed class RoundState
	{
		public Dictionary<DraftSeat, ulong> PackIdsBySeat { get; } = new();
		public Dictionary<DraftSeat, HashSet<ulong>> CardsBySeat { get; } = new();
		public Dictionary<DraftSeat, card_instance_bits> Picks { get; } = new();
		public List<DraftSeat> ActiveSeats { get; }
		public TaskCompletionSource AllPicked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public RoundState(List<DraftSeat> activeSeats)
		{
			ActiveSeats = activeSeats;
		}
	}
}
