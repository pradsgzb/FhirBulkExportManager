# Deployment

Use the release workflow or these stages on a controlled Windows runner:

1. Restore exact centrally pinned dependencies.
2. Run dependency vulnerability audit.
3. Build the complete solution in Release with warnings as errors.
4. Run all tests and repository invariant checks.
5. Publish self-contained `win-x64` output.
6. Generate an SBOM.
7. Build MSIX using `installer/Build-Msix.ps1`.
8. Sign MSIX using an enterprise certificate supplied outside source control.
9. Generate SHA-256 checksums.
10. Pilot installation/upgrade/uninstall with active-job and retained-data tests.

The MSIX publisher must match the certificate subject. Never commit a PFX or password. Durable state and data must remain outside the versioned installation directory. Back up both planes before major upgrades, confirm the new binary recognizes the schema and pinned plugins, then permit workers to recover.

