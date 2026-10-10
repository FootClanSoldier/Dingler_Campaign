namespace Dingler.Game.Campaign;

/// <summary>
/// Persisted owner for one PvE champion. Champion-owned deck/talent data and the
/// currently rendered scene live here, while PANORAMA/QUEST/DUNGEON instances keep
/// their own stable campaign identities in <see cref="Campaigns"/>.
/// </summary>
public sealed class CampaignChampionRun
{
    public int FormatVersion { get; set; } = 2;
    public ulong ProfileId { get; set; }
    public ulong ChampionId { get; set; }
    public int Race { get; set; }
    public ulong LastDeckId { get; set; }
    public List<string> ChampionTalents { get; set; } = new();
    public ulong CurrentCampaignId { get; set; }
    public List<CampaignRunRecord> Campaigns { get; set; } = new();
}
