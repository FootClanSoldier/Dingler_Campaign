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
    /// Exposes eight deterministic test champions through the normal profile stream
    /// until original AddChampion/profile persistence is implemented.
    /// </summary>
    public bool BootstrapChampion { get; init; }
    public string BootstrapChampionName { get; init; } = "CampaignTest";
    public int BootstrapChampionClass { get; init; } = 3;
    public int BootstrapChampionGender { get; init; } = 1;
}
