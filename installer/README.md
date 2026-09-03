# Windows packaging

The release pipeline publishes a self-contained `win-x64` WPF application and packages it as MSIX with `Build-Msix.ps1`. The publisher string must exactly match the enterprise code-signing certificate subject. Certificates and passwords are supplied only through protected release secrets and are never stored in this repository.

The MSIX installation directory contains binaries only. SQLite state, protected secrets, logs, and exported FHIR data remain under the configured durable locations in the current user's local application data directory, so install, upgrade, and uninstall do not silently delete healthcare exports.
