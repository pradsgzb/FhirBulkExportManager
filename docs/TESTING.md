# Testing

The solution includes test projects for Domain, Application/concurrency, Infrastructure, SQLite persistence/migration, eClinicalWorks, end-to-end protocol integration, architecture boundaries, and WPF composition.

Covered safety cases include:

- Draft does not imply a remote export;
- illegal domain transitions;
- exact opaque `Content-Location` retention;
- polling blocked without `Content-Location`;
- Retry-After seconds and HTTP-date;
- configured 20 workers on 8 cores resolves to 8;
- fair cross-site selection;
- per-site configuration above four is rejected;
- truncated byte count cannot become Downloaded;
- Downloaded and Validated remain distinct;
- valid/invalid streaming NDJSON;
- serious OperationOutcome detection;
- atomic commit generation/revalidation;
- SQLite migration and restart durability;
- duplicate resource-type files in a manifest;
- deterministic `202 → 202 → 200 → DELETE → 404` integration lifecycle;
- architecture dependency rules;
- WPF command composition.

Before production, also execute vendor sandbox qualification, large sparse/streamed file tests, forced process termination at each durable boundary, low-disk injection, SQLite contention, network fault injection, multi-day soak testing, accessibility review, and signed installer upgrade/uninstall tests.

