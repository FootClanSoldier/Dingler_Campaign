using System.Text.Json.Nodes;

namespace Dingler.Game.Campaign;

/// <summary>
/// Applies independent reward rules to completed campaign events. Gold remains
/// display-only; XP is committed by the campaign store with each reward receipt.
/// </summary>
public sealed class CampaignRewardService
{
    public JsonObject Apply(CampaignRunRecord run, string encounterGuid, bool won,
        IReadOnlyList<Guid>? capturedTemplates,
        Func<string, string, string, ulong?> capturedCardInstanceId,
        Func<string, string, bool> isKnownLootTemplate, out string summary)
    {
        if (!won || !CampaignRewardCatalog.TryGet(encounterGuid, out var definition))
        {
            summary = "none";
            return CampaignStateFactory.EmptyAppliedUpdates();
        }
        return ApplyRules(run, definition.Rewards, "Victory",
            $"{definition.EncounterGuid}:", capturedTemplates,
            (ruleId, template) => capturedCardInstanceId(definition.EncounterGuid, ruleId, template),
            isKnownLootTemplate, out summary);
    }

    /// <summary>
    /// Must be invoked inside the caller's atomic QUEST turn-in transaction,
    /// after the authored quest has genuinely reached the Complete state.
    /// </summary>
    public JsonObject ApplyQuestCompletion(CampaignRunRecord quest,
        Func<string, string, bool> isKnownLootTemplate, out string summary)
    {
        if (!string.Equals(quest.CampaignType, "QUEST", StringComparison.OrdinalIgnoreCase)
            || quest.State["Finished"] is null
            || !string.Equals(quest.State["FinishReason"]?.ToString(), "Complete", StringComparison.OrdinalIgnoreCase)
            || !CampaignRewardCatalog.TryGetQuest(quest.TemplateName, out var definition))
        {
            summary = "not catalogued";
            return CampaignStateFactory.EmptyAppliedUpdates();
        }
        return ApplyRules(quest, definition.Rewards, "QuestCompleted",
            $"quest:{definition.Quest.ToLowerInvariant()}:", null, null,
            isKnownLootTemplate, out summary);
    }

    private static JsonObject ApplyRules(CampaignRunRecord run,
        IReadOnlyList<CampaignRewardRule> rules, string trigger, string claimPrefix,
        IReadOnlyList<Guid>? capturedTemplates,
        Func<string, string, ulong?>? capturedCardInstanceId,
        Func<string, string, bool> isKnownLootTemplate, out string summary)
    {
        var applied = CampaignStateFactory.EmptyAppliedUpdates();
        run.RewardClaims ??= new Dictionary<string, string>();
        var reports = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Trigger != trigger)
                continue;
            var key = claimPrefix + rule.Id;
            if (rule.ClaimPolicy == "OncePerCampaign" && run.RewardClaims.ContainsKey(key))
                continue;
            var result = CampaignRewardConditionEvaluator.Evaluate(rule.Conditions, capturedTemplates);
            if (!result.Satisfied)
                continue;

            // Resolve popup card IDs before committing a once-only XP receipt,
            // so a compound reward is never displayed partially.
            var captured = new List<(string Template, ulong InstanceId)>();
            var failed = false;
            foreach (var grant in rule.Grants.Where(static grant => grant.Type == "CapturedCard"))
            {
                var template = result.MatchedCapturedTemplates.FirstOrDefault();
                var instanceId = template is not null
                    ? capturedCardInstanceId?.Invoke(rule.Id, template) : null;
                if (template is null || instanceId is null)
                {
                    failed = true;
                    break;
                }
                captured.Add((template, instanceId.Value));
            }
            // Validate templates before emitting any display reward or XP receipt.
            var pendingLoot = new List<CampaignRewardDisplayLoot>();
            for (var grantIndex = 0; grantIndex < rule.Grants.Count && !failed; grantIndex++)
            {
                var grant = rule.Grants[grantIndex];
                if (grant.Type is not ("Card" or "Item"))
                    continue;
                var template = grant.TemplateGuid!;
                if (!isKnownLootTemplate(grant.Type, template))
                {
                    failed = true;
                    break;
                }
                for (var unit = 0; unit < grant.Quantity!.Value; unit++)
                {
                    var loot = CampaignRewardDisplayLoot.Create(run, $"{key}:{grantIndex}",
                        grant.Type, template, unit);
                    if (pendingLoot.Any(existing => existing.InstanceId == loot.InstanceId))
                    {
                        failed = true;
                        break;
                    }
                    pendingLoot.Add(loot);
                }
            }
            if (failed)
                continue;

            foreach (var grant in rule.Grants)
            {
                switch (grant.Type)
                {
                    case "XP": AddCurrency(applied, "XP", grant.Amount!.Value); break;
                    case "Gold": AddCurrency(applied, "GOLD", grant.Amount!.Value); break;
                    case "CapturedCard":
                        var card = captured[0];
                        AddCapturedCard(applied, card.Template, card.InstanceId);
                        break;
                    case "Card":
                    case "Item":
                        // Display card/item grants without changing the profile collection.
                        break;
                }
            }
            foreach (var loot in pendingLoot)
            {
                loot.AddApplied(applied);
            }
            if (rule.ClaimPolicy == "OncePerCampaign")
                run.RewardClaims[key] = "claimed";
            reports.Add($"{rule.Id} ({rule.Evidence}): " + string.Join(", ", rule.Grants.Select(grant =>
                grant.Type == "CapturedCard" ? $"captured={captured[0].Template}" :
                    grant.Type is "Card" or "Item" ? $"{grant.Type}={grant.Quantity}x{grant.TemplateGuid}" :
                    $"{grant.Type}={grant.Amount}")));
        }
        // The client normally displays Completed actions in received order.
        // Apply one shared display order after independent reward rules resolve.
        CampaignRewardDisplayOrder.Apply(applied);
        summary = reports.Count == 0 ? "already claimed / condition unmet" : string.Join("; ", reports);
        return applied;
    }

    private static void AddCurrency(JsonObject applied, string kind, int amount)
    {
        ((JsonArray)applied["Completed"]!).Add(new JsonObject
        {
            ["ItemKind"] = kind,
            ["ItemQuantity"] = amount,
            ["ItemAction"] = "GRANT",
            ["ItemTemplate"] = string.Empty,
            ["RCode"] = kind,
        });
    }

    private static void AddCapturedCard(JsonObject applied, string template, ulong instanceId)
    {
        ((JsonArray)applied["Cards"]!).Add(new JsonObject
        {
            ["Card"] = new JsonObject
            {
                ["Id"] = instanceId,
                ["TemplateID"] = template,
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
            ["ItemTemplate"] = template,
            ["RCode"] = "CARD",
        });
    }
}
