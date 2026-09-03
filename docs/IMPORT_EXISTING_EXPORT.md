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

1. The application validates the selected configuration and the pasted URL.
2. It rejects a `Content-Location` already assigned to another local job.
3. It stores the URL without rebuilding or changing its opaque query string.
4. It creates the job directly in `WaitingForPoll` and queues an immediate status request.
5. The normal polling, manifest persistence, download, validation, commit, and optional cleanup workflow continues.

The import workflow never calls `$export`. The original Postman exchange cannot be reconstructed automatically; HTTP exchanges performed by this application after import are recorded using the normal history and redaction rules.

For safety, remote URLs must use HTTPS. HTTP is accepted only for loopback test endpoints, and URLs containing embedded credentials or fragments are rejected.
