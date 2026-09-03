using System.Data;
using System.Data.Common;
using System.Globalization;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FhirBulkExportManager.Persistence.Sqlite;

public sealed class SqliteExportStore(IDbContextFactory<ExportDbContext> contextFactory) : IExportStore
{
    public async Task AddJobAsync(ExportJob job, ExportJobEvent createdEvent, AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        context.ExportJobs.Add(job);
        context.ExportJobEvents.Add(createdEvent);
        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ExportJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<ExportJob>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var querySql = BuildJobQuerySql(query);
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var count = await CountJobsAsync(connection, querySql, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return new PagedResult<ExportJob>([], 0);
            }

            var itemParameters = querySql.Parameters.ToList();
            itemParameters.Add(new SqlParameterValue("@take", Math.Clamp(query.Take, 1, 500)));
            itemParameters.Add(new SqlParameterValue("@skip", Math.Max(0, query.Skip)));
            var itemSql = $"""
                SELECT *
                FROM "ExportJobs"
                WHERE {querySql.WhereClause}
                ORDER BY julianday("CreatedAtUtc") DESC, "Id" ASC
                LIMIT @take OFFSET @skip
                """;

            var items = await context.ExportJobs
                .FromSqlRaw(itemSql, CreateParameters(itemParameters).Cast<object>().ToArray())
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return new PagedResult<ExportJob>(items, count);
        }
        finally
        {
            if (openedHere)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<ExportJob>> GetRunnableJobsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        ExportJobState[] runnable =
        [
            ExportJobState.Queued,
            ExportJobState.Authenticating,
            ExportJobState.Starting,
            ExportJobState.Running,
            ExportJobState.WaitingForPoll,
            ExportJobState.ManifestReady,
            ExportJobState.QueuedForDownload,
            ExportJobState.Downloading,
            ExportJobState.Validating,
            ExportJobState.Committed,
            ExportJobState.CleanupRequested,
            ExportJobState.CancelRequested,
            ExportJobState.RetryPending,
        ];

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ExportJobs
            .FromSqlInterpolated($"""
                SELECT *
                FROM "ExportJobs"
                WHERE "State" IN (
                    {(int)runnable[0]}, {(int)runnable[1]}, {(int)runnable[2]},
                    {(int)runnable[3]}, {(int)runnable[4]}, {(int)runnable[5]},
                    {(int)runnable[6]}, {(int)runnable[7]}, {(int)runnable[8]},
                    {(int)runnable[9]}, {(int)runnable[10]}, {(int)runnable[11]},
                    {(int)runnable[12]}
                )
                AND (
                    "NextPollAtUtc" IS NULL
                    OR julianday("NextPollAtUtc") <= julianday({now})
                    OR "State" NOT IN (
                        {(int)ExportJobState.WaitingForPoll},
                        {(int)ExportJobState.RetryPending},
                        {(int)ExportJobState.CleanupRequested}
                    )
                )
                AND (
                    "LeaseExpiresAtUtc" IS NULL
                    OR julianday("LeaseExpiresAtUtc") <= julianday({now})
                )
                ORDER BY "Priority" DESC,
                         julianday(COALESCE("NextPollAtUtc", "CreatedAtUtc")) ASC,
                         "Id" ASC
                LIMIT {Math.Clamp(take, 1, 1000)}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpdateJobAsync(ExportJob job, ExportJobEvent jobEvent, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AttachModified(context, job, expectedVersion);
        context.ExportJobEvents.Add(jobEvent);
        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveManifestAsync(ExportJob job, ExportJobEvent jobEvent, IReadOnlyCollection<ExportFile> files, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        AttachModified(context, job, expectedVersion);
        context.ExportJobEvents.Add(jobEvent);
        context.ExportFiles.AddRange(files);
        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportFile>> GetFilesAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ExportFiles.AsNoTracking().Where(x => x.ExportJobId == jobId).OrderBy(x => x.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportFile?> GetFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ExportFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateFileAsync(ExportFile file, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = context.Attach(file);
        entry.State = EntityState.Modified;
        entry.Property(x => x.Version).OriginalValue = expectedVersion;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportJobEvent>> GetTimelineAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.ExportJobEvents
            .FromSqlInterpolated($"""
                SELECT *
                FROM "ExportJobEvents"
                WHERE "ExportJobId" = {jobId}
                ORDER BY julianday("TimestampUtc") ASC, "Id" ASC
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryAcquireLeaseAsync(Guid jobId, string owner, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var expires = now.Add(duration);
        var affected = await context.Database.ExecuteSqlAsync($"""
            UPDATE "ExportJobs"
            SET "LeaseOwner" = {owner},
                "LeaseAcquiredAtUtc" = {now},
                "LeaseExpiresAtUtc" = {expires}
            WHERE "Id" = {jobId}
              AND (
                  "LeaseExpiresAtUtc" IS NULL
                  OR julianday("LeaseExpiresAtUtc") <= julianday({now})
                  OR "LeaseOwner" = {owner}
              );
            """, cancellationToken)
            .ConfigureAwait(false);
        return affected == 1;
    }

    public async Task ReleaseLeaseAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlAsync($"""
            UPDATE "ExportJobs"
            SET "LeaseOwner" = NULL,
                "LeaseAcquiredAtUtc" = NULL,
                "LeaseExpiresAtUtc" = NULL
            WHERE "Id" = {jobId}
              AND "LeaseOwner" = {owner};
            """, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlAsync($"""
            UPDATE "ExportJobs"
            SET "LeaseOwner" = NULL,
                "LeaseAcquiredAtUtc" = NULL,
                "LeaseExpiresAtUtc" = NULL
            WHERE "LeaseExpiresAtUtc" IS NOT NULL
              AND julianday("LeaseExpiresAtUtc") <= julianday({now});
            """, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteJobMetadataAsync(Guid jobId, AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await context.ExportFiles.Where(x => x.ExportJobId == jobId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.ExportJobEvents.Where(x => x.ExportJobId == jobId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await context.ExportJobs.Where(x => x.Id == jobId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted != 1)
        {
            throw new DbUpdateConcurrencyException("Export job was changed or deleted before metadata deletion completed.");
        }

        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AttachModified(ExportDbContext context, ExportJob job, long expectedVersion)
    {
        var entry = context.Attach(job);
        entry.State = EntityState.Modified;
        entry.Property(x => x.Version).OriginalValue = expectedVersion;
        entry.Property(x => x.LeaseOwner).IsModified = false;
        entry.Property(x => x.LeaseAcquiredAtUtc).IsModified = false;
        entry.Property(x => x.LeaseExpiresAtUtc).IsModified = false;
    }

    private static JobQuerySql BuildJobQuerySql(JobQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<SqlParameterValue>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            clauses.Add("(\"Name\" LIKE @search OR \"GroupId\" LIKE @search OR CAST(\"Id\" AS TEXT) LIKE @search)");
            parameters.Add(new SqlParameterValue("@search", $"%{query.Search.Trim()}%"));
        }

        if (query.States is { Count: > 0 })
        {
            var stateNames = new List<string>();
            foreach (var state in query.States.Distinct())
            {
                var name = $"@state{stateNames.Count}";
                stateNames.Add(name);
                parameters.Add(new SqlParameterValue(name, (int)state));
            }

            clauses.Add($"\"State\" IN ({string.Join(", ", stateNames)})");
        }

        if (query.VendorId is { } vendorId)
        {
            clauses.Add("\"VendorId\" = @vendorId");
            parameters.Add(new SqlParameterValue("@vendorId", vendorId));
        }

        if (query.SiteId is { } siteId)
        {
            clauses.Add("\"SiteId\" = @siteId");
            parameters.Add(new SqlParameterValue("@siteId", siteId));
        }

        if (query.CreatedFromUtc is { } from)
        {
            clauses.Add("julianday(\"CreatedAtUtc\") >= julianday(@createdFromUtc)");
            parameters.Add(new SqlParameterValue("@createdFromUtc", from));
        }

        if (query.CreatedToUtc is { } to)
        {
            clauses.Add("julianday(\"CreatedAtUtc\") <= julianday(@createdToUtc)");
            parameters.Add(new SqlParameterValue("@createdToUtc", to));
        }

        return new JobQuerySql(
            clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses),
            parameters);
    }

    private static async Task<int> CountJobsAsync(
        DbConnection connection,
        JobQuerySql querySql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"ExportJobs\" WHERE {querySql.WhereClause};";
        foreach (var parameter in CreateParameters(querySql.Parameters))
        {
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return checked((int)Convert.ToInt64(result, CultureInfo.InvariantCulture));
    }

    private static SqliteParameter[] CreateParameters(IEnumerable<SqlParameterValue> values) =>
        values.Select(x => new SqliteParameter(x.Name, x.Value)).ToArray();

    private sealed record JobQuerySql(string WhereClause, IReadOnlyList<SqlParameterValue> Parameters);

    private sealed record SqlParameterValue(string Name, object Value);
}
