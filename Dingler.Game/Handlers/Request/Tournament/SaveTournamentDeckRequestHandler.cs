extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Tournaments;
using HexGame::Game.Client.Network.Tournaments;
using HexGame::Game.Shared.Network.Tournaments;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Tournament;

[Authenticated]
public class SaveTournamentDeckRequestHandler : IRequestHandler<SaveTournamentDeckRequestArgs, SaveTournamentDeckResponse>
{
	private readonly TournamentManager _tournamentManager;
	private readonly ILogger<SaveTournamentDeckRequestHandler> _logger;

	public SaveTournamentDeckRequestHandler(TournamentManager tournamentManager,
		ILogger<SaveTournamentDeckRequestHandler> logger)
	{
		_tournamentManager = tournamentManager;
		_logger = logger;
	}

	public SaveTournamentDeckResponse HandleRequest(SessionContext context,
		SaveTournamentDeckRequestArgs request)
	{
		var username = context.UserName!;
		if (!_tournamentManager.TryGetTournament(request.TournamentID, out var tournament))
		{
			_logger.LogError("User {username} tried to save deck for tournament {tournamentId} but it does not exist",
				username, request.TournamentID);
			return new SaveTournamentDeckResponse()
			{
				Success = false,
				Error = ESaveTournamentDeckError.InternalServerError,
				ErrorMessage = "Tournament does not exist"
			};
		}

		tournament.UpdateDeck(username, request.TournamentDeck);

		return new SaveTournamentDeckResponse()
		{
			Success = true,
			Error = ESaveTournamentDeckError.Ok
		};
	}
}