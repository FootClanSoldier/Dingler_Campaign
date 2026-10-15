extern alias HexGame;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Tournaments;

namespace Dingler.Game.Tournaments;

public static class PairingGenerator
{
	public static List<TournamentPairing> GeneratePairings(List<TournamentPlayerInfo> players, Dictionary<string, deck_bits> decks, int round,
		ETournamentStyle style, Dictionary<string, HashSet<string>> previousOpponents)
	{
		if (style.HasFlag(ETournamentStyle.Swiss))
			return GenerateSwissPairings(players, decks, previousOpponents);

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

	private static List<TournamentPairing> GenerateSwissPairings(List<TournamentPlayerInfo> players, Dictionary<string, deck_bits> decks, Dictionary<string, HashSet<string>> previousOpponents)
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

			if (!previousOpponents.TryGetValue(player1.Name, out var player1Previous))
			{
				player1Previous = new HashSet<string>();
				previousOpponents[player1.Name] = player1Previous;
			}
			
			if (!previousOpponents.TryGetValue(player2.Name, out var player2Previous))
			{
				player2Previous = new HashSet<string>();
				previousOpponents[player2.Name] = player2Previous;
			}

			var playerIndex = 2;
			
			while (player1Previous.Contains(player2.Name))
			{
				if (unpaired.Count <= playerIndex)
				{
					// If we can't find a player a hero hasn't played (this should never happen, but edge cases
					// being what they are...) just fight the best guy again. Not sure how magic handles it but fuck it
					// this sucks
					player2 = unpaired[1];
					break;
				}
				player2 = unpaired[playerIndex];
				playerIndex++;
			}

			player1Previous.Add(player2.Name);
			player2Previous.Add(player1.Name);
			
			unpaired.Remove(player2);
			unpaired.RemoveAt(0);
			pairings.Add(new TournamentPairing(player1, player2, decks));
		}

		if (unpaired.Count == 1)
			pairings.Add(new TournamentPairing(unpaired[0], null, decks, bye: true));

		return pairings;
	}
}
