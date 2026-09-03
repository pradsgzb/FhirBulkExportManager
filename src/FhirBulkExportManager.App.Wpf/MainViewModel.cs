using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using FhirBulkExportManager.App.Wpf.Presentation;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Application.Models;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.App.Wpf;

public sealed class MainViewModel : ObservableObject
{
    private readonly ExportJobService jobService;
    private readonly IConfigurationStore configurationStore;
    private readonly ISecretStore secretStore;
    private readonly IWindowsIdentityProvider identityProvider;
    private readonly IVendorPluginCatalog pluginCatalog;
    private readonly IManagedStorage storage;
    private readonly ILocalCommitService commitService;
    private readonly IUserDialogService dialogs;
    private readonly IWindowNavigationService navigation;
    private readonly TimeProvider timeProvider;
    private ExportJob? selectedJob;
    private JobStatusHistoryItem? selectedStatusHistory;
    private HttpExchangeViewItem? selectedHttpExchange;
    private string jobDetailStatus = "Select a job to inspect its history.";
    private long jobDetailLoadGeneration;
    private Vendor? selectedVendor;
    private Site? selectedSite;
    private CredentialProfile? selectedCredential;
    private Vendor? selectedImportVendor;
    private Site? selectedImportSite;
    private CredentialProfile? selectedImportCredential;
    private Vendor? selectedConnectionVendor;
    private Site? selectedConnectionSite;
    private string searchText = string.Empty;
    private string statusMessage = "Ready";
    private string newJobName = string.Empty;
    private string newJobDescription = string.Empty;
    private ExportType newExportType = ExportType.Group;
    private string newGroupId = string.Empty;
    private string newSinceUtc = string.Empty;
    private string newResourceTypes = "Patient,Encounter,Observation,Condition,MedicationRequest";
    private string newTypeFilters = string.Empty;
    private string newVendorOptionsJson = "{}";
    private JobPriority newPriority = JobPriority.Normal;
    private bool newAutomaticCleanup;
    private string importJobName = string.Empty;
    private string importJobDescription = string.Empty;
    private string importContentLocation = string.Empty;
    private string importRemoteStartedUtc = string.Empty;
    private ExportType importExportType = ExportType.Group;
    private string importGroupId = string.Empty;
    private string importSinceUtc = string.Empty;
    private string importResourceTypes = "Patient,Encounter,Observation,Condition,MedicationRequest";
    private string importTypeFilters = string.Empty;
    private string importVendorOptionsJson = "{}";
    private JobPriority importPriority = JobPriority.Normal;
    private bool importAutomaticCleanup;
    private string connectionSiteName = string.Empty;
    private string connectionFhirBaseUrl = string.Empty;
    private string connectionClientId = string.Empty;
    private string connectionClientSecret = string.Empty;
    private string connectionTokenEndpoint = string.Empty;
    private string connectionAssertionEndpoint = string.Empty;
    private string connectionScope = string.Empty;
    private string connectionAudience = string.Empty;
    private string connectionAuthConfigurationJson = "{}";

    public MainViewModel(
        ExportJobService jobService,
        IConfigurationStore configurationStore,
        ISecretStore secretStore,
        IWindowsIdentityProvider identityProvider,
        IVendorPluginCatalog pluginCatalog,
        IManagedStorage storage,
        ILocalCommitService commitService,
        IUserDialogService dialogs,
        IWindowNavigationService navigation,
        IOptions<ProcessingOptions> processingOptions,
        IOptions<StorageOptions> storageOptions,
        TimeProvider timeProvider)
    {
        this.jobService = jobService;
        this.configurationStore = configurationStore;
        this.secretStore = secretStore;
        this.identityProvider = identityProvider;
        this.pluginCatalog = pluginCatalog;
        this.storage = storage;
        this.commitService = commitService;
        this.dialogs = dialogs;
        this.navigation = navigation;
        this.timeProvider = timeProvider;
        ConfiguredExportLimit = processingOptions.Value.MaxConcurrentExports;
        EffectiveExportLimit = processingOptions.Value.EffectiveMaxConcurrentExports;
        DetectedLogicalProcessors = Environment.ProcessorCount;
        PerSiteDownloadLimit = processingOptions.Value.PerSiteFileDownloadLimit;
        DataRoot = storageOptions.Value.DataRoot;
        MinimumFreeDiskSpaceGB = storageOptions.Value.MinimumFreeDiskSpaceGB;

        RefreshCommand = new AsyncRelayCommand(() => RunAsync(() => RefreshAsync()));
        SearchCommand = new AsyncRelayCommand(() => RunAsync(() => RefreshJobsAsync()));
        CreateJobCommand = new AsyncRelayCommand(() => RunAsync(CreateJobAsync));
        ImportExistingCommand = new AsyncRelayCommand(() => RunAsync(ImportExistingExportAsync));
        StartCommand = new AsyncRelayCommand(() => RunAsync(() => RunSelectedAsync(job => jobService.StartAsync(job.Id, CancellationToken.None))), () => SelectedJob?.State == ExportJobState.Draft);
        StatusNowCommand = new AsyncRelayCommand(() => RunAsync(() => RunSelectedAsync(job => jobService.GetStatusNowAsync(job.Id, CancellationToken.None))), () => SelectedJob is { HasConfirmedRemoteJob: true, State: ExportJobState.WaitingForPoll or ExportJobState.Running or ExportJobState.RetryPending });
        PauseCommand = new AsyncRelayCommand(() => RunAsync(() => RunSelectedAsync(job => jobService.PauseAsync(job.Id, CancellationToken.None))), () => SelectedJob?.State is ExportJobState.Queued or ExportJobState.Authenticating or ExportJobState.Running or ExportJobState.WaitingForPoll or ExportJobState.QueuedForDownload or ExportJobState.Downloading);
        ResumeCommand = new AsyncRelayCommand(() => RunAsync(() => RunSelectedAsync(job => jobService.ResumeAsync(job.Id, CancellationToken.None))), () => SelectedJob?.State == ExportJobState.Paused);
        CancelRemoteCommand = new AsyncRelayCommand(() => RunAsync(CancelRemoteAsync), () => SelectedJob is { HasConfirmedRemoteJob: true, State: ExportJobState.Running or ExportJobState.WaitingForPoll or ExportJobState.Paused or ExportJobState.Unknown });
        CleanupRemoteCommand = new AsyncRelayCommand(() => RunAsync(CleanupRemoteAsync), () => SelectedJob?.State == ExportJobState.Committed);
        ApprovePartialCommand = new AsyncRelayCommand(() => RunAsync(ApprovePartialAsync), () => SelectedJob?.State == ExportJobState.HeldForReview);
        ArchiveCommand = new AsyncRelayCommand(() => RunAsync(() => RunSelectedAsync(job => jobService.ArchiveAsync(job.Id, CancellationToken.None))), () => SelectedJob?.State is ExportJobState.Draft or ExportJobState.Completed or ExportJobState.Cancelled or ExportJobState.Expired or ExportJobState.FailedPermanent or ExportJobState.Committed or ExportJobState.HeldForReview);
        DeleteLocalCommand = new AsyncRelayCommand(() => RunAsync(DeleteLocalAsync), () => SelectedJob?.State is ExportJobState.Draft or ExportJobState.Archived or ExportJobState.Completed or ExportJobState.Cancelled or ExportJobState.Expired or ExportJobState.FailedPermanent);
        OpenFolderCommand = new AsyncRelayCommand(() => RunAsync(OpenFolderAsync), () => SelectedJob is not null);
        DetailsCommand = new AsyncRelayCommand(() => RunAsync(ShowDetailsAsync), () => SelectedJob is not null);
        SaveConnectionCommand = new AsyncRelayCommand(() => RunAsync(SaveConnectionAsync), () => SelectedConnectionVendor is not null);
        TestConnectionCommand = new AsyncRelayCommand(() => RunAsync(TestConnectionAsync), () => SelectedConnectionSite is not null);
    }

    public ObservableCollection<ExportJob> Jobs { get; } = [];
    public ObservableCollection<Vendor> Vendors { get; } = [];
    public ObservableCollection<Site> Sites { get; } = [];
    public ObservableCollection<CredentialProfile> Credentials { get; } = [];
    public ObservableCollection<JobStatusHistoryItem> SelectedJobHistory { get; } = [];
    public ObservableCollection<HttpExchangeViewItem> SelectedHttpExchanges { get; } = [];
    public Array ExportTypes => Enum.GetValues(typeof(ExportType));
    public Array Priorities => Enum.GetValues(typeof(JobPriority));

    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand CreateJobCommand { get; }
    public ICommand ImportExistingCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StatusNowCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelRemoteCommand { get; }
    public ICommand CleanupRemoteCommand { get; }
    public ICommand ApprovePartialCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand DeleteLocalCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand SaveConnectionCommand { get; }
    public ICommand TestConnectionCommand { get; }

    public int ConfiguredExportLimit { get; }
    public int EffectiveExportLimit { get; }
    public int DetectedLogicalProcessors { get; }
    public int PerSiteDownloadLimit { get; }
    public string DataRoot { get; }
    public int MinimumFreeDiskSpaceGB { get; }
    public bool MinimizeToTray { get; set; } = true;

    public ExportJob? SelectedJob
    {
        get => selectedJob;
        set
        {
            if (SetProperty(ref selectedJob, value))
            {
                CommandManager.InvalidateRequerySuggested();
                var generation = Interlocked.Increment(ref jobDetailLoadGeneration);
                _ = LoadSelectedJobHistoryAsync(value?.Id, generation);
            }
        }
    }

    public JobStatusHistoryItem? SelectedStatusHistory
    {
        get => selectedStatusHistory;
        set
        {
            if (SetProperty(ref selectedStatusHistory, value))
            {
                Replace(SelectedHttpExchanges, value?.HttpExchanges ?? []);
                SelectedHttpExchange = SelectedHttpExchanges.FirstOrDefault();
            }
        }
    }

    public HttpExchangeViewItem? SelectedHttpExchange
    {
        get => selectedHttpExchange;
        set => SetProperty(ref selectedHttpExchange, value);
    }

    public string JobDetailStatus { get => jobDetailStatus; private set => SetProperty(ref jobDetailStatus, value); }

    public Vendor? SelectedVendor
    {
        get => selectedVendor;
        set
        {
            if (SetProperty(ref selectedVendor, value) && value is not null)
            {
                SelectedSite = Sites.FirstOrDefault(x => x.VendorId == value.Id);
                SelectedCredential = Credentials.FirstOrDefault(x => x.VendorId == value.Id);
            }
        }
    }
    public Site? SelectedSite { get => selectedSite; set => SetProperty(ref selectedSite, value); }
    public CredentialProfile? SelectedCredential { get => selectedCredential; set => SetProperty(ref selectedCredential, value); }
    public Vendor? SelectedImportVendor
    {
        get => selectedImportVendor;
        set
        {
            if (SetProperty(ref selectedImportVendor, value) && value is not null)
            {
                SelectedImportSite = Sites.FirstOrDefault(x => x.VendorId == value.Id);
                SelectedImportCredential = Credentials.FirstOrDefault(x =>
                    x.Id == SelectedImportSite?.CredentialProfileId && x.VendorId == value.Id)
                    ?? Credentials.FirstOrDefault(x => x.VendorId == value.Id);
            }
        }
    }
    public Site? SelectedImportSite
    {
        get => selectedImportSite;
        set
        {
            if (SetProperty(ref selectedImportSite, value) && value is not null)
            {
                SelectedImportCredential = Credentials.FirstOrDefault(x => x.Id == value.CredentialProfileId)
                    ?? Credentials.FirstOrDefault(x => x.VendorId == value.VendorId);
            }
        }
    }
    public CredentialProfile? SelectedImportCredential { get => selectedImportCredential; set => SetProperty(ref selectedImportCredential, value); }
    public Vendor? SelectedConnectionVendor { get => selectedConnectionVendor; set { if (SetProperty(ref selectedConnectionVendor, value)) CommandManager.InvalidateRequerySuggested(); } }
    public Site? SelectedConnectionSite { get => selectedConnectionSite; set { if (SetProperty(ref selectedConnectionSite, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string SearchText { get => searchText; set => SetProperty(ref searchText, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string NewJobName { get => newJobName; set => SetProperty(ref newJobName, value); }
    public string NewJobDescription { get => newJobDescription; set => SetProperty(ref newJobDescription, value); }
    public ExportType NewExportType { get => newExportType; set => SetProperty(ref newExportType, value); }
    public string NewGroupId { get => newGroupId; set => SetProperty(ref newGroupId, value); }
    public string NewSinceUtc { get => newSinceUtc; set => SetProperty(ref newSinceUtc, value); }
    public string NewResourceTypes { get => newResourceTypes; set => SetProperty(ref newResourceTypes, value); }
    public string NewTypeFilters { get => newTypeFilters; set => SetProperty(ref newTypeFilters, value); }
    public string NewVendorOptionsJson { get => newVendorOptionsJson; set => SetProperty(ref newVendorOptionsJson, value); }
    public JobPriority NewPriority { get => newPriority; set => SetProperty(ref newPriority, value); }
    public bool NewAutomaticCleanup { get => newAutomaticCleanup; set => SetProperty(ref newAutomaticCleanup, value); }
    public string ImportJobName { get => importJobName; set => SetProperty(ref importJobName, value); }
    public string ImportJobDescription { get => importJobDescription; set => SetProperty(ref importJobDescription, value); }
    public string ImportContentLocation { get => importContentLocation; set => SetProperty(ref importContentLocation, value); }
    public string ImportRemoteStartedUtc { get => importRemoteStartedUtc; set => SetProperty(ref importRemoteStartedUtc, value); }
    public ExportType ImportExportType { get => importExportType; set => SetProperty(ref importExportType, value); }
    public string ImportGroupId { get => importGroupId; set => SetProperty(ref importGroupId, value); }
    public string ImportSinceUtc { get => importSinceUtc; set => SetProperty(ref importSinceUtc, value); }
    public string ImportResourceTypes { get => importResourceTypes; set => SetProperty(ref importResourceTypes, value); }
    public string ImportTypeFilters { get => importTypeFilters; set => SetProperty(ref importTypeFilters, value); }
    public string ImportVendorOptionsJson { get => importVendorOptionsJson; set => SetProperty(ref importVendorOptionsJson, value); }
    public JobPriority ImportPriority { get => importPriority; set => SetProperty(ref importPriority, value); }
    public bool ImportAutomaticCleanup { get => importAutomaticCleanup; set => SetProperty(ref importAutomaticCleanup, value); }
    public string ConnectionSiteName { get => connectionSiteName; set => SetProperty(ref connectionSiteName, value); }
    public string ConnectionFhirBaseUrl { get => connectionFhirBaseUrl; set => SetProperty(ref connectionFhirBaseUrl, value); }
    public string ConnectionClientId { get => connectionClientId; set => SetProperty(ref connectionClientId, value); }
    public string ConnectionClientSecret { get => connectionClientSecret; set => SetProperty(ref connectionClientSecret, value); }
    public string ConnectionTokenEndpoint { get => connectionTokenEndpoint; set => SetProperty(ref connectionTokenEndpoint, value); }
    public string ConnectionAssertionEndpoint { get => connectionAssertionEndpoint; set => SetProperty(ref connectionAssertionEndpoint, value); }
    public string ConnectionScope { get => connectionScope; set => SetProperty(ref connectionScope, value); }
    public string ConnectionAudience { get => connectionAudience; set => SetProperty(ref connectionAudience, value); }
    public string ConnectionAuthConfigurationJson { get => connectionAuthConfigurationJson; set => SetProperty(ref connectionAuthConfigurationJson, value); }

    public int DraftCount => Jobs.Count(x => x.State == ExportJobState.Draft);
    public int ActiveCount => Jobs.Count(x => !x.IsTerminal && x.State is not ExportJobState.Draft and not ExportJobState.Paused and not ExportJobState.HeldForReview and not ExportJobState.Unknown);
    public int DownloadingCount => Jobs.Count(x => x.State is ExportJobState.QueuedForDownload or ExportJobState.Downloading or ExportJobState.Validating);
    public int HeldCount => Jobs.Count(x => x.State == ExportJobState.HeldForReview);
    public int FailedCount => Jobs.Count(x => x.State is ExportJobState.FailedPermanent or ExportJobState.Expired or ExportJobState.Unknown);
    public int CompletedCount => Jobs.Count(x => x.State == ExportJobState.Completed);
    public bool HasActiveJobs => ActiveCount > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken) => await RefreshAsync(cancellationToken).ConfigureAwait(true);

    public void SetPassword(string value) => ConnectionClientSecret = value;

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var vendors = await configurationStore.GetVendorsAsync(enabledOnly: true, cancellationToken).ConfigureAwait(true);
        var sites = await configurationStore.GetSitesAsync(vendorId: null, enabledOnly: true, cancellationToken: cancellationToken).ConfigureAwait(true);
        var credentials = await configurationStore.GetCredentialProfilesAsync(vendorId: null, cancellationToken: cancellationToken).ConfigureAwait(true);
        Replace(Vendors, vendors);
        Replace(Sites, sites);
        Replace(Credentials, credentials);
        SelectedVendor ??= Vendors.FirstOrDefault();
        SelectedSite ??= Sites.FirstOrDefault();
        SelectedCredential ??= Credentials.FirstOrDefault();
        SelectedImportVendor ??= Vendors.FirstOrDefault();
        SelectedImportSite ??= Sites.FirstOrDefault(x => x.VendorId == SelectedImportVendor?.Id);
        SelectedImportCredential ??= Credentials.FirstOrDefault(x => x.Id == SelectedImportSite?.CredentialProfileId)
            ?? Credentials.FirstOrDefault(x => x.VendorId == SelectedImportVendor?.Id);
        SelectedConnectionVendor ??= Vendors.FirstOrDefault();
        SelectedConnectionSite ??= Sites.FirstOrDefault();
        await RefreshJobsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshJobsAsync(CancellationToken cancellationToken = default)
    {
        var result = await jobService.QueryAsync(new JobQuery(Search: SearchText, Take: 500), cancellationToken).ConfigureAwait(true);
        var selectedId = SelectedJob?.Id;
        Replace(Jobs, result.Items);
        SelectedJob = selectedId is null ? Jobs.FirstOrDefault() : Jobs.FirstOrDefault(x => x.Id == selectedId) ?? Jobs.FirstOrDefault();
        RaiseDashboardCounts();
        StatusMessage = $"Showing {Jobs.Count:N0} of {result.TotalCount:N0} jobs. Last refreshed {timeProvider.GetLocalNow():t}.";
    }

    private async Task LoadSelectedJobHistoryAsync(Guid? jobId, long generation)
    {
        if (jobId is null)
        {
            Replace(SelectedJobHistory, []);
            SelectedStatusHistory = null;
            JobDetailStatus = "Select a job to inspect its history.";
            return;
        }

        JobDetailStatus = "Loading job history…";
        try
        {
            var events = await jobService.GetTimelineAsync(jobId.Value, CancellationToken.None).ConfigureAwait(true);
            if (generation != Interlocked.Read(ref jobDetailLoadGeneration) || SelectedJob?.Id != jobId)
            {
                return;
            }

            var history = events
                .OrderByDescending(item => item.TimestampUtc)
                .ThenByDescending(item => item.Id)
                .Select(JobStatusHistoryItem.Create)
                .ToArray();
            Replace(SelectedJobHistory, history);
            SelectedStatusHistory = SelectedJobHistory.FirstOrDefault();
            var exchangeCount = history.Sum(item => item.HttpExchanges.Count);
            JobDetailStatus = $"{history.Length:N0} status entries · {exchangeCount:N0} preserved HTTP exchanges";
        }
        catch (Exception exception)
        {
            if (generation != Interlocked.Read(ref jobDetailLoadGeneration))
            {
                return;
            }

            Replace(SelectedJobHistory, []);
            SelectedStatusHistory = null;
            JobDetailStatus = "Could not load history: " + SecurityRedactor.Redact(exception.Message);
        }
    }

    private async Task CreateJobAsync()
    {
        if (SelectedVendor is null || SelectedSite is null || SelectedCredential is null)
        {
            throw new InvalidOperationException("Configure and select a vendor, site, and credential profile first.");
        }

        using (JsonDocument.Parse(string.IsNullOrWhiteSpace(NewVendorOptionsJson) ? "{}" : NewVendorOptionsJson))
        {
        }
        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(NewSinceUtc))
        {
            if (!DateTimeOffset.TryParse(NewSinceUtc, out var parsedSince))
            {
                throw new InvalidOperationException("_since must be a valid date/time with timezone, or blank.");
            }

            since = parsedSince;
        }

        var command = new CreateExportJobCommand(
            NewJobName,
            NewJobDescription,
            SelectedVendor.Id,
            SelectedSite.Id,
            SelectedCredential.Id,
            NewExportType,
            string.IsNullOrWhiteSpace(NewGroupId) ? null : NewGroupId,
            since,
            SplitValues(NewResourceTypes, ','),
            SplitValues(NewTypeFilters, ';'),
            "application/fhir+ndjson",
            string.IsNullOrWhiteSpace(NewVendorOptionsJson) ? "{}" : NewVendorOptionsJson,
            DataRoot,
            NewAutomaticCleanup,
            NewPriority);
        var id = await jobService.CreateDraftAsync(command, CancellationToken.None).ConfigureAwait(true);
        NewJobName = string.Empty;
        NewJobDescription = string.Empty;
        StatusMessage = $"Draft {id:D} created. No remote export was started.";
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    private async Task ImportExistingExportAsync()
    {
        if (SelectedImportVendor is null || SelectedImportSite is null || SelectedImportCredential is null)
        {
            throw new InvalidOperationException("Configure and select a vendor, site, and credential profile first.");
        }

        using (JsonDocument.Parse(string.IsNullOrWhiteSpace(ImportVendorOptionsJson) ? "{}" : ImportVendorOptionsJson))
        {
        }

        DateTimeOffset? remoteStartedAtUtc = null;
        if (!string.IsNullOrWhiteSpace(ImportRemoteStartedUtc))
        {
            if (!DateTimeOffset.TryParse(ImportRemoteStartedUtc, out var parsedRemoteStartedAtUtc))
            {
                throw new InvalidOperationException("Remote start time must be a valid date/time with timezone, or blank.");
            }

            remoteStartedAtUtc = parsedRemoteStartedAtUtc;
        }

        DateTimeOffset? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(ImportSinceUtc))
        {
            if (!DateTimeOffset.TryParse(ImportSinceUtc, out var parsedSinceUtc))
            {
                throw new InvalidOperationException("_since must be a valid date/time with timezone, or blank.");
            }

            sinceUtc = parsedSinceUtc;
        }

        var command = new ImportExistingExportCommand(
            ImportJobName,
            ImportJobDescription,
            SelectedImportVendor.Id,
            SelectedImportSite.Id,
            SelectedImportCredential.Id,
            ImportContentLocation,
            remoteStartedAtUtc,
            ImportExportType,
            string.IsNullOrWhiteSpace(ImportGroupId) ? null : ImportGroupId,
            sinceUtc,
            SplitValues(ImportResourceTypes, ','),
            SplitValues(ImportTypeFilters, ';'),
            "application/fhir+ndjson",
            string.IsNullOrWhiteSpace(ImportVendorOptionsJson) ? "{}" : ImportVendorOptionsJson,
            DataRoot,
            ImportAutomaticCleanup,
            ImportPriority);
        var id = await jobService.ImportExistingAsync(command, CancellationToken.None).ConfigureAwait(true);
        ImportJobName = string.Empty;
        ImportJobDescription = string.Empty;
        ImportContentLocation = string.Empty;
        ImportRemoteStartedUtc = string.Empty;
        await RefreshJobsAsync().ConfigureAwait(true);
        SelectedJob = Jobs.FirstOrDefault(x => x.Id == id);
        StatusMessage = $"Existing export {id:D} imported. Monitoring was queued without sending $export.";
    }

    private async Task RunSelectedAsync(Func<ExportJob, Task<JobActionResult>> operation)
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select an export job first.");
        var result = await operation(job).ConfigureAwait(true);
        StatusMessage = result.Message;
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    private async Task CancelRemoteAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a job.");
        var confirmation = dialogs.RequestTypedConfirmation(
            "Cancel remote export",
            $"This sends DELETE to the exact remote Content-Location and may permanently cancel the server-side export.\n\nJob: {job.Name}\nSite ID: {job.SiteId}\nState: {job.State}\n\nThis does not mean 'pause local processing'.",
            "CANCEL");
        if (confirmation is null)
        {
            return;
        }

        await jobService.RequestRemoteCancelAsync(job.Id, confirmation, CancellationToken.None).ConfigureAwait(true);
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    private async Task CleanupRemoteAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a job.");
        var confirmation = dialogs.RequestTypedConfirmation(
            "Clean up remote export",
            $"This sends DELETE only to the original Content-Location after re-verifying the durable local commit.\n\nJob: {job.Name}\nState: {job.State}",
            "CLEANUP");
        if (confirmation is null)
        {
            return;
        }

        await jobService.RequestCleanupAsync(job.Id, confirmation, commitService, storage, CancellationToken.None).ConfigureAwait(true);
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    private async Task ApprovePartialAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a job.");
        var confirmation = dialogs.RequestTypedConfirmation(
            "Approve partial export",
            "Fatal or error OperationOutcome entries were detected. Review the Errors and Manifest tabs before approving. Approval permits the workflow to continue and may permit configured cleanup.",
            "APPROVE");
        if (confirmation is null)
        {
            return;
        }

        await jobService.ApprovePartialExportAsync(job.Id, confirmation, CancellationToken.None).ConfigureAwait(true);
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    private async Task DeleteLocalAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a job.");
        var deleteFiles = dialogs.Confirm("Delete downloaded files?", "Select Yes to delete downloaded export files and metadata. Select No to delete metadata only and leave files on disk.");
        var confirmation = dialogs.RequestTypedConfirmation(
            "Delete local job",
            $"This permanently deletes local job metadata{(deleteFiles ? " and the managed export folder" : string.Empty)}. It does not send DELETE to the FHIR server.\n\nJob: {job.Name}\nState: {job.State}",
            "DELETE");
        if (confirmation is null)
        {
            return;
        }

        await jobService.DeleteLocalJobAsync(job.Id, deleteFiles, confirmation, storage, CancellationToken.None).ConfigureAwait(true);
        SelectedJob = null;
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    private async Task OpenFolderAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a job.");
        var vendor = await configurationStore.GetVendorAsync(job.VendorId, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var paths = storage.GetPaths(vendor.PluginId, job.SiteId, job.Id);
        await storage.EnsureExportDirectoriesAsync(paths, CancellationToken.None).ConfigureAwait(true);
        _ = Process.Start(new ProcessStartInfo(paths.Root) { UseShellExecute = true });
    }

    private Task ShowDetailsAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select a job.");
        return navigation.ShowJobDetailAsync(job.Id, CancellationToken.None);
    }

    private async Task SaveConnectionAsync()
    {
        var vendor = SelectedConnectionVendor ?? throw new InvalidOperationException("Select a vendor.");
        var anonymous = string.Equals(vendor.PluginId, FirstRunBootstrapper.PublicFhirR4PluginId, StringComparison.Ordinal);
        var fhirUri = Guard.AbsoluteHttpsUri(ConnectionFhirBaseUrl, nameof(ConnectionFhirBaseUrl));
        if (!anonymous)
        {
            _ = Guard.AbsoluteHttpsUri(ConnectionTokenEndpoint, nameof(ConnectionTokenEndpoint));
            if (!string.IsNullOrWhiteSpace(ConnectionAssertionEndpoint))
            {
                _ = Guard.AbsoluteHttpsUri(ConnectionAssertionEndpoint, nameof(ConnectionAssertionEndpoint));
            }

            using (JsonDocument.Parse(string.IsNullOrWhiteSpace(ConnectionAuthConfigurationJson) ? "{}" : ConnectionAuthConfigurationJson))
            {
            }
            if (string.IsNullOrWhiteSpace(ConnectionClientSecret) && string.IsNullOrWhiteSpace(ConnectionAssertionEndpoint))
            {
                throw new InvalidOperationException("Configure a HealthWizz assertion endpoint or provide a client secret for direct OAuth client credentials.");
            }
        }

        var now = timeProvider.GetUtcNow();
        var secretReference = string.Empty;
        if (!anonymous && !string.IsNullOrWhiteSpace(ConnectionClientSecret))
        {
            secretReference = await secretStore.SaveAsync("oauth-client-secret", ConnectionClientSecret.AsMemory(), CancellationToken.None).ConfigureAwait(true);
        }

        try
        {
            var profile = new CredentialProfile(
                Guid.NewGuid(), vendor.Id, ConnectionSiteName + (anonymous ? " Anonymous" : " OAuth"), anonymous ? "None" : "OAuth2ClientCredentials",
                anonymous ? null : ConnectionClientId, secretReference, anonymous ? null : ConnectionTokenEndpoint, now,
                anonymous ? null : ConnectionScope, anonymous ? null : ConnectionAudience,
                anonymous || string.IsNullOrWhiteSpace(ConnectionAssertionEndpoint) ? null : ConnectionAssertionEndpoint,
                anonymous || string.IsNullOrWhiteSpace(ConnectionAuthConfigurationJson) ? "{}" : ConnectionAuthConfigurationJson);
            var correlation = Guid.NewGuid().ToString("N");
            await configurationStore.AddCredentialProfileAsync(profile, Audit("CredentialProfileCreated", profile.Id, correlation), CancellationToken.None).ConfigureAwait(true);
            var site = new Site(Guid.NewGuid(), vendor.Id, ConnectionSiteName, fhirUri, profile.Id, now);
            await configurationStore.AddSiteAsync(site, Audit("SiteCreated", site.Id, correlation), CancellationToken.None).ConfigureAwait(true);
            ConnectionClientSecret = string.Empty;
            StatusMessage = "Connection profile saved. Use Test Connection before creating an export.";
            await RefreshAsync().ConfigureAwait(true);
            SelectedConnectionSite = Sites.FirstOrDefault(x => x.Id == site.Id);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(secretReference))
            {
                await secretStore.DeleteAsync(secretReference, CancellationToken.None).ConfigureAwait(true);
            }

            throw;
        }
    }

    private async Task TestConnectionAsync()
    {
        var site = SelectedConnectionSite ?? throw new InvalidOperationException("Select a site.");
        var vendor = await configurationStore.GetVendorAsync(site.VendorId, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var plugin = pluginCatalog.GetRequired(vendor.PluginId, vendor.PluginVersion);
        var context = new VendorSiteContext(vendor.Id, site.Id, new Uri(site.FhirBaseUrl), site.TenantIdentifier, site.PracticeCode, "{}");
        var result = await plugin.TestConnectionAsync(context, token: null, cancellationToken: CancellationToken.None).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Summary);
        }

        dialogs.ShowInfo("Connection succeeded", result.Summary + " No export was created.");
        StatusMessage = result.Summary;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = "Operation failed safely: " + SecurityRedactor.Redact(exception.Message);
            dialogs.ShowError("Operation failed", StatusMessage);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action) => await RunAsync(() => action(CancellationToken.None)).ConfigureAwait(true);

    private AuditEvent Audit(string action, Guid entityId, string correlationId) =>
        new(timeProvider.GetUtcNow(), identityProvider.GetCurrentIdentity(), action, "Configuration", entityId.ToString("D"), "Succeeded", correlationId);

    private static IReadOnlyList<string> SplitValues(string value, char separator) =>
        value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void RaiseDashboardCounts()
    {
        foreach (var name in new[] { nameof(DraftCount), nameof(ActiveCount), nameof(DownloadingCount), nameof(HeldCount), nameof(FailedCount), nameof(CompletedCount), nameof(HasActiveJobs) })
        {
            OnPropertyChanged(name);
        }
    }
}
