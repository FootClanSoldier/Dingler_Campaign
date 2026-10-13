namespace Dingler.Game.Tournaments;

public interface ITournamentPhase
{
	Task RunAsync(Tournament tournament, CancellationToken token);
}
