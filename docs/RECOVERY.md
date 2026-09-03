# Recovery

Startup performs these gates before processing:

1. Verify local database path and compatible schema.
2. Back up and apply tracked migrations when required.
3. Enable SQLite WAL/busy timeout/foreign keys.
4. Load only trusted compatible plugins.
5. Clear expired worker leases.
6. Reconstruct work from non-terminal persisted states.
7. Never enqueue Draft, Paused, Held-for-review, or Unknown jobs automatically.

Interrupted `Downloading` or `Validating` files are moved to file-level retry. A `.partial` file resumes only when a validator (ETag or Last-Modified) makes Range safe; otherwise the next response overwrites/restarts it. A file is not renamed to its final name until the complete byte count and SHA-256 are known.

If restart finds `Starting` plus a pre-dispatch kickoff journal but no `Content-Location`, the job becomes `Unknown`. An operator must reconcile with the vendor instead of allowing an automatic duplicate kickoff.

If cleanup fails, the committed export remains locally valid. Cleanup retry/verification state does not downgrade the integrity of already committed files.

