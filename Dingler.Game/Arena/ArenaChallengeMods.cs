extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Challenge.Restriction;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Campaign.Messages.Arena;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Arena;

/// <summary>
/// Hogarth's 21 in-battle challenges and four buffs, represented as battle mods.
/// Their server-side configurations are not present in the client data.
/// Each row maps a challenge objective to original HEX encounter modifications:
/// "starts with X in play" = AddCard into the Warzone; an action the opponent "played" = AddCard into CastSpells (cast for
/// free, through the chain); RunAbilities only runs chain-ignoring abilities (the two "Arena Challenge" cards are built for
/// it); the challenge conversation sits on exactly one round-0 mod; mods are single-use, so every battle gets fresh ones.
/// </summary>
public static class ArenaChallengeMods
{
	private static ResourceId R(string guid) => new(guid);

	private sealed record Row(string ConversationId, Func<Random, List<EncounterModBase>> Mods);

	private static EncounterModAddCard InPlay(string card, EModTarget target, string? conversation = null) => new()
	{
		m_CardId = R(card), m_Amount = 1, m_Collection = ECardCollections.Warzone, m_ModTargetPlayer = target, m_RoundToApply = 0,
		m_ConversationId = conversation is null ? ResourceId.Invalid : R(conversation),
	};

	private static EncounterModAddCard Cast(string card, EModTarget target, string? conversation = null, int round = 0) => new()
	{
		m_CardId = R(card), m_Amount = 1, m_Collection = ECardCollections.CastSpells, m_ModTargetPlayer = target, m_RoundToApply = round,
		m_ConversationId = conversation is null ? ResourceId.Invalid : R(conversation),
	};

	private static EncounterModRunAbilities Run(string card, EModTarget target, string conversation) => new()
	{
		m_CardId = R(card), m_ModTargetPlayer = target, m_RoundToApply = 0, m_ConversationId = R(conversation),
	};

	// Shows the conversation only (its Apply does nothing); the engine builds the same kind of mod itself.
	private static EncounterModBase DialogOnly(string conversation) => new()
	{
		m_ConversationId = R(conversation), m_ModTargetPlayer = EModTarget.UserPlayer, m_RoundToApply = 0,
	};

	private static Row InPlayAi(string conversation, string card) => new(conversation, _ => new() { InPlay(card, EModTarget.AIPlayer, conversation) });

	private static readonly string[] Incantations =
	{
		"f8103511-772f-40ea-8599-04d520508bac", "4113287c-f5d5-495e-a517-90b84e076450", "19bd7f07-03d4-43bc-89f7-29d747563937",
		"3a6c51e8-cf1a-4b76-a774-010003648323", "ed650412-ba7f-4bda-bc87-1bbd8ca2d352",
	};

	private static readonly Dictionary<string, Row> Rows = new List<Row>
	{
		InPlayAi("f65ad7d2-6858-4ddc-9c37-170a1c6620e3", "8bf3184f-b2b4-4646-b2e9-ac39079978c9"),   // Cerebral Fulmination
		InPlayAi("ac9ad78f-64da-41ea-9134-99000b9193c1", "171dc660-b8b1-49cf-981d-1de17e8e2478"),   // Command Tower
		InPlayAi("967a76de-b1f8-4cb4-8c9d-ceb12cc1e256", "a0e08e35-6084-42b8-a2d8-ccf513d8bed0"),   // Inferno
		new("e1da7090-db19-445d-941f-d342fc998aae", _ => new()                                      // Tunneling: Wormoid Grub (Tunneling 4), face down
		{
			new EncounterModAddCard
			{
				m_CardId = R("71eb42ef-ea8a-45a9-85a1-a418041bcf00"), m_Amount = 1, m_Collection = ECardCollections.Underground,
				m_ModTargetPlayer = EModTarget.AIPlayer, m_RoundToApply = 0, m_ConversationId = R("e1da7090-db19-445d-941f-d342fc998aae"),
			},
		}),
		new("9dae10f6-4354-4828-a40c-3901d9e46e49", _ => new()                                      // Booby Traps: six in each deck
		{
			new EncounterModAddCard
			{
				m_CardId = R("9c1acda8-778b-4dd0-b278-7fee21e203af"), m_Amount = 6, m_Collection = ECardCollections.Deck, m_Shuffle = true,
				m_ModTargetPlayer = EModTarget.All, m_RoundToApply = 0, m_ConversationId = R("9dae10f6-4354-4828-a40c-3901d9e46e49"),
			},
		}),
		new("d158bb4e-02bf-436c-b4e5-63607a431517", _ => new()                                      // Soothing Song (rebuilt: no such card exists)
		{
			new EncounterModAddChampionHealth { m_Amount = 7, m_Absolute = false, m_ModTargetPlayer = EModTarget.AIPlayer, m_RoundToApply = 0 },
			Cast("4d9fc2a2-76e0-4ff5-842f-5690b8d7d3c6", EModTarget.AIPlayer, "d158bb4e-02bf-436c-b4e5-63607a431517"),   // Zodiac Observer: draw two
		}),
		new("1c4639c1-6e69-4721-ba57-d24200876e85", _ => new()                                      // Mastery of Time: the AI takes two turns in a row
		{
			Cast("ab3537a8-03d2-46fc-a669-743ea178ff9b", EModTarget.AIPlayer, "1c4639c1-6e69-4721-ba57-d24200876e85"),
		}),
		new("e4d1400b-08bc-4cd4-9b34-cfe68981ea57", rng => new()                                    // Incantation: a random one for each player
		{
			InPlay(Incantations[rng.Next(Incantations.Length)], EModTarget.AIPlayer, "e4d1400b-08bc-4cd4-9b34-cfe68981ea57"),
			InPlay(Incantations[rng.Next(Incantations.Length)], EModTarget.UserPlayer),
		}),
		InPlayAi("40ff5749-4156-4152-b377-584df1d49b37", "decb8ca0-0a46-4761-8a51-a68c669d9e1d"),   // Shrine of Prosperity
		InPlayAi("06eb1c27-63d3-4896-b832-c5a61b894268", "65e87d93-2a98-45e2-a2e9-0fc037c51ef8"),   // Headless Executioner
		InPlayAi("aa15c8d8-b026-4d72-bf98-0f17e9a0c745", "1e1959a2-752e-4a89-a0d9-1505ddfb5396"),   // Blizzard
		InPlayAi("3642d41c-374c-40fe-bf68-d9b033a32ddc", "c996eb73-4c0d-42a1-8313-4baaf12f36e0"),   // Primordial Caves
		InPlayAi("fe2a099e-9cc3-46a6-addc-1d33dec87789", "df783cf9-1dba-4256-a10a-ff8ba3b1b37e"),   // Gooplasm
		InPlayAi("1672f0d8-ace5-471e-8d51-257cd01cfd04", "9315202a-8779-48b8-8b9a-37c64783e793"),   // Might Makes Right
		InPlayAi("99e34c3e-15d4-4d9a-89c1-7ad669a8a8b6", "0d2dab18-1526-4e0f-b5a5-499543fdc003"),   // Closed Coffins
		InPlayAi("4cb40e9a-9a91-4526-bfe9-18d7eaf275b4", "b81fbba2-0af9-4605-8298-56791ff95e63"),   // Bellow of the Arena
		InPlayAi("7ad55c77-e1d4-4563-ac09-b55dbf35e996", "de46f966-5e08-4d6f-805d-8ac634c5d745"),   // Frostheart Fractured
		new("b7cb1780-8f1b-4577-a506-fa70c26819d2", _ => new()                                      // Locket of Reflection: both players
		{
			InPlay("15ff8319-6330-43e4-8e7b-b87b1be999d6", EModTarget.All, "b7cb1780-8f1b-4577-a506-fa70c26819d2"),
		}),
		new("a3b49a66-69d0-4e2d-8d76-a98673d3710d", _ => new()                                      // Random Banner: the game's "Arena Challenge - Banners"
		{
			Run("7f1b4013-74f5-495e-82e1-cd480a8b2f69", EModTarget.All, "a3b49a66-69d0-4e2d-8d76-a98673d3710d"),
		}),
		new("c6c597d4-b486-4396-ba8f-6ac45ce71ee7", _ => new()                                      // Clash of Steel: announced at the start, cast at round 3
		{
			DialogOnly("c6c597d4-b486-4396-ba8f-6ac45ce71ee7"),
			Cast("30e00b0c-dafa-4afb-9e50-53ac1af5f12f", EModTarget.AIPlayer, round: 3),
		}),
		new("0b24cb58-704e-425b-9129-954946229fb9", _ => new()                                      // Runic Actions: the game's "Arena Challenge - Runic" (covers both players)
		{
			Run("abbb7f51-94e9-4b3b-95c0-0455583502e2", EModTarget.AIPlayer, "0b24cb58-704e-425b-9129-954946229fb9"),
		}),
	}.ToDictionary(r => r.ConversationId);

	// ---------- buffs (design 03 D6-A: a won challenge gives one at random; it applies at the next boss fight) ----------

	public sealed record Buff(string Kind, string NameKey, string RewardConversation, string BossNotification, Func<EncounterModBase> Mod);

	public static readonly IReadOnlyList<Buff> Buffs = new List<Buff>
	{
		new("Health", "ArenaBuff_Health", "1786304b-da44-4842-b80a-9d6b5245d6c8", "37c49517-da25-468d-8627-7859b1b82d1e",
			() => new EncounterModAddChampionHealth { m_Amount = 5, m_Absolute = false, m_ModTargetPlayer = EModTarget.UserPlayer }),
		new("Charge", "ArenaBuff_Charge", "577a31d3-3396-4a60-8803-2413aafda66a", "2aecfe2b-0194-45e1-93ba-7b1cb0337ec2",
			() => new EncounterModAddResource { m_ChargeValue = 2, m_Absolute = false, m_ModTargetPlayer = EModTarget.UserPlayer }),
		new("Resource", "ArenaBuff_Resources", "9a9d9508-8107-440a-a6ba-8de166ce35e0", "5d8dc35a-8fa9-432a-bfe8-395c8a359b55",
			() => new EncounterModAddResource { m_MaxResourceValue = 1, m_Absolute = false, m_ModTargetPlayer = EModTarget.UserPlayer }),
		new("Brawler", "ArenaBuff_Brawler", "b823948d-041b-4d8c-b435-624dfca129af", "ddb2195f-07b1-473c-bef7-ed6097d26cbc",
			() => new EncounterModAddCard { m_CardId = R("b050ae8f-2840-4396-81ac-c85b58aff9d6"), m_Amount = 1, m_Collection = ECardCollections.Warzone, m_ModTargetPlayer = EModTarget.UserPlayer }),
	};

	public const string ChallengeWinStrikeRemoval = "d5c5101b-4f9d-4aef-b04d-6f0e8e831b43";
	public const string PerfectedTierStrikeRemoval = "0c625da8-c284-4b62-bfe8-c0abd867ab76";

	public static Buff? FindBuff(string kind) => Buffs.FirstOrDefault(b => b.Kind == kind);

	/// <summary>The lobby's buff slot: Name is a text key the client localizes; clicking the slot replays ConversationID
	/// (the Reward line). Description and TemplateID are never read by the client.</summary>
	public static ArenaBuff ToArenaBuff(Buff buff) => new()
	{
		Name = buff.NameKey,
		Description = buff.NameKey,
		TemplateID = R(buff.BossNotification),
		ConversationID = R(buff.RewardConversation),
	};

	/// <summary>Fresh battle mods for a fight: its challenge (if any), the pending buffs at a boss fight (each with its own
	/// Boss Notification), and Hogarth's pending lines from the lobby (buff rewards, strike removals).
	/// Order on screen: each conversation mod pushes a wait onto the engine's action stack, which runs last-in first-out,
	/// so the list is built in reverse: the pending lines come last in the list and play first, then the buff
	/// notifications, then the challenge's own line. Mods without a conversation apply at once.</summary>
	public static List<EncounterModBase> ForFight(ArenaRunRecord run, ArenaFightRecord fight, Random rng, IReadOnlyList<string> pendingLines)
	{
		var mods = new List<EncounterModBase>();
		if (fight.Challenge is not null && Rows.TryGetValue(fight.Challenge, out var row))
			mods.AddRange(row.Mods(rng));
		if (fight.Order % 5 == 4)
			foreach (var buff in run.Buffs.Select(FindBuff).OfType<Buff>().Distinct())
			{
				var mod = buff.Mod();
				mod.m_RoundToApply = 0;
				mod.m_ConversationId = R(buff.BossNotification);
				mods.Add(mod);
			}
		// The owner's choice (2026-10-05): Hogarth's lobby lines play at the start of the next battle. Pushed in the lobby
		// (BuffConversation 10047), a line cut short by pressing Battle leaves the client's notification flag stuck on,
		// which blocks Withdraw and the end-of-run summary until HEX restarts.
		foreach (var line in pendingLines.Distinct().Reverse())
			mods.Add(DialogOnly(line));
		return mods;
	}

	/// <summary>A challenge's heading and objective as the conversation itself holds them (for the reconnect reply,
	/// GetArenaMCChallenge 10027, which rebuilds the objective panel from these strings).</summary>
	public static (string Heading, string Objective) ObjectiveOf(string conversationId)
	{
		var templates = HexGame::Singleton<TemplateManager>.Instance;
		if (templates.ConversationTemplates.TryGetValue(R(conversationId), out var conversation) && conversation?.m_Answers is not null)
			foreach (var answer in conversation.m_Answers.Values)
				foreach (var ev in answer.m_EventsToFireWhenSelected ?? new List<ChallengeEvent>())
					if (ev is UIEventShowObjectivePanel panel)
						return (panel.m_ObjectiveHeading ?? string.Empty, panel.m_ObjectiveText ?? string.Empty);
		return (string.Empty, string.Empty);
	}
}
