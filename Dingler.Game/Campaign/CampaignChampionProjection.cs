extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;

namespace Dingler.Game.Campaign;

/// <summary>Projects a saved player-created champion into the original HEX profile and game domain.</summary>
public static class CampaignChampionProjection
{
    public static champion_bits CreateFromSaved(CampaignChampionRun saved)
    {
        var champion = new champion_bits
        {
            Id = saved.ChampionId,
            Name = saved.Name,
            Level = Math.Max(1, saved.Level),
            CurrentXP = Math.Max(0, saved.CurrentXP),
            Race = (ERace)saved.Race,
            ChampionClass = (EChampionClass)saved.ChampionClass,
            Gender = (EGender)saved.Gender,
            PetName = saved.PetName,
            OwnerChampionId = 0,
            ReckID = new UID(UID.Type.ServicePlayer, saved.ProfileId),
            LastCampaignID = saved.CurrentCampaignId,
            LastDeckID = saved.LastDeckId,
        };
        ApplyTalents(champion, saved.ChampionTalents);
        return champion;
    }

    private static void ApplyTalents(champion_bits champion, IEnumerable<string>? talentIds)
    {
        if (talentIds is null)
            return;

        // ChampionTalents is not exposed by every shipped champion_bits variant.
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
