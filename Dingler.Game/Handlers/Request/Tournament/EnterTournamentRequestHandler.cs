extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Tournaments;
using HexGame::Game.Client.Network.Tournaments;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network.Tournaments;

namespace Dingler.Game.Handlers.Request.Tournament;

[Authenticated]
public sealed class EnterTournamentRequestHandler : IAsyncRequestHandler<EnterTournamentRequestArgs, EnterTournamentResponse>
{
	private readonly EnterTournamentResponse _invalidDeckError;
	private readonly EnterTournamentResponse _invalidTournamentError;
	private readonly TournamentManager _tournamentManager;

	public EnterTournamentRequestHandler(TournamentManager tournamentManager)
	{
		_invalidDeckError = new EnterTournamentResponse()
		{
			Error = EEnterTournamentError.InvalidDeckIdError,
			ErrorMessage = "Invalid deck"
		};

		_invalidTournamentError = new EnterTournamentResponse()
		{
			Error = EEnterTournamentError.InvalidTouranmentError,
			ErrorMessage = "Invalid tournament"
		};

		_tournamentManager = tournamentManager;
	}
	
	public async Task<EnterTournamentResponse> HandleRequestAsync(SessionContext context, EnterTournamentRequestArgs request, CancellationToken token)
	{
		var tournamentId = request.TournamentID;

		if (!_tournamentManager.TryGetRegisterableTournament(tournamentId, out var tournament))
			return _invalidTournamentError;

		// This is a bad fix, come back and handle this later.
		deck_bits? deck;

		if (request.DeckId.GetInstanceId() == 0)
		{
			deck = new deck_bits();
		}
		else if (!context.Decks.TryGetValue(request.DeckId.GetInstanceId(), out deck))
			return _invalidDeckError;

		var playerUid = request.PlayerId.IsValid()
			? request.PlayerId
			: new UID(UID.Type.ServicePlayer, context.ProfileId);

		var result = await tournament.RegisterAsync(context.UserName!, deck, playerUid);

		if (!result.IsSuccess)
		{
			return new EnterTournamentResponse()
			{
				Error = EEnterTournamentError.InternalServerError,
				ErrorMessage = result.FailureReason
			};
		}
		
		return new EnterTournamentResponse()
		{
			Error = EEnterTournamentError.Ok,
			ErrorMessage = "Success",
			isWaitingRoom = tournament is WaitingRoom,
			TournamentID = result.TournamentId,
			TournamentDeckInfo = deck
		};
	}
}