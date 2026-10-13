extern alias HexGame;
using HexGame::Game.Shared.Domain;

namespace Dingler.Game.Draft;

public sealed class DraftSeat
{
	public string Name { get; }
	public ulong ProfileId { get; }
	public List<card_instance_bits> Collection { get; } = new();
	public List<card_instance_bits> CurrentPack { get; set; } = new();

	public DraftSeat(string name, ulong profileId)
	{
		Name = name;
		ProfileId = profileId;
	}
}
