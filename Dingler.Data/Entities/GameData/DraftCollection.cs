namespace Dingler.Data.Entities.GameData;

public class DraftCollection
{
    public int Id { get; set; }

    public int TournamentId { get; set; }

    public ulong PlayerProfileId { get; set; }

    public string? CardsJson { get; set; }

    public string? DeckJson { get; set; }
}
