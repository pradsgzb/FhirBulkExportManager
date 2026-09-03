# Concurrency and scheduling

The scheduler uses a bounded `Channel<Guid>` and a fixed number of asynchronous consumers. The worker count is always `min(configured, logical processors)` with a minimum of one. Job acquisition uses an atomic SQLite lease; optimistic concurrency protects subsequent updates.

Candidates are grouped by Site and selected round-robin so a site with a large backlog cannot indefinitely starve another site. Within a site, higher priority is considered first, followed by due/creation time. Processing one durable step at a time also returns scheduling opportunity to other sites.

Downloads use keyed per-site semaphores. The effective site limit is:

```text
min(user setting, plugin maximum, 4)
```

The limit is aggregate across all jobs for that Site—not four multiplied by job count. HTTP and disk work use asynchronous streaming; no operating-system thread is dedicated to each job or file.

