extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Tournaments;
using HexGame::Game.Client.Network.Tournaments;
using HexGame::Game.Shared.Network.Tournaments;

namespace Dingler.Game.Handlers.Request.Tournament;

[Authenticated]
public sealed class DraftSelectCardRequestHandler : IRequestHandler<DraftSelectCardRequestArgs, DraftSelectCardResponse>
{
	private readonly TournamentManager _tournamentManager;

	public DraftSelectCardRequestHandler(TournamentManager tournamentManager)
	{
		_tournamentManager = tournamentManager;
	}

	public DraftSelectCardResponse HandleRequest(SessionContext context, DraftSelectCardRequestArgs request)
	{
		if (context.UserName is null ||
		    !_tournamentManager.TryGetTournament(request.TournamentID, out var tournament) ||
		    tournament.DraftPhase is null)
		{
			return new DraftSelectCardResponse
			{
				Error = EDraftSelectCardError.InvalidTournamentError,
				ErrorMessage = "Tournament does not exist"
			};
		}

		var error = tournament.DraftPhase.SelectCard(context.UserName, request.PackID, request.CardID, request.TournamentID);

		return new DraftSelectCardResponse { Error = error };
	}
}
