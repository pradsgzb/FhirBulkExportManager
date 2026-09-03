using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Application.Tests;

public sealed class ApplicationSettingsAndPasswordTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-03T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Preferences_are_normalized_and_persisted()
    {
        var store = new MemorySettingsStore();
        var service = new ApplicationSettingsService(store, new TestIdentityProvider(), new FixedTimeProvider(Now));

        await service.LoadAsync(CancellationToken.None);
        await service.SaveAsync(
            new ApplicationPreferences
            {
                DateMode = DateDisplayMode.Local,
                AutoRefreshEnabled = true,
                AutoRefreshIntervalSeconds = 999,
                MinimizeToTray = false,
            },
            CancellationToken.None);

        Assert.Equal(DateDisplayMode.Local, service.Current.DateMode);
        Assert.Equal(300, service.Current.AutoRefreshIntervalSeconds);
        Assert.True(service.Current.AutoRefreshEnabled);
        Assert.False(service.Current.MinimizeToTray);
        Assert.Single(store.Values);
        Assert.Equal("ApplicationPreferencesUpdated", Assert.Single(store.Audits).Action);
    }

    [Fact]
    public async Task Password_verifier_supports_change_and_command_line_reset_without_storing_plaintext()
    {
        var store = new MemorySettingsStore();
        var service = new PasswordProtectionService(store, new TestIdentityProvider(), new FixedTimeProvider(Now));
        const string original = "correct horse battery staple";
        const string replacement = "new correct horse battery staple";

        await service.SetOrChangeAsync(null, original, CancellationToken.None);

        Assert.True(await service.IsConfiguredAsync(CancellationToken.None));
        Assert.True(await service.VerifyAsync(original, CancellationToken.None));
        Assert.False(await service.VerifyAsync("incorrect password", CancellationToken.None));
        Assert.DoesNotContain(original, Assert.Single(store.Values).Value, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetOrChangeAsync("incorrect password", replacement, CancellationToken.None));

        await service.SetOrChangeAsync(original, replacement, CancellationToken.None);
        Assert.True(await service.VerifyAsync(replacement, CancellationToken.None));
        Assert.False(await service.VerifyAsync(original, CancellationToken.None));

        await service.ResetAsync(CancellationToken.None);
        Assert.False(await service.IsConfiguredAsync(CancellationToken.None));
        Assert.Equal("ApplicationPasswordResetFromCommandLine", store.Audits[^1].Action);
    }

    private sealed class MemorySettingsStore : IApplicationSettingsStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public List<AuditEvent> Audits { get; } = [];

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[key] = value;
            Audits.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Remove(key);
            Audits.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class TestIdentityProvider : IWindowsIdentityProvider
    {
        public string GetCurrentIdentity() => "test-user";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
