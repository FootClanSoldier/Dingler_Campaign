extern alias HexGame;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Draft;

public class DraftPack
{
	public List<CardTemplate> Cards { get; } = new();
}