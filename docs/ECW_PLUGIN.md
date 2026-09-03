# eClinicalWorks plugin

The eClinicalWorks adapter supports:

- direct OAuth client credentials as a fallback;
- the HealthWizz JSON assertion request followed by eCW OAuth exchange without a client secret;
- anonymous FHIR `/metadata` connection testing;
- System, Group, and Patient Bulk Data kickoff;
- seconds and HTTP-date `Retry-After`;
- manifest preservation/normalization;
- authorized same-origin downloads;
- remote cancellation and post-commit cleanup;
- cleanup verification by 404.

When `AssertionEndpoint` is populated, the default request is JSON containing `clientId` and `aud`; `aud` defaults to `TokenEndpoint` when no explicit Audience is configured. The HealthWizz response may be a raw JWT (the normal eCW flow), a JSON string, or a JSON property selected by `AssertionResponseProperty`. The eCW token exchange sends `grant_type`, `client_assertion_type`, `client_assertion`, and the configured `scope`; it does not send `client_id` or `client_secret` in assertion mode.

`AssertionRequestFields` and `TokenAdditionalFields` remain optional advanced overrides. A client secret is only loaded and sent when no HealthWizz assertion endpoint is configured. The metadata-only connection test does not call either authentication endpoint and does not send an Authorization header.

For production onboarding, validate endpoint URLs, OAuth scope/audience, export types, retention/DELETE behavior, download host behavior, and rate limits with eClinicalWorks before activation.
