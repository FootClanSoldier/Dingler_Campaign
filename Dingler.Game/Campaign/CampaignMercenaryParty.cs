namespace Dingler.Game.Campaign;

/// <summary>A saved party member identifies an owned mercenary inventory template and its profile deck.</summary>
public sealed class CampaignMercenaryPartyMember
{
    public string MercenaryItemGuid { get; set; } = string.Empty;
    public ulong DeckTemplateId { get; set; }
    public int Upgrade { get; set; }
}
