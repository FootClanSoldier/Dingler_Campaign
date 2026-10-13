namespace Dingler.Game.Campaign;

/// <summary>Persistent campaign storage configuration.</summary>
public sealed class CampaignOptions
{
    public string StorePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "data", "campaign");
}
