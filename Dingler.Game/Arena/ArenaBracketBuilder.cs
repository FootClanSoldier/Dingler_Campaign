namespace Dingler.Game.Arena;

/// <summary>
/// Builds a 20-fight Frost Ring run:
/// D1-A, the 2018 bracket:
///   tier 1 = 4 of the 6 tier-1 regulars + Eternal Guardian;
///   tiers 2-4 = 4 standard opponents, of which 1/2/2 are their Elite version, + a normal boss;
///   tier 4's boss = an Elite boss (the final fight). No champion twice: an Elite counts as its normal version.
/// Elite fights occupy fights 9, 12, 14, 17 and 19
///   (1-based), and each rare encounter (Uruunaz, Zakiir) is rolled once per run at 1% and replaces one of the normal
///   regular fights 6, 7, 8, 11, 13, 16 or 18 (never a tier's first fight).
/// One of Hogarth's 21 challenges appears on a random regular fight of each tier.
/// </summary>
public static class ArenaBracketBuilder
{
	public const double RareChancePerRun = 0.01;                 // per rare encounter, per run
	// Fixed Elite slots (0-based orders = fights 9, 12, 14, 17, 19): 1/2/2 in tiers 2-4.
	private static readonly HashSet<int> EliteOrders = new() { 8, 11, 13, 16, 18 };
	// The normal regular fights a rare encounter may replace (fights 6, 7, 8, 11, 13, 16, 18).
	private static readonly int[] RareOrders = { 5, 6, 7, 10, 12, 15, 17 };

	public static List<ArenaFightRecord> Build(ulong arenaId, Random rng)
	{
		var slots = new ArenaOpponent[20];

		// Tier 1: four of the tier-1 regulars, then Eternal Guardian.
		var tier1 = Shuffle(ArenaRoster.InGroup(ArenaGroup.Tier1Regular), rng);
		for (var i = 0; i < 4; i++) slots[i] = tier1[i];
		slots[4] = ArenaRoster.InGroup(ArenaGroup.Tier1Boss)[0];

		// Tiers 2-4: twelve different standard opponents; the Elite version in the fixed Elite slots.
		var standard = Shuffle(ArenaRoster.InGroup(ArenaGroup.Standard), rng);
		var next = 0;
		for (var tier = 2; tier <= 4; tier++)
		{
			for (var p = 0; p < 4; p++)
			{
				var order = (tier - 1) * 5 + p;
				var normal = standard[next++];
				slots[order] = EliteOrders.Contains(order) ? ArenaRoster.Twin(normal) ?? normal : normal;
			}
		}

		// Bosses: three different ones; the final boss in its Elite version.
		var bosses = Shuffle(ArenaRoster.InGroup(ArenaGroup.Boss), rng);
		slots[9] = bosses[0];
		slots[14] = bosses[1];
		slots[19] = ArenaRoster.Twin(bosses[2]) ?? bosses[2];

		// Rare encounters: one of the normal (non-Elite) regular fights of tiers 2-4.
		var rareCandidates = Shuffle(RareOrders, rng);
		var rareSlots = new HashSet<int>();
		foreach (var rare in ArenaRoster.InGroup(ArenaGroup.Rare))
		{
			if (rng.NextDouble() >= RareChancePerRun) continue;
			var slot = rareCandidates.First(s => !rareSlots.Contains(s));
			rareSlots.Add(slot);
			slots[slot] = rare;
		}

		// Hogarth's challenges: one per tier, on a regular fight that isn't a rare encounter.
		var challenges = ArenaRoster.Challenges;
		var challengeAt = new Dictionary<int, string>();
		for (var tier = 1; tier <= 4 && challenges.Count > 0; tier++)
		{
			var candidates = Enumerable.Range((tier - 1) * 5, 4).Where(o => !rareSlots.Contains(o)).ToList();
			challengeAt[candidates[rng.Next(candidates.Count)]] = challenges[rng.Next(challenges.Count)].ToString();
		}

		return Enumerable.Range(0, 20).Select(order => new ArenaFightRecord
		{
			FightId = arenaId * 32 + (ulong)order + 1,
			Order = order,
			ChallengerId = slots[order].Challenger.ChallengerID,
			Challenge = challengeAt.GetValueOrDefault(order),
		}).ToList();
	}

	private static List<T> Shuffle<T>(IReadOnlyList<T> items, Random rng)
	{
		var list = items.ToList();
		for (var i = list.Count - 1; i > 0; i--)
		{
			var j = rng.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
		return list;
	}
}
