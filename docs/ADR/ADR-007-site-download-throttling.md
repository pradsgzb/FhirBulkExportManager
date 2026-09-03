# ADR-007: Per-site download throttling

**Status:** Accepted

Use keyed semaphores aggregated by Site. The effective limit is the minimum of user, plugin, and hard limit four. This prevents multiple jobs from multiplying a site's concurrency.

