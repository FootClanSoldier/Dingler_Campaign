using System.Text.Json.Nodes;

namespace Dingler.Game.Campaign;

/// <summary>
/// Small persistent boundary for the first campaign vertical slice.
/// The live client-facing GameplayState is deliberately retained as JSON because
/// ServiceCampaign itself transports JSON inside CampSysGeneral.Envelope.
/// </summary>
public sealed class CampaignRunRecord
{
    public ulong ProfileId { get; set; }
    public ulong ChampionId { get; set; }
    public ulong CampaignId { get; set; }
    public int Race { get; set; }
    public string CampaignType { get; set; } = "PANORAMA";
    public string TemplateName { get; set; } = "AZ1";
    public bool Started { get; set; }

    // Phase 1.1 profile compatibility: persist the client-selected PvE deck and talents
    // for the temporary bootstrap champion until Dingler has first-class champion persistence.
    public ulong LastDeckId { get; set; }
    public List<string> ChampionTalents { get; set; } = new();

    public JsonObject State { get; set; } = new();
}
