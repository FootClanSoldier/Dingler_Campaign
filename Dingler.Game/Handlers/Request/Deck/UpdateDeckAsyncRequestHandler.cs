extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Services;
using HexGame::Game.Client.Network.Profile;
using HexGame::Game.Shared.Network.Profile;
using HexGame::GPG.Core;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Deck;

[Authenticated]
public sealed class UpdateDeckAsyncRequestHandler : IAsyncRequestHandler<UpdateDeckRequestArgs, UpdateDeckResponse>
{
	private readonly DeckService _deckService;
	private readonly ILogger<UpdateDeckAsyncRequestHandler> _logger;

	public UpdateDeckAsyncRequestHandler(DeckService deckService, ILogger<UpdateDeckAsyncRequestHandler> logger)
	{
		_deckService = deckService;
		_logger = logger;
	}

	public async Task<UpdateDeckResponse> HandleRequestAsync(SessionContext context, UpdateDeckRequestArgs request,
		CancellationToken token)
	{
		try
		{
			return await _deckService.UpdateDeckAsync(context, request);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not update deck {deckId} for user {username}", request.DeckID, context.UserName!);
			return new UpdateDeckResponse()
			{
				DeckID = request.DeckID,
				Error = EUpdateDeckError.InternalServerError,
				ErrorMessage = "Could not update deck."
			};
		}
	}
}