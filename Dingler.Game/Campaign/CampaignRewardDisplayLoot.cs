using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Dingler.Game.Campaign;

/// <summary>Transient card/item reward for the client popup; never added to persistent ownership.</summary>
public sealed class CampaignRewardDisplayLoot
{
    public string ClaimKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string TemplateGuid { get; set; } = string.Empty;
    public ulong InstanceId { get; set; }

    public static CampaignRewardDisplayLoot Create(CampaignRunRecord run, string claimKey,
        string kind, string templateGuid, int unitIndex)
    {
        // Deterministic client-visible reward IDs remain within HEX's 56-bit instance range.
        // They do not represent ownership in the profile collection.
        var key = $"campaign-loot-v1|{run.ProfileId}|{run.ChampionId}|{run.CampaignId}|{claimKey}|{unitIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var id = 0x0040_0000_0000_0000UL |
            (BinaryPrimitives.ReadUInt64LittleEndian(hash) & 0x003F_FFFF_FFFF_FFFFUL);
        return new CampaignRewardDisplayLoot
        {
            ClaimKey = $"{claimKey}:{unitIndex}",
            Kind = kind,
            TemplateGuid = templateGuid,
            InstanceId = id,
        };
    }

    public void AddApplied(JsonObject applied)
    {
        if (Kind == "Card")
        {
            ((JsonArray)applied["Cards"]!).Add(new JsonObject
            {
                ["Card"] = new JsonObject
                {
                    ["Id"] = InstanceId,
                    ["TemplateID"] = TemplateGuid,
                    ["CardStats"] = new JsonObject(),
                    ["IsFoil"] = false,
                    ["IsExtended"] = false,
                    ["SocketedGems"] = 0,
                    ["IsNotTradeable"] = false,
                    ["EscrowStatus"] = "Clean",
                },
            });
        }
        else if (Kind == "Item")
        {
            ((JsonArray)applied["Items"]!).Add(new JsonObject
            {
                ["Item"] = new JsonObject
                {
                    ["Id"] = InstanceId,
                    ["TemplateID"] = TemplateGuid,
                    ["BoundToProfile"] = true,
                    ["ItemQuantity"] = 1,
                    ["EscrowStatus"] = "Clean",
                },
            });
        }
        else
        {
            throw new InvalidOperationException($"Unsupported campaign loot kind: {Kind}");
        }

        ((JsonArray)applied["Completed"]!).Add(new JsonObject
        {
            ["ItemKind"] = Kind == "Card" ? "BOACARD" : "BOAITEM",
            ["ItemQuantity"] = 1,
            ["ItemAction"] = "GRANT",
            ["ItemTemplate"] = TemplateGuid,
            ["RCode"] = Kind == "Card" ? "CARD" : "ITEM",
        });
    }
}
