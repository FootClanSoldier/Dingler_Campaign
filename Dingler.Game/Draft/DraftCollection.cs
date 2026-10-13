extern alias HexGame;
using System.Text.Json;
using Dingler.Game.Domain;
using HexGame::Game.Shared.Domain;

namespace Dingler.Game.Draft;

public static class DraftCollection
{
	public static string SerializeCards(IEnumerable<card_instance_bits> cards)
	{
		var bits = cards.Select(c => new DinglerCardBits
		{
			Id = c.Id,
			TemplateId = c.TemplateID,
			IsFoil = c.IsFoil,
			IsExtended = c.IsExtended,
			SocketedGems = c.SocketedGems,
			IsNotTradeable = c.IsNotTradeable,
			EscrowStatus = c.EscrowStatus
		}).ToList();

		return JsonSerializer.Serialize(bits);
	}

	public static List<card_instance_bits> DeserializeCards(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return new List<card_instance_bits>();

		var bits = JsonSerializer.Deserialize<List<DinglerCardBits>>(json);

		return bits is null
			? new List<card_instance_bits>()
			: bits.Select(ToCardBits).ToList();
	}

	public static string SerializeDeck(deck_bits deck)
	{
		return JsonSerializer.Serialize(new DinglerDeckBits(deck));
	}

	public static deck_bits? DeserializeDeck(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return null;

		var bits = JsonSerializer.Deserialize<DinglerDeckBits>(json);

		return bits?.ToDeckBits();
	}

	public static deck_bits ToPoolDeck(IEnumerable<card_instance_bits> cards, ulong playerId)
	{
		return new deck_bits
		{
			Id = 0,
			player_id = playerId,
			CardsInDeck = new List<card_instance_bits>(),
			CardsInSideboard = new List<card_instance_bits>(cards)
		};
	}

	private static card_instance_bits ToCardBits(DinglerCardBits bits)
	{
		return new card_instance_bits
		{
			Id = bits.Id,
			TemplateID = bits.TemplateId,
			IsFoil = bits.IsFoil,
			IsExtended = bits.IsExtended,
			SocketedGems = bits.SocketedGems,
			IsNotTradeable = bits.IsNotTradeable,
			EscrowStatus = bits.EscrowStatus
		};
	}
}
