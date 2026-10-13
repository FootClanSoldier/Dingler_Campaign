extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics.Transactions;
using Dingler.Game.Games;

namespace Dingler.Game.Arena.Ai;

/// <summary>
/// The client-side session the client's AI (Game.Shared.AI.AITactical) needs: it refuses anything but a
/// ClientSessionBase. Events are routed into it one by one; the AI's moves go straight to the authoritative engine.
/// Livelock guard: the AI re-submits the same refused play whenever it gets priority back with an
/// unchanged state; after 3 identical submissions in a row that card is left out of its options until the turn ends.
/// </summary>
internal sealed class ArenaAiMirror : ClientSessionBase
{
	private readonly HexRulesEngine _engine;
	public AIPlayer? SelfPlayer;
	public readonly HashSet<string> SuppressedCards = new();
	public int Suppressions;
	public int SubmittedTotal;
	private string? _lastSignature;
	private int _repeat;

	public ArenaAiMirror(SessionState state, HexRulesEngine engine) : base(state)
	{
		_engine = engine;
	}

	public void Route(SessionEventArgs e) => RouteMessage(e);

	public override bool SubmitTransaction(Transaction transaction)
	{
		SubmittedTotal++;
		var signature = transaction.ToString();
		if (transaction is not PassPriorityTransaction && signature == _lastSignature)
		{
			if (++_repeat >= 2)
			{
				var match = System.Text.RegularExpressions.Regex.Match(signature, @"SessionCardId\(([^)]*)\)");
				if (match.Success && SuppressedCards.Add(match.Groups[1].Value))
					Suppressions++;
			}
		}
		else
		{
			_lastSignature = signature;
			_repeat = 0;
		}
		return _engine.SubmitAiTransaction(transaction);
	}

	public override bool Update() => false;
	protected override void UpdateThread() { }
	public override void DispatchSessionEvent(Player player, SessionEventArgs args) { }
	public override bool IsWaitingOnTransaction() => false;
}
