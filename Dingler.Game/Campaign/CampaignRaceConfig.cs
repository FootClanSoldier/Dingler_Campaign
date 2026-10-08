namespace Dingler.Game.Campaign;

/// <summary>
/// Client-authored AZ0 starter-panorama identities recovered from HEX data/client behavior.
/// Keeping them data-like here makes it easy to move them to extracted metadata later.
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
