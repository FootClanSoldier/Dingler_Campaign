extern alias HexGame;
using System.Text.Json.Nodes;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Campaign;

/// <summary>Persists cumulative champion XP and projects the resulting champion into campaign rewards.</summary>
internal static class CampaignChampionProgression
{
    private static readonly int[] OriginalLevelThresholds =
    [
        0, 1000, 2800, 5000, 8000, 12000, 17500, 25000,
        40000, 50000, 60000, 70000, 80000, 92000, 110000,
    ];

    public static void ApplyGrantedXp(CampaignChampionRun champion, JsonObject applied)
    {
        if (applied["Completed"] is not JsonArray completed)
            return;

        long earned = 0;
        foreach (var entry in completed.OfType<JsonObject>())
        {
            if (entry["ItemKind"]?.ToString() == "XP"
                && entry["ItemAction"]?.ToString() == "GRANT"
                && int.TryParse(entry["ItemQuantity"]?.ToString(), out var amount)
                && amount > 0)
                earned += amount;
        }

        if (earned == 0)
            return;

        champion.CurrentXP = (int)Math.Min(int.MaxValue, Math.Max(0L, champion.CurrentXP) + earned);
        champion.Level = LevelForCumulativeXp(champion.CurrentXP);

        // The client applies this authoritative champion delta before opening the
        // XP reward popup. Include the fields it copies into its existing champion.
        ((JsonArray)applied["Champions"]!).Add(new JsonObject
        {
            ["Champ"] = new JsonObject
            {
                ["Id"] = champion.ChampionId,
                ["Name"] = champion.Name,
                ["Level"] = champion.Level,
                ["CurrentXP"] = champion.CurrentXP,
                ["Race"] = champion.Race,
                ["ChampionClass"] = champion.ChampionClass,
                ["Gender"] = champion.Gender,
                ["PetName"] = champion.PetName,
                ["OwnerChampionId"] = 0,
                ["LastCampaignID"] = champion.CurrentCampaignId,
                ["LastDeckID"] = champion.LastDeckId,
                ["IsDeleted"] = false,
                ["LastRespec"] = 0,
                ["FreeRespec"] = 0,
            },
        });
    }

    private static int LevelForCumulativeXp(int xp)
    {
        // Prefer the original loaded HEX table; the embedded thresholds below
        // permit startup and tests when the template table is not yet available.
        try
        {
            var original = HexGame::Singleton<TemplateManager>.Instance.GetCampaignXpTable();
            if (original?.Table is { Count: > 0 } entries)
            {
                var level = 1;
                foreach (var entry in entries.OrderBy(item => item.Level))
                    if (entry.Level >= 1 && xp >= entry.LevelXpRequirement)
                        level = entry.Level;
                return level;
            }
        }
        catch (InvalidOperationException) { }
        catch (NullReferenceException) { }

        var fallbackLevel = 1;
        for (var i = 1; i < OriginalLevelThresholds.Length; i++)
            if (xp >= OriginalLevelThresholds[i])
                fallbackLevel = i + 1;
        return fallbackLevel;
    }
}
