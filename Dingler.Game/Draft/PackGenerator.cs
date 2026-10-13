extern alias HexGame;
using System.Diagnostics;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Resources;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Draft;

public class PackGenerator
{
	private const byte NUMBER_OF_COMMONS = 12;
	private const byte NUMBER_OF_UNCOMMONS = 4;
	private readonly Dictionary<ResourceId, Dictionary<ERarity, List<CardTemplate>>> _cardsBySet;

	public PackGenerator()
	{
		var templateManager = HexGame::Singleton<TemplateManager>.Instance;
		_cardsBySet = new Dictionary<ResourceId, Dictionary<ERarity, List<CardTemplate>>>();
		foreach (var card in templateManager.Cards.Values)
		{
			if (!card.AppearsInBoosterPack())
				continue;

			if (!_cardsBySet.TryGetValue(card.m_SetId, out var setCards))
			{
				setCards = new Dictionary<ERarity, List<CardTemplate>>();
				_cardsBySet[card.m_SetId] = setCards;
			}

			if (!setCards.TryGetValue(card.CardRarity, out var cards))
			{
				cards = new List<CardTemplate>();
				setCards[card.CardRarity] = cards;
			}

			cards.Add(card);
		}
	}

	public DraftPack GeneratePack(ResourceId setId)
	{
		var commons = _cardsBySet[setId][ERarity.Common];
		var uncommons = _cardsBySet[setId][ERarity.Uncommon];
		var rares = _cardsBySet[setId][ERarity.Rare];
		var legendaries = _cardsBySet[setId][ERarity.Legendary];
		var rng = new Random();
		var draftPack = new DraftPack();

		var isLegendaryPack = rng.Next(0, 8) == 7;

		if (isLegendaryPack)
		{
			var card = legendaries[rng.Next(0, legendaries.Count)];
			draftPack.Cards.Add(card);
		}
		else
		{
			var card = rares[rng.Next(0, rares.Count)];
			draftPack.Cards.Add(card);
		}

		var uncommonCards = GenerateUniqueCards(uncommons, NUMBER_OF_UNCOMMONS, rng);
		draftPack.Cards.AddRange(uncommonCards);

		var commonCards = GenerateUniqueCards(commons, NUMBER_OF_COMMONS, rng);
		draftPack.Cards.AddRange(commonCards);

		return draftPack;
	}

	public DraftPack GeneratePack(int setNumber)
	{
		var setId = BuiltInResources.SetsBySetNumber[setNumber].First();
		return GeneratePack(setId);
	}

	private List<CardTemplate> GenerateUniqueCards(List<CardTemplate> pool, int count, Random rng)
	{
		var list = new List<CardTemplate>();
		var hashset = new HashSet<int>();
		for (int i = 0; i < count; i++)
		{
			int id;
			do
			{
				id = rng.Next(0, pool.Count);
			} while (hashset.Contains(id));

			hashset.Add(id);
			list.Add(pool[id]);
		}

		return list;
	}
}