extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;

namespace Dingler.Game.Campaign;

/// <summary>
/// Optional test-only champion used to expose the normal PvE UI path while Dingler's
/// real AddChampion/profile persistence is still unimplemented.
/// </summary>
public static class CampaignBootstrapChampion
{
    public static ulong ChampionId(ulong profileId, int race)
    {
        // Stable raw champion_bits.Id. The low-level UID wrapping is performed by the client.
        return unchecked(profileId * 1000UL + (ulong)Math.Clamp(race, 1, 8));
    }

    public static champion_bits Create(
        ulong profileId,
        CampaignOptions options,
        ulong lastCampaignId = 0,
        ulong lastDeckId = 0,
        IEnumerable<string>? championTalents = null)
    {
        var race = Math.Clamp(options.DefaultRace, 1, 8);
        var champion = new champion_bits
        {
            Name = options.BootstrapChampionName,
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
        // Avoid making Phase 1.1 depend on a compile-time ChampionTalents property while
        // still populating it when this client's contract exposes List<ResourceId>.
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
