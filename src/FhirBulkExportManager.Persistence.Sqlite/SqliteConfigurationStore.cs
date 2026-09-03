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

    public async Task AddConnectionAsync(
        Site site,
        CredentialProfile credentialProfile,
        AuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        if (site.CredentialProfileId != credentialProfile.Id || site.VendorId != credentialProfile.VendorId)
        {
            throw new InvalidOperationException("Site and credential profile must belong to the same connection and vendor.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        context.CredentialProfiles.Add(credentialProfile);
        context.Sites.Add(site);
        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateConnectionAsync(
        Site site,
        CredentialProfile credentialProfile,
        AuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        if (site.CredentialProfileId != credentialProfile.Id || site.VendorId != credentialProfile.VendorId)
        {
            throw new InvalidOperationException("Site and credential profile must belong to the same connection and vendor.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await context.Sites.AnyAsync(x => x.Id == site.Id, cancellationToken).ConfigureAwait(false) ||
            !await context.CredentialProfiles.AnyAsync(x => x.Id == credentialProfile.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The connection no longer exists. Refresh the connection list and try again.");
        }

        context.Entry(site).State = EntityState.Modified;
        context.Entry(credentialProfile).State = EntityState.Modified;
        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectionDeleteResult> DeleteConnectionAsync(
        Guid siteId,
        Guid credentialProfileId,
        AuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var site = await context.Sites.SingleOrDefaultAsync(x => x.Id == siteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected connection no longer exists.");
        if (site.CredentialProfileId != credentialProfileId)
        {
            throw new InvalidOperationException("The selected credential profile does not belong to this connection.");
        }

        if (await context.ExportJobs.AnyAsync(x => x.SiteId == siteId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("This connection cannot be deleted because one or more export jobs reference it. Preserve the connection for durable job history.");
        }

        var credential = await context.CredentialProfiles
            .SingleOrDefaultAsync(x => x.Id == credentialProfileId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The connection credential profile no longer exists.");
        var credentialUsedByAnotherSite = await context.Sites
            .AnyAsync(x => x.Id != siteId && x.CredentialProfileId == credentialProfileId, cancellationToken)
            .ConfigureAwait(false);
        var credentialUsedByAnyJob = await context.ExportJobs
            .AnyAsync(x => x.CredentialProfileId == credentialProfileId, cancellationToken)
            .ConfigureAwait(false);

        context.Sites.Remove(site);
        var credentialDeleted = !credentialUsedByAnotherSite && !credentialUsedByAnyJob;
        if (credentialDeleted)
        {
            context.CredentialProfiles.Remove(credential);
        }

        context.AuditEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionDeleteResult(credentialDeleted);
    }

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
