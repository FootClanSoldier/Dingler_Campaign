extern alias HexGame;

using Dingler.Game.GameObjects;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Games;

/// <summary>
/// Standard PvE campaign bootstrap. Unlike Frost Ring, campaign encounters keep their native
/// IsEncounter | IsPvE flags so the original HEX authoritative session can consume SceneTemplateId
/// and perform its own PvE encounter/deck initialization.
/// </summary>
public sealed partial class HexRulesEngine
{
    private UID _campaignHumanId = UID.Invalid;
    private champion_bits? _campaignChampion;
    private deck_bits? _campaignDeck;
    private string? _campaignKeepName;

    internal void ConfigureCampaignPlayer(UID humanId, champion_bits champion, deck_bits deck, string keepName)
    {
        _campaignHumanId = humanId;
        _campaignChampion = champion;
        _campaignDeck = deck;
        _campaignKeepName = keepName;
    }

    internal bool TryGetCampaignDeckAndChampInfo(
        UID player,
        out champion_bits? champ,
        out deck_bits? deck,
        out string? keepName,
        out Dictionary<string, int>? dungeonIntTacData,
        out List<ResourceId>? partyMembers)
    {
        if (_campaignChampion is null || _campaignDeck is null || !player.Equals(_campaignHumanId))
        {
            champ = null;
            deck = null;
            keepName = null;
            dungeonIntTacData = null;
            partyMembers = null;
            return false;
        }

        champ = _campaignChampion;
        deck = _campaignDeck;
        keepName = _campaignKeepName ?? string.Empty;
        dungeonIntTacData = new Dictionary<string, int>();
        partyMembers = new List<ResourceId>();
        _logger?.LogInformation(
            "Campaign engine {Session}: original PvE initializer requested champion {Champion} and deck {Deck}",
            m_SessionId, champ.Id, deck.Id);
        return true;
    }

    /// <summary>
    /// Let the original HEX PvE initializer consume the campaign scene and the champion/deck supplied
    /// through HasDeckAndChampInfo. The local AI seat is attached before this call, so all initial
    /// GameStarted/player/option events are captured for the original AI just as in Frost Ring.
    /// </summary>
    internal void StartCampaignGame(TrackedPlayer human, TrackedPlayer ai)
    {
        if (!InitializeGame())
            throw new InvalidOperationException("campaign battle failed to initialize through original PvE encounter setup");

        if (human.m_ChampionCard is null)
            throw new InvalidOperationException("original PvE encounter setup did not load the campaign champion/deck");

        if (ai.m_ChampionCard is null)
            throw new InvalidOperationException("original PvE encounter setup did not load the encounter AI/deck from SceneTemplateId");

        _logger?.LogInformation(
            "Campaign engine {Session}: original PvE initializer loaded player {PlayerChampion} and AI {AiChampion}",
            m_SessionId, human.m_ChampionCard.GetName(), ai.m_ChampionCard.GetName());

        if (CurrentTurnPhase == ETurnPhases.NotPlaying)
            CheckStartGameMethod.Invoke(this, null);

        if (CurrentTurnPhase == ETurnPhases.NotPlaying)
            throw new InvalidOperationException("campaign battle initialized but did not start");
    }
}
