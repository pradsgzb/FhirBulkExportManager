# Plugin development

Implement `IVendorPlugin` from `FhirBulkExportManager.PluginContracts`. The contract covers authentication, connection test, kickoff URI construction, kickoff, polling, exact-manifest normalization, download-request creation, cancellation, cleanup, and cleanup verification.

Rules:

1. Do not reference EF Core entities or WPF.
2. Treat `Content-Location` as opaque and return the exact absolute value supplied by the server.
3. Do not log tokens, assertions, client secrets, FHIR bodies, or patient identifiers.
4. Use the provided `TimeProvider` for scheduling decisions.
5. Return typed dispositions; do not require the core to parse exception messages.
6. Do not automatically forward Authorization to unrelated download hosts.
7. Preserve every manifest entry, including repeated resource types.
8. DELETE only the exact status URI for cancel/cleanup.

External plugin layout:

```text
Plugins/<vendor>/
  plugin.json
  Vendor.Plugin.dll
  dependencies...
```

`plugin.json` declares ID, semantic version, host range, publisher, entry assembly, and SHA-256. External plugins currently require one public parameterless `IVendorPlugin` implementation; built-in plugins may use dependency injection. The release process should update the manifest hash after building and signing.

