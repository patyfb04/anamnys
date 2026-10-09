using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Data;

// AddNpgsqlDbContext turns on Npgsql's retrying execution strategy, which refuses
// user-initiated transactions unless the whole unit runs through the strategy. A transient
// failure replays the unit from the start, so the unit begins with a clean change tracker.
// Committing after an early return is harmless: nothing was written.
public static class DbTransactions
{
    public static Task<T> InTransactionAsync<T>(
        this AnamnysDbContext db, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await work(ct);
            await tx.CommitAsync(ct);
            return result;
        }, cancellationToken);
}
