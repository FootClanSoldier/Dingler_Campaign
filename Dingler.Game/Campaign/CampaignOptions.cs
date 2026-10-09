namespace Dingler.Game.Campaign;

/// <summary>
/// Campaign bootstrap settings. DefaultRace follows HEX's ERace values:
/// 1 Human, 2 Elf, 3 Coyotle, 4 Orc, 5 Dwarf, 6 Shin'hare, 7 Vennen, 8 Necrotic.
/// </summary>
public sealed class CampaignOptions
{
    public string StorePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "data", "campaign");
    public int DefaultRace { get; init; } = 1;

    /// <summary>
    /// Temporary vertical-slice aid. Current Dingler does not persist/stream PvE champions yet.
    /// When true, CollectionCacheService can expose one deterministic champion in reckoning_bits.
    /// </summary>
    public bool BootstrapChampion { get; init; }
    public string BootstrapChampionName { get; init; } = "CampaignTest";
    public int BootstrapChampionClass { get; init; } = 3;
    public int BootstrapChampionGender { get; init; } = 1;
}
