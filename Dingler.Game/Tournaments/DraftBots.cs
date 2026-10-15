namespace Dingler.Game.Tournaments;

// Debug-only helpers for the seeded draft bots (see TournamentManager.SeedDraftBotsAsync).
public static class DraftBots
{
	public const string NamePrefix = "DraftBot";

	public static bool IsBot(string username) =>
		username.StartsWith(NamePrefix, StringComparison.Ordinal);
}
