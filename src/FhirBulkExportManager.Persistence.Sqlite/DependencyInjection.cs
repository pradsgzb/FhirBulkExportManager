using FhirBulkExportManager.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.Persistence.Sqlite;

public static class DependencyInjection
{
    public static IServiceCollection AddSqlitePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SqlitePersistenceOptions.SectionName);
        services.AddOptions<SqlitePersistenceOptions>().Bind(section);
        var options = section.Get<SqlitePersistenceOptions>() ?? new SqlitePersistenceOptions();
        var path = Path.GetFullPath(options.DatabasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = Math.Clamp(options.BusyTimeoutSeconds, 1, 300),
        }.ToString();

        services.AddPooledDbContextFactory<ExportDbContext>(builder => builder.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(ExportDbContext).Assembly.GetName().Name!)));
        services.AddSingleton<IExportStore, SqliteExportStore>();
        services.AddSingleton<IConfigurationStore, SqliteConfigurationStore>();
        services.AddSingleton<IDatabaseLifecycle, SqliteDatabaseLifecycle>();
        return services;
    }
}
