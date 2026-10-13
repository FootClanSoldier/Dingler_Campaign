namespace Dingler.Game.Campaign;

/// <summary>
/// Evaluates nested All/Any groups against authoritative battle receipts.
/// A missing condition imposes no additional restriction. Matched card GUIDs
/// are returned so CapturedCard grants use the actual original card template.
/// </summary>
public static class CampaignRewardConditionEvaluator
{
    public sealed record Result(bool Satisfied, IReadOnlyList<string> MatchedCapturedTemplates);

    public static Result Evaluate(CampaignRewardCondition? condition,
        IReadOnlyList<Guid>? capturedTemplates)
    {
        if (condition is null)
            return new Result(true, Array.Empty<string>());

        if (condition.Type == "TamedCard")
        {
            var id = Guid.Parse(condition.TemplateGuid!);
            return capturedTemplates?.Contains(id) == true
                ? new Result(true, new[] { id.ToString("D") })
                : new Result(false, Array.Empty<string>());
        }

        var matches = new List<string>();
        var count = 0;
        foreach (var item in condition.Items!)
        {
            var result = Evaluate(item, capturedTemplates);
            if (result.Satisfied)
            {
                count++;
                matches.AddRange(result.MatchedCapturedTemplates);
            }
        }
        var satisfied = condition.Operator == "All"
            ? count == condition.Items.Count
            : count > 0;
        return new Result(satisfied, satisfied
            ? matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>());
    }

    public static IReadOnlyList<string> CaptureTemplateGuids(CampaignRewardCondition? condition)
    {
        if (condition is null)
            return Array.Empty<string>();
        if (condition.Type == "TamedCard")
            return new[] { Guid.Parse(condition.TemplateGuid!).ToString("D") };
        return condition.Items!.SelectMany(CaptureTemplateGuids)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    }

    public static void Validate(CampaignRewardCondition? condition, int depth = 0)
    {
        if (condition is null)
            return;
        if (depth > 8)
            throw new InvalidDataException("Campaign reward conditions are too deeply nested");
        if (condition.Type == "TamedCard")
        {
            if (condition.Operator is not null || condition.Items is not null
                || !Guid.TryParse(condition.TemplateGuid, out var id) || id == Guid.Empty)
                throw new InvalidDataException("Invalid TamedCard reward condition");
            condition.TemplateGuid = id.ToString("D");
            return;
        }
        if (condition.Type is not null || condition.Operator is not ("All" or "Any")
            || condition.TemplateGuid is not null || condition.Items is null
            || condition.Items.Count == 0)
            throw new InvalidDataException($"Unknown or incomplete reward condition: {condition.Type ?? condition.Operator}");
        foreach (var item in condition.Items)
            Validate(item ?? throw new InvalidDataException("Null campaign reward condition"), depth + 1);
    }
}
