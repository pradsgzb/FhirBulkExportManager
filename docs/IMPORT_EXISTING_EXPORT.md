# Import an Existing Bulk Export

Use **Import Existing Export** when a FHIR Bulk Data `$export` request was already submitted outside the application, such as through Postman.

## Required information

- Friendly job name
- Vendor, site, and credential profile
- Exact `Content-Location` returned by the kickoff response
- Export type and Group ID when importing a Group export
- Local export options, including priority and optional automatic cleanup

The remote start time, `_since`, `_type`, and `_typeFilter` values are optional metadata. They do not cause a new kickoff request.

## Processing behavior

1. The application validates the selected configuration and requires the pasted URL to have the same scheme, host, and port as the selected site's FHIR base URL.
2. It rejects a `Content-Location` already assigned to another local job.
3. It stores the URL without rebuilding or changing its opaque query string.
4. It creates the job directly in `WaitingForPoll` and queues an immediate status request.
5. The normal polling, manifest persistence, download, validation, commit, and optional cleanup workflow continues.

The import workflow never calls `$export`. The original Postman exchange cannot be reconstructed automatically; HTTP exchanges performed by this application after import are recorded using the normal history and redaction rules.

For safety, remote URLs must use HTTPS. HTTP is accepted only for loopback test endpoints, and URLs containing embedded credentials or fragments are rejected.

## Repair an imported job that used the wrong connection

An imported job keeps the vendor plugin, site, and credential selected when it was imported. If an authenticated `Content-Location` was accidentally imported with an anonymous Public/Open FHIR connection, its status request has no bearer token and the server will return `401 Unauthorized`.

To repair that existing job without starting another export:

1. Select the affected job on **Dashboard & Jobs**. The **Plugin** column helps identify an incorrect `public-fhir-r4` assignment.
2. Open **Import Existing Export**.
3. Select the authenticated vendor and the exact site connection that was used to start the export. The credential is taken from that site.
4. Select **Repair Selected Imported Job with This Connection** and confirm.

The repair is allowed only for an externally started job that still has no manifest. It preserves the exact `Content-Location` and all existing history, records an `ImportedConnectionReassigned` history event, clears the prior retry error, and queues an authenticated status request. It never sends `$export`.

If an anonymous Public/Open FHIR connection receives `401`, the job now stops automatic authentication retries with `AnonymousConnectionCannotAuthenticate` so the configuration error is visible instead of looping indefinitely.
