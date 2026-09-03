# Security

## Protected data

FHIR exports may contain PHI. Operators must place the data root on an organization-managed encrypted Windows volume, restrict Windows account access, apply approved retention, and include both SQLite and exported files in backup/incident plans.

- OAuth secrets are encrypted using Windows DPAPI `CurrentUser`; only references enter SQLite.
- Access tokens remain in memory and expire from the token cache.
- TLS validation is enabled. The production UI accepts HTTPS endpoints only.
- Logs redact authorization, secret, password, refresh-token, and assertion patterns and do not log FHIR bodies.
- NDJSON is created only below a deterministic managed root using generated filenames.
- Cross-origin HTTP redirects never receive an existing Authorization header.
- Plugin loading requires compatibility plus an allowlisted or manifest-matching assembly hash unless an administrator deliberately changes the unsigned-plugin policy.
- Toast/UI messages contain operational identifiers, not resource bodies.
- Transitive NuGet dependencies are centrally pinned and audited in CI. `System.Security.Cryptography.Xml` is pinned to `10.0.11`, above the `10.0.10` fixed-version floor for [GHSA-23rf-6693-g89p](https://github.com/advisories/GHSA-23rf-6693-g89p).

## Threat boundaries

| Threat | Control |
| --- | --- |
| Token exfiltration by redirect | Disable automatic redirects; strip Authorization on origin change |
| Path traversal in manifest URL | Ignore remote path for naming; generated safe file names and root containment checks |
| Duplicate remote kickoff | Durable pre-dispatch journal; ambiguous result becomes `Unknown` |
| Premature remote deletion | Re-hash and verify local commit immediately before DELETE |
| Corrupt/truncated local file | `.partial`, content-length/range checks, SHA-256, streaming JSON validation |
| Malicious plugin | Controlled directory, compatibility and hash trust; plugins documented as full-trust code |
| Secret leakage in support data | Diagnostic bundle contains state counts/sanitized settings only |
| SQLite corruption/contention | WAL, busy timeout, short transactions, version tokens, backups |

Assembly isolation is not a sandbox. Only organization-approved plugin binaries should be installed.
