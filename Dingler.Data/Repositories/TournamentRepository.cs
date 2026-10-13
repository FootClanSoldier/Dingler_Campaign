using System.Linq.Expressions;
using Dingler.Data.Context;
using Dingler.Data.Entities.GameData;
using Microsoft.EntityFrameworkCore;

namespace Dingler.Data.Repositories;

public class TournamentRepository
{
    private IDbContextFactory<GameDataContext> _dbContextFactory;

    public TournamentRepository(IDbContextFactory<GameDataContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<List<T>> GetTournamentsAsync<T>(Expression<Func<Tournament, T>> selector, CancellationToken token)
    {
        var context = await _dbContextFactory.CreateDbContextAsync(token);
        
        return await context.Tournaments.Select(selector).ToListAsync(token);
    }

    public async Task<List<Tournament>> GetTournamentsAsync(CancellationToken token)
    {
        var context = await _dbContextFactory.CreateDbContextAsync(token);

        return await context.Tournaments
            .Include(t => t.DraftSets)
            .Include(t => t.TournamentType)
            .Include(t => t.MatchType)
            .Include(t => t.StartCondition)
            .AsNoTracking()
            .ToListAsync(token);
    }

    public async Task<T?> GetTournamentAsync<T>(Expression<Func<Tournament, T>> selector, int id)
    {
        var context = await _dbContextFactory.CreateDbContextAsync();
        
        return await context.Tournaments.Where(t => t.Id == id).Select(selector).FirstOrDefaultAsync();
    }
}