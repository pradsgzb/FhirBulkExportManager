# Job HTTP history

The Dashboard & Jobs screen uses each job's immutable `ExportJobEvent` timeline as a master-detail history. New network operations store a `JobEventMetadataContract` JSON envelope in the event's existing `MetadataJson` column, so this feature does not require a database migration.

Captured job operations include authentication, export kickoff, status polling, cancellation, cleanup verification, redirects, and file downloads. Control-response bodies are stored in the event metadata. Download response bodies remain in managed storage as the downloaded `.ndjson` file; the history records the file reference instead of duplicating potentially very large data in SQLite.

`X-Progress` is read case-insensitively and is the authoritative server progress text. Its exact value is stored with the relevant history entry and the latest non-empty value is shown on the job.

## Security boundary

Header names, body structure, timing, method, URI, and status are retained, but reusable credentials are never written to job history. Values for `Authorization`, cookies, API keys, OAuth tokens, client assertions, client secrets, refresh tokens, and passwords are stored as `[REDACTED]`. Sensitive query-string and JSON/form fields are treated the same way.

History capture begins after this patch is installed. Existing events remain visible, but headers and bodies that were not previously persisted cannot be reconstructed or backfilled.
