# Implementation status — 1.0.0 source baseline

## Implemented in source

- 21 populated .NET projects in one Visual Studio solution
- WPF Generic Host composition and MVVM operator UI
- vendor/site/credential setup and non-exporting connection test
- DPAPI user-scoped secret storage
- normalized SQLite control-plane model, WAL startup, migration, backup, leases, optimistic concurrency
- explicit domain state machine and immutable job timeline
- durable ambiguous-kickoff strategy and exact Content-Location storage
- eClinicalWorks OAuth/HealthWizz configurable assertion chain
- anonymous public FHIR R4 plugin with seeded HAPI, SMART R4, and SMART Bulk Data test vendors
- standard Bulk Data kickoff/poll/manifest/cancel/cleanup behavior
- bounded fair export scheduler and per-site download limiting
- streaming partial downloads, secure redirects, safe paths, disk reserve
- streaming UTF-8 NDJSON and OperationOutcome validation
- SHA-256, checksums file, atomic local commit, cleanup gate and verification
- sanitized logs and diagnostic bundle service
- deterministic mock FHIR server and nine test projects
- CI, CodeQL, dependency/secret scanning, MSIX, signing hooks, SBOM, checksums
- architecture, security, recovery, deployment, testing, plugin, and troubleshooting documents

## Release qualification still required

- Run restore/build/all tests on a Windows machine with .NET 10 SDK and NuGet access.
- Resolve any compiler/analyzer/package changes exposed by that authoritative build.
- Execute eClinicalWorks sandbox certification with the issued endpoint and assertion schema.
- Run forced-crash, multi-gigabyte, low-disk, contention, proxy/TLS, and multi-day soak tests.
- Replace fallback MSIX artwork with approved brand assets.
- Sign through the organization's protected certificate process.
- Perform clinical-site security, privacy, backup/restore, accessibility, and installer upgrade acceptance.

This distinction is intentional: complete source is provided, but “production-qualified” is a deployment decision supported by recorded evidence, not a label applied without executing the required environment-specific tests.
