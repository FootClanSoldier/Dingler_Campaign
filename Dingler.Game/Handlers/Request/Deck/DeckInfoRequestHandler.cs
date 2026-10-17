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
public sealed class DeckInfoRequestHandler : IRequestHandler<GetDeckInfoRequestArgs, GetDeckInfoResponse>
{
	private readonly DeckService _deckService;
	private readonly ILogger<DeckInfoRequestHandler> _logger;
	
	public DeckInfoRequestHandler(DeckService deckService, ILogger<DeckInfoRequestHandler> logger)
	{
		_deckService = deckService;
		_logger = logger;
	}

	public GetDeckInfoResponse HandleRequest(SessionContext context, GetDeckInfoRequestArgs request)
	{
		try
		{
			return _deckService.GetDeckInfo(context, request);
		}
		catch (InvalidOperationException ex)
		{
			_logger.LogError(ex, "Could not get deck id for user");
			return new GetDeckInfoResponse()
			{
				Error = EGetDeckInfoError.InternalServerError,
				ErrorMessage = "Could not retrieve deck information."
			};
		}
	}
}