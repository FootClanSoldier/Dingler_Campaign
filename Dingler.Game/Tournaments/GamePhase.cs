namespace Dingler.Game.Tournaments;

public sealed class GamePhase : ITournamentPhase
{
	public Task RunAsync(Tournament tournament, CancellationToken token)
	{
		return tournament.RunGamePhaseAsync(token);
	}
}
