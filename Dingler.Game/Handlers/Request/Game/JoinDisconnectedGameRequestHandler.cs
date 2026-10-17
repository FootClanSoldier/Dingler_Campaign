extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Games;
using HexGame::Game.Client.Network.LoadBalancer;
using HexGame::Game.Shared.Network.LoadBalancer;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Game;

[Authenticated]
public sealed class JoinDisconnectedGameRequestHandler
	: IRequestHandler<JoinDisconnectedGameRequestArgs, JoinDisconnectedGameResponse>
{
	private readonly GameManager _gameManager;
	private readonly ILogger<JoinDisconnectedGameRequestHandler> _logger;

	public JoinDisconnectedGameRequestHandler(GameManager gameManager,
		ILogger<JoinDisconnectedGameRequestHandler> logger)
	{
		_gameManager = gameManager;
		_logger = logger;
	}

	public JoinDisconnectedGameResponse HandleRequest(SessionContext context,
		JoinDisconnectedGameRequestArgs request)
	{
		if (!_gameManager.TryGetGameForPlayer(context.UserName!, out _))
		{
			_logger.LogError("User {username} tried to rejoin game but wasn't in it?", context.UserName);
			return new JoinDisconnectedGameResponse { Error = EJoinDisconnectedGameError.InternalServerError };
		}

		return new JoinDisconnectedGameResponse { RoutingPlayerId = request.PlayerId };
	}
}
