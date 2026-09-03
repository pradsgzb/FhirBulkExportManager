using FhirBulkExportManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace FhirBulkExportManager.Persistence.Sqlite;

public sealed class ExportDbContext(DbContextOptions<ExportDbContext> options) : DbContext(options)
{
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<CredentialProfile> CredentialProfiles => Set<CredentialProfile>();
    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();
    public DbSet<ExportJobEvent> ExportJobEvents => Set<ExportJobEvent>();
    public DbSet<ExportFile> ExportFiles => Set<ExportFile>();
    public DbSet<ExportAttempt> ExportAttempts => Set<ExportAttempt>();
    public DbSet<DownloadAttempt> DownloadAttempts => Set<DownloadAttempt>();
    public DbSet<PluginInstallation> PluginInstallations => Set<PluginInstallation>();
    public DbSet<PluginConfiguration> PluginConfigurations => Set<PluginConfiguration>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureModel(modelBuilder);
    }

    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Vendor>(entity =>
        {
            entity.ToTable("Vendors");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PluginId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PluginVersion).HasMaxLength(50).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<Site>(entity =>
        {
            entity.ToTable("Sites");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.FhirBaseUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.TenantIdentifier).HasMaxLength(200);
            entity.Property(x => x.PracticeCode).HasMaxLength(200);
            entity.HasIndex(x => new { x.VendorId, x.Name }).IsUnique();
            entity.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CredentialProfile>().WithMany().HasForeignKey(x => x.CredentialProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CredentialProfile>(entity =>
        {
            entity.ToTable("CredentialProfiles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.AuthenticationType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SecretReference).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.TokenEndpoint).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.AssertionEndpoint).HasMaxLength(2048);
            entity.Property(x => x.Scope).HasMaxLength(1000);
            entity.Property(x => x.Audience).HasMaxLength(1000);
            entity.HasIndex(x => new { x.VendorId, x.Name }).IsUnique();
            entity.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExportJob>(entity =>
        {
            entity.ToTable("ExportJobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(300).IsRequired();
            entity.Property(x => x.PluginId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PluginVersion).HasMaxLength(50).IsRequired();
            entity.Property(x => x.RemoteRequestUrl).HasMaxLength(4096);
            entity.Property(x => x.RemoteStatusUrl).HasMaxLength(4096);
            entity.Property(x => x.ManifestSha256).HasMaxLength(64);
            entity.Property(x => x.LastErrorCode).HasMaxLength(200);
            entity.Property(x => x.LastErrorMessage).HasMaxLength(4000);
            entity.Property(x => x.LeaseOwner).HasMaxLength(200);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.State, x.NextPollAtUtc });
            entity.HasIndex(x => new { x.SiteId, x.State });
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.RemoteStatusUrl);
            entity.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CredentialProfile>().WithMany().HasForeignKey(x => x.CredentialProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExportJobEvent>(entity =>
        {
            entity.ToTable("ExportJobEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Message).HasMaxLength(4000).IsRequired();
            entity.HasIndex(x => new { x.ExportJobId, x.TimestampUtc });
            entity.HasOne<ExportJob>().WithMany().HasForeignKey(x => x.ExportJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExportFile>(entity =>
        {
            entity.ToTable("ExportFiles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RemoteUrl).HasMaxLength(4096).IsRequired();
            entity.Property(x => x.ResourceType).HasMaxLength(100);
            entity.Property(x => x.SafeLocalFileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.RelativeLocalPath).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.Property(x => x.ETag).HasMaxLength(1000);
            entity.Property(x => x.LastError).HasMaxLength(4000);
            entity.Property(x => x.ValidationSummaryJson).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.ExportJobId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.ExportJobId, x.Status });
            entity.HasOne<ExportJob>().WithMany().HasForeignKey(x => x.ExportJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExportAttempt>(entity =>
        {
            entity.ToTable("ExportAttempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ExportJobId, x.StartedAtUtc });
            entity.HasOne<ExportJob>().WithMany().HasForeignKey(x => x.ExportJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DownloadAttempt>(entity =>
        {
            entity.ToTable("DownloadAttempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ExportFileId, x.AttemptNumber });
            entity.HasOne<ExportFile>().WithMany().HasForeignKey(x => x.ExportFileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PluginInstallation>(entity =>
        {
            entity.ToTable("PluginInstallations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PluginId, x.Version }).IsUnique();
        });

        modelBuilder.Entity<PluginConfiguration>(entity =>
        {
            entity.ToTable("PluginConfigurations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PluginId, x.SiteId }).IsUnique();
            entity.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationSetting>(entity =>
        {
            entity.ToTable("ApplicationSettings");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(300);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("AuditEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WindowsUser).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(200).IsRequired();
            entity.Property(x => x.EntityType).HasMaxLength(200).IsRequired();
            entity.Property(x => x.EntityId).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Result).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.TimestampUtc);
            entity.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        modelBuilder.Entity<SchemaVersion>(entity =>
        {
            entity.ToTable("SchemaVersions");
            entity.HasKey(x => x.Version);
            entity.Property(x => x.HostVersion).HasMaxLength(50).IsRequired();
        });
    }
}
