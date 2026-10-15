extern alias HexGame;
using System.Text.Json;
using Dingler.Game.Campaign;
using Dingler.Game.Services;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Network;
using HexGame::Game.Shared.Network.HConnect;
using HexGame::Game.Shared.Profile;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.AuctionHouse;

/// <summary>ServiceProfile custom request bridge for original HEX profile-deck templates.</summary>
[Authenticated]
public sealed class NetworkRequestHandler : IRequestHandler<Network.Request, Network.Response>
{
    private readonly CampaignRunStore _store;
    private readonly CollectionCacheService _collection;
    private readonly ILogger<NetworkRequestHandler>? _logger;

    public NetworkRequestHandler(CampaignRunStore store, CollectionCacheService collection,
        ILogger<NetworkRequestHandler>? logger = null)
    {
        _store = store;
        _collection = collection;
        _logger = logger;
    }

    public Network.Response HandleRequest(SessionContext context, Network.Request request)
    {
        if (request.Envelope is not { Length: > 0 })
            return EmptyResponse();

        try
        {
            using var doc = JsonDocument.Parse(request.Envelope);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("action", out var action)
                || action.ValueKind != JsonValueKind.String
                || action.GetString() != Network.ReqAction.PDeckTempSave)
                return EmptyResponse();

            return SaveProfileDeck(context, root);
        }
        catch (InvalidDataException ex)
        {
            _logger?.LogWarning("Campaign: rejected profile deck save for {user}: {reason}",
                context.UserName, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogWarning("Campaign: rejected profile deck request for {user}: {reason}",
                context.UserName, ex.Message);
        }
        catch (JsonException)
        {
            _logger?.LogWarning("Campaign: rejected malformed profile deck request for {user}: invalid JSON",
                context.UserName);
        }
        catch (FormatException)
        {
            _logger?.LogWarning("Campaign: rejected malformed profile deck request for {user}: invalid encoding",
                context.UserName);
        }
        catch (Exception ex)
        {
            // Untrusted profile-deck bytes must not terminate the client session.
            _logger?.LogError(ex, "Campaign: failed to process profile deck request for {user}",
                context.UserName);
        }

        return RejectedSaveResponse();
    }

    private Network.Response SaveProfileDeck(SessionContext context, JsonElement root)
    {
        if (!root.TryGetProperty("Template", out var payload)
            || payload.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("missing deck template bytes");

        var bytes = payload.GetBytesFromBase64();
        if (bytes.Length is 0 or > 131072)
            throw new InvalidDataException("deck template size outside 1..131072 bytes");

        var template = ProfileDeckTemplate.FromBytes(bytes);
        if (!template.Champ.IsValid)
            throw new InvalidDataException("invalid mercenary template ID");
        if (string.IsNullOrWhiteSpace(template.Name))
            throw new InvalidDataException("missing deck name");

        var cards = template.CardDesc.ToArray();
        if (cards.Any(static card => card.Count < 1))
            throw new InvalidDataException("non-positive card quantity");
        var totalCards = cards.Sum(static card => (long)card.Count);
        if (totalCards == 0)
            throw new InvalidDataException("empty deck");
        if (totalCards > 500)
            throw new InvalidDataException("deck exceeds 500 cards");

        var manager = HexGame::Singleton<TemplateManager>.Instance;
        if (!manager.Mercenaries.TryGetValue(template.Champ, out _)
            || !manager.InventoryItems.Values.OfType<InventoryMercenaryData>()
                .Any(item => item.MercenaryTemplate?.Id == template.Champ
                    && _collection.IsOwnedMercenary(context.ProfileId, item.Id)))
            throw new InvalidOperationException("Mercenary deck must belong to an owned original mercenary");
        if (cards.Any(card => !manager.Cards.ContainsKey(card.Temp)))
            throw new InvalidDataException("unknown card template");

        var ownedCards = _collection.CollectionIds.Values
            .GroupBy(static templateId => templateId)
            .ToDictionary(static group => group.Key, static group => group.Count());
        foreach (var group in cards.GroupBy(static card => card.Temp))
        {
            if (!ownedCards.TryGetValue(group.Key, out var count)
                || group.Sum(card => (long)card.Count) > count)
                throw new InvalidOperationException("Mercenary deck exceeds the player's card collection");
        }

        ulong existingId = 0;
        if (root.TryGetProperty("DeckTemplateID", out var id) && id.ValueKind is not JsonValueKind.Null
            and not JsonValueKind.Undefined && !id.TryGetUInt64(out existingId))
            throw new InvalidDataException("invalid profile deck ID");

        var saved = _store.SaveProfileDeck(context.ProfileId, existingId,
            template.Champ.ToString(), template.Name, bytes, context.CurrentCampaignChampionId);
        var native = new SavedProfileDeckTemplate
        {
            Id = saved.Id,
            Name = saved.Name,
            Comp = false,
            Data = saved.Data,
        };
        var envelope = EncData.Encode(native);
        _logger?.LogInformation("Campaign: saved mercenary profile deck {deck} for {user}, mercenary={merc}",
            saved.Id, context.UserName, template.Champ);
        return new Network.Response { Envelope = envelope };
    }

    private static Network.Response EmptyResponse() => new() { Envelope = new byte[1] };

    private static Network.Response RejectedSaveResponse() => new()
    {
        // The client only treats SavedProfileDeckTemplate as a successful save.
        // A native encoded string is safe to decode and cannot be mistaken for one.
        Envelope = EncData.Encode("Profile deck save rejected"),
    };
}
