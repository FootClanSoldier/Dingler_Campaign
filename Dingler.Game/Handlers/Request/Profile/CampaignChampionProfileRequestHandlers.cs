extern alias HexGame;
using Dingler.Game.Campaign;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.Network.Profile;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Profile;

/// <summary>
/// Profile compatibility for the temporary campaign bootstrap champion.
/// The client sends this after choosing/saving a PvE deck, before it launches ServiceCampaign.
/// The request expects no reply; persisting the raw deck instance id is enough for the next profile stream.
/// </summary>
[Authenticated]
public sealed class UpdateChampionDeckIdRequestHandler : IRequestHandler<UpdateChampionDeckIDRequestArgs>
{
    private readonly CampaignOptions _options;
    private readonly CampaignRunStore _store;
    private readonly ILogger<UpdateChampionDeckIdRequestHandler>? _logger;

    public UpdateChampionDeckIdRequestHandler(
        CampaignOptions options,
        CampaignRunStore store,
        ILogger<UpdateChampionDeckIdRequestHandler>? logger = null)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    public void HandleRequest(SessionContext context, UpdateChampionDeckIDRequestArgs request)
    {
        if (!_options.BootstrapChampion)
            return;

        var expectedChampionId = CampaignBootstrapChampion.ChampionId(context.ProfileId, _options.DefaultRace);
        var championId = expectedChampionId;
        if (CampaignChampionRequestReader.TryReadUInt64(
                request, out var requestedChampionId,
                "ChampionID", "ChampionId", "ChampionUID", "ChampionUid", "ChampID", "ChampId", "Champion"))
        {
            championId = NormalizeChampionId(requestedChampionId, expectedChampionId);
        }

        if (championId != expectedChampionId)
        {
            _logger?.LogWarning(
                "Campaign: {user} tried to update deck for champion {champion}; expected bootstrap champion {expected}",
                context.UserName, championId, expectedChampionId);
            return;
        }

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

        _store.SetChampionDeck(context.ProfileId, championId, _options.DefaultRace, deckId);
        _logger?.LogInformation(
            "Campaign: {user} selected PvE deck {deck} for champion {champion}",
            context.UserName, deckId, championId);
    }

    private static ulong NormalizeChampionId(ulong candidate, ulong expected)
    {
        if (candidate == expected)
            return candidate;

        // Tolerate a packed UID represented as UInt64 instead of the UID contract.
        return candidate > byte.MaxValue && (candidate >> 8) == expected ? expected : candidate;
    }

    private static ulong NormalizeDeckId(ulong candidate, SessionContext context)
    {
        if (candidate == 0 || context.Decks.ContainsKey(candidate))
            return candidate;

        // Same compatibility rule as above: deck UIDs use the raw instance id as their high bits.
        var unpacked = candidate >> 8;
        return unpacked != 0 && context.Decks.ContainsKey(unpacked) ? unpacked : candidate;
    }
}

/// <summary>
/// Stores the bootstrap champion's currently selected talents. The client sends this adjacent to
/// UpdateChampionDeckID while leaving the deck builder. No battle/talent rules are implemented here.
/// </summary>
[Authenticated]
public sealed class UpdateChampionTalentsRequestHandler : IRequestHandler<UpdateChampionTalentsRequestArgs>
{
    private readonly CampaignOptions _options;
    private readonly CampaignRunStore _store;
    private readonly ILogger<UpdateChampionTalentsRequestHandler>? _logger;

    public UpdateChampionTalentsRequestHandler(
        CampaignOptions options,
        CampaignRunStore store,
        ILogger<UpdateChampionTalentsRequestHandler>? logger = null)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    public void HandleRequest(SessionContext context, UpdateChampionTalentsRequestArgs request)
    {
        if (!_options.BootstrapChampion)
            return;

        var expectedChampionId = CampaignBootstrapChampion.ChampionId(context.ProfileId, _options.DefaultRace);
        var championId = expectedChampionId;
        if (CampaignChampionRequestReader.TryReadUInt64(
                request, out var requestedChampionId,
                "ChampionID", "ChampionId", "ChampionUID", "ChampionUid", "ChampID", "ChampId", "Champion"))
        {
            if (requestedChampionId == expectedChampionId)
                championId = requestedChampionId;
            else if (requestedChampionId > byte.MaxValue && (requestedChampionId >> 8) == expectedChampionId)
                championId = expectedChampionId;
            else
                championId = requestedChampionId;
        }

        if (championId != expectedChampionId)
        {
            _logger?.LogWarning(
                "Campaign: {user} tried to update talents for champion {champion}; expected bootstrap champion {expected}",
                context.UserName, championId, expectedChampionId);
            return;
        }

        if (!CampaignChampionRequestReader.TryReadTalentIds(request, out var talents))
        {
            _logger?.LogWarning(
                "Campaign: could not read talents from {requestType}; members: {members}",
                request.GetType().FullName, CampaignChampionRequestReader.DescribePublicMembers(request));
            return;
        }

        _store.SetChampionTalents(context.ProfileId, championId, _options.DefaultRace, talents);
        _logger?.LogInformation(
            "Campaign: {user} stored {count} talent(s) for champion {champion}",
            context.UserName, talents.Count, championId);
    }
}
