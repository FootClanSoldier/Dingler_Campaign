extern alias HexGame;
using Dingler.Game.Campaign;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.Network.Profile;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Profile;

/// <summary>
/// Persists deck selection for player-created campaign champions.
/// The client sends this after choosing/saving a PvE deck, before it launches ServiceCampaign.
/// The request expects no reply; persisting the raw deck instance id is enough for the next profile stream.
/// </summary>
[Authenticated]
public sealed class UpdateChampionDeckIdRequestHandler : IRequestHandler<UpdateChampionDeckIDRequestArgs>
{
    private readonly CampaignRunStore _store;
    private readonly ILogger<UpdateChampionDeckIdRequestHandler>? _logger;

    public UpdateChampionDeckIdRequestHandler(
        CampaignRunStore store,
        ILogger<UpdateChampionDeckIdRequestHandler>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public void HandleRequest(SessionContext context, UpdateChampionDeckIDRequestArgs request)
    {
        if (!CampaignChampionRequestReader.TryReadUInt64(
                request, out var requestedChampionId,
                "ChampionID", "ChampionId", "ChampionUID", "ChampionUid", "ChampID", "ChampId", "Champion"))
        {
            _logger?.LogWarning(
                "Campaign: {user} sent {requestType} without a champion ID; members: {members}",
                context.UserName, request.GetType().Name,
                CampaignChampionRequestReader.DescribePublicMembers(request));
            return;
        }

        if (!_store.TryGetOwnedChampion(context.ProfileId, requestedChampionId,
                out var owner) &&
            !_store.TryGetOwnedChampion(context.ProfileId, requestedChampionId >> 8,
                out owner))
        {
            _logger?.LogWarning(
                "Campaign: {user} tried to update deck for unknown or unowned champion {champion}",
                context.UserName, requestedChampionId);
            return;
        }
        var championId = owner.ChampionId;
        var race = owner.Race;

        if (!CampaignChampionRequestReader.TryReadUInt64(
                request, out var deckId,
                "DeckID", "DeckId", "NewDeckID", "NewDeckId", "SelectedDeckID", "SelectedDeckId",
                "LastDeckID", "LastDeckId", "Deck"))
        {
            _logger?.LogWarning(
                "Campaign: could not read deck id from {requestType}; members: {members}",
                request.GetType().FullName, CampaignChampionRequestReader.DescribePublicMembers(request));
            return;
        }

        deckId = NormalizeDeckId(deckId, context);
        if (deckId != 0)
        {
            if (!context.Decks.TryGetValue(deckId, out var deck))
            {
                _logger?.LogWarning(
                    "Campaign: {user} selected unknown PvE deck {deck} for champion {champion}",
                    context.UserName, deckId, championId);
                return;
            }

            if (deck.PVEChampionId != 0 && deck.PVEChampionId != championId)
            {
                _logger?.LogWarning(
                    "Campaign: {user}'s deck {deck} belongs to PvE champion {deckChampion}, not {champion}",
                    context.UserName, deckId, deck.PVEChampionId, championId);
                return;
            }
        }

        _store.SetChampionDeck(context.ProfileId, championId, race, deckId);
        _logger?.LogInformation(
            "Campaign: {user} selected PvE deck {deck} for champion {champion}",
            context.UserName, deckId, championId);
    }

    private static ulong NormalizeDeckId(ulong candidate, SessionContext context)
    {
        if (candidate == 0 || context.Decks.ContainsKey(candidate))
            return candidate;

        // A packed HEX UID stores the deck instance ID in its upper 56 bits.
        var unpacked = candidate >> 8;
        return unpacked != 0 && context.Decks.ContainsKey(unpacked) ? unpacked : candidate;
    }
}

/// <summary>
/// Stores the selected campaign champion's talents. The client sends this adjacent to
/// UpdateChampionDeckID while leaving the deck builder. No battle/talent rules are implemented here.
/// </summary>
[Authenticated]
public sealed class UpdateChampionTalentsRequestHandler : IRequestHandler<UpdateChampionTalentsRequestArgs>
{
    private readonly CampaignRunStore _store;
    private readonly ILogger<UpdateChampionTalentsRequestHandler>? _logger;

    public UpdateChampionTalentsRequestHandler(
        CampaignRunStore store,
        ILogger<UpdateChampionTalentsRequestHandler>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public void HandleRequest(SessionContext context, UpdateChampionTalentsRequestArgs request)
    {
        if (!CampaignChampionRequestReader.TryReadUInt64(
                request, out var requestedChampionId,
                "ChampionID", "ChampionId", "ChampionUID", "ChampionUid", "ChampID", "ChampId", "Champion"))
        {
            _logger?.LogWarning(
                "Campaign: {user} sent {requestType} without a champion ID; members: {members}",
                context.UserName, request.GetType().Name,
                CampaignChampionRequestReader.DescribePublicMembers(request));
            return;
        }

        if (!_store.TryGetOwnedChampion(context.ProfileId, requestedChampionId,
                out var owner) &&
            !_store.TryGetOwnedChampion(context.ProfileId, requestedChampionId >> 8,
                out owner))
        {
            _logger?.LogWarning(
                "Campaign: {user} tried to update talents for unknown or unowned champion {champion}",
                context.UserName, requestedChampionId);
            return;
        }
        var championId = owner.ChampionId;
        var race = owner.Race;

        if (!CampaignChampionRequestReader.TryReadTalentIds(request, out var talents))
        {
            _logger?.LogWarning(
                "Campaign: could not read talents from {requestType}; members: {members}",
                request.GetType().FullName, CampaignChampionRequestReader.DescribePublicMembers(request));
            return;
        }

        _store.SetChampionTalents(context.ProfileId, championId, race, talents);
        _logger?.LogInformation(
            "Campaign: {user} stored {count} talent(s) for champion {champion}",
            context.UserName, talents.Count, championId);
    }
}
