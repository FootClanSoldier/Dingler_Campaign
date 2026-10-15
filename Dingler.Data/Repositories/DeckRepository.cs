using Dingler.Data.Context;
using System.Text.Json;
using Dingler.Data.Entities.GameData;
using Dingler.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dingler.Data.Repositories
{
    public sealed class DeckRepository : BaseRepository<GameDataContext>
    {
        public DeckRepository(IDbContextFactory<GameDataContext> factory, SqliteWriter<GameDataContext> writer)
            : base(factory, writer)
        { }

        public async Task<Deck?> GetDeckById(int id)
        {
            await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

            return await context.Decks
                .Where(d => d.Id == id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
        }

        public async Task<List<Deck>> GetAllDecksOwnedByPlayerIdAsync(ulong id)
        {
            await using var context = await _factory.CreateDbContextAsync()
                .ConfigureAwait(false);

            return await context.Decks
                .Where(d => d.PlayerProfileId == id)
                .ToListAsync()
                .ConfigureAwait(false);
        }

        public Task<Deck> CreateDeckAsync(Deck deck)
        {
            return EnqueueWriteAsync(async context =>
            {
                await context.Decks.AddAsync(deck);
                return deck;
            });
        }

        public Task UpdateDeckAsync(Deck deck)
        {
            return EnqueueWriteAsync(context =>
            {
                context.Decks.Update(deck);
                return Task.CompletedTask;
            });
        }

        /// <summary>Delete only PvE decks explicitly bound to this profile's deleted champion.</summary>
        public Task<List<ulong>> RemoveChampionDecksAsync(ulong profileId, ulong championId)
        {
            return EnqueueWriteAsync<List<ulong>>(async context =>
            {
                var decks = await context.Decks.Where(deck => deck.PlayerProfileId == profileId
                    && deck.DeckBitsJson != null).ToListAsync().ConfigureAwait(false);
                var removed = new List<ulong>();
                foreach (var deck in decks)
                {
                    using var document = JsonDocument.Parse(deck.DeckBitsJson!);
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty("PVEChampionId", out var owner) ||
                        !owner.TryGetUInt64(out var savedChampionId) || savedChampionId != championId)
                        continue;
                    context.Decks.Remove(deck);
                    removed.Add((ulong)deck.Id);
                }
                return removed;
            });
        }

        public Task RemoveDeckAsync(int id)
        {
            return EnqueueWriteAsync(async context =>
            {
                var deck = await context.Decks.FindAsync(id).ConfigureAwait(false);
                
                if (deck is null)
                    return;

                context.Decks.Remove(deck);
            });
        }

        public Task<ulong> RemoveDeckWithNameOwnedByPlayer(string name, ulong playerId)
        {

            return EnqueueWriteAsync<ulong>(async context =>
            {
                var deck = await context.Decks
                    .Where(d => d.DeckName.ToLower().Equals(name.ToLower()) && d.PlayerProfileId == playerId)
                    .FirstOrDefaultAsync();

                if (deck is null)
                {
                    return 0;
                }

                var deckId = (ulong)deck.Id;

                context.Decks.Remove(deck);

                return deckId;
            });
        }
    }
}