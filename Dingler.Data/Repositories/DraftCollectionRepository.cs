using Dingler.Data.Context;
using Dingler.Data.Entities.GameData;
using Dingler.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dingler.Data.Repositories
{
    public sealed class DraftCollectionRepository : BaseRepository<GameDataContext>
    {
        public DraftCollectionRepository(IDbContextFactory<GameDataContext> factory, SqliteWriter<GameDataContext> writer)
            : base(factory, writer)
        { }

        public async Task<List<DraftCollection>> GetByTournamentAsync(int tournamentId)
        {
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            return await context.DraftCollections
                .Where(d => d.TournamentId == tournamentId)
                .ToListAsync()
                .ConfigureAwait(false);
        }

        public async Task<DraftCollection?> GetByTournamentAndPlayerAsync(int tournamentId, ulong playerProfileId)
        {
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            return await context.DraftCollections
                .Where(d => d.TournamentId == tournamentId && d.PlayerProfileId == playerProfileId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
        }

        public Task UpsertAsync(DraftCollection collection)
        {
            return EnqueueWriteAsync(async context =>
            {
                var existing = await context.DraftCollections
                    .Where(d => d.TournamentId == collection.TournamentId && d.PlayerProfileId == collection.PlayerProfileId)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);

                if (existing is null)
                {
                    await context.DraftCollections.AddAsync(collection).ConfigureAwait(false);
                }
                else
                {
                    if (collection.CardsJson is not null)
                        existing.CardsJson = collection.CardsJson;

                    if (collection.DeckJson is not null)
                        existing.DeckJson = collection.DeckJson;

                    context.DraftCollections.Update(existing);
                }
            });
        }
    }
}
