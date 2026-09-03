# ADR-003: SQLite WAL control plane

**Status:** Accepted

Use local-disk SQLite with EF Core, WAL, busy timeout, short transactions, optimistic concurrency, and leases. It provides durable low-administration state without pretending to be distributed coordination.

