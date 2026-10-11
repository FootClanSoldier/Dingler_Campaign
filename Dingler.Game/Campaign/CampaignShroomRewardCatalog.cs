extern alias HexGame;

using System.Text.Json.Nodes;
using HexGame::Game.Shared;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Campaign;

/// <summary>
/// Interim AZ1 Shroom Haus card offers from IanUtley's client-data seed script.
/// These are reference-server choices, not recovered original HEX rewards.
/// </summary>
public static class CampaignShroomRewardCatalog
{
    private static readonly (string Name, string Id)[] Offers =
    [
        ("Builder Bot", "85112245-7aed-4a5b-9de9-3c262f11168a"),
        ("Deployment Orders", "fa7414ab-01d2-484f-8bab-68a03d9cce96"),
        ("Tricerobot", "5793dc95-b2e3-40d7-8010-5507d07f5328"),
    ];

    public static IReadOnlyList<string> ChoiceIds => Offers.Select(static offer => offer.Id).ToArray();

    public static bool IsChoice(string? guid) =>
        !string.IsNullOrWhiteSpace(guid) && Offers.Any(offer =>
            string.Equals(offer.Id, guid, StringComparison.OrdinalIgnoreCase));

    public static bool TryGetVerifiedClientTemplates(out IReadOnlyList<string> choices)
    {
        choices = Array.Empty<string>();
        var cards = HexGame::Singleton<TemplateManager>.Instance.Cards;
        foreach (var offer in Offers)
        {
            if (!cards.TryGetValue(new ResourceId(offer.Id), out var card) || !card.IsOwnable())
                return false;
        }
        choices = ChoiceIds;
        return true;
    }

    public static JsonObject BuildAppliedCard(string templateId, ulong instanceId)
    {
        var applied = CampaignStateFactory.EmptyAppliedUpdates();
        ((JsonArray)applied["Cards"]!).Add(new JsonObject
        {
            ["Card"] = new JsonObject
            {
                ["Id"] = instanceId,
                ["TemplateID"] = templateId,
                ["CardStats"] = new JsonObject(),
                ["IsFoil"] = false,
                ["IsExtended"] = false,
                ["SocketedGems"] = 0,
                ["IsNotTradeable"] = false,
                ["EscrowStatus"] = "Clean",
            },
        });
        ((JsonArray)applied["Completed"]!).Add(new JsonObject
        {
            ["ItemKind"] = "BOACARD",
            ["ItemQuantity"] = 1,
            ["ItemAction"] = "GRANT",
            ["ItemTemplate"] = templateId,
            ["RCode"] = "CARD",
        });
        return applied;
    }
}
