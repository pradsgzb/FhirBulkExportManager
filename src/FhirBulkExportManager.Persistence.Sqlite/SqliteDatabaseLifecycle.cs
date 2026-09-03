using FhirBulkExportManager.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Persistence.Sqlite;

public sealed class SqliteDatabaseLifecycle(
    IDbContextFactory<ExportDbContext> contextFactory,
    IOptions<SqlitePersistenceOptions> options,
    TimeProvider timeProvider) : IDatabaseLifecycle
{
    public const int CurrentSchemaVersion = 1;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.GetFullPath(options.Value.DatabasePath);
        RejectNetworkPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Any() && File.Exists(databasePath))
        {
            var backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "Backups");
            await BackupAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        }

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        // Busy/locked retries are configured for every pooled connection through
        // SqliteConnectionStringBuilder.DefaultTimeout in DependencyInjection.
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);

        var version = await context.SchemaVersions.AsNoTracking().MaxAsync(x => (int?)x.Version, cancellationToken).ConfigureAwait(false) ?? 0;
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Database schema {version} is newer than supported schema {CurrentSchemaVersion}. Workers were not started.");
        }
    }

    public async Task<string> BackupAsync(string destinationDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationDirectory);
        var sourcePath = Path.GetFullPath(options.Value.DatabasePath);
        var destinationPath = Path.Combine(destinationDirectory, $"exports-{timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.db");
        var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly };
        var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate };
        await using var source = new SqliteConnection(sourceBuilder.ConnectionString);
        await using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        return destinationPath;
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.SchemaVersions.AsNoTracking().MaxAsync(x => (int?)x.Version, cancellationToken).ConfigureAwait(false) ?? 0;
    }

    private static void RejectNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc))
        {
            throw new InvalidOperationException("The SQLite state database must be stored on a local disk, not a network share.");
        }
    }
}
