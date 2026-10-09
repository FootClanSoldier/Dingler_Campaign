extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Campaign;
using Dingler.Game.Games;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Network.LoadBalancer;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Game;

[Authenticated]
public sealed class ReadyForGameSetupRequestHandler : IRequestHandler<ReadyForGameSetupRequestArgs, ReadyForGameSetupResponseArgs>
{
	private readonly GameManager _gameManager;
	private readonly CampaignBattleService _campaignBattles;
	private readonly ILogger<ReadyForGameSetupRequestHandler>? _logger;

	public ReadyForGameSetupRequestHandler(
		GameManager gameManager,
		CampaignBattleService campaignBattles,
		ILogger<ReadyForGameSetupRequestHandler>? logger = null)
	{
		_gameManager = gameManager;
		_campaignBattles = campaignBattles;
		_logger = logger;
	}

	public ReadyForGameSetupResponseArgs HandleRequest(SessionContext context,
		ReadyForGameSetupRequestArgs request)
	{
		if (context.UserName is null ||
		    !_gameManager.TryGetGameForPlayer(context.UserName, out var match))
		{
			if (_campaignBattles.TryGetJoined(context, out var joined))
			{
				_logger?.LogInformation(
					"Campaign battle: {user} reached ReadyForGameSetup for battle {game}; engine creation is the next slice",
					context.UserName, joined.Pending.GameId);
			}

			return new ReadyForGameSetupResponseArgs
			{
				SessionState = null,
				DeckId = UID.Invalid,
				DeckTemplateId = ResourceId.Invalid,
				OpponentsInfo = new List<PlayerState>(),
				TurnOrder = new List<UID>(),
				seedZ = 0,
				seedW = 0
			};
		}

		return match.BuildGameSetupResponse(request.PlayerId);
	}
}
