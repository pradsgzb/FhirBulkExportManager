# Requirements traceability

| Requirement area | Primary implementation | Verification |
| --- | --- | --- |
| Draft-only job creation | `Application/ExportJobService.CreateDraftAsync` | Domain/application behavior and UI wording |
| Formal legal states | `Domain/ExportJob` | `ExportJobStateMachineTests` |
| Durable state/events/audit | `SqliteExportStore.UpdateJobAsync` | transactional repository design |
| Exact Content-Location | eCW `KickoffAsync`; `ExportJob.ConfirmRemoteJob` | plugin/domain tests |
| Ambiguous kickoff | pre-dispatch journal; `Unknown` route | state-machine code and recovery docs |
| Retry-After | `Shared/RetryAfterParser`; plugin poll mapping | seconds/date tests |
| Global CPU ceiling | `ExportSchedulerService` and `ConcurrencyLimits` | 20-on-8 test |
| Fair scheduling | `FairSiteSelector` | cross-site test |
| Duplicate processing | SQLite lease + version token | persistence/worker code |
| Per-site max four | `SiteDownloadLimiter` | limit tests |
| Streaming downloads | `SecureFileDownloadService` | response-headers-read and bounded buffer |
| Safe partial/restart | `.partial`, validator-aware Range, byte check | infrastructure code/tests |
| Redirect token safety | manual redirects and same-origin check | infrastructure code |
| Safe names/paths | `ManagedStorage.CreateSafeFileName` and containment | infrastructure code |
| Disk reserve | `ManagedStorage.EnsureCapacityAsync` | worker download gate |
| NDJSON validation | `NdjsonValidator` | valid/truncated/type/outcome tests |
| SHA-256 | `HashUtility` and file download service | commit tests |
| Atomic local commit | `LocalCommitService` | commit/revalidation test |
| Partial success | persisted serious-outcome flag; `HeldForReview` | validator/state code |
| Cleanup invariant | commit revalidation in application and worker | cleanup code |
| Cleanup verification | adapter GET expects 404 | integration lifecycle test |
| OAuth/HealthWizz | `EClinicalWorksPlugin.AuthenticateAsync` | mock token/assertion endpoints |
| Plugin isolation/version pin | PluginContracts/PluginHost | architecture tests |
| WPF MVVM/operator actions | App.Wpf views/viewmodels/commands | UI composition test |
| SQLite WAL/migration/backup | `SqliteDatabaseLifecycle`; tracked migration | persistence test |
| Secrets | `WindowsDpapiSecretStore` | Windows deployment review |
| Logging redaction | `RollingFileLoggerProvider`; `SecurityRedactor` | repository review |
| Mock server | `tools/MockBulkFhirServer` | integration test |
| CI/release/MSIX/SBOM | `.github/workflows`; `installer` | repository script and CI |

Some qualification activities require a controlled Windows build agent and a real vendor sandbox; they are explicitly listed in `VALIDATION_REPORT.md` rather than represented as already completed.

