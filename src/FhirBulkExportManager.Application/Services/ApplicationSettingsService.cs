using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Application.Services;

public sealed class ApplicationSettingsService(
    IApplicationSettingsStore settingsStore,
    IWindowsIdentityProvider identityProvider,
    TimeProvider timeProvider)
{
    private const string PreferencesKey = "application.preferences.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool loaded;

    public ApplicationPreferences Current { get; private set; } = new();
    public string? LoadWarning { get; private set; }

    public async Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken)
    {
        if (loaded)
        {
            return Current;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (loaded)
            {
                return Current;
            }

            var value = await settingsStore.GetValueAsync(PreferencesKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(value))
            {
                Current = new ApplicationPreferences();
            }
            else
            {
                try
                {
                    Current = (JsonSerializer.Deserialize<ApplicationPreferences>(value, JsonDefaults.Options) ?? new ApplicationPreferences()).Normalize();
                }
                catch (JsonException)
                {
                    Current = new ApplicationPreferences();
                    LoadWarning = "Saved display and refresh preferences were invalid, so safe defaults were loaded. Saving an option will replace the invalid values.";
                }
            }

            loaded = true;
            return Current;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.Normalize();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (loaded && normalized == Current && LoadWarning is null)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            var correlationId = Guid.NewGuid().ToString("N");
            var audit = new AuditEvent(
                now,
                identityProvider.GetCurrentIdentity(),
                "ApplicationPreferencesUpdated",
                nameof(ApplicationSetting),
                PreferencesKey,
                "Succeeded",
                correlationId);
            var json = JsonSerializer.Serialize(normalized, JsonDefaults.Options);
            await settingsStore.SetValueAsync(PreferencesKey, json, audit, cancellationToken).ConfigureAwait(false);
            Current = normalized;
            LoadWarning = null;
            loaded = true;
        }
        finally
        {
            gate.Release();
        }
    }
}
