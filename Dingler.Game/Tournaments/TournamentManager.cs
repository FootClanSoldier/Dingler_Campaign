extern alias HexGame;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Data.Repositories;
using Dingler.Game.Games;
using Dingler.Game.Protocol.Messages.Json;
using Dingler.Game.States;
using Dingler.Game.Tournaments.Registration.Rules;
using Dingler.Game.Tournaments.StartCondition;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network.Tournaments;
using HexGame::Game.Shared.Tournaments;
using HexGame::Game.Shared.TournamentSystemMkII;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Tournaments;

// TODO Actually make a web api and let players cancel tournaments through this

public class TournamentManager : IAsyncStartupService
{
	private readonly ConcurrentDictionary<ulong, Tournament> _tournaments = new();
	private readonly ConcurrentDictionary<ulong, WaitingRoom> _waitingRooms = new();
	private readonly ConcurrentDictionary<string, ulong> _playerMap = new();
	private readonly TournamentRepository _tournamentRepository;
	private readonly Dictionary<ulong, CancellationTokenSource> _tournamentCtsCollection;
	private readonly IServiceProvider _serviceProvider;
	private readonly TournamentCommunicator _tournamentCommunicator;
	private readonly ILoggerFactory _loggerFactory;

	private static readonly JsonSerializerOptions JsonSerializerOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		IncludeFields = true,
		Converters =
		{
			new TournamentDescConverter(),
			new RoomUpdateConverter(),
			new WaitingRoomUpdateConverter(),
			new TournamentInfoConverter(),
			new TournamentPlayerInfoConverter(),
			new TournamentGameInfoConverter(),
		}
	};

	private ulong _currentTournamentId = 100000;
	private ulong _virtualTournamentId = long.MaxValue;

	public TournamentManager(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
	{
		_serviceProvider = serviceProvider;
		_tournamentRepository = serviceProvider.GetRequiredService<TournamentRepository>();
		_tournamentCtsCollection = new Dictionary<ulong, CancellationTokenSource>();
		_tournamentCommunicator = _serviceProvider.GetRequiredService<TournamentCommunicator>();
		_loggerFactory = loggerFactory;
	}

	public InitTournamentDescriptionsEventArgs? StartupDescriptions { get; private set; }

	public async Task InitializeAsync(CancellationToken token)
	{
		var tournaments = await _tournamentRepository.GetTournamentsAsync(token);

		var descriptionList = new List<TournamentDesc>();
		var waitingRooms = tournaments.Where(t => t.StartConditionId == 1).ToList();
		var scheduledTournaments = tournaments.Where(t => t.StartConditionId == 2).ToList();

		foreach (var waitingRoomDto in waitingRooms)
		{
			var waitingRoomId = Interlocked.Increment(ref _currentTournamentId);
			var tournamentSettings = CreateTournamentSettings(waitingRoomDto);
			var gameSettings = CreateGameSettings(waitingRoomDto);
			var waitingRoom = new WaitingRoom(this, tournamentSettings, gameSettings);
			_waitingRooms.TryAdd(waitingRoomId, waitingRoom);
			descriptionList.Add(new TournamentDesc(waitingRoomId, tournamentSettings.MinPlayers, 
				tournamentSettings.MaxPlayers, tournamentSettings.Description, Array.Empty<string>(), 0, 0, 0)
			{
				TournamentState = TournamentState.WaitForStart,
				tournamentFees = new(),
				tournamentRewards = new(),
				RoomType = "waitRoom",
				Format = tournamentSettings.TournamentFormat,
				Style = tournamentSettings.TournamentStyle,
				CompletionType = ETournamentCompletionType.Invalid,
				LastUpdateTime = DateTime.MinValue.Ticks,
				TournamentStartTime = DateTime.MinValue.Ticks,
				TournamentEndTime = DateTime.MinValue.Ticks,
			});

			_ = waitingRoom.RunAsync(_serviceProvider.GetRequiredService<ICancellationManager>().StoppingToken);
		}

		var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
		var gameManager = _serviceProvider.GetRequiredService<GameManager>();
		var cancellationManager = _serviceProvider.GetRequiredService<ICancellationManager>();

		foreach (var tournamentDto in scheduledTournaments)
		{
			var tournamentId = Interlocked.Increment(ref _currentTournamentId);
			var tournamentSettings = CreateTournamentSettings(tournamentDto);
			var gameSettings = CreateGameSettings(tournamentDto);

			var startCondition = new ScheduledStartCondition(tournamentDto.StartDate);

			var cts = cancellationManager.CreateLinkedSource();
			_tournamentCtsCollection.TryAdd(tournamentId, cts);

			var tournament = new Tournament(tournamentId, startCondition, sessionManager, gameManager,
				tournamentSettings, gameSettings, cts.Token, BuildPhases(tournamentSettings));

			_tournamentCommunicator.RegisterTournamentMethods(tournament);
			tournament.Cleanup += Cleanup;
			tournament.PlayerDeregistered += OnPlayerDeregistered;
			_tournaments.TryAdd(tournamentId, tournament);
			_ = tournament.LaunchAsync(cts.Token);

			descriptionList.Add(new TournamentDesc(tournamentId, tournamentSettings.MinPlayers,
				tournamentSettings.MaxPlayers, tournamentSettings.Description, Array.Empty<string>(), 0, 0, 0)
			{
				TournamentState = TournamentState.WaitForStart,
				tournamentFees = new(),
				tournamentRewards = new(),
				RoomType = "",
				Format = tournamentSettings.TournamentFormat,
				Style = tournamentSettings.TournamentStyle,
				CompletionType = ETournamentCompletionType.Invalid,
				LastUpdateTime = DateTime.MinValue.Ticks,
				TournamentStartTime = DateTime.MinValue.Ticks,
				TournamentEndTime = DateTime.MinValue.Ticks,
			});
		}

		var descriptionJson = JsonSerializer.Serialize(descriptionList, JsonSerializerOptions);
		var descriptionBytes = Encoding.UTF8.GetBytes(descriptionJson);

		GZipper.GZipBytes(ref descriptionBytes);

		var tournamentDescCollection = new TournamentDescCollection(descriptionBytes);
		StartupDescriptions = new InitTournamentDescriptionsEventArgs(tournamentDescCollection);
	}

	private void OnPlayerDeregistered(Tournament tournament, TournamentRoomState state, string username)
	{
		UntrackPlayer(username);
	}

	public Tournament CreateTournament(TournamentSettings tournamentSettings, GameSettings gameSettings)
	{
		var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
		var gameManager = _serviceProvider.GetRequiredService<GameManager>();
		var assignedId = tournamentSettings.MaxPlayers == 2
			? Interlocked.Increment(ref _virtualTournamentId)
			: Interlocked.Increment(ref _currentTournamentId);
		var rules = CreateRules(tournamentSettings, gameSettings);
		var cancellationManager = _serviceProvider.GetRequiredService<ICancellationManager>();
		var cts = cancellationManager.CreateLinkedSource();
		_tournamentCtsCollection.TryAdd(assignedId, cts);
		// Hardcoding to just make on full rooms for now. I hate this and want to move on. I'll eventually let players
		// schedule tournaments but god I just want a break
		var tournament = new Tournament(assignedId, new WhenFullStartCondition(), sessionManager, gameManager, tournamentSettings,
			gameSettings, cts.Token, BuildPhases(tournamentSettings));

		_tournamentCommunicator.RegisterTournamentMethods(tournament);
		tournament.Cleanup += Cleanup;
		tournament.PlayerDeregistered += OnPlayerDeregistered;
		_ = tournament.LaunchAsync(cts.Token);
		_tournaments.TryAdd(assignedId, tournament);
		return tournament;
	}

	public void UntrackPlayer(string username)
	{
		_playerMap.Remove(username, out _);
		var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
		
		if (!sessionManager.TryGetUserSession(username, out var context))
			return;

		context.CurrentTournamentId = 0;
	}
	
	private static ETournamentFormats GetFormat(int tournamentTypeId) => tournamentTypeId switch
	{
		1 => ETournamentFormats.Booster_Draft | ETournamentFormats.Immortal,
		2 => ETournamentFormats.Sealed_Deck | ETournamentFormats.Immortal,
		4 => ETournamentFormats.Immortal,
		_ => ETournamentFormats.Constructed,
	};

	private static ETournamentFormats GetSetFormatFlags(IEnumerable<int> draftSetIds)
	{
		var flags = ETournamentFormats.Constructed;
		foreach (var setId in draftSetIds)
		{
			flags |= setId switch
			{
				1 or 2 => ETournamentFormats.Chapter1,
				3 or 4 => ETournamentFormats.Chapter2,
				5 or 6 => ETournamentFormats.Chapter3,
				7 or 8 => ETournamentFormats.Chapter4,
				9 => ETournamentFormats.Chapter5,
				_ => ETournamentFormats.Constructed,
			};
		}

		return flags;
	}

	private static TournamentSettings CreateTournamentSettings(Data.Entities.GameData.Tournament tournament)
	{
		var format = GetFormat(tournament.TournamentType.Id);

		var style = tournament.MatchType.Id == 1
			? ETournamentStyle.Single_Elimination
			: ETournamentStyle.Swiss;

		var draftSetIds = tournament.DraftSets?
			.OrderBy(ds => ds.Order)
			.Select(ds => ds.CardSetId)
			.ToList() ?? new List<int>();

		format |= GetSetFormatFlags(draftSetIds);

		return new TournamentSettings(DateTime.Now.Ticks, tournament.NeededPlayers, tournament.NeededPlayers,
			tournament.Description, DateTime.Now.Ticks, DateTime.MaxValue.Ticks, format, style, isSpawner: true,
			isWaitingRoom: false, configId: tournament.Id, draftSetIds: draftSetIds);
	}

	private static GameSettings CreateGameSettings(Data.Entities.GameData.Tournament tournament)
	{
		var sessionFlags = ESessionFlags.IsTournament;
		var seriesType = SessionStateEncounterData.SeriesType.CONSTRUCTED;

		switch (tournament.TournamentType.Id)
		{
			case 1: // draft
				sessionFlags |= ESessionFlags.IsDraft;
				seriesType = SessionStateEncounterData.SeriesType.DRAFT;
				break;
			case 2: // sealed
				sessionFlags |= ESessionFlags.IsSealed;
				seriesType = SessionStateEncounterData.SeriesType.SEALED;
				break;
			case 3: // standard
				sessionFlags |= ESessionFlags.IsStandardPvP;
				break;
			default: // immortal
				sessionFlags |= ESessionFlags.IsImmortalPvP;
				break;
		}

		return new GameSettings(sessionFlags, GetFormat(tournament.TournamentType.Id), seriesType);
	}

	private List<ITournamentPhase> BuildPhases(TournamentSettings settings)
	{
		var phases = new List<ITournamentPhase>();

		if (settings.TournamentFormat.HasFlag(ETournamentFormats.Booster_Draft))
		{
			var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
			var repository = _serviceProvider.GetRequiredService<DraftCollectionRepository>();
			var logger = _loggerFactory.CreateLogger<DraftPhase>();
			phases.Add(new DraftPhase(sessionManager, repository, settings.DraftSetIds, settings.ConfigId, logger));
			phases.Add(new DeckBuildingPhase(sessionManager, repository, settings.ConfigId));
		}

		phases.Add(new GamePhase());
		return phases;
	}

	public void TrackPlayer(string username, ulong tournamentId)
	{
		_playerMap[username] = tournamentId;
	}

	public async Task<bool> ReconnectAsync(SessionContext context)
	{
		if (!_playerMap.TryGetValue(context.UserName!, out var tournamentId) ||
		    !_tournaments.TryGetValue(tournamentId, out var tournament))
		{
			return false;
		}

		context.CurrentTournamentId = tournamentId;
		await tournament.ReconnectAsync(context);
		return true;
	}

	public bool TryGetTournamentPlayerIsIn(string username, [MaybeNullWhen(false)] out ulong tournamentId)
	{
		return _playerMap.TryGetValue(username, out tournamentId);
	}

	public bool TryGetTournament(ulong id, [MaybeNullWhen(false)] out Tournament tournament)
	{
		return _tournaments.TryGetValue(id, out tournament);
	}

	public bool TryGetWaitingRoom(ulong id, [MaybeNullWhen(false)] out WaitingRoom waitingRoom)
	{
		return _waitingRooms.TryGetValue(id, out waitingRoom);
	}

	public bool TryGetRegisterableTournament(ulong id, [MaybeNullWhen(false)] out IRegisterable tournament)
	{
		if (_tournaments.TryGetValue(id, out var t))
		{
			if (t.IsFinished)
			{
				tournament = null;
				return false;
			}
			
			tournament = t;
			return true;
		}

		if (_waitingRooms.TryGetValue(id, out var w))
		{
			tournament = w;
			return true;
		}

		tournament = null;
		return false;
	}
	
	// Joining is separate from registering. Think of registering as "I paid to be here".
	// Joining is actually showing up. Hex cares for some reason.

	public bool TryGetJoinableTournament(ulong id, [MaybeNullWhen(false)] out IJoinable tournament)
	{
		if (_tournaments.TryGetValue(id, out var t))
		{
			tournament = t;
			return true;
		}

		if (_waitingRooms.TryGetValue(id, out var w))
		{
			tournament = w;
			return true;
		}

		tournament = null;
		return false;
	}

	private static List<IRegistrationRule> CreateRules(TournamentSettings tournamentSettings, GameSettings gameSettings)
	{
		return
		[
			new DeckFormatRule(tournamentSettings.TournamentFormat),
			new DeckSizeRule(tournamentSettings.TournamentFormat),
		];
	}
	
	public void Cleanup(Tournament tournament)
	{
		tournament.Cleanup -= Cleanup;
		tournament.PlayerDeregistered -= OnPlayerDeregistered;
		_tournamentCommunicator.UnregisterTournamentMethods(tournament);
		_tournaments.TryRemove(tournament.Id, out _);

		if (!_tournamentCtsCollection.Remove(tournament.Id, out var cts))
			return;
		
		if (!cts.IsCancellationRequested)
			cts.Cancel();
		cts.Dispose();
	}

	public async Task SeedDraftBotsAsync(int count)
	{
		#if RELEASE
		return;
		#endif
		var draftTournament = _tournaments.Values.FirstOrDefault(t => t.DraftPhase is not null);
		if (draftTournament is null)
			return;

		for (var i = 0; i < count; i++)
		{
			var username = $"DraftBot{i + 1}";
			var playerUid = new UID(UID.Type.ServicePlayer, (ulong)(1_000_000 + i));

			draftTournament.DraftPhase!.AddAutoPicker(username);
			await draftTournament.RegisterAsync(username, new deck_bits(), playerUid);
		}
	}
}