# eClinicalWorks plugin

The eClinicalWorks adapter supports:

- direct OAuth client credentials;
- a configurable HealthWizz assertion request followed by eCW OAuth exchange;
- FHIR `/metadata` connection testing;
- System, Group, and Patient Bulk Data kickoff;
- seconds and HTTP-date `Retry-After`;
- manifest preservation/normalization;
- authorized same-origin downloads;
- remote cancellation and post-commit cleanup;
- cleanup verification by 404.

The exact HealthWizz assertion schema varies by integration and was not invented in source. When `AssertionEndpoint` is populated, `AssertionRequestFields` must be configured from vendor documentation. Response property and extra token fields are configurable. Secret template substitution occurs only in memory.

For production onboarding, validate endpoint URLs, OAuth scope/audience, export types, retention/DELETE behavior, download host behavior, and rate limits with eClinicalWorks before activation.

