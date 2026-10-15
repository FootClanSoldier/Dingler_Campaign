extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Mechanics.Transactions;

namespace Dingler.Game.Games;

/// <summary>
/// Minimal in-engine seat for a seeded draft bot. It answers the pre-game decisions (go first/second, mulligan), then
/// either concedes the first time it has real priority (<paramref name="concede"/>) or just passes priority forever
/// (the designated winner in a bot-vs-bot game).
/// </summary>
internal sealed class TournamentBotSeat
{
	private readonly HexRulesEngine _engine;
	private readonly Player _bot;
	private readonly bool _concede;

	private bool _choseFirst;
	private bool _mulliganAccepted;
	private bool _conceded;

	public TournamentBotSeat(HexRulesEngine engine, Player bot, bool concede)
	{
		_engine = engine;
		_bot = bot;
		_concede = concede;
		engine.FlushReady += OnFlush;
	}

	private void OnFlush()
	{
		if (_engine.IsGameEnded || _conceded)
			return;

		var priority = _engine.GetPriorityPlayer();
		if (priority is null || priority.m_PlayerId != _bot.m_PlayerId)
			return;

		switch (_engine.CurrentTurnPhase)
		{
			case ETurnPhases.PickGoesFirst:
				if (_choseFirst)
					return;

				_choseFirst = true;
				_engine.SubmitTransaction(ChoosePlayTransaction.Create(_bot.m_PlayerId));
				return;

			case ETurnPhases.Mulligan:
				if (_mulliganAccepted)
					return;

				_mulliganAccepted = true;
				_engine.SubmitTransaction(AcceptStartingHandTransaction.Create(_bot.m_PlayerId));
				return;

			default:
				if (_concede)
				{
					_conceded = true;
					_engine.SubmitTransaction(QuitGameTransaction.Create(_bot.m_PlayerId, false, false));
				}
				else
				{
					_engine.SubmitTransaction(PassPriorityTransaction.Create(_bot.m_PlayerId, _engine.CurrentTurnPhase));
				}
				return;
		}
	}
}
