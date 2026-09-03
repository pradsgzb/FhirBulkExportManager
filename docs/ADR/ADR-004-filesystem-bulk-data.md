# ADR-004: Filesystem bulk-data plane

**Status:** Accepted

Store large NDJSON on a managed filesystem and only metadata in SQLite. This keeps memory and database size bounded and enables streaming, partial recovery, atomic rename, and independent integrity checks.

