extern alias HexGame;
using Dingler.Game.Tournaments;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Client.Network.Tournaments;
using HexGame::Game.Shared.Network.Tournaments;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Tournament;

[Authenticated]
public sealed class DraftHighlightCardRequestHandler : IRequestHandler<DraftHighlightCardRequestArgs, DraftHighlightCardResponse>
{
	private readonly TournamentManager _tournamentManager;
	private readonly ILogger<DraftHighlightCardRequestHandler> _logger;

	public DraftHighlightCardRequestHandler(TournamentManager tournamentManager,
		ILogger<DraftHighlightCardRequestHandler> logger)
	{
		_tournamentManager = tournamentManager;
		_logger = logger;
	}
	
	public DraftHighlightCardResponse HandleRequest(SessionContext context, DraftHighlightCardRequestArgs request)
	{
		var username = context.UserName!;
		if (!_tournamentManager.TryGetTournament(request.TournamentID, out var tournament) ||
		    tournament.DraftPhase is null)
		{
			_logger.LogError("User {username} tried to add highlight card but tournament {tournamentId} does not exist",
				username, request.TournamentID);
			return new DraftHighlightCardResponse
			{
				Error = EDraftHighlightCardError.InternalServerError,
				ErrorMessage = "Tournament does not exist"
			};
		}

		EDraftHighlightCardError error = tournament.DraftPhase.SetCardForTimeout(username, request.CardId);
		
		return new DraftHighlightCardResponse();
	}
}