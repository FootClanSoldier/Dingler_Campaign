using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dingler.Game.Campaign;

/// <summary>
/// One persisted campaign instance. Champion ownership/deck/talent data is stored
/// once by CampaignChampionRun and projected onto these ignored properties when a
/// record is handed to existing campaign/battle code.
/// </summary>
public sealed class CampaignRunRecord
{
    [JsonIgnore]
    public ulong ProfileId { get; set; }

    [JsonIgnore]
    public ulong ChampionId { get; set; }

    [JsonIgnore]
    public int Race { get; set; }

    public ulong CampaignId { get; set; }
    public string CampaignType { get; set; } = "PANORAMA";
    public string TemplateName { get; set; } = "AZ1";
    public bool Started { get; set; }

    [JsonIgnore]
    public ulong LastDeckId { get; set; }

    [JsonIgnore]
    public List<string> ChampionTalents { get; set; } = new();

    public JsonObject State { get; set; } = new();
}
