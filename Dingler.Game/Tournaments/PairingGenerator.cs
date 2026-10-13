extern alias HexGame;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Tournaments;

namespace Dingler.Game.Tournaments;

public static class PairingGenerator
{
	public static List<TournamentPairing> GeneratePairings(List<TournamentPlayerInfo> players, Dictionary<string, deck_bits> decks, int round,
		ETournamentStyle style)
	{
		if (style.HasFlag(ETournamentStyle.Swiss))
			return GenerateSwissPairings(players, decks);

		return GenerateSingleEliminationPairings(players, decks);
	}

	private static List<TournamentPairing> GenerateSingleEliminationPairings(List<TournamentPlayerInfo> players, Dictionary<string, deck_bits> decks)
	{
		var pairings = new List<TournamentPairing>();
		var ordered = players.OrderBy(p => p.Rank).ThenBy(p => p.Name).ToList();

		for (var i = 0; i < ordered.Count; i += 2)
		{
			if (i + 1 >= ordered.Count)
			{
				pairings.Add(new TournamentPairing(ordered[i], null, new Dictionary<string, deck_bits>(), bye: true));
				break;
			}

			pairings.Add(new TournamentPairing(ordered[i], ordered[i + 1], decks));
		}

		return pairings;
	}

	private static List<TournamentPairing> GenerateSwissPairings(List<TournamentPlayerInfo> players, Dictionary<string, deck_bits> decks)
	{
		var pairings = new List<TournamentPairing>();
		var unpaired = players
			.Where(p => p.EliminationReason == ETournamentPlayerEliminationReason.TPE_NotEliminated)
			.OrderByDescending(p => p.Points)
			.ThenByDescending(p => p.Wins)
			.ThenBy(p => p.Losses)
			.ThenBy(p => p.Name)
			.ToList();

		while (unpaired.Count > 1)
		{
			var player1 = unpaired[0];
			var player2 = unpaired[1];
			
			unpaired.Remove(player2);
			unpaired.RemoveAt(0);
			pairings.Add(new TournamentPairing(player1, player2, decks));
		}

		if (unpaired.Count == 1)
			pairings.Add(new TournamentPairing(unpaired[0], null, decks, bye: true));

		return pairings;
	}
}
