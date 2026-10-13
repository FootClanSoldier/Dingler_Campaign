namespace Dingler.Game.Campaign;

/// <summary>
/// AZ0 starter-panorama identities extracted from the original HEX client.
/// </summary>
public sealed record CampaignRaceConfig(
    int Race,
    string Name,
    string Bundle,
    string Prefab,
    string IntroNpc,
    string TrainerNpc,
    string QuestNpc,
    string TrainingNode,
    string IntroConversation,
    string BattleConversation,
    string TrainingSuccessConversation,
    string TrainingFailConversation,
    string QuestConversation,
    string TransitionConversation,
    string TrainingEncounter,
    string AiChampionGuid,
    string Gameboard,
    string AiPersonality = "Comfortable");
