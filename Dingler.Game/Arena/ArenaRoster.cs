extern alias HexGame;
using Dingler.Game.Protocol;
using HexGame::Game.Shared;
using HexGame::Reckoning.Campaign.Messages.Arena;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Arena;

/// <summary>The opponent groups for the 2018 Frost Ring bracket.</summary>
public enum ArenaGroup
{
	Tier1Regular,   // 6 decks, 15 health
	Tier1Boss,      // Eternal Guardian, 19 health
	Standard,       // 39 decks, 23 health
	StandardElite,  // their 39 Elite versions, 25 health
	Boss,           // 10 bosses + Hogarth, 30 health
	BossElite,      // their 11 Elite versions, 30 health
	Rare,           // Uruunaz and Zakiir, 40 health
	Unclassified,
}

public sealed record ArenaOpponent(ArenaChallenger Challenger, ArenaGroup Group, string PairKey, bool IsElite, string ChampionName);

/// <summary>
/// The arena opponents sorted into bracket groups, and Hogarth's challenges, all read from the client's own game data
/// at first use. Group rule (data fields only): an Elite has "Elite" in its champion's name; then the champion's
/// starting health decides the group. Elite and normal versions share a pair key: the deck name without "_Elite"
/// (four Elite decks keep the plain name, so the key alone is not unique; use it together with IsElite).
/// </summary>
public static class ArenaRoster
{
	private sealed record Data(
		IReadOnlyList<ArenaOpponent> Opponents,
		Dictionary<ulong, ArenaOpponent> ById,
		Dictionary<(string, bool), ArenaOpponent> ByPair,
		IReadOnlyList<ResourceId> Challenges);

	private static readonly Lazy<Data> LazyData = new(Build);

	public static IReadOnlyList<ArenaOpponent> Opponents => LazyData.Value.Opponents;

	/// <summary>Hogarth's 21 in-battle challenge conversations (design 03 D3-A).</summary>
	public static IReadOnlyList<ResourceId> Challenges => LazyData.Value.Challenges;

	public static IReadOnlyList<ArenaOpponent> InGroup(ArenaGroup group) => Opponents.Where(o => o.Group == group).ToList();

	public static ArenaOpponent? Find(ulong challengerId) => LazyData.Value.ById.GetValueOrDefault(challengerId);

	/// <summary>The Elite version of a normal opponent, or the normal version of an Elite; null if it has none.</summary>
	public static ArenaOpponent? Twin(ArenaOpponent opponent) => LazyData.Value.ByPair.GetValueOrDefault((opponent.PairKey, !opponent.IsElite));

	private static Data Build()
	{
		var templates = HexGame::Singleton<TemplateManager>.Instance;
		var opponents = new List<ArenaOpponent>();

		foreach (var challenger in ArenaCatalog.Challengers)
		{
			var group = ArenaGroup.Unclassified;
			var pairKey = string.Empty;
			var isElite = false;
			var championName = challenger.ChallengerName;

			if (templates.Decks.TryGetValue(challenger.EncounterDeck, out var deck) &&
			    templates.Champions.TryGetValue(deck.m_ChampionId, out var champion))
			{
				championName = champion.m_Name ?? string.Empty;
				isElite = championName.Contains("Elite", StringComparison.Ordinal);
				pairKey = deck.m_DeckName.EndsWith("_Elite", StringComparison.Ordinal) ? deck.m_DeckName[..^"_Elite".Length] : deck.m_DeckName;
				group = (champion.m_StartingHealth, isElite) switch
				{
					(15, false) => ArenaGroup.Tier1Regular,
					(19, false) => ArenaGroup.Tier1Boss,
					(23, false) => ArenaGroup.Standard,
					(25, true) => ArenaGroup.StandardElite,
					(30, false) => ArenaGroup.Boss,
					(30, true) => ArenaGroup.BossElite,
					(40, _) => ArenaGroup.Rare,
					_ => ArenaGroup.Unclassified,
				};
			}

			opponents.Add(new ArenaOpponent(challenger, group, pairKey, isElite, championName));
		}

		var byPair = new Dictionary<(string, bool), ArenaOpponent>();
		foreach (var o in opponents.Where(o => o.Group is not ArenaGroup.Unclassified))
			byPair.TryAdd((o.PairKey, o.IsElite), o);

		// Hogarth's challenge conversations: his conversations named "...Challenge..." (the two "Strike Removal" ones aside).
		var challenges = templates.ConversationTemplates.Values
			.Where(c => c.m_OwnerName == "Hogarth" && c.m_Name != null &&
			            c.m_Name.Contains("Challenge", StringComparison.Ordinal) && !c.m_Name.Contains("Strike", StringComparison.Ordinal))
			.OrderBy(c => c.m_Name, StringComparer.Ordinal)
			.Select(c => c.m_Id)
			.ToList();

		var counts = string.Join("/", Enum.GetValues<ArenaGroup>().Select(g => opponents.Count(o => o.Group == g)));
		StaticLogger.LogInformation("Arena roster: groups {counts} (expected 6/1/39/39/11/11/2/0), {challenges} Hogarth challenges (expected 21)",
			counts, challenges.Count);

		return new Data(opponents, opponents.ToDictionary(o => o.Challenger.ChallengerID), byPair, challenges);
	}
}
