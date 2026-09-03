# Status recovery, metadata discovery, and HTTP copying

## Recover a failed remote status request

`FailedPermanent` is an operator-action state, not a terminal state. It is deliberately not retried automatically because repeated 400 or authorization responses can create unnecessary traffic.

When a job has a persisted `Content-Location` and has not received a manifest, select the job and choose **Get Status Now**. The application clears the current failure, moves the same job back to `Running`, and performs a GET against the original exact `Content-Location`. It does not submit another `$export` request.

The status action remains unavailable for `FailedPermanent` jobs that already have a manifest because those failures belong to download, validation, or commit processing rather than remote-status polling.

## CapabilityStatement and OAuth token endpoint

**Test Selected (/metadata only)** performs an unauthenticated GET against:

```text
{FHIR base URL}/metadata
```

Every metadata test attempt is appended to the connection's local metadata history, including its outcome, timestamp, redacted request headers/body, and redacted response headers/body. The latest successful CapabilityStatement is also retained for direct copying.

The application searches `CapabilityStatement.rest[*].security.extension` recursively for the SMART `oauth-uris` token extension. When an authenticated connection advertises a valid HTTPS token URL, the credential profile's OAuth token endpoint and the metadata history are committed in one SQLite transaction. Any cached access token is invalidated. If no token URL is advertised, the existing configured token endpoint is retained as a fallback.

Metadata history uses the existing `PluginConfigurations` table, so this patch requires no database migration.

## Copy HTTP diagnostics

The job HTTP inspector provides:

- **Copy Request**
- **Copy Response**
- **Copy Request + Response**

The Connections tab provides:

- **Copy Saved Metadata JSON**
- **Copy Metadata HTTP History**

Copied HTTP text includes the request line, response status, headers, body, timing, and failure details when available. Reusable credentials—including bearer tokens, cookies, client assertions, and secrets—remain redacted before persistence and copying.

Error responses are captured whether the server returns JSON, XML, HTML, or plain text. The body is displayed as text in the inspector (HTML is never executed), and a declared response charset is honored. Status text is derived from the numeric status code, so a malformed server response such as `400 OK` is displayed as `400 Bad Request`; the numeric status remains authoritative.
