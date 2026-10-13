extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Services;
using HexGame::Game.Client.Network.Profile;
using HexGame::Game.Shared.Network.Profile;

namespace Dingler.Game.Handlers.Request.Deck;

[Authenticated]
public sealed class AddNewDeckAsyncRequestHandler : IAsyncRequestHandler<AddNewDeckRequestArgs, AddNewDeckResponse>
{
	private readonly DeckService _deckService;

	public AddNewDeckAsyncRequestHandler(DeckService deckService)
	{
		_deckService = deckService;
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
            Dingler.Game.Protocol.StaticLogger.LogError(
                ex, "Deck creation request failed for an authenticated profile.");
			return new AddNewDeckResponse()
			{
				Error = EAddNewDeckError.InternalServerError,
				ErrorMessage = "Could not save deck."
			};
		}
	}
}