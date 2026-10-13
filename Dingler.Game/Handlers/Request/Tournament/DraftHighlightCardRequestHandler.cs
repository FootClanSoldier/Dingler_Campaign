extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Client.Network.Tournaments;
using HexGame::Game.Shared.Network.Tournaments;

namespace Dingler.Game.Handlers.Request.Tournament;

[Authenticated]
public sealed class DraftHighlightCardRequestHandler : IRequestHandler<DraftHighlightCardRequestArgs, DraftHighlightCardResponse>
{
	public DraftHighlightCardResponse HandleRequest(SessionContext context, DraftHighlightCardRequestArgs request)
	{
		return new DraftHighlightCardResponse();
	}
}
