# CI/CD

`ci.yml` runs on each pull request and main-branch push: restore, vulnerable-package audit, Release build, all tests with coverage, repository checks, result upload, and an independent history-wide secret scan.

`codeql.yml` runs C# CodeQL on pull requests, main, and weekly. Dependabot monitors NuGet and GitHub Actions.

`release.yml` repeats qualification before publishing. It creates a self-contained WPF build, SBOM, MSIX, optional signature, and SHA-256. Only artifacts from the successful qualification job are uploaded/released. Use protected production environments and required reviewers for signing secrets.

