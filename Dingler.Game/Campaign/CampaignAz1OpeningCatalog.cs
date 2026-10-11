extern alias HexGame;

using System.Text.RegularExpressions;
using HexGame::Game.Shared;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Campaign;

/// <summary>
/// Resolves the AZ1 welcome conversation from the original loaded HEX client
/// templates. Authored Node 2 has a different quest hand-off per faction.
/// </summary>
public static class CampaignAz1OpeningCatalog
{
    private static readonly Regex NodeTwoName = new(
        @"^AZ1\s*-\s*Node\s*0*2(?:\s+[A-Z])?\s*-",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryGetWelcome(int race, out string conversationId, out string conversationName)
    {
        conversationId = string.Empty;
        conversationName = string.Empty;
        var ardent = race is >= 1 and <= 4;
        var npc = ardent ? "Belarius" : "Takumi";
        var faction = ardent ? "Ardent" : "Underworld";

        // The TemplateManager is populated from the player's installed HEX
        // gamedata before ServiceCampaign requests are processed.
        var templates = HexGame::Singleton<TemplateManager>.Instance.ConversationTemplates.Values;
        var candidates = templates
            .Where(template => template.m_Name is not null && NodeTwoName.IsMatch(template.m_Name))
            .Select(template => new
            {
                Template = template,
                Score = Score(template.m_Name ?? string.Empty, template.m_OwnerName ?? string.Empty, npc, faction),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Template.m_Name, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0 ||
            (candidates.Count > 1 && candidates[0].Score == candidates[1].Score
             && !candidates[0].Template.m_Id.Equals(candidates[1].Template.m_Id)))
            return false;

        conversationId = candidates[0].Template.m_Id.ToString();
        conversationName = candidates[0].Template.m_Name ?? string.Empty;
        return Guid.TryParse(conversationId, out var id) && id != Guid.Empty;
    }

    private static int Score(string name, string owner, string npc, string faction)
    {
        var npcMatch = string.Equals(owner, npc, StringComparison.OrdinalIgnoreCase)
            || name.Contains(npc, StringComparison.OrdinalIgnoreCase);
        var factionMatch = name.Contains(faction, StringComparison.OrdinalIgnoreCase)
            || (faction == "Ardent" && name.Contains("Aria", StringComparison.OrdinalIgnoreCase));
        if (!npcMatch && !factionMatch)
            return 0;
        // A named owner is the best original-data evidence that this is the
        // correct faction's conversation, not a similarly titled later quest.
        if (!string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, npc, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains("Quest End", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quest Completed", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Repeat", StringComparison.OrdinalIgnoreCase))
            return 0;
        var score = npcMatch ? 100 : 30;
        if (factionMatch)
            score += 25;
        if (name.Contains("First", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Welcome", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Step 1", StringComparison.OrdinalIgnoreCase))
            score += 10;
        return score;
    }
}
