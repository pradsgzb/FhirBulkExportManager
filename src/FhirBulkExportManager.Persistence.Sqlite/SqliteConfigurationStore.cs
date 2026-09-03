using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace FhirBulkExportManager.Persistence.Sqlite;

public sealed class SqliteConfigurationStore(IDbContextFactory<ExportDbContext> contextFactory) : IConfigurationStore
{
    public async Task<IReadOnlyList<Vendor>> GetVendorsAsync(bool enabledOnly, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Vendors.AsNoTracking();
        if (enabledOnly)
        {
            query = query.Where(x => x.Enabled);
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Vendor?> GetVendorAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Vendors.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public Task AddVendorAsync(Vendor vendor, AuditEvent auditEvent, CancellationToken cancellationToken) => AddAsync(vendor, auditEvent, cancellationToken);

    public async Task<IReadOnlyList<Site>> GetSitesAsync(Guid? vendorId, bool enabledOnly, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Sites.AsNoTracking();
        if (vendorId is { } id)
        {
            query = query.Where(x => x.VendorId == id);
        }

        if (enabledOnly)
        {
            query = query.Where(x => x.Enabled);
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Site?> GetSiteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Sites.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public Task AddSiteAsync(Site site, AuditEvent auditEvent, CancellationToken cancellationToken) => AddAsync(site, auditEvent, cancellationToken);

    public async Task<CredentialProfile?> GetCredentialProfileAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.CredentialProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CredentialProfile>> GetCredentialProfilesAsync(Guid? vendorId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.CredentialProfiles.AsNoTracking();
        if (vendorId is { } id)
        {
            query = query.Where(x => x.VendorId == id);
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task AddCredentialProfileAsync(CredentialProfile profile, AuditEvent auditEvent, CancellationToken cancellationToken) => AddAsync(profile, auditEvent, cancellationToken);

    private async Task AddAsync<T>(T entity, AuditEvent auditEvent, CancellationToken cancellationToken) where T : class
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        context.Add(entity);
        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

