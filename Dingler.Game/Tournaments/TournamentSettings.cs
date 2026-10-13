extern alias HexGame;
using System;
using HexGame::Game.Shared.Tournaments;

namespace Dingler.Game.Tournaments;

public class TournamentSettings
{
	public long RegistrationOpenTime { get; }
	public int MaxPlayers { get; }
	public int MinPlayers { get; }
	public string Description { get; }
	public long TournamentStartTime { get; }
	public long TournamentEndTime { get; }
	public ETournamentFormats TournamentFormat { get; }
	public ETournamentStyle TournamentStyle { get; }
	public bool IsSpawner { get; set; }
	public bool IsWaitingRoom { get; set; }

	public int ConfigId { get; }

	public IReadOnlyList<int> DraftSetIds { get; }

	public TournamentSettings(long registrationOpenTime, int maxPlayers, int minPlayers, string description,
		long tournamentStartTime, long tournamentEndTime, ETournamentFormats tournamentFormat,
		ETournamentStyle tournamentStyle, bool isSpawner, bool isWaitingRoom, int configId = 0,
		IReadOnlyList<int>? draftSetIds = null)
	{
		RegistrationOpenTime = registrationOpenTime;
		MaxPlayers = maxPlayers;
		MinPlayers = minPlayers;
		Description = description;
		TournamentStartTime = tournamentStartTime;
		TournamentEndTime = tournamentEndTime;
		TournamentFormat = tournamentFormat;
		TournamentStyle = tournamentStyle;
		IsSpawner = isSpawner;
		IsWaitingRoom = isWaitingRoom;
		ConfigId = configId;
		DraftSetIds = draftSetIds ?? Array.Empty<int>();
	}

	public TournamentSettings CreateWaitingRoomSettings()
	{
		return new TournamentSettings(RegistrationOpenTime, MaxPlayers, MinPlayers, Description, TournamentStartTime,
			TournamentEndTime, TournamentFormat, TournamentStyle, false, true, ConfigId, DraftSetIds);
	}

}