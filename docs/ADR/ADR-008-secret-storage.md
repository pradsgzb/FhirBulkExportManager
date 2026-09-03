# ADR-008: Windows DPAPI secrets

**Status:** Accepted

Protect client secrets with Windows DPAPI CurrentUser and store only opaque references in SQLite. Keep access tokens in memory. Do not build custom cryptography or application password storage.

