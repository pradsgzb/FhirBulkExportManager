# ADR-006: Bounded fair scheduler

**Status:** Accepted

Use bounded channels, asynchronous workers, round-robin site selection, persisted due times, and atomic job leases. Enforce an effective worker ceiling no higher than logical processor count.

