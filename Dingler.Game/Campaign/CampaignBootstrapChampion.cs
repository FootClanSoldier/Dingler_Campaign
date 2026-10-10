extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;

namespace Dingler.Game.Campaign;

/// <summary>
/// Test-only champions used to expose the normal PvE UI path while Dingler's
/// real AddChampion/profile persistence is still unimplemented.
/// </summary>
public static class CampaignBootstrapChampion
{
    public static ulong ChampionId(ulong profileId, int race)
    {
        // Stable raw champion_bits.Id. The low-level UID wrapping is performed by the client.
        return unchecked(profileId * 1000UL + (ulong)Math.Clamp(race, 1, 8));
    }

    private static readonly string[] RaceNames =
    [
        "", "Human", "Elf", "Coyotle", "Orc", "Dwarf", "Shinhare", "Vennen", "Necrotic",
    ];

    // Bootstrap champion IDs are derived from the profile ID and HEX's eight ERace values.
    // Accept the raw ID and the packed UID form used by some profile requests.
    public static bool TryResolveChampionId(
        ulong profileId, ulong candidate, out ulong championId, out int race)
    {
        for (var value = 1; value <= 8; value++)
        {
            var expected = ChampionId(profileId, value);
            if (candidate != expected && (candidate <= byte.MaxValue || (candidate >> 8) != expected))
                continue;

            championId = expected;
            race = value;
            return true;
        }

        championId = 0;
        race = 0;
        return false;
    }

    public static champion_bits Create(
        ulong profileId,
        CampaignOptions options,
        int race,
        ulong lastCampaignId = 0,
        ulong lastDeckId = 0,
        IEnumerable<string>? championTalents = null)
    {
        race = Math.Clamp(race, 1, 8);
        var champion = new champion_bits
        {
            Name = race == 1
                ? options.BootstrapChampionName
                : options.BootstrapChampionName + RaceNames[race],
            Id = ChampionId(profileId, race),
            Level = 1,
            CurrentXP = 0,
            ChampionClass = (EChampionClass)options.BootstrapChampionClass,
            Race = (ERace)race,
            Gender = (EGender)options.BootstrapChampionGender,
            OwnerChampionId = 0,
            LastCampaignID = lastCampaignId,
            LastDeckID = lastDeckId,
            PetName = string.Empty,
        };

        ApplyTalents(champion, championTalents);
        return champion;
    }

    private static void ApplyTalents(champion_bits champion, IEnumerable<string>? talentIds)
    {
        if (talentIds is null)
            return;

        // The exact champion_bits surface differs between shipped client assemblies.
        // Avoid a compile-time dependency on ChampionTalents because the exact champion_bits
        // surface differs between shipped client assemblies; populate it when exposed.
        var property = typeof(champion_bits).GetProperty("ChampionTalents");
        if (property?.CanWrite != true)
            return;

        var talents = new List<ResourceId>();
        foreach (var value in talentIds)
        {
            if (Guid.TryParse(value, out var guid))
                talents.Add(new ResourceId(guid));
        }

        if (property.PropertyType.IsAssignableFrom(talents.GetType()))
            property.SetValue(champion, talents);
    }
}
