# Threat model summary

Assets include OAuth credentials, access tokens, FHIR/PHI files, manifests, job state, audit evidence, plugins, and release artifacts. Trust boundaries exist at Windows identity/DPAPI, vendor HTTPS endpoints, download redirects, external plugins, local storage ACLs, backup systems, CI dependencies, and operator destructive actions.

Highest-risk abuse cases are bearer-token disclosure, malicious redirect, duplicate kickoff, premature cleanup, path traversal, truncated data accepted as complete, malicious plugin execution, secret/PHI logging, local disk exhaustion, and database rollback/corruption. Preventive controls and residual operational responsibilities are detailed in `docs/SECURITY.md`.

Review this model whenever authentication, plugin trust, storage destination, diagnostic collection, background-service hosting, or cleanup behavior changes.
