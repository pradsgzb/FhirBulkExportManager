using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Persistence.Sqlite;
using FhirBulkExportManager.PluginContracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Persistence.Tests;

public sealed class SqliteLifecycleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fhir-sqlite-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Migration_creates_schema_and_draft_survives_new_context()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        var dbOptions = new DbContextOptionsBuilder<ExportDbContext>().UseSqlite($"Data Source={path};Foreign Keys=True").Options;
        var factory = new TestFactory(dbOptions);
        var lifecycle = new SqliteDatabaseLifecycle(factory, Options.Create(new SqlitePersistenceOptions { DatabasePath = path }), TimeProvider.System);
        await lifecycle.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var vendor = new Vendor(Guid.NewGuid(), "Test Vendor", "test", "1.0.0", now);
        var credential = new CredentialProfile(
            Guid.NewGuid(),
            vendor.Id,
            "Test Credential",
            "client_credentials",
            "test-client",
            "secret-ref",
            "https://auth.test.example/token",
            now);
        var site = new Site(Guid.NewGuid(), vendor.Id, "Test Site", new Uri("https://fhir.test.example"), credential.Id, now);
        var job = new ExportJob(Guid.NewGuid(), "Durable Draft", vendor.Id, site.Id, credential.Id, "test", "1.0.0", ExportType.System, JobPriority.Normal, directory, now);
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.Vendors.Add(vendor);
            context.CredentialProfiles.Add(credential);
            context.Sites.Add(site);
            context.ExportJobs.Add(job);
            await context.SaveChangesAsync();
        }

        await using var reopened = await factory.CreateDbContextAsync();
        var loaded = await reopened.ExportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);

        Assert.Equal(ExportJobState.Draft, loaded.State);
        Assert.Equal(1, await lifecycle.GetSchemaVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Lease_operations_compare_datetimeoffset_values_safely_on_sqlite()
    {
        var factory = await CreateInitializedFactoryAsync();
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.FromHours(5.5));
        var configuration = await SeedConfigurationAsync(factory, now);
        var job = CreateJob("Lease test", configuration, now);
        _ = job.Transition(ExportJobState.Queued, now, "queued", "Queued", Guid.NewGuid(), "lease-test");

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.ExportJobs.Add(job);
            await context.SaveChangesAsync();
        }

        var store = new SqliteExportStore(factory);
        Assert.True(await store.TryAcquireLeaseAsync(job.Id, "worker-a", now, TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.False(await store.TryAcquireLeaseAsync(job.Id, "worker-b", now.AddSeconds(30), TimeSpan.FromMinutes(1), CancellationToken.None));

        // Use a different offset for the same timeline to verify instant-based comparison.
        await store.ClearExpiredLeasesAsync(now.ToUniversalTime().AddMinutes(2), CancellationToken.None);
        var cleared = await store.GetJobAsync(job.Id, CancellationToken.None);
        Assert.NotNull(cleared);
        Assert.Null(cleared.LeaseOwner);
        Assert.Null(cleared.LeaseAcquiredAtUtc);
        Assert.Null(cleared.LeaseExpiresAtUtc);

        var reacquiredAt = now.ToUniversalTime().AddMinutes(2);
        Assert.True(await store.TryAcquireLeaseAsync(job.Id, "worker-b", reacquiredAt, TimeSpan.FromMinutes(1), CancellationToken.None));
        await store.ReleaseLeaseAsync(job.Id, "worker-b", CancellationToken.None);
        Assert.Null((await store.GetJobAsync(job.Id, CancellationToken.None))!.LeaseOwner);
    }

    [Fact]
    public async Task Job_queries_order_and_filter_datetimeoffset_values_safely_on_sqlite()
    {
        var factory = await CreateInitializedFactoryAsync();
        var configurationTime = DateTimeOffset.Parse("2026-09-03T03:00:00Z");
        var configuration = await SeedConfigurationAsync(factory, configurationTime);

        // The text representations sort in the opposite order from the UTC instants.
        var olderInstant = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.FromHours(5.5));
        var newerInstant = new DateTimeOffset(2026, 9, 2, 23, 45, 0, TimeSpan.FromHours(-4));
        var older = CreateJob("Older instant", configuration, olderInstant);
        var newer = CreateJob("Newer instant", configuration, newerInstant);
        _ = older.Transition(ExportJobState.Queued, olderInstant, "queued", "Queued", Guid.NewGuid(), "older");
        _ = newer.Transition(ExportJobState.Queued, newerInstant, "queued", "Queued", Guid.NewGuid(), "newer");

        var earlyEvent = new ExportJobEvent(
            older.Id,
            ExportJobState.Draft,
            ExportJobState.Queued,
            "early",
            olderInstant,
            "Early event",
            Guid.NewGuid(),
            "early");
        var lateEvent = new ExportJobEvent(
            older.Id,
            ExportJobState.Queued,
            ExportJobState.Authenticating,
            "late",
            newerInstant,
            "Late event",
            Guid.NewGuid(),
            "late");

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.ExportJobs.AddRange(older, newer);
            context.ExportJobEvents.AddRange(lateEvent, earlyEvent);
            await context.SaveChangesAsync();
        }

        var store = new SqliteExportStore(factory);
        var page = await store.QueryJobsAsync(new JobQuery(Take: 10), CancellationToken.None);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { newer.Id, older.Id }, page.Items.Select(x => x.Id).ToArray());

        var threshold = DateTimeOffset.Parse("2026-09-03T03:40:00Z");
        var filtered = await store.QueryJobsAsync(
            new JobQuery(CreatedFromUtc: threshold, Take: 10),
            CancellationToken.None);
        Assert.Equal(newer.Id, Assert.Single(filtered.Items).Id);

        var runnable = await store.GetRunnableJobsAsync(
            DateTimeOffset.Parse("2026-09-03T04:00:00Z"),
            10,
            CancellationToken.None);
        Assert.Equal(new[] { older.Id, newer.Id }, runnable.Select(x => x.Id).ToArray());

        var timeline = await store.GetTimelineAsync(older.Id, CancellationToken.None);
        Assert.Equal(new[] { "early", "late" }, timeline.Select(x => x.EventType).ToArray());
    }

    [Fact]
    public async Task Remote_status_url_lookup_finds_an_imported_export_by_its_exact_opaque_value()
    {
        var factory = await CreateInitializedFactoryAsync();
        var now = DateTimeOffset.Parse("2026-09-03T03:00:00Z");
        var configuration = await SeedConfigurationAsync(factory, now);
        var job = CreateJob("Imported export", configuration, now);
        const string exact = "https://vendor.example/status?job_id=A%2FB&opaque=x%20y";
        job.ConfirmRemoteJob(exact, now, now);
        _ = job.Transition(ExportJobState.WaitingForPoll, now, "imported", "Imported", Guid.NewGuid(), "imported");

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.ExportJobs.Add(job);
            await context.SaveChangesAsync();
        }

        var store = new SqliteExportStore(factory);
        var loaded = await store.GetJobByRemoteStatusUrlAsync(exact, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(job.Id, loaded.Id);
        Assert.Equal(exact, loaded.RemoteStatusUrl);
        Assert.Null(await store.GetJobByRemoteStatusUrlAsync(exact + "-different", CancellationToken.None));
    }

    [Fact]
    public async Task Connection_can_be_updated_and_deleted_transactionally_when_unused()
    {
        var factory = await CreateInitializedFactoryAsync();
        var now = DateTimeOffset.Parse("2026-09-03T03:00:00Z");
        var configuration = await SeedConfigurationAsync(factory, now);
        var store = new SqliteConfigurationStore(factory);
        var site = await store.GetSiteAsync(configuration.SiteId, CancellationToken.None);
        var credential = await store.GetCredentialProfileAsync(configuration.CredentialProfileId, CancellationToken.None);
        Assert.NotNull(site);
        Assert.NotNull(credential);
        var updatedAt = now.AddMinutes(10);
        site.Update("Updated Site", new Uri("https://updated.example/fhir"), credential.Id, updatedAt);
        credential.Update(
            "Updated Credential",
            "client_credentials",
            "updated-client",
            "updated-secret-reference",
            "https://updated.example/token",
            updatedAt,
            "system/*.read");

        await store.UpdateConnectionAsync(
            site,
            credential,
            Audit(updatedAt, "ConnectionUpdated", site.Id),
            CancellationToken.None);

        var reloadedSite = await store.GetSiteAsync(site.Id, CancellationToken.None);
        var reloadedCredential = await store.GetCredentialProfileAsync(credential.Id, CancellationToken.None);
        Assert.NotNull(reloadedSite);
        Assert.NotNull(reloadedCredential);
        Assert.Equal("Updated Site", reloadedSite.Name);
        Assert.Equal("https://updated.example/fhir", reloadedSite.FhirBaseUrl);
        Assert.Equal("updated-client", reloadedCredential.ClientId);

        var deleted = await store.DeleteConnectionAsync(
            site.Id,
            credential.Id,
            Audit(updatedAt.AddMinutes(1), "ConnectionDeleted", site.Id),
            CancellationToken.None);

        Assert.True(deleted.CredentialDeleted);
        Assert.Null(await store.GetSiteAsync(site.Id, CancellationToken.None));
        Assert.Null(await store.GetCredentialProfileAsync(credential.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Connection_delete_is_blocked_when_a_job_references_the_site()
    {
        var factory = await CreateInitializedFactoryAsync();
        var now = DateTimeOffset.Parse("2026-09-03T03:00:00Z");
        var configuration = await SeedConfigurationAsync(factory, now);
        var job = CreateJob("Connection reference", configuration, now);
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.ExportJobs.Add(job);
            await context.SaveChangesAsync();
        }

        var store = new SqliteConfigurationStore(factory);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteConnectionAsync(
            configuration.SiteId,
            configuration.CredentialProfileId,
            Audit(now.AddMinutes(1), "ConnectionDeleted", configuration.SiteId),
            CancellationToken.None));

        Assert.Contains("export jobs reference it", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(await store.GetSiteAsync(configuration.SiteId, CancellationToken.None));
        Assert.NotNull(await store.GetCredentialProfileAsync(configuration.CredentialProfileId, CancellationToken.None));
    }

    [Fact]
    public async Task Metadata_history_and_discovered_token_endpoint_are_saved_together()
    {
        var factory = await CreateInitializedFactoryAsync();
        var now = DateTimeOffset.Parse("2026-09-03T03:00:00Z");
        var configuration = await SeedConfigurationAsync(factory, now);
        var store = new SqliteConfigurationStore(factory);
        var credential = await store.GetCredentialProfileAsync(configuration.CredentialProfileId, CancellationToken.None);
        Assert.NotNull(credential);
        const string tokenEndpoint = "https://discovered.example/oauth/token";
        const string capabilityStatement = "{\"resourceType\":\"CapabilityStatement\"}";
        credential.Update(
            credential.Name,
            credential.AuthenticationType,
            credential.ClientId,
            credential.SecretReference,
            tokenEndpoint,
            now.AddMinutes(1),
            credential.Scope,
            credential.Audience,
            credential.AssertionEndpoint,
            credential.AdditionalConfigurationJson);
        var exchange = new HttpExchangeContract(
            "FHIR capability statement",
            now,
            now.AddMilliseconds(50),
            "GET",
            "https://fhir.example/metadata",
            [new HttpHeaderContract("Accept", ["application/fhir+json"])],
            null,
            200,
            "OK",
            [new HttpHeaderContract("Content-Type", ["application/fhir+json"])],
            capabilityStatement);
        var attempt = new ConnectionMetadataAttempt(now, true, 200, "Metadata succeeded.", [exchange]);

        await store.RecordConnectionMetadataAsync(
            "test",
            configuration.SiteId,
            credential,
            attempt,
            capabilityStatement,
            tokenEndpoint,
            Audit(now, "ConnectionMetadataRecorded", configuration.SiteId),
            CancellationToken.None);
        var failedExchange = exchange with
        {
            StartedAtUtc = now.AddMinutes(2),
            CompletedAtUtc = now.AddMinutes(2).AddMilliseconds(30),
            ResponseStatusCode = 400,
            ResponseReasonPhrase = "Bad Request",
            ResponseBody = "{\"resourceType\":\"OperationOutcome\"}",
        };
        await store.RecordConnectionMetadataAsync(
            "test",
            configuration.SiteId,
            credential,
            new ConnectionMetadataAttempt(now.AddMinutes(2), false, 400, "Metadata failed.", [failedExchange]),
            capabilityStatementJson: null,
            discoveredTokenEndpoint: null,
            auditEvent: Audit(now.AddMinutes(2), "ConnectionMetadataRecorded", configuration.SiteId),
            cancellationToken: CancellationToken.None);

        var history = await store.GetConnectionMetadataAsync("test", configuration.SiteId, CancellationToken.None);
        Assert.NotNull(history);
        Assert.Equal(capabilityStatement, history.LatestCapabilityStatementJson);
        Assert.Equal(tokenEndpoint, history.DiscoveredTokenEndpoint);
        Assert.Equal(2, history.Attempts.Count);
        var savedAttempt = history.Attempts[0];
        Assert.Equal(200, savedAttempt.StatusCode);
        var savedExchange = Assert.Single(savedAttempt.HttpExchanges);
        Assert.Equal(capabilityStatement, savedExchange.ResponseBody);
        Assert.False(history.Attempts[1].Succeeded);
        Assert.Equal(400, history.Attempts[1].StatusCode);
        Assert.Equal(tokenEndpoint, (await store.GetCredentialProfileAsync(credential.Id, CancellationToken.None))!.TokenEndpoint);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestFactory(DbContextOptions<ExportDbContext> options) : IDbContextFactory<ExportDbContext>
    {
        public ExportDbContext CreateDbContext() => new(options);
        public Task<ExportDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ExportDbContext(options));
    }

    private async Task<TestFactory> CreateInitializedFactoryAsync()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        var dbOptions = new DbContextOptionsBuilder<ExportDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;
        var factory = new TestFactory(dbOptions);
        var lifecycle = new SqliteDatabaseLifecycle(
            factory,
            Options.Create(new SqlitePersistenceOptions { DatabasePath = path }),
            TimeProvider.System);
        await lifecycle.InitializeAsync(CancellationToken.None);
        return factory;
    }

    private static async Task<(Guid VendorId, Guid CredentialProfileId, Guid SiteId)> SeedConfigurationAsync(
        TestFactory factory,
        DateTimeOffset now)
    {
        var vendor = new Vendor(Guid.NewGuid(), "Test Vendor", "test", "1.0.0", now);
        var credential = new CredentialProfile(
            Guid.NewGuid(),
            vendor.Id,
            "Test Credential",
            "client_credentials",
            "test-client",
            "secret-ref",
            "https://auth.test.example/token",
            now);
        var site = new Site(
            Guid.NewGuid(),
            vendor.Id,
            "Test Site",
            new Uri("https://fhir.test.example"),
            credential.Id,
            now);

        await using var context = await factory.CreateDbContextAsync();
        context.Vendors.Add(vendor);
        context.CredentialProfiles.Add(credential);
        context.Sites.Add(site);
        await context.SaveChangesAsync();
        return (vendor.Id, credential.Id, site.Id);
    }

    private static ExportJob CreateJob(
        string name,
        (Guid VendorId, Guid CredentialProfileId, Guid SiteId) configuration,
        DateTimeOffset createdAtUtc) =>
        new(
            Guid.NewGuid(),
            name,
            configuration.VendorId,
            configuration.SiteId,
            configuration.CredentialProfileId,
            "test",
            "1.0.0",
            ExportType.System,
            JobPriority.Normal,
            Path.GetTempPath(),
            createdAtUtc);

    private static AuditEvent Audit(DateTimeOffset now, string action, Guid entityId) =>
        new(now, "TEST\\operator", action, "Connection", entityId.ToString("D"), "Succeeded", Guid.NewGuid().ToString("N"));
}
