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
public sealed class AddNewDeckAsyncRequestHandler : IAsyncRequestHandler<AddNewDeckRequestArgs, AddNewDeckResponse>
{
	private readonly DeckService _deckService;
	private readonly ILogger<AddNewDeckAsyncRequestHandler> _logger;

	public AddNewDeckAsyncRequestHandler(DeckService deckService, ILogger<AddNewDeckAsyncRequestHandler> logger)
	{
		_deckService = deckService;
		_logger = logger;
	}

	public async Task<AddNewDeckResponse> HandleRequestAsync(SessionContext context, AddNewDeckRequestArgs request,
		CancellationToken token)

	{
		try
		{
			return await _deckService.AddNewDeck(context, request);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not save deck for user {username}", context.UserName!);
			return new AddNewDeckResponse()
			{
				Error = EAddNewDeckError.InternalServerError,
				ErrorMessage = "Could not save deck."
			};
		}
	}
}