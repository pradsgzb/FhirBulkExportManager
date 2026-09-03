using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using FhirBulkExportManager.App.Wpf.Presentation;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Application.Models;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;
using FhirBulkExportManager.Worker;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.App.Wpf;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string DefaultEClinicalWorksTokenEndpoint = "https://oauthserver.eclinicalworks.com/oauth/oauth2/token";
    private readonly ExportJobService jobService;
    private readonly IConfigurationStore configurationStore;
    private readonly ISecretStore secretStore;
    private readonly IWindowsIdentityProvider identityProvider;
    private readonly IVendorPluginCatalog pluginCatalog;
    private readonly AuthenticationTokenProvider tokenProvider;
    private readonly IManagedStorage storage;
    private readonly ILocalCommitService commitService;
    private readonly IUserDialogService dialogs;
    private readonly IWindowNavigationService navigation;
    private readonly TimeProvider timeProvider;
    private readonly ApplicationSettingsService applicationSettings;
    private readonly PasswordProtectionService passwordProtection;
    private readonly ITransientNotificationService transientNotifications;
    private readonly DispatcherTimer autoRefreshTimer;
    private readonly DispatcherTimer settingsSaveTimer;
    private IReadOnlyList<ExportJobEvent> selectedJobEvents = [];
    private IReadOnlyList<ExportFile> selectedJobFiles = [];
    private JobDashboardSnapshot jobDashboard = JobDashboardSnapshot.Empty;
    private DateDisplayMode dateDisplayMode = DateDisplayMode.Utc;
    private bool autoRefreshEnabled;
    private int autoRefreshIntervalSeconds = 30;
    private DateTimeOffset? nextAutoRefreshAtUtc;
    private string autoRefreshCountdownText = "Auto refresh off";
    private bool minimizeToTray = true;
    private bool showNetworkAccessHintOnBadRequest = true;
    private string settingsStatus = "Options are saved automatically.";
    private bool passwordConfigured;
    private bool initialized;
    private bool refreshInProgress;
    private bool settingsDirty;
    private bool disposed;
    private ExportJob? selectedJob;
    private JobStatusHistoryItem? selectedStatusHistory;
    private HttpExchangeViewItem? selectedHttpExchange;
    private string jobDetailStatus = "Select a job to inspect its history.";
    private long jobDetailLoadGeneration;
    private string connectionMetadataStatus = "Select a connection to view its saved metadata status.";
    private string connectionMetadataJson = string.Empty;
    private string connectionMetadataHttpHistoryText = string.Empty;
    private long connectionMetadataLoadGeneration;
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
    private bool connectionRemoveStoredSecret;
    private string connectionTokenEndpoint = string.Empty;
    private string connectionAssertionEndpoint = string.Empty;
    private string connectionScope = string.Empty;
    private string connectionAudience = string.Empty;
    private string connectionAuthConfigurationJson = "{}";
    private Guid? editingConnectionSiteId;
    private Guid? editingCredentialProfileId;
    private string editingOriginalSecretReference = string.Empty;

    public MainViewModel(
        ExportJobService jobService,
        IConfigurationStore configurationStore,
        ISecretStore secretStore,
        IWindowsIdentityProvider identityProvider,
        IVendorPluginCatalog pluginCatalog,
        AuthenticationTokenProvider tokenProvider,
        IManagedStorage storage,
        ILocalCommitService commitService,
        IUserDialogService dialogs,
        IWindowNavigationService navigation,
        ApplicationSettingsService applicationSettings,
        PasswordProtectionService passwordProtection,
        ITransientNotificationService transientNotifications,
        IOptions<ProcessingOptions> processingOptions,
        IOptions<StorageOptions> storageOptions,
        TimeProvider timeProvider)
    {
        this.jobService = jobService;
        this.configurationStore = configurationStore;
        this.secretStore = secretStore;
        this.identityProvider = identityProvider;
        this.pluginCatalog = pluginCatalog;
        this.tokenProvider = tokenProvider;
        this.storage = storage;
        this.commitService = commitService;
        this.dialogs = dialogs;
        this.navigation = navigation;
        this.applicationSettings = applicationSettings;
        this.passwordProtection = passwordProtection;
        this.transientNotifications = transientNotifications;
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
        RepairImportedConnectionCommand = new AsyncRelayCommand(
            () => RunAsync(RepairImportedConnectionAsync),
            () => SelectedJob is
            {
                HasConfirmedRemoteJob: true,
                RemoteRequestUrl: null,
                ManifestJson: null,
                State: ExportJobState.Running or
                    ExportJobState.WaitingForPoll or
                    ExportJobState.RetryPending or
                    ExportJobState.FailedTransient or
                    ExportJobState.FailedPermanent or
                    ExportJobState.Paused or
                    ExportJobState.Unknown,
            });
        StartCommand = new AsyncRelayCommand(() => RunAsync(() => RunSelectedAsync(job => jobService.StartAsync(job.Id, CancellationToken.None))), () => SelectedJob?.State == ExportJobState.Draft);
        StatusNowCommand = new AsyncRelayCommand(
            () => RunAsync(() => RunSelectedAsync(job => jobService.GetStatusNowAsync(job.Id, CancellationToken.None))),
            () => SelectedJob is { HasConfirmedRemoteJob: true } job &&
                  (job.State is ExportJobState.WaitingForPoll or ExportJobState.Running or ExportJobState.RetryPending ||
                   job.State == ExportJobState.FailedPermanent && job.ManifestJson is null));
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
        EditConnectionCommand = new AsyncRelayCommand(() => RunAsync(EditSelectedConnectionAsync), () => SelectedConnectionSite is not null);
        DeleteConnectionCommand = new AsyncRelayCommand(() => RunAsync(DeleteSelectedConnectionAsync), () => SelectedConnectionSite is not null);
        NewConnectionCommand = new AsyncRelayCommand(() => RunAsync(ResetConnectionEditorAsync));
        TestConnectionCommand = new AsyncRelayCommand(() => RunAsync(TestConnectionAsync), () => SelectedConnectionSite is not null);
        CopyHttpRequestCommand = new AsyncRelayCommand(() => RunAsync(() => CopyTextAsync(SelectedHttpExchange?.RequestText, "HTTP request")), () => SelectedHttpExchange is not null);
        CopyHttpResponseCommand = new AsyncRelayCommand(() => RunAsync(() => CopyTextAsync(SelectedHttpExchange?.ResponseText, "HTTP response")), () => SelectedHttpExchange is not null);
        CopyHttpExchangeCommand = new AsyncRelayCommand(() => RunAsync(() => CopyTextAsync(SelectedHttpExchange?.AllText, "complete HTTP exchange")), () => SelectedHttpExchange is not null);
        CopyConnectionMetadataCommand = new AsyncRelayCommand(() => RunAsync(() => CopyTextAsync(ConnectionMetadataJson, "saved CapabilityStatement")), () => !string.IsNullOrWhiteSpace(ConnectionMetadataJson));
        CopyConnectionMetadataHttpCommand = new AsyncRelayCommand(() => RunAsync(() => CopyTextAsync(ConnectionMetadataHttpHistoryText, "metadata HTTP history")), () => !string.IsNullOrWhiteSpace(ConnectionMetadataHttpHistoryText));
        ChangePasswordCommand = new AsyncRelayCommand(() => RunAsync(ChangePasswordAsync));
        RemovePasswordCommand = new AsyncRelayCommand(() => RunAsync(RemovePasswordAsync), () => PasswordConfigured);
        RestoreDefaultOptionsCommand = new AsyncRelayCommand(() => RunAsync(RestoreDefaultOptionsAsync));

        autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        settingsSaveTimer.Tick += SettingsSaveTimer_Tick;
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
    public ICommand RepairImportedConnectionCommand { get; }
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
    public ICommand EditConnectionCommand { get; }
    public ICommand DeleteConnectionCommand { get; }
    public ICommand NewConnectionCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand CopyHttpRequestCommand { get; }
    public ICommand CopyHttpResponseCommand { get; }
    public ICommand CopyHttpExchangeCommand { get; }
    public ICommand CopyConnectionMetadataCommand { get; }
    public ICommand CopyConnectionMetadataHttpCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand RemovePasswordCommand { get; }
    public ICommand RestoreDefaultOptionsCommand { get; }

    public int ConfiguredExportLimit { get; }
    public int EffectiveExportLimit { get; }
    public int DetectedLogicalProcessors { get; }
    public int PerSiteDownloadLimit { get; }
    public string DataRoot { get; }
    public int MinimumFreeDiskSpaceGB { get; }
    public Array DateDisplayModes => Enum.GetValues(typeof(DateDisplayMode));

    public DateDisplayMode SelectedDateDisplayMode
    {
        get => dateDisplayMode;
        set
        {
            if (SetProperty(ref dateDisplayMode, value))
            {
                DateTimeDisplayPolicy.Mode = value;
                OnPropertyChanged(nameof(DateDisplayDescription));
                PreferencesChanged();
                if (initialized)
                {
                    _ = RunAsync(RefreshJobsAsync);
                    var generation = Interlocked.Increment(ref connectionMetadataLoadGeneration);
                    _ = LoadConnectionMetadataAsync(SelectedConnectionSite, generation);
                }
            }
        }
    }

    public string DateDisplayDescription => SelectedDateDisplayMode == DateDisplayMode.Local
        ? $"Local time ({TimeZoneInfo.Local.DisplayName})"
        : "Coordinated Universal Time (UTC)";

    public bool AutoRefreshEnabled
    {
        get => autoRefreshEnabled;
        set
        {
            if (SetProperty(ref autoRefreshEnabled, value))
            {
                ResetAutoRefreshDeadline();
                PreferencesChanged();
            }
        }
    }

    public int AutoRefreshIntervalSeconds
    {
        get => autoRefreshIntervalSeconds;
        set
        {
            var normalized = Math.Clamp(value, 5, 300);
            if (SetProperty(ref autoRefreshIntervalSeconds, normalized))
            {
                OnPropertyChanged(nameof(AutoRefreshIntervalText));
                ResetAutoRefreshDeadline();
                PreferencesChanged();
            }
        }
    }

    public string AutoRefreshIntervalText => $"{AutoRefreshIntervalSeconds} seconds";
    public string AutoRefreshCountdownText { get => autoRefreshCountdownText; private set => SetProperty(ref autoRefreshCountdownText, value); }

    public bool MinimizeToTray
    {
        get => minimizeToTray;
        set
        {
            if (SetProperty(ref minimizeToTray, value))
            {
                PreferencesChanged();
            }
        }
    }

    public bool ShowNetworkAccessHintOnBadRequest
    {
        get => showNetworkAccessHintOnBadRequest;
        set
        {
            if (SetProperty(ref showNetworkAccessHintOnBadRequest, value))
            {
                PreferencesChanged();
            }
        }
    }

    public string SettingsStatus { get => settingsStatus; private set => SetProperty(ref settingsStatus, value); }
    public bool PasswordConfigured
    {
        get => passwordConfigured;
        private set
        {
            if (SetProperty(ref passwordConfigured, value))
            {
                OnPropertyChanged(nameof(PasswordStatus));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public string PasswordStatus => PasswordConfigured ? "Password protection is enabled" : "Password protection is not configured";
    public JobDashboardSnapshot JobDashboard { get => jobDashboard; private set => SetProperty(ref jobDashboard, value); }

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
        set
        {
            if (SetProperty(ref selectedHttpExchange, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
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
    public Vendor? SelectedConnectionVendor
    {
        get => selectedConnectionVendor;
        set
        {
            if (SetProperty(ref selectedConnectionVendor, value))
            {
                if (!IsEditingConnection)
                {
                    ConnectionTokenEndpoint = string.Equals(value?.PluginId, "eclinicalworks", StringComparison.Ordinal)
                        ? DefaultEClinicalWorksTokenEndpoint
                        : string.Empty;
                }

                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public Site? SelectedConnectionSite
    {
        get => selectedConnectionSite;
        set
        {
            if (SetProperty(ref selectedConnectionSite, value))
            {
                CommandManager.InvalidateRequerySuggested();
                var generation = Interlocked.Increment(ref connectionMetadataLoadGeneration);
                _ = LoadConnectionMetadataAsync(value, generation);
            }
        }
    }
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
    public bool ConnectionRemoveStoredSecret { get => connectionRemoveStoredSecret; set => SetProperty(ref connectionRemoveStoredSecret, value); }
    public string ConnectionTokenEndpoint { get => connectionTokenEndpoint; set => SetProperty(ref connectionTokenEndpoint, value); }
    public string ConnectionAssertionEndpoint { get => connectionAssertionEndpoint; set => SetProperty(ref connectionAssertionEndpoint, value); }
    public string ConnectionScope { get => connectionScope; set => SetProperty(ref connectionScope, value); }
    public string ConnectionAudience { get => connectionAudience; set => SetProperty(ref connectionAudience, value); }
    public string ConnectionAuthConfigurationJson { get => connectionAuthConfigurationJson; set => SetProperty(ref connectionAuthConfigurationJson, value); }
    public string ConnectionMetadataStatus { get => connectionMetadataStatus; private set => SetProperty(ref connectionMetadataStatus, value); }
    public string ConnectionMetadataJson
    {
        get => connectionMetadataJson;
        private set
        {
            if (SetProperty(ref connectionMetadataJson, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public string ConnectionMetadataHttpHistoryText
    {
        get => connectionMetadataHttpHistoryText;
        private set
        {
            if (SetProperty(ref connectionMetadataHttpHistoryText, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool IsEditingConnection => editingConnectionSiteId is not null;
    public bool CanChangeConnectionVendor => !IsEditingConnection;
    public string ConnectionEditorTitle => IsEditingConnection ? "Edit Selected Connection" : "Create New Connection";
    public string ConnectionSaveButtonText => IsEditingConnection ? "Save Connection Changes" : "Create Connection";

    public int DraftCount => Jobs.Count(x => x.State == ExportJobState.Draft);
    public int ActiveCount => Jobs.Count(x => !x.IsTerminal && x.State is not ExportJobState.Draft and not ExportJobState.Paused and not ExportJobState.HeldForReview and not ExportJobState.Unknown and not ExportJobState.FailedPermanent);
    public int DownloadingCount => Jobs.Count(x => x.State is ExportJobState.QueuedForDownload or ExportJobState.Downloading or ExportJobState.Validating);
    public int HeldCount => Jobs.Count(x => x.State == ExportJobState.HeldForReview);
    public int FailedCount => Jobs.Count(x => x.State is ExportJobState.FailedPermanent or ExportJobState.Expired or ExportJobState.Unknown);
    public int CompletedCount => Jobs.Count(x => x.State == ExportJobState.Completed);
    public bool HasActiveJobs => ActiveCount > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var preferences = await applicationSettings.LoadAsync(cancellationToken).ConfigureAwait(true);
        dateDisplayMode = preferences.DateMode;
        autoRefreshEnabled = preferences.AutoRefreshEnabled;
        autoRefreshIntervalSeconds = preferences.AutoRefreshIntervalSeconds;
        minimizeToTray = preferences.MinimizeToTray;
        showNetworkAccessHintOnBadRequest = preferences.ShowNetworkAccessHintOnBadRequest;
        DateTimeDisplayPolicy.Mode = dateDisplayMode;
        PasswordConfigured = await passwordProtection.IsConfiguredAsync(cancellationToken).ConfigureAwait(true);
        SettingsStatus = applicationSettings.LoadWarning ?? "Options are saved automatically.";
        foreach (var propertyName in new[]
        {
            nameof(SelectedDateDisplayMode), nameof(DateDisplayDescription), nameof(AutoRefreshEnabled),
            nameof(AutoRefreshIntervalSeconds), nameof(AutoRefreshIntervalText), nameof(MinimizeToTray),
            nameof(ShowNetworkAccessHintOnBadRequest),
        })
        {
            OnPropertyChanged(propertyName);
        }

        initialized = true;
        ResetAutoRefreshDeadline();
        autoRefreshTimer.Start();
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public void SetPassword(string value) => ConnectionClientSecret = value;

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        try
        {
        var selectedVendorId = SelectedVendor?.Id;
        var selectedSiteId = SelectedSite?.Id;
        var selectedCredentialId = SelectedCredential?.Id;
        var selectedImportVendorId = SelectedImportVendor?.Id;
        var selectedImportSiteId = SelectedImportSite?.Id;
        var selectedImportCredentialId = SelectedImportCredential?.Id;
        var selectedConnectionVendorId = SelectedConnectionVendor?.Id;
        var selectedConnectionSiteId = SelectedConnectionSite?.Id;
        var vendors = await configurationStore.GetVendorsAsync(enabledOnly: true, cancellationToken).ConfigureAwait(true);
        var sites = await configurationStore.GetSitesAsync(vendorId: null, enabledOnly: true, cancellationToken: cancellationToken).ConfigureAwait(true);
        var credentials = await configurationStore.GetCredentialProfilesAsync(vendorId: null, cancellationToken: cancellationToken).ConfigureAwait(true);
        Replace(Vendors, vendors);
        Replace(Sites, sites);
        Replace(Credentials, credentials);
        SelectedVendor = Vendors.FirstOrDefault(x => x.Id == selectedVendorId) ?? Vendors.FirstOrDefault();
        SelectedSite = Sites.FirstOrDefault(x => x.Id == selectedSiteId)
            ?? Sites.FirstOrDefault(x => x.VendorId == SelectedVendor?.Id);
        SelectedCredential = Credentials.FirstOrDefault(x => x.Id == selectedCredentialId)
            ?? Credentials.FirstOrDefault(x => x.VendorId == SelectedVendor?.Id);
        SelectedImportVendor = Vendors.FirstOrDefault(x => x.Id == selectedImportVendorId) ?? Vendors.FirstOrDefault();
        SelectedImportSite = Sites.FirstOrDefault(x => x.Id == selectedImportSiteId)
            ?? Sites.FirstOrDefault(x => x.VendorId == SelectedImportVendor?.Id);
        SelectedImportCredential = Credentials.FirstOrDefault(x => x.Id == selectedImportCredentialId)
            ?? Credentials.FirstOrDefault(x => x.Id == SelectedImportSite?.CredentialProfileId)
            ?? Credentials.FirstOrDefault(x => x.VendorId == SelectedImportVendor?.Id);
        SelectedConnectionVendor = Vendors.FirstOrDefault(x => x.Id == selectedConnectionVendorId) ?? Vendors.FirstOrDefault();
        SelectedConnectionSite = Sites.FirstOrDefault(x => x.Id == selectedConnectionSiteId) ?? Sites.FirstOrDefault();
        await RefreshJobsCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            refreshInProgress = false;
            ResetAutoRefreshDeadline();
        }
    }

    private async Task RefreshJobsAsync(CancellationToken cancellationToken = default)
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        try
        {
            await RefreshJobsCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            refreshInProgress = false;
            ResetAutoRefreshDeadline();
        }
    }

    private async Task RefreshJobsCoreAsync(CancellationToken cancellationToken = default)
    {
        var result = await jobService.QueryAsync(new JobQuery(Search: SearchText, Take: 500), cancellationToken).ConfigureAwait(true);
        var selectedId = SelectedJob?.Id;
        Replace(Jobs, result.Items);
        SelectedJob = selectedId is null ? Jobs.FirstOrDefault() : Jobs.FirstOrDefault(x => x.Id == selectedId) ?? Jobs.FirstOrDefault();
        RaiseDashboardCounts();
        StatusMessage = $"Showing {Jobs.Count:N0} of {result.TotalCount:N0} jobs. Last refreshed {DateTimeDisplayPolicy.Format(timeProvider.GetUtcNow())}.";
    }

    private async Task LoadSelectedJobHistoryAsync(Guid? jobId, long generation)
    {
        if (jobId is null)
        {
            selectedJobEvents = [];
            selectedJobFiles = [];
            Replace(SelectedJobHistory, []);
            SelectedStatusHistory = null;
            JobDashboard = JobDashboardSnapshot.Empty;
            JobDetailStatus = "Select a job to inspect its history.";
            return;
        }

        JobDetailStatus = "Loading job history…";
        try
        {
            var eventsTask = jobService.GetTimelineAsync(jobId.Value, CancellationToken.None);
            var filesTask = jobService.GetFilesAsync(jobId.Value, CancellationToken.None);
            await Task.WhenAll(eventsTask, filesTask).ConfigureAwait(true);
            var events = await eventsTask.ConfigureAwait(true);
            var files = await filesTask.ConfigureAwait(true);
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
            selectedJobEvents = events;
            selectedJobFiles = files;
            JobDashboard = JobProgressAnalytics.Calculate(SelectedJob, events, files, timeProvider.GetUtcNow());
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
            selectedJobEvents = [];
            selectedJobFiles = [];
            SelectedStatusHistory = null;
            JobDashboard = JobDashboardSnapshot.Empty;
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

    private async Task RepairImportedConnectionAsync()
    {
        var job = SelectedJob ?? throw new InvalidOperationException("Select the imported job to repair on the Dashboard & Jobs tab first.");
        var vendor = SelectedImportVendor ?? throw new InvalidOperationException("Select the vendor that was used to start the export.");
        var site = SelectedImportSite ?? throw new InvalidOperationException("Select the site connection that was used to start the export.");
        var credential = SelectedImportCredential ?? throw new InvalidOperationException("The selected site connection has no credential profile.");
        if (site.VendorId != vendor.Id || site.CredentialProfileId != credential.Id)
        {
            throw new InvalidOperationException("The selected vendor, site, and credential do not describe one saved connection.");
        }

        if (!dialogs.Confirm(
            "Repair imported job connection",
            $"Reassign the existing imported job to this connection?\n\nJob: {job.Name}\nConnection: {site.Name}\nVendor: {vendor.Name}\n\nThe exact Content-Location and all existing history will be retained. No new $export request will be sent."))
        {
            return;
        }

        var jobId = job.Id;
        var previousCredentialProfileId = job.CredentialProfileId;
        var result = await jobService.ReassignImportedConnectionAsync(
            jobId,
            vendor.Id,
            site.Id,
            credential.Id,
            CancellationToken.None).ConfigureAwait(true);
        tokenProvider.Invalidate(previousCredentialProfileId);
        tokenProvider.Invalidate(credential.Id);
        StatusMessage = result.Message;
        await RefreshJobsAsync().ConfigureAwait(true);
        SelectedJob = Jobs.FirstOrDefault(x => x.Id == jobId);
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
        if (await passwordProtection.IsConfiguredAsync(CancellationToken.None).ConfigureAwait(true))
        {
            var password = dialogs.RequestPassword(
                "Confirm application password",
                "Remote cancellation is destructive. Enter the application password before continuing to the typed confirmation.");
            if (password is null)
            {
                return;
            }

            if (!await passwordProtection.VerifyAsync(password, CancellationToken.None).ConfigureAwait(true))
            {
                throw new InvalidOperationException("The application password is incorrect. The remote export was not cancelled.");
            }
        }

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
        Site? existingSite = null;
        CredentialProfile? existingCredential = null;
        if (editingConnectionSiteId is { } siteId && editingCredentialProfileId is { } credentialProfileId)
        {
            existingSite = await configurationStore.GetSiteAsync(siteId, CancellationToken.None).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The connection no longer exists. Refresh and try again.");
            existingCredential = await configurationStore.GetCredentialProfileAsync(credentialProfileId, CancellationToken.None).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The connection credential profile no longer exists.");
            if (existingSite.VendorId != vendor.Id || existingCredential.VendorId != vendor.Id)
            {
                throw new InvalidOperationException("A connection cannot be moved to another vendor. Create a new connection for the other vendor.");
            }
        }

        using (JsonDocument.Parse(string.IsNullOrWhiteSpace(ConnectionAuthConfigurationJson) ? "{}" : ConnectionAuthConfigurationJson))
        {
        }

        if (!anonymous)
        {
            _ = Guard.AbsoluteHttpsUri(ConnectionTokenEndpoint, nameof(ConnectionTokenEndpoint));
            if (!string.IsNullOrWhiteSpace(ConnectionAssertionEndpoint))
            {
                _ = Guard.AbsoluteHttpsUri(ConnectionAssertionEndpoint, nameof(ConnectionAssertionEndpoint));
            }
        }

        var now = timeProvider.GetUtcNow();
        var retainedSecretReference = existingCredential is null || ConnectionRemoveStoredSecret
            ? string.Empty
            : existingCredential.SecretReference;
        var newSecretReference = string.Empty;
        if (!anonymous && !string.IsNullOrWhiteSpace(ConnectionClientSecret))
        {
            newSecretReference = await secretStore.SaveAsync("oauth-client-secret", ConnectionClientSecret.AsMemory(), CancellationToken.None).ConfigureAwait(true);
        }

        var secretReference = anonymous
            ? string.Empty
            : string.IsNullOrWhiteSpace(newSecretReference) ? retainedSecretReference : newSecretReference;
        if (!anonymous && string.IsNullOrWhiteSpace(secretReference) && string.IsNullOrWhiteSpace(ConnectionAssertionEndpoint))
        {
            if (!string.IsNullOrWhiteSpace(newSecretReference))
            {
                await secretStore.DeleteAsync(newSecretReference, CancellationToken.None).ConfigureAwait(true);
            }

            throw new InvalidOperationException("Configure a HealthWizz assertion endpoint or provide a client secret for direct OAuth client credentials.");
        }

        var persistenceSucceeded = false;
        try
        {
            var correlation = Guid.NewGuid().ToString("N");
            Site savedSite;
            CredentialProfile savedCredential;
            if (existingSite is not null && existingCredential is not null)
            {
                existingCredential.Update(
                    ConnectionSiteName + (anonymous ? " Anonymous" : " OAuth"),
                    anonymous ? "None" : "OAuth2ClientCredentials",
                    anonymous ? null : ConnectionClientId,
                    secretReference,
                    anonymous ? null : ConnectionTokenEndpoint,
                    now,
                    anonymous ? null : ConnectionScope,
                    anonymous ? null : ConnectionAudience,
                    anonymous || string.IsNullOrWhiteSpace(ConnectionAssertionEndpoint) ? null : ConnectionAssertionEndpoint,
                    anonymous || string.IsNullOrWhiteSpace(ConnectionAuthConfigurationJson) ? "{}" : ConnectionAuthConfigurationJson);
                existingSite.Update(
                    ConnectionSiteName,
                    fhirUri,
                    existingCredential.Id,
                    now,
                    existingSite.Description,
                    existingSite.TenantIdentifier,
                    existingSite.PracticeCode);
                await configurationStore.UpdateConnectionAsync(
                    existingSite,
                    existingCredential,
                    Audit("ConnectionUpdated", existingSite.Id, correlation),
                    CancellationToken.None).ConfigureAwait(true);
                savedSite = existingSite;
                savedCredential = existingCredential;
            }
            else
            {
                savedCredential = new CredentialProfile(
                    Guid.NewGuid(), vendor.Id, ConnectionSiteName + (anonymous ? " Anonymous" : " OAuth"), anonymous ? "None" : "OAuth2ClientCredentials",
                    anonymous ? null : ConnectionClientId, secretReference, anonymous ? null : ConnectionTokenEndpoint, now,
                    anonymous ? null : ConnectionScope, anonymous ? null : ConnectionAudience,
                    anonymous || string.IsNullOrWhiteSpace(ConnectionAssertionEndpoint) ? null : ConnectionAssertionEndpoint,
                    anonymous || string.IsNullOrWhiteSpace(ConnectionAuthConfigurationJson) ? "{}" : ConnectionAuthConfigurationJson);
                savedSite = new Site(Guid.NewGuid(), vendor.Id, ConnectionSiteName, fhirUri, savedCredential.Id, now);
                await configurationStore.AddConnectionAsync(
                    savedSite,
                    savedCredential,
                    Audit("ConnectionCreated", savedSite.Id, correlation),
                    CancellationToken.None).ConfigureAwait(true);
            }

            persistenceSucceeded = true;
            tokenProvider.Invalidate(savedCredential.Id);
            ConnectionClientSecret = string.Empty;
            ConnectionRemoveStoredSecret = false;
            if (!string.IsNullOrWhiteSpace(editingOriginalSecretReference) &&
                !string.Equals(editingOriginalSecretReference, secretReference, StringComparison.Ordinal))
            {
                await DeleteRetiredSecretWithWarningAsync(editingOriginalSecretReference, "Connection changes were saved").ConfigureAwait(true);
            }

            ResetConnectionEditor();
            await RefreshAsync().ConfigureAwait(true);
            SelectedConnectionSite = Sites.FirstOrDefault(x => x.Id == savedSite.Id);
            StatusMessage = "Connection saved. Test it before starting or importing an export.";
        }
        catch
        {
            if (!persistenceSucceeded && !string.IsNullOrWhiteSpace(newSecretReference))
            {
                await DeleteRetiredSecretWithWarningAsync(newSecretReference, "Connection save failed").ConfigureAwait(true);
            }

            throw;
        }
    }

    private async Task EditSelectedConnectionAsync()
    {
        var selected = SelectedConnectionSite ?? throw new InvalidOperationException("Select a connection to edit.");
        var site = await configurationStore.GetSiteAsync(selected.Id, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("The selected connection no longer exists.");
        var credential = await configurationStore.GetCredentialProfileAsync(site.CredentialProfileId, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("The selected connection credential profile is missing.");
        var vendor = Vendors.FirstOrDefault(x => x.Id == site.VendorId)
            ?? await configurationStore.GetVendorAsync(site.VendorId, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("The selected connection vendor is missing.");

        editingConnectionSiteId = site.Id;
        editingCredentialProfileId = credential.Id;
        editingOriginalSecretReference = credential.SecretReference;
        SelectedConnectionVendor = vendor;
        ConnectionSiteName = site.Name;
        ConnectionFhirBaseUrl = site.FhirBaseUrl;
        ConnectionClientId = credential.ClientId;
        ConnectionClientSecret = string.Empty;
        ConnectionRemoveStoredSecret = false;
        ConnectionTokenEndpoint = credential.TokenEndpoint;
        ConnectionAssertionEndpoint = credential.AssertionEndpoint ?? string.Empty;
        ConnectionScope = credential.Scope ?? string.Empty;
        ConnectionAudience = credential.Audience ?? string.Empty;
        ConnectionAuthConfigurationJson = string.IsNullOrWhiteSpace(credential.AdditionalConfigurationJson)
            ? "{}"
            : credential.AdditionalConfigurationJson;
        RaiseConnectionEditorState();
        StatusMessage = $"Editing connection '{site.Name}'. Leave the secret blank to keep the stored value.";
    }

    private async Task DeleteSelectedConnectionAsync()
    {
        var selected = SelectedConnectionSite ?? throw new InvalidOperationException("Select a connection to delete.");
        var site = await configurationStore.GetSiteAsync(selected.Id, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("The selected connection no longer exists.");
        var credential = await configurationStore.GetCredentialProfileAsync(site.CredentialProfileId, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("The selected connection credential profile is missing.");
        var confirmation = dialogs.RequestTypedConfirmation(
            "Delete connection",
            $"This permanently deletes the local connection profile. It does not contact the FHIR server. Deletion is blocked if any export job references it.\n\nConnection: {site.Name}\nFHIR base: {site.FhirBaseUrl}",
            "DELETE CONNECTION");
        if (confirmation is null)
        {
            return;
        }

        var correlation = Guid.NewGuid().ToString("N");
        var result = await configurationStore.DeleteConnectionAsync(
            site.Id,
            credential.Id,
            Audit("ConnectionDeleted", site.Id, correlation),
            CancellationToken.None).ConfigureAwait(true);
        tokenProvider.Invalidate(credential.Id);
        if (result.CredentialDeleted && !string.IsNullOrWhiteSpace(credential.SecretReference))
        {
            await DeleteRetiredSecretWithWarningAsync(credential.SecretReference, "The connection was deleted").ConfigureAwait(true);
        }

        if (editingConnectionSiteId == site.Id)
        {
            ResetConnectionEditor();
        }

        SelectedConnectionSite = null;
        await RefreshAsync().ConfigureAwait(true);
        StatusMessage = $"Connection '{site.Name}' was deleted locally.";
    }

    private Task ResetConnectionEditorAsync()
    {
        ResetConnectionEditor();
        StatusMessage = "Connection editor cleared for a new connection.";
        return Task.CompletedTask;
    }

    private void ResetConnectionEditor()
    {
        editingConnectionSiteId = null;
        editingCredentialProfileId = null;
        editingOriginalSecretReference = string.Empty;
        ConnectionSiteName = string.Empty;
        ConnectionFhirBaseUrl = string.Empty;
        ConnectionClientId = string.Empty;
        ConnectionClientSecret = string.Empty;
        ConnectionRemoveStoredSecret = false;
        ConnectionTokenEndpoint = string.Equals(SelectedConnectionVendor?.PluginId, "eclinicalworks", StringComparison.Ordinal)
            ? DefaultEClinicalWorksTokenEndpoint
            : string.Empty;
        ConnectionAssertionEndpoint = string.Empty;
        ConnectionScope = string.Empty;
        ConnectionAudience = string.Empty;
        ConnectionAuthConfigurationJson = "{}";
        RaiseConnectionEditorState();
    }

    private void RaiseConnectionEditorState()
    {
        OnPropertyChanged(nameof(IsEditingConnection));
        OnPropertyChanged(nameof(CanChangeConnectionVendor));
        OnPropertyChanged(nameof(ConnectionEditorTitle));
        OnPropertyChanged(nameof(ConnectionSaveButtonText));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DeleteRetiredSecretWithWarningAsync(string secretReference, string completedAction)
    {
        try
        {
            await secretStore.DeleteAsync(secretReference, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            dialogs.ShowError(
                "Protected secret cleanup warning",
                completedAction + ", but the retired encrypted secret file could not be removed. " + SecurityRedactor.Redact(exception.Message));
        }
    }

    private async Task TestConnectionAsync()
    {
        var site = SelectedConnectionSite ?? throw new InvalidOperationException("Select a site.");
        var vendor = await configurationStore.GetVendorAsync(site.VendorId, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var credential = await configurationStore.GetCredentialProfileAsync(site.CredentialProfileId, CancellationToken.None).ConfigureAwait(true)
            ?? throw new InvalidOperationException("Credential profile is missing.");
        var plugin = pluginCatalog.GetRequired(vendor.PluginId, vendor.PluginVersion);
        var context = new VendorSiteContext(vendor.Id, site.Id, new Uri(site.FhirBaseUrl), site.TenantIdentifier, site.PracticeCode, "{}");
        var result = await plugin.TestConnectionAsync(context, token: null, cancellationToken: CancellationToken.None).ConfigureAwait(true);
        var now = timeProvider.GetUtcNow();
        var anonymous = string.Equals(credential.AuthenticationType, "None", StringComparison.OrdinalIgnoreCase);
        var advertisedTokenEndpoint = result.DiscoveredTokenEndpoint;
        var tokenEndpointUpdated = false;
        var endpointWarning = string.Empty;
        if (result.Succeeded && !anonymous && !string.IsNullOrWhiteSpace(advertisedTokenEndpoint))
        {
            try
            {
                var discoveredTokenEndpoint = Guard.AbsoluteHttpsUri(
                    advertisedTokenEndpoint,
                    nameof(result.DiscoveredTokenEndpoint),
                    allowLoopbackHttp: true).AbsoluteUri;
                tokenEndpointUpdated = !string.Equals(credential.TokenEndpoint, discoveredTokenEndpoint, StringComparison.OrdinalIgnoreCase);
                credential.Update(
                    credential.Name,
                    credential.AuthenticationType,
                    credential.ClientId,
                    credential.SecretReference,
                    discoveredTokenEndpoint,
                    now,
                    credential.Scope,
                    credential.Audience,
                    credential.AssertionEndpoint,
                    credential.AdditionalConfigurationJson);
            }
            catch (ArgumentException)
            {
                endpointWarning = " Metadata advertised a non-HTTPS or invalid OAuth token endpoint; the existing configured value was retained.";
            }
        }

        var attempt = new ConnectionMetadataAttempt(
            now,
            result.Succeeded,
            result.StatusCode is { } statusCode ? (int)statusCode : null,
            result.Summary,
            result.HttpExchanges ?? []);
        var correlation = Guid.NewGuid().ToString("N");
        await configurationStore.RecordConnectionMetadataAsync(
            vendor.PluginId,
            site.Id,
            credential,
            attempt,
            result.SanitizedCapabilityStatementJson,
            advertisedTokenEndpoint,
            Audit("ConnectionMetadataRecorded", site.Id, correlation),
            CancellationToken.None).ConfigureAwait(true);
        if (tokenEndpointUpdated)
        {
            tokenProvider.Invalidate(credential.Id);
            if (editingConnectionSiteId == site.Id)
            {
                ConnectionTokenEndpoint = credential.TokenEndpoint;
            }
        }

        var generation = Interlocked.Increment(ref connectionMetadataLoadGeneration);
        await LoadConnectionMetadataAsync(site, generation).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            if (result.StatusCode == HttpStatusCode.BadRequest)
            {
                transientNotifications.ShowNetworkAccessHint("retrieving FHIR metadata");
            }

            throw new InvalidOperationException(result.Summary);
        }

        var endpointMessage = tokenEndpointUpdated
            ? $" OAuth token endpoint updated from metadata to {credential.TokenEndpoint}."
            : !anonymous && string.IsNullOrWhiteSpace(advertisedTokenEndpoint)
                ? " Metadata did not advertise an OAuth token endpoint; the existing configured value was retained."
                : endpointWarning;
        dialogs.ShowInfo("Connection succeeded", result.Summary + endpointMessage + " Metadata and the complete HTTP exchange were saved. No export was created.");
        StatusMessage = result.Summary + endpointMessage;
    }

    private async Task LoadConnectionMetadataAsync(Site? site, long generation)
    {
        if (site is null)
        {
            ConnectionMetadataJson = string.Empty;
            ConnectionMetadataHttpHistoryText = string.Empty;
            ConnectionMetadataStatus = "Select a connection to view its saved metadata status.";
            return;
        }

        ConnectionMetadataStatus = "Loading saved metadata…";
        try
        {
            var vendor = Vendors.FirstOrDefault(x => x.Id == site.VendorId)
                ?? await configurationStore.GetVendorAsync(site.VendorId, CancellationToken.None).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Vendor is missing.");
            var history = await configurationStore.GetConnectionMetadataAsync(vendor.PluginId, site.Id, CancellationToken.None).ConfigureAwait(true);
            if (generation != Interlocked.Read(ref connectionMetadataLoadGeneration) || SelectedConnectionSite?.Id != site.Id)
            {
                return;
            }

            if (history is null)
            {
                ConnectionMetadataJson = string.Empty;
                ConnectionMetadataHttpHistoryText = string.Empty;
                ConnectionMetadataStatus = "No metadata test has been saved for this connection.";
                return;
            }

            ConnectionMetadataJson = history.LatestCapabilityStatementJson ?? string.Empty;
            ConnectionMetadataHttpHistoryText = string.Join(
                Environment.NewLine + Environment.NewLine + new string('=', 80) + Environment.NewLine + Environment.NewLine,
                history.Attempts.SelectMany((attempt, attemptIndex) =>
                    attempt.HttpExchanges.Select((exchange, exchangeIndex) =>
                        $"Metadata test {attemptIndex + 1}, HTTP exchange {exchangeIndex + 1} · {DateTimeDisplayPolicy.Format(attempt.RetrievedAtUtc)} · {(attempt.Succeeded ? "Succeeded" : "Failed")}" +
                        Environment.NewLine + new HttpExchangeViewItem(exchange).AllText)));
            var latestAttempt = history.Attempts.LastOrDefault();
            var endpoint = string.IsNullOrWhiteSpace(history.DiscoveredTokenEndpoint)
                ? "not advertised"
                : history.DiscoveredTokenEndpoint;
            ConnectionMetadataStatus = latestAttempt is null
                ? "Stored metadata history is empty."
                : $"{history.Attempts.Count:N0} metadata test(s) preserved. Last test: {DateTimeDisplayPolicy.Format(latestAttempt.RetrievedAtUtc)} ({(latestAttempt.Succeeded ? "succeeded" : "failed")}). OAuth token endpoint: {endpoint}.";
        }
        catch (Exception exception)
        {
            if (generation != Interlocked.Read(ref connectionMetadataLoadGeneration))
            {
                return;
            }

            ConnectionMetadataJson = string.Empty;
            ConnectionMetadataHttpHistoryText = string.Empty;
            ConnectionMetadataStatus = "Could not load saved metadata: " + SecurityRedactor.Redact(exception.Message);
        }
    }

    private Task CopyTextAsync(string? text, string description)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"No {description} is available to copy.");
        }

        System.Windows.Clipboard.SetText(text);
        StatusMessage = $"Copied {description} to the clipboard with secrets redacted.";
        return Task.CompletedTask;
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

    private async Task ChangePasswordAsync()
    {
        var configured = await passwordProtection.IsConfiguredAsync(CancellationToken.None).ConfigureAwait(true);
        var request = dialogs.RequestPasswordChange(configured);
        if (request is null)
        {
            return;
        }

        await passwordProtection.SetOrChangeAsync(request.CurrentPassword, request.NewPassword, CancellationToken.None).ConfigureAwait(true);
        PasswordConfigured = true;
        dialogs.ShowInfo(
            configured ? "Password changed" : "Password protection enabled",
            "The application password was saved safely. It will be required at the next startup and before cancelling a remote export.");
        SettingsStatus = "Security options updated.";
    }

    private async Task RemovePasswordAsync()
    {
        if (!await passwordProtection.IsConfiguredAsync(CancellationToken.None).ConfigureAwait(true))
        {
            PasswordConfigured = false;
            return;
        }

        var password = dialogs.RequestPassword(
            "Remove application password",
            "Enter the current application password. Removing it disables startup protection and the extra password check for remote cancellation.");
        if (password is null ||
            !dialogs.Confirm("Remove password protection", "Remove the application password from this Windows installation?"))
        {
            return;
        }

        await passwordProtection.RemoveAsync(password, CancellationToken.None).ConfigureAwait(true);
        PasswordConfigured = false;
        SettingsStatus = "Password protection removed.";
    }

    private async Task RestoreDefaultOptionsAsync()
    {
        if (!dialogs.Confirm(
            "Restore default options",
            "Restore date display, auto-refresh, tray behavior, and network-hint options to their defaults? Password protection is not changed."))
        {
            return;
        }

        ApplyPreferences(new ApplicationPreferences());
        settingsDirty = true;
        await SavePendingSettingsAsync().ConfigureAwait(true);
        await RefreshJobsAsync().ConfigureAwait(true);
    }

    private void ApplyPreferences(ApplicationPreferences preferences)
    {
        dateDisplayMode = preferences.DateMode;
        autoRefreshEnabled = preferences.AutoRefreshEnabled;
        autoRefreshIntervalSeconds = preferences.AutoRefreshIntervalSeconds;
        minimizeToTray = preferences.MinimizeToTray;
        showNetworkAccessHintOnBadRequest = preferences.ShowNetworkAccessHintOnBadRequest;
        DateTimeDisplayPolicy.Mode = dateDisplayMode;
        foreach (var propertyName in new[]
        {
            nameof(SelectedDateDisplayMode), nameof(DateDisplayDescription), nameof(AutoRefreshEnabled),
            nameof(AutoRefreshIntervalSeconds), nameof(AutoRefreshIntervalText), nameof(MinimizeToTray),
            nameof(ShowNetworkAccessHintOnBadRequest),
        })
        {
            OnPropertyChanged(propertyName);
        }

        ResetAutoRefreshDeadline();
    }

    private void PreferencesChanged()
    {
        if (!initialized || disposed)
        {
            return;
        }

        settingsDirty = true;
        SettingsStatus = "Saving options…";
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    private async void SettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        settingsSaveTimer.Stop();
        try
        {
            await SavePendingSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            SettingsStatus = "Options could not be saved: " + SecurityRedactor.Redact(exception.Message);
        }
    }

    private async Task SavePendingSettingsAsync()
    {
        if (!settingsDirty)
        {
            return;
        }

        await applicationSettings.SaveAsync(
            new ApplicationPreferences
            {
                DateMode = dateDisplayMode,
                AutoRefreshEnabled = autoRefreshEnabled,
                AutoRefreshIntervalSeconds = autoRefreshIntervalSeconds,
                MinimizeToTray = minimizeToTray,
                ShowNetworkAccessHintOnBadRequest = showNetworkAccessHintOnBadRequest,
            },
            CancellationToken.None).ConfigureAwait(true);
        settingsDirty = false;
        SettingsStatus = "Options saved automatically.";
    }

    private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        JobDashboard = JobProgressAnalytics.Calculate(SelectedJob, selectedJobEvents, selectedJobFiles, timeProvider.GetUtcNow());
        UpdateAutoRefreshCountdown();
        if (!AutoRefreshEnabled || refreshInProgress || nextAutoRefreshAtUtc is null || timeProvider.GetUtcNow() < nextAutoRefreshAtUtc)
        {
            return;
        }

        try
        {
            await RefreshJobsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = "Auto refresh failed safely: " + SecurityRedactor.Redact(exception.Message);
            ResetAutoRefreshDeadline();
        }
    }

    private void ResetAutoRefreshDeadline()
    {
        nextAutoRefreshAtUtc = AutoRefreshEnabled
            ? timeProvider.GetUtcNow().AddSeconds(AutoRefreshIntervalSeconds)
            : null;
        UpdateAutoRefreshCountdown();
    }

    private void UpdateAutoRefreshCountdown()
    {
        if (!AutoRefreshEnabled || nextAutoRefreshAtUtc is null)
        {
            AutoRefreshCountdownText = "Auto refresh off";
            return;
        }

        if (refreshInProgress)
        {
            AutoRefreshCountdownText = "Refreshing…";
            return;
        }

        var seconds = Math.Max(0, (int)Math.Ceiling((nextAutoRefreshAtUtc.Value - timeProvider.GetUtcNow()).TotalSeconds));
        AutoRefreshCountdownText = $"Next refresh in {seconds}s";
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        autoRefreshTimer.Stop();
        autoRefreshTimer.Tick -= AutoRefreshTimer_Tick;
        settingsSaveTimer.Stop();
        settingsSaveTimer.Tick -= SettingsSaveTimer_Tick;
        if (settingsDirty)
        {
            try
            {
                applicationSettings.SaveAsync(
                    new ApplicationPreferences
                    {
                        DateMode = dateDisplayMode,
                        AutoRefreshEnabled = autoRefreshEnabled,
                        AutoRefreshIntervalSeconds = autoRefreshIntervalSeconds,
                        MinimizeToTray = minimizeToTray,
                        ShowNetworkAccessHintOnBadRequest = showNetworkAccessHintOnBadRequest,
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // The most recent successfully saved settings remain valid.
            }
        }

        GC.SuppressFinalize(this);
    }

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
