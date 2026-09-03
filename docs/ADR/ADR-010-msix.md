# ADR-010: MSIX packaging

**Status:** Accepted

Use self-contained Windows publish plus signed MSIX for install/upgrade/uninstall. Keep durable state outside the package installation path and inject signing credentials only through protected release infrastructure.

