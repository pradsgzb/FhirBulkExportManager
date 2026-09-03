using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace FhirBulkExportManager.Persistence.Sqlite.Migrations;

[Migration("202609030001_InitialCreate")]
[DbContext(typeof(ExportDbContext))]
public sealed class InitialCreate : Migration
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        InitialCreateModel.BuildModel(modelBuilder);
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE Vendors (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, PluginId TEXT NOT NULL, PluginVersion TEXT NOT NULL, Enabled INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
CREATE UNIQUE INDEX IX_Vendors_Name ON Vendors(Name);
CREATE TABLE CredentialProfiles (Id TEXT NOT NULL PRIMARY KEY, VendorId TEXT NOT NULL, Name TEXT NOT NULL, AuthenticationType TEXT NOT NULL, ClientId TEXT NOT NULL, SecretReference TEXT NOT NULL, Scope TEXT NULL, Audience TEXT NULL, TokenEndpoint TEXT NOT NULL, AssertionEndpoint TEXT NULL, AdditionalConfigurationJson TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, FOREIGN KEY (VendorId) REFERENCES Vendors(Id) ON DELETE RESTRICT);
CREATE UNIQUE INDEX IX_CredentialProfiles_VendorId_Name ON CredentialProfiles(VendorId, Name);
CREATE TABLE Sites (Id TEXT NOT NULL PRIMARY KEY, VendorId TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT NULL, FhirBaseUrl TEXT NOT NULL, TenantIdentifier TEXT NULL, PracticeCode TEXT NULL, CredentialProfileId TEXT NOT NULL, Enabled INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, FOREIGN KEY (VendorId) REFERENCES Vendors(Id) ON DELETE RESTRICT, FOREIGN KEY (CredentialProfileId) REFERENCES CredentialProfiles(Id) ON DELETE RESTRICT);
CREATE INDEX IX_Sites_CredentialProfileId ON Sites(CredentialProfileId);
CREATE UNIQUE INDEX IX_Sites_VendorId_Name ON Sites(VendorId, Name);
CREATE TABLE ExportJobs (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NULL, VendorId TEXT NOT NULL, SiteId TEXT NOT NULL, CredentialProfileId TEXT NOT NULL, PluginId TEXT NOT NULL, PluginVersion TEXT NOT NULL, ExportType INTEGER NOT NULL, Priority INTEGER NOT NULL, GroupId TEXT NULL, SinceUtc TEXT NULL, ResourceTypesJson TEXT NOT NULL, TypeFiltersJson TEXT NOT NULL, VendorOptionsJson TEXT NOT NULL, OutputFormat TEXT NOT NULL, LocalDestination TEXT NOT NULL, AutomaticCleanupEnabled INTEGER NOT NULL, PartialExportApproved INTEGER NOT NULL, State INTEGER NOT NULL, RemoteRequestUrl TEXT NULL, RemoteStatusUrl TEXT NULL, RemoteTransactionTime TEXT NULL, RemoteExpiresAtUtc TEXT NULL, CreatedAtUtc TEXT NOT NULL, QueuedAtUtc TEXT NULL, StartedAtUtc TEXT NULL, LastPolledAtUtc TEXT NULL, NextPollAtUtc TEXT NULL, ManifestReceivedAtUtc TEXT NULL, CommittedAtUtc TEXT NULL, CleanupRequestedAtUtc TEXT NULL, CancelRequestedAtUtc TEXT NULL, CompletedAtUtc TEXT NULL, ProgressText TEXT NULL, ManifestJson TEXT NULL, ManifestSha256 TEXT NULL, RequiresAccessToken INTEGER NOT NULL, OutputFileCount INTEGER NOT NULL, DeletedFileCount INTEGER NOT NULL, ErrorFileCount INTEGER NOT NULL, DownloadedFileCount INTEGER NOT NULL, ValidatedFileCount INTEGER NOT NULL, TotalBytes INTEGER NOT NULL, DownloadedBytes INTEGER NOT NULL, TotalRecords INTEGER NOT NULL, RetryCount INTEGER NOT NULL, LastErrorCode TEXT NULL, LastErrorMessage TEXT NULL, LeaseOwner TEXT NULL, LeaseAcquiredAtUtc TEXT NULL, LeaseExpiresAtUtc TEXT NULL, RemoteCleanupVerified INTEGER NOT NULL, Version INTEGER NOT NULL, FOREIGN KEY (VendorId) REFERENCES Vendors(Id) ON DELETE RESTRICT, FOREIGN KEY (SiteId) REFERENCES Sites(Id) ON DELETE RESTRICT, FOREIGN KEY (CredentialProfileId) REFERENCES CredentialProfiles(Id) ON DELETE RESTRICT);
CREATE INDEX IX_ExportJobs_CredentialProfileId ON ExportJobs(CredentialProfileId);
CREATE INDEX IX_ExportJobs_State_NextPollAtUtc ON ExportJobs(State, NextPollAtUtc);
CREATE INDEX IX_ExportJobs_SiteId_State ON ExportJobs(SiteId, State);
CREATE INDEX IX_ExportJobs_CreatedAtUtc ON ExportJobs(CreatedAtUtc);
CREATE INDEX IX_ExportJobs_RemoteStatusUrl ON ExportJobs(RemoteStatusUrl);
CREATE INDEX IX_ExportJobs_VendorId ON ExportJobs(VendorId);
CREATE TABLE ExportJobEvents (Id TEXT NOT NULL PRIMARY KEY, ExportJobId TEXT NOT NULL, PreviousState INTEGER NOT NULL, NewState INTEGER NOT NULL, EventType TEXT NOT NULL, TimestampUtc TEXT NOT NULL, Message TEXT NOT NULL, OperationId TEXT NOT NULL, CorrelationId TEXT NOT NULL, MetadataJson TEXT NOT NULL, FOREIGN KEY (ExportJobId) REFERENCES ExportJobs(Id) ON DELETE CASCADE);
CREATE INDEX IX_ExportJobEvents_ExportJobId_TimestampUtc ON ExportJobEvents(ExportJobId, TimestampUtc);
CREATE TABLE ExportFiles (Id TEXT NOT NULL PRIMARY KEY, ExportJobId TEXT NOT NULL, Category INTEGER NOT NULL, ResourceType TEXT NULL, RemoteUrl TEXT NOT NULL, Sequence INTEGER NOT NULL, OriginalSuggestedName TEXT NULL, SafeLocalFileName TEXT NOT NULL, RelativeLocalPath TEXT NOT NULL, Status INTEGER NOT NULL, AttemptCount INTEGER NOT NULL, ExpectedBytes INTEGER NULL, ActualBytes INTEGER NOT NULL, RecordCount INTEGER NOT NULL, Sha256 TEXT NULL, ETag TEXT NULL, LastModifiedUtc TEXT NULL, CreatedAtUtc TEXT NOT NULL, DownloadStartedAtUtc TEXT NULL, DownloadCompletedAtUtc TEXT NULL, ValidatedAtUtc TEXT NULL, LastError TEXT NULL, ValidationSummaryJson TEXT NOT NULL, HasSeriousOperationOutcome INTEGER NOT NULL, Version INTEGER NOT NULL, FOREIGN KEY (ExportJobId) REFERENCES ExportJobs(Id) ON DELETE CASCADE);
CREATE UNIQUE INDEX IX_ExportFiles_ExportJobId_Sequence ON ExportFiles(ExportJobId, Sequence);
CREATE INDEX IX_ExportFiles_ExportJobId_Status ON ExportFiles(ExportJobId, Status);
CREATE TABLE ExportAttempts (Id TEXT NOT NULL PRIMARY KEY, ExportJobId TEXT NOT NULL, OperationId TEXT NOT NULL, Operation TEXT NOT NULL, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL, Outcome INTEGER NOT NULL, HttpStatus INTEGER NULL, ErrorCode TEXT NULL, FOREIGN KEY (ExportJobId) REFERENCES ExportJobs(Id) ON DELETE CASCADE);
CREATE INDEX IX_ExportAttempts_ExportJobId_StartedAtUtc ON ExportAttempts(ExportJobId, StartedAtUtc);
CREATE TABLE DownloadAttempts (Id TEXT NOT NULL PRIMARY KEY, ExportFileId TEXT NOT NULL, AttemptNumber INTEGER NOT NULL, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL, ResumeOffset INTEGER NOT NULL, BytesTransferred INTEGER NOT NULL, Outcome INTEGER NOT NULL, ErrorCode TEXT NULL, FOREIGN KEY (ExportFileId) REFERENCES ExportFiles(Id) ON DELETE CASCADE);
CREATE INDEX IX_DownloadAttempts_ExportFileId_AttemptNumber ON DownloadAttempts(ExportFileId, AttemptNumber);
CREATE TABLE PluginInstallations (Id TEXT NOT NULL PRIMARY KEY, PluginId TEXT NOT NULL, Version TEXT NOT NULL, Publisher TEXT NOT NULL, InstallPath TEXT NOT NULL, AssemblySha256 TEXT NOT NULL, InstalledAtUtc TEXT NOT NULL, Trusted INTEGER NOT NULL);
CREATE UNIQUE INDEX IX_PluginInstallations_PluginId_Version ON PluginInstallations(PluginId, Version);
CREATE TABLE PluginConfigurations (Id TEXT NOT NULL PRIMARY KEY, PluginId TEXT NOT NULL, SiteId TEXT NULL, ConfigurationJson TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, FOREIGN KEY (SiteId) REFERENCES Sites(Id) ON DELETE CASCADE);
CREATE UNIQUE INDEX IX_PluginConfigurations_PluginId_SiteId ON PluginConfigurations(PluginId, SiteId);
CREATE INDEX IX_PluginConfigurations_SiteId ON PluginConfigurations(SiteId);
CREATE TABLE ApplicationSettings (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
CREATE TABLE AuditEvents (Id TEXT NOT NULL PRIMARY KEY, TimestampUtc TEXT NOT NULL, WindowsUser TEXT NOT NULL, Action TEXT NOT NULL, EntityType TEXT NOT NULL, EntityId TEXT NOT NULL, Result TEXT NOT NULL, CorrelationId TEXT NOT NULL, SafeMetadataJson TEXT NOT NULL);
CREATE INDEX IX_AuditEvents_TimestampUtc ON AuditEvents(TimestampUtc);
CREATE INDEX IX_AuditEvents_EntityType_EntityId ON AuditEvents(EntityType, EntityId);
CREATE TABLE SchemaVersions (Version INTEGER NOT NULL PRIMARY KEY, HostVersion TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
INSERT INTO SchemaVersions(Version, HostVersion, AppliedAtUtc) VALUES (1, '1.0.0', CURRENT_TIMESTAMP);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS SchemaVersions;
DROP TABLE IF EXISTS AuditEvents;
DROP TABLE IF EXISTS ApplicationSettings;
DROP TABLE IF EXISTS PluginConfigurations;
DROP TABLE IF EXISTS PluginInstallations;
DROP TABLE IF EXISTS DownloadAttempts;
DROP TABLE IF EXISTS ExportAttempts;
DROP TABLE IF EXISTS ExportFiles;
DROP TABLE IF EXISTS ExportJobEvents;
DROP TABLE IF EXISTS ExportJobs;
DROP TABLE IF EXISTS Sites;
DROP TABLE IF EXISTS CredentialProfiles;
DROP TABLE IF EXISTS Vendors;
""");
    }
}
