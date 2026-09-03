# Database

`exports.db` is a local SQLite control-plane database. Startup rejects UNC/network paths, runs tracked EF Core migrations, enables foreign keys, sets a busy timeout, and requests WAL mode. A backup is made before pending migrations are applied to an existing database.

## Tables

| Table | Purpose |
| --- | --- |
| Vendors | Vendor identity and pinned plugin version |
| Sites | FHIR endpoint/customer profile |
| CredentialProfiles | OAuth metadata and DPAPI secret reference |
| ExportJobs | Durable aggregate state, scheduling, manifest, progress, lease |
| ExportJobEvents | Immutable chronological transitions |
| ExportFiles | One row per output/deleted/error manifest entry |
| ExportAttempts | Export-operation journal |
| DownloadAttempts | File-operation journal |
| PluginInstallations | Trusted installed plugin metadata |
| PluginConfigurations | Host/site plugin settings |
| ApplicationSettings | Persisted settings extension point |
| AuditEvents | Operator/configuration action audit |
| SchemaVersions | Host/database compatibility gate |

`ExportJobs.Version` and `ExportFiles.Version` are optimistic-concurrency tokens. Scheduler leases contain an owner and expiry. Stale leases are cleared at startup and are also eligible for atomic reacquisition after expiry.

## Transaction rules

- Job change, job event, and audit event are committed together.
- Manifest normalization inserts all file rows in the same transaction as the `ManifestReady` transition.
- Writes are short and never span authentication, HTTP, filesystem streaming, hashing, or validation.
- A concurrency conflict aborts the transaction rather than silently overwriting another worker/operator action.

Database backup does not back up exported NDJSON. Protect both control and data planes using the organization's approved encrypted backup system.

