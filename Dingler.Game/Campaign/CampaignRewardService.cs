using System.Text.Json.Nodes;

namespace Dingler.Game.Campaign;

/// <summary>
/// Applies independent reward rules to completed campaign events. Gold and XP
/// are presentation grants; native captured cards also create profile receipts.
/// </summary>
public sealed class CampaignRewardService
{
    public JsonObject Apply(CampaignRunRecord run, string encounterGuid, bool won,
        IReadOnlyList<Guid>? capturedTemplates,
        Func<string, string, string, ulong?> capturedCardInstanceId,
        out string summary)
    {
        if (!won || !CampaignRewardCatalog.TryGet(encounterGuid, out var definition))
        {
            summary = "none";
            return CampaignStateFactory.EmptyAppliedUpdates();
        }
        return ApplyRules(run, definition.Rewards, "Victory",
            $"{definition.EncounterGuid}:", capturedTemplates,
            (ruleId, template) => capturedCardInstanceId(definition.EncounterGuid, ruleId, template),
            out summary);
    }

    /// <summary>
    /// Must be invoked inside the caller's atomic QUEST turn-in transaction,
    /// after the authored quest has genuinely reached the Complete state.
    /// </summary>
    public JsonObject ApplyQuestCompletion(CampaignRunRecord quest, out string summary)
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
            $"quest:{definition.Quest.ToLowerInvariant()}:", null, null, out summary);
    }

    private static JsonObject ApplyRules(CampaignRunRecord run,
        IReadOnlyList<CampaignRewardRule> rules, string trigger, string claimPrefix,
        IReadOnlyList<Guid>? capturedTemplates,
        Func<string, string, ulong?>? capturedCardInstanceId,
        out string summary)
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

            // Resolve card instance IDs before emitting any grant or committing
            // a receipt, so an unavailable profile collection cannot create a
            // partial payout for a compound reward rule.
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
                }
            }
            if (rule.ClaimPolicy == "OncePerCampaign")
                run.RewardClaims[key] = captured.Count > 0 ? captured[0].Template : "claimed";
            reports.Add($"{rule.Id} ({rule.Evidence}): " + string.Join(", ", rule.Grants.Select(grant =>
                grant.Type == "CapturedCard" ? $"captured={captured[0].Template}" : $"{grant.Type}={grant.Amount}")));
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
