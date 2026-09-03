using System.Collections.ObjectModel;
using System.Text.Json;
using FhirBulkExportManager.App.Wpf.Presentation;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.App.Wpf;

public sealed class JobDetailViewModel(IExportStore exportStore, TimeProvider timeProvider) : ObservableObject
{
    private ExportJob? job;
    private string manifestText = string.Empty;
    private string diagnosticsText = string.Empty;
    private JobDashboardSnapshot dashboard = JobDashboardSnapshot.Empty;

    public ExportJob? Job { get => job; private set => SetProperty(ref job, value); }
    public ObservableCollection<ExportFile> Files { get; } = [];
    public ObservableCollection<ExportFile> ErrorFiles { get; } = [];
    public ObservableCollection<ExportJobEvent> Timeline { get; } = [];
    public string ManifestText { get => manifestText; private set => SetProperty(ref manifestText, value); }
    public string DiagnosticsText { get => diagnosticsText; private set => SetProperty(ref diagnosticsText, value); }
    public JobDashboardSnapshot Dashboard { get => dashboard; private set => SetProperty(ref dashboard, value); }

    public async Task LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        Job = await exportStore.GetJobAsync(jobId, cancellationToken).ConfigureAwait(true)
            ?? throw new KeyNotFoundException("Export job no longer exists.");
        var files = await exportStore.GetFilesAsync(jobId, cancellationToken).ConfigureAwait(true);
        var events = await exportStore.GetTimelineAsync(jobId, cancellationToken).ConfigureAwait(true);
        Replace(Files, files);
        Replace(ErrorFiles, files.Where(x => x.Category == ExportFileCategory.Error));
        Replace(Timeline, events);
        Dashboard = JobProgressAnalytics.Calculate(Job, events, files, timeProvider.GetUtcNow());
        ManifestText = PrettyJson(Job.ManifestJson);
        DiagnosticsText = JsonSerializer.Serialize(new
        {
            Job.Id,
            Job.State,
            Job.PluginId,
            Job.PluginVersion,
            Job.RetryCount,
            Job.LastErrorCode,
            Job.LastErrorMessage,
            Job.LeaseOwner,
            Job.LeaseExpiresAtUtc,
            FileCount = files.Count,
        }, JsonDefaults.Options);
    }

    private static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "Manifest has not been received.";
        }

        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, JsonDefaults.Options);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
