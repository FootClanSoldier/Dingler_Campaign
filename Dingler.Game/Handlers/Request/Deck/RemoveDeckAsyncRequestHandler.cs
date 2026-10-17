extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Services;
using HexGame::Game.Client.Network.Profile;
using HexGame::Game.Shared.Network.Profile;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Deck;

[Authenticated]
public sealed class RemoveDeckAsyncRequestHandler : IAsyncRequestHandler<RemoveDeckRequestArgs, RemoveDeckResponse>
{
	private readonly DeckService _deckService;
	private readonly ILogger<RemoveDeckAsyncRequestHandler> _logger;

	public RemoveDeckAsyncRequestHandler(DeckService deckService, ILogger<RemoveDeckAsyncRequestHandler> logger)
	{
		_deckService = deckService;
		_logger = logger;
	}

	public async Task<RemoveDeckResponse> HandleRequestAsync(SessionContext context, RemoveDeckRequestArgs request,
		CancellationToken token)

	{
		try
		{
			return await _deckService.RemoveDeckAsync(context, request);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not remove deck {deckId} for user {username}", request.DeckID, context.UserName!);
			return new RemoveDeckResponse()
			{
				DeckID = request.DeckID,
				Error = ERemoveDeckError.InternalServerError,
				ErrorMessage = "Could not remove deck.",
				succeded = false
			};
		}
	}
}