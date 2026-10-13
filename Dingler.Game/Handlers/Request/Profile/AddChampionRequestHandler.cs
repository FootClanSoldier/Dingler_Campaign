extern alias HexGame;
using Dingler.Game.Campaign;
using Dingler.Game.Services;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Client.Network.Profile;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Network.Profile;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Profile;

[Authenticated]
public sealed class AddChampionRequestHandler : IAsyncRequestHandler<AddChampionRequestArgs, AddChampionResponse>
{
    private readonly CampaignRunStore _store;
    private readonly DeckService _deckService;
    private readonly ILogger<AddChampionRequestHandler>? _logger;

    public AddChampionRequestHandler(CampaignRunStore store,
        DeckService deckService, ILogger<AddChampionRequestHandler>? logger = null)
    {
        _store = store;
        _deckService = deckService;
        _logger = logger;
    }

    public async Task<AddChampionResponse> HandleRequestAsync(
        SessionContext context, AddChampionRequestArgs request, CancellationToken token)
    {
        if (request.AuthID != context.AccountId ||
            request.ReckoningID.GetInstanceId() != context.ProfileId ||
            request.OwnerChampionId != 0)
            return Failure(EAddChampionError.InternalServerError, "Invalid champion owner");

        var name = request.ChampionName?.Trim() ?? string.Empty;
        if (name.Length is < 4 or > 32 || name.Any(char.IsControl))
            return Failure(EAddChampionError.InvalidChampionName, "Champion name must contain 4–32 characters");
        if ((int)request.Race is < 1 or > 8 ||
            request.ChampionClass is not (EChampionClass.Cleric or EChampionClass.Warrior
                or EChampionClass.Mage or EChampionClass.Ranger) ||
            request.Gender is not (EGender.Male or EGender.Female) ||
            request.Race is (ERace.Dwarf or ERace.Vennen) && request.Gender != EGender.Male ||
            request.ChampionClass == EChampionClass.Ranger && string.IsNullOrWhiteSpace(request.PetName))
            return Failure(EAddChampionError.InternalServerError, "Unsupported champion selection");

        CampaignChampionRun? saved = null;
        HexGame::Game.Shared.Domain.deck_bits? deck = null;
        var committed = false;
        try
        {
            saved = _store.CreateChampion(context.ProfileId, name, (int)request.Race,
                (int)request.ChampionClass, (int)request.Gender,
                request.ChampionClass == EChampionClass.Ranger ? request.PetName.Trim() : string.Empty);
            deck = await _deckService.CreateChampionStarterDeckAsync(context, saved.ChampionId,
                name, request.Race).ConfigureAwait(false);
            // The client opens the campaign deck editor when a new champion has no selected deck.
            // DeckBits is still required in AddChampionResponse, but the client selects a deck on save.
            committed = true;
            _logger?.LogInformation("Campaign: created champion {champion} for profile {profile} race={race} class={class}",
                saved.ChampionId, context.ProfileId, request.Race, request.ChampionClass);
            return new AddChampionResponse
            {
                Error = EAddChampionError.Ok,
                ChampionBits = CampaignChampionProjection.CreateFromSaved(saved),
                DeckBits = deck,
            };
        }
        catch (ArgumentException ex) when (saved is null)
        {
            _logger?.LogWarning("Campaign: champion creation rejected: {reason}", ex.Message);
            return Failure(EAddChampionError.InvalidChampionName, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Campaign: champion creation failed for profile {profile}", context.ProfileId);
            if (!committed && deck is not null)
            {
                try { await _deckService.DiscardChampionStarterDeckAsync(context, deck.Id).ConfigureAwait(false); }
                catch (Exception cleanupError) { _logger?.LogError(cleanupError, "Campaign: starter deck rollback failed"); }
            }
            if (!committed && saved is not null)
            {
                try { _store.RollbackUninitializedChampion(context.ProfileId, saved.ChampionId); }
                catch (Exception cleanupError) { _logger?.LogError(cleanupError, "Campaign: champion rollback failed"); }
            }
            return Failure(EAddChampionError.InternalServerError, "Could not create champion");
        }
    }

    private static AddChampionResponse Failure(EAddChampionError error, string message) =>
        new() { Error = error, ErrorMessage = message };
}
