extern alias HexGame;
using Dingler.Game.Campaign;
using Dingler.Game.Services;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Client.Network.Profile;
using HexGame::Game.Shared.Network.Profile;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Profile;

[Authenticated]
public sealed class DeleteChampionRequestHandler : IAsyncRequestHandler<DeleteChampionRequestArgs, DeleteChampionResponse>
{
    private readonly CampaignRunStore _store;
    private readonly CampaignBattleService _battles;
    private readonly DeckService _decks;
    private readonly ILogger<DeleteChampionRequestHandler>? _logger;

    public DeleteChampionRequestHandler(CampaignRunStore store, CampaignBattleService battles,
        DeckService decks, ILogger<DeleteChampionRequestHandler>? logger = null)
    {
        _store = store;
        _battles = battles;
        _decks = decks;
        _logger = logger;
    }

    public async Task<DeleteChampionResponse> HandleRequestAsync(
        SessionContext context, DeleteChampionRequestArgs request, CancellationToken token)
    {
        if (request.AuthID != context.AccountId ||
            request.ReckoningID.GetInstanceId() != context.ProfileId ||
            request.ChampionId == 0 ||
            !_store.TryGetChampionForDeletion(context.ProfileId, request.ChampionId, out var champion))
            return Failure("Champion is not owned by this profile");

        if (_battles.HasChampionBattle(context.ProfileId, champion.ChampionId))
            return Failure("Cannot delete a champion during a campaign battle");

        try
        {
            if (!_store.TryDeleteChampion(context.ProfileId, champion.ChampionId, out var deleted))
                return Failure("Champion is not available for deletion");

            // Keep a hidden, retryable deletion marker until all cleanup succeeds.
            if (context.CurrentCampaignChampionId == deleted.ChampionId)
                context.CurrentCampaignChampionId = 0;
            var removedDecks = await _decks.DeleteChampionDecksAsync(context, deleted.ChampionId)
                .ConfigureAwait(false);
            var removedTemplates = _store.RemoveUnreferencedProfileDecks(context.ProfileId, deleted.ChampionId);
            _store.PermanentlyDeleteChampion(context.ProfileId, deleted.ChampionId);

            _logger?.LogInformation(
                "Campaign: permanently deleted champion {champion}; removed {decks} PvE decks and {templates} unused mercenary templates",
                deleted.ChampionId, removedDecks.Count, removedTemplates.Count);

            return new DeleteChampionResponse
            {
                Error = EDeleteChampionError.Ok,
                ChampionBits = CampaignChampionProjection.CreateFromSaved(deleted),
            };
        }
        catch (Exception ex)
        {
            // The IsDeleted marker remains on disk after a cleanup failure. A repeat
            // of transaction 2035 can safely retry the idempotent cleanup steps.
            _logger?.LogError(ex, "Campaign: could not finish deletion of champion {champion}", request.ChampionId);
            return Failure("Could not finish champion deletion");
        }
    }

    private static DeleteChampionResponse Failure(string message) => new()
    {
        Error = EDeleteChampionError.InternalServerError,
        ErrorMessage = message,
    };
}
