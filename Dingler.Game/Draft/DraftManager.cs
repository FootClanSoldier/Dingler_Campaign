extern alias HexGame;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Draft;

public static class DraftManager
{
	public static List<card_instance_bits> MaterializePack(PackGenerator generator, int setNumber, ref ulong idCounter)
	{
		var pack = generator.GeneratePack(setNumber);

		var cards = new List<card_instance_bits>(pack.Cards.Count);
		foreach (var template in pack.Cards.OrderBy(c => c.CardRarity))
		{
			cards.Add(new card_instance_bits { Id = ++idCounter, TemplateID = template.m_Id });
		}

		return cards;
	}

	public static card_instance_bits PickRarest(IReadOnlyList<card_instance_bits> cards)
	{
		return cards.OrderByDescending(c => c.Template?.CardRarity ?? ERarity.Unknown).First();
	}
	
	public static List<card_instance_bits> GetBasicShards(int countPerShard, ref ulong idCounter)
	{
		var templateManager = HexGame::Singleton<TemplateManager>.Instance;
		var shards = new List<card_instance_bits>();

		foreach (var card in templateManager.Cards.Values)
		{
			if (!card.IsBasicResource())
				continue;

			for (var i = 0; i < countPerShard; i++)
				shards.Add(new card_instance_bits { Id = ++idCounter, TemplateID = card.m_Id });
		}

		return shards;
	}
}
