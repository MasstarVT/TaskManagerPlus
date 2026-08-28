using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows.Data;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the "Devices &amp; Drivers" tab (#453-461). On-demand like StabilityViewModel/
/// SystemSpecsViewModel - an initial load plus a manual Refresh command, no DispatcherTimer -
/// since `driverquery /v` + a full Win32_PnPSignedDriver sweep + per-row registry reads is exactly
/// the kind of "recursive inventory sweep, gate behind a button" work CLAUDE.md calls out, and the
/// catalog-aware signature check (#454) is deliberately its own separate opt-in action on top of
/// that (bulk-verifying every driver's signature can itself take a while).
///
/// Designed so later chunks (device-tree/phantom-devices, driver-store, WHEA-details, filter-
/// drivers) can add more sections to this same tab/ViewModel without restructuring what's here:
/// each concern (inventory grid, per-device file detail, signature verification, security posture)
/// is a self-contained group of properties/commands rather than intertwined state.
/// </summary>
public sealed class DevicesDriversViewModel : ObservableObject
{
    // --- #453: primary inventory grid ---
    public ObservableCollection<DriverInventoryRow> Drivers { get; } = new();

    /// <summary>#461: filtered view backing the DataGrid - ThirdPartyOnly toggles the predicate
    /// rather than rebuilding Drivers, so per-row signature/match-quality state (and the DataGrid's
    /// own selection) survives a filter flip.</summary>
    public ICollectionView DriversView { get; }

    private bool _thirdPartyOnly = true; // #461: ON by default
    public bool ThirdPartyOnly
    {
        get => _thirdPartyOnly;
        set
        {
            if (!SetProperty(ref _thirdPartyOnly, value)) return;
            DriversView.Refresh();
        }
    }

    private bool FilterDriver(object obj) => !ThirdPartyOnly || (obj is DriverInventoryRow row && row.IsThirdParty);

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string? _refreshErrorText;
    public string? RefreshErrorText { get => _refreshErrorText; private set => SetProperty(ref _refreshErrorText, value); }

    private DateTime? _lastRefreshedUtc;
    public string LastRefreshedText => _lastRefreshedUtc is { } t
        ? $"Last refreshed {t.ToLocalTime():g} - {Drivers.Count} drivers"
        : "Not yet loaded";

    public AsyncRelayCommand RefreshCommand { get; }

    // --- #459: per-device installed-file detail pane, populated when a grid row is selected ---
    private DriverInventoryRow? _selectedDriver;
    public DriverInventoryRow? SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (!SetProperty(ref _selectedDriver, value)) return;
            _ = LoadSelectedDriverFilesAsync();
        }
    }

    public ObservableCollection<DriverFileInfo> SelectedDriverFiles { get; } = new();

    private bool _isLoadingDriverFiles;
    public bool IsLoadingDriverFiles { get => _isLoadingDriverFiles; private set => SetProperty(ref _isLoadingDriverFiles, value); }

    private string _selectedDriverFilesStatusText = "Select a driver above to list the files its package installed.";
    public string SelectedDriverFilesStatusText { get => _selectedDriverFilesStatusText; private set => SetProperty(ref _selectedDriverFilesStatusText, value); }

    /// <summary>Per-file "Verify" in the detail pane - reuses CatalogSignatureService directly,
    /// same as VerifyCommand below (#454).</summary>
    public RelayCommand VerifyFileCommand { get; }

    // --- #454/#455/#457: catalog-aware signature verification, per-row or bulk ---
    public RelayCommand VerifyCommand { get; }
    public AsyncRelayCommand VerifyAllCommand { get; }

    private bool _isVerifyingAll;
    public bool IsVerifyingAll { get => _isVerifyingAll; private set => SetProperty(ref _isVerifyingAll, value); }

    private string _verifyAllProgressText = string.Empty;
    public string VerifyAllProgressText { get => _verifyAllProgressText; private set => SetProperty(ref _verifyAllProgressText, value); }

    // --- #456: security posture (code-integrity bypass detection) ---
    private bool _testSigningEnabled;
    public bool TestSigningEnabled { get => _testSigningEnabled; private set => SetProperty(ref _testSigningEnabled, value); }

    private bool _noIntegrityChecksEnabled;
    public bool NoIntegrityChecksEnabled { get => _noIntegrityChecksEnabled; private set => SetProperty(ref _noIntegrityChecksEnabled, value); }

    private bool _driverSignatureEnforcementDisabled;
    public bool DriverSignatureEnforcementDisabled { get => _driverSignatureEnforcementDisabled; private set => SetProperty(ref _driverSignatureEnforcementDisabled, value); }

    private int _codeIntegrityBlockedEventCount;
    public int CodeIntegrityBlockedEventCount { get => _codeIntegrityBlockedEventCount; private set => SetProperty(ref _codeIntegrityBlockedEventCount, value); }

    private string _lastCodeIntegrityBlockedEventText = "None in the last 30 days";
    public string LastCodeIntegrityBlockedEventText { get => _lastCodeIntegrityBlockedEventText; private set => SetProperty(ref _lastCodeIntegrityBlockedEventText, value); }

    public ObservableCollection<string> CodeIntegrityRecentMessages { get; } = new();

    /// <summary>Drives the security-posture row's warning styling - true when any bypass signal is
    /// on, or any blocked-image event was found. A quick flag, not a verdict: testsigning/
    /// nointegritychecks are sometimes turned on deliberately by a developer, and a handful of old
    /// blocked-image events from months ago don't necessarily mean anything is wrong today.</summary>
    public bool IsSecurityPostureConcerning =>
        TestSigningEnabled || NoIntegrityChecksEnabled || DriverSignatureEnforcementDisabled || CodeIntegrityBlockedEventCount > 0;

    // --- sub-tab strip: this tab's second top-level view (#468's device tree) is distinct from
    // the driver-inventory grid above - toggled by two buttons rather than a WPF TabControl, kept
    // simple since it's just one bool driving two panels' visibility. ---
    private bool _isDeviceTreeViewActive;
    public bool IsDeviceTreeViewActive { get => _isDeviceTreeViewActive; set => SetProperty(ref _isDeviceTreeViewActive, value); }
    public RelayCommand ShowDriverInventoryViewCommand { get; }
    public RelayCommand ShowDeviceTreeViewCommand { get; }

    // --- #463 (event-log half)/#464/#466: cheap targeted event-log queries and a bounded WMI+
    // registry sweep, all folded into this tab's existing on-demand Refresh (RefreshAsync below)
    // rather than needing their own separate buttons - none of these are expensive enough on their
    // own to warrant it (unlike #462's setupapi.dev.log parse, which genuinely is). ---
    private readonly EventLogService _eventLog = new();

    public ObservableCollection<PnpConfigurationFailure> PnpConfigurationFailures { get; } = new();
    public ObservableCollection<BootDriverLoadFailure> BootDriverLoadFailures { get; } = new();
    public ObservableCollection<DriverVersionConsistencyGroup> VersionConsistencyIssues { get; } = new();

    // --- #462/#463 (setupapi half): driver install timeline + failures, parsed from
    // setupapi.dev.log - gated behind its own Load button since that file can be tens of MB. ---
    public ObservableCollection<DriverInstallEvent> InstallTimeline { get; } = new();
    public ObservableCollection<DriverInstallFailure> InstallFailures { get; } = new();

    private bool _isLoadingTimeline;
    public bool IsLoadingTimeline { get => _isLoadingTimeline; private set => SetProperty(ref _isLoadingTimeline, value); }

    private bool _hasLoadedTimelineOnce;

    private bool _timelineLast30DaysOnly = true;
    public bool TimelineLast30DaysOnly
    {
        get => _timelineLast30DaysOnly;
        set
        {
            if (!SetProperty(ref _timelineLast30DaysOnly, value)) return;
            if (_hasLoadedTimelineOnce) _ = LoadTimelineAsync(); // re-filter against the already-loaded log
        }
    }

    private string _timelineStatusText = "Not loaded - setupapi.dev.log can be tens of MB, so it's only parsed when you click Load.";
    public string TimelineStatusText { get => _timelineStatusText; private set => SetProperty(ref _timelineStatusText, value); }

    public AsyncRelayCommand LoadTimelineCommand { get; }

    // --- #465: best-effort logman NT Kernel Logger driver-load capture - see
    // DriverLoadTraceService's remarks for why the .etl isn't parsed in-app. ---
    private bool _isDriverLoadTraceRunning;
    public bool IsDriverLoadTraceRunning { get => _isDriverLoadTraceRunning; private set => SetProperty(ref _isDriverLoadTraceRunning, value); }

    private string _driverLoadTraceStatusText =
        "Not running. Starts a best-effort NT Kernel Logger capture (logman) of driver/module load and unload events to a .etl file - open it in Windows Performance Analyzer, since this app doesn't parse ETW events in-app.";
    public string DriverLoadTraceStatusText { get => _driverLoadTraceStatusText; private set => SetProperty(ref _driverLoadTraceStatusText, value); }

    private string? _driverLoadTraceFilePath;
    public string? DriverLoadTraceFilePath { get => _driverLoadTraceFilePath; private set => SetProperty(ref _driverLoadTraceFilePath, value); }

    private string? _driverLoadTraceFilePathPending;

    public AsyncRelayCommand StartDriverLoadTraceCommand { get; }
    public AsyncRelayCommand StopDriverLoadTraceCommand { get; }

    // --- #467: class-wide filter driver inspection, gated behind its own Load button (a full
    // Control\Class sweep). Per-device filters (also #467) are read cheaply and lazily below,
    // whenever a #468 device-tree row is selected - see SelectedDeviceNode. ---
    public ObservableCollection<ClassFilterEntry> ClassFilters { get; } = new();

    private bool _isLoadingClassFilters;
    public bool IsLoadingClassFilters { get => _isLoadingClassFilters; private set => SetProperty(ref _isLoadingClassFilters, value); }

    private string _classFiltersStatusText = "Not loaded - click Load to scan every device setup class's registered filter drivers.";
    public string ClassFiltersStatusText { get => _classFiltersStatusText; private set => SetProperty(ref _classFiltersStatusText, value); }

    public AsyncRelayCommand LoadClassFiltersCommand { get; }

    // --- #468/#469/#471: device tree, this tab's second top-level view - device-centric, grouped
    // by setup class, distinct from the driver-file-centric inventory grid above. ---
    public ObservableCollection<PnpDeviceNode> DeviceTree { get; } = new();
    public ICollectionView DeviceTreeView { get; }

    private List<PnpDeviceNode> _presentDeviceNodes = new();
    private List<PnpDeviceNode>? _nonPresentDeviceNodes; // null until #471's toggle is switched on at least once

    private bool _isLoadingDeviceTree;
    public bool IsLoadingDeviceTree { get => _isLoadingDeviceTree; private set => SetProperty(ref _isLoadingDeviceTree, value); }

    private string _deviceTreeStatusText = "Not yet loaded.";
    public string DeviceTreeStatusText { get => _deviceTreeStatusText; private set => SetProperty(ref _deviceTreeStatusText, value); }

    /// <summary>#471: lazily triggers the one-time SetupDiGetClassDevs(DIGCF_ALLCLASSES) sweep the
    /// first time it's switched on - not re-run automatically after that (matches Load's own
    /// "explicit action" gating) until the next full Load.</summary>
    private bool _showNonPresentDevices;
    public bool ShowNonPresentDevices
    {
        get => _showNonPresentDevices;
        set
        {
            if (!SetProperty(ref _showNonPresentDevices, value)) return;
            if (value && _nonPresentDeviceNodes is null) _ = LoadNonPresentDevicesAsync();
            else RebuildDeviceTree();
        }
    }

    public AsyncRelayCommand LoadDeviceTreeCommand { get; }

    private PnpDeviceNode? _selectedDeviceNode;
    public PnpDeviceNode? SelectedDeviceNode
    {
        get => _selectedDeviceNode;
        set
        {
            if (!SetProperty(ref _selectedDeviceNode, value)) return;
            // #467 (per-device half): a single cheap registry-key read, not a tree walk, so this
            // runs directly on selection rather than needing its own button.
            SelectedDeviceFilters.Clear();
            foreach (var f in ClassFilterDriverService.ReadDeviceFilters(value?.DeviceId)) SelectedDeviceFilters.Add(f);
        }
    }

    public ObservableCollection<ClassFilterEntry> SelectedDeviceFilters { get; } = new();

    public DevicesDriversViewModel()
    {
        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;

        DeviceTreeView = CollectionViewSource.GetDefaultView(DeviceTree);
        DeviceTreeView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PnpDeviceNode.ClassName)));
        DeviceTreeView.SortDescriptions.Add(new SortDescription(nameof(PnpDeviceNode.ClassName), ListSortDirection.Ascending));
        DeviceTreeView.SortDescriptions.Add(new SortDescription(nameof(PnpDeviceNode.Name), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        VerifyCommand = new RelayCommand(param => _ = VerifyRowAsync(param as DriverInventoryRow));
        VerifyFileCommand = new RelayCommand(param => _ = VerifyFileAsync(param as DriverFileInfo));
        VerifyAllCommand = new AsyncRelayCommand(VerifyAllAsync);

        ShowDriverInventoryViewCommand = new RelayCommand(_ => IsDeviceTreeViewActive = false);
        ShowDeviceTreeViewCommand = new RelayCommand(_ => IsDeviceTreeViewActive = true);

        LoadTimelineCommand = new AsyncRelayCommand(LoadTimelineAsync);
        StartDriverLoadTraceCommand = new AsyncRelayCommand(StartDriverLoadTraceAsync);
        StopDriverLoadTraceCommand = new AsyncRelayCommand(StopDriverLoadTraceAsync);
        LoadClassFiltersCommand = new AsyncRelayCommand(LoadClassFiltersAsync);
        LoadDeviceTreeCommand = new AsyncRelayCommand(LoadDeviceTreeAsync);

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var rows = await DriverInventoryService.ListAsync();
            Drivers.Clear();
            foreach (var r in rows) Drivers.Add(r);

            var posture = await CodeIntegrityPostureService.ReadAsync();
            ApplyPosture(posture);

            // #463 (event-log half) / #464: cheap targeted event-log queries - safe to fold into
            // this tab's existing on-demand Refresh rather than needing their own separate buttons.
            var pnpConfigFailures = await Task.Run(() => _eventLog.ReadPnpConfigurationFailures());
            PnpConfigurationFailures.Clear();
            foreach (var f in pnpConfigFailures) PnpConfigurationFailures.Add(f);

            var bootFailures = await Task.Run(() => _eventLog.ReadBootDriverLoadFailures());
            BootDriverLoadFailures.Clear();
            foreach (var f in bootFailures) BootDriverLoadFailures.Add(f);

            // #466: driver version consistency across identical devices - a bounded WMI + registry
            // sweep, likewise cheap enough to fold into this tab's existing Refresh.
            var versionIssues = await DriverVersionConsistencyService.ScanAsync();
            VersionConsistencyIssues.Clear();
            foreach (var g in versionIssues) VersionConsistencyIssues.Add(g);

            _lastRefreshedUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(LastRefreshedText));
            RefreshErrorText = null;
        }
        catch (Exception ex)
        {
            RefreshErrorText = $"Couldn't load driver inventory: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyPosture(CodeIntegrityPostureService.CodeIntegrityPosture posture)
    {
        TestSigningEnabled = posture.TestSigningEnabled;
        NoIntegrityChecksEnabled = posture.NoIntegrityChecksEnabled;
        DriverSignatureEnforcementDisabled = posture.DriverSignatureEnforcementDisabled;
        CodeIntegrityBlockedEventCount = posture.BlockedImageEventCount;
        LastCodeIntegrityBlockedEventText = posture.LastBlockedImageEventTime is { } t
            ? $"Last: {t:g}" : "None in the last 30 days";

        CodeIntegrityRecentMessages.Clear();
        foreach (var m in posture.RecentBlockedImageMessages) CodeIntegrityRecentMessages.Add(m);

        OnPropertyChanged(nameof(IsSecurityPostureConcerning));
    }

    /// <summary>#459: lists the files SelectedDriver's package installed. Guards against a stale
    /// response landing after the user has already clicked a different row while this was in
    /// flight.</summary>
    private async Task LoadSelectedDriverFilesAsync()
    {
        SelectedDriverFiles.Clear();
        var driver = SelectedDriver;
        if (driver is null)
        {
            SelectedDriverFilesStatusText = "Select a driver above to list the files its package installed.";
            return;
        }
        if (driver.PnpDeviceId is not { Length: > 0 } deviceId)
        {
            SelectedDriverFilesStatusText = "No PnP device association found for this driver - can't list installed files.";
            return;
        }

        IsLoadingDriverFiles = true;
        SelectedDriverFilesStatusText = "Loading installed files...";
        try
        {
            var files = await DriverInventoryService.ListDriverFilesAsync(deviceId);
            if (!ReferenceEquals(SelectedDriver, driver)) return; // selection moved on while awaiting

            foreach (var f in files) SelectedDriverFiles.Add(f);
            SelectedDriverFilesStatusText = files.Count == 0 ? "No files found for this driver package." : string.Empty;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(SelectedDriver, driver))
                SelectedDriverFilesStatusText = $"Couldn't list installed files: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(SelectedDriver, driver)) IsLoadingDriverFiles = false;
        }
    }

    private async Task VerifyRowAsync(DriverInventoryRow? row)
    {
        if (row is null) return;
        row.SignatureStatus = "Checking...";
        var result = await CatalogSignatureService.VerifyAsync(row.FilePath);
        ApplySignatureResult(row, result);
        ReportSignatureSummary();
    }

    private async Task VerifyFileAsync(DriverFileInfo? file)
    {
        if (file is null) return;
        file.SignatureStatus = "Checking...";
        var result = await CatalogSignatureService.VerifyAsync(file.FilePath);
        file.SignatureStatus = result.Status;
    }

    /// <summary>#454's bulk "Verify all" - deliberately opt-in (a full catalog-aware check per
    /// driver, run with a small bounded degree of parallelism rather than one-at-a-time, but still
    /// genuinely slow on a system with several hundred drivers - that's expected, not a bug).</summary>
    private async Task VerifyAllAsync()
    {
        var targets = Drivers.ToList();
        if (targets.Count == 0) return;

        IsVerifyingAll = true;
        VerifyAllProgressText = $"Verifying signatures... 0/{targets.Count}";
        try
        {
            int done = 0;
            using var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount));
            var tasks = targets.Select(async row =>
            {
                await gate.WaitAsync();
                try
                {
                    row.SignatureStatus = "Checking...";
                    var result = await CatalogSignatureService.VerifyAsync(row.FilePath);
                    ApplySignatureResult(row, result);
                }
                finally
                {
                    gate.Release();
                    int completed = Interlocked.Increment(ref done);
                    VerifyAllProgressText = $"Verifying signatures... {completed}/{targets.Count}";
                }
            });
            await Task.WhenAll(tasks);

            ReportSignatureSummary();
            int flagged = Drivers.Count(d => d.IsUnsignedOrTestSigned);
            VerifyAllProgressText = $"Checked {targets.Count} driver{(targets.Count == 1 ? "" : "s")} - {flagged} flagged as unsigned/test-signed.";
        }
        finally
        {
            IsVerifyingAll = false;
        }
    }

    private static void ApplySignatureResult(DriverInventoryRow row, CatalogSignatureService.CatalogSignatureResult result)
    {
        row.SignatureStatus = result.Status;
        row.SignerName = result.SignerName;
        row.IsWhql = result.IsWhql;
        row.IsCatalogSigned = result.IsCatalogSigned;
    }

    /// <summary>#455: updates the session-lifetime shared cache SummaryViewModel's Health Check
    /// card reads - see DriverSignatureSummaryState's remarks for why this is a static rather than
    /// a ViewModel-to-ViewModel reference.</summary>
    private void ReportSignatureSummary()
    {
        int count = Drivers.Count(d => d.IsUnsignedOrTestSigned);
        DriverSignatureSummaryState.Report(count);
    }

    /// <summary>#462/#463 (setupapi half): parses setupapi.dev.log into a timeline plus the
    /// subset that failed - the button-gated action itself (never run automatically), since this
    /// file can be tens of MB.</summary>
    private async Task LoadTimelineAsync()
    {
        IsLoadingTimeline = true;
        _hasLoadedTimelineOnce = true;
        TimelineStatusText = "Parsing setupapi.dev.log...";
        try
        {
            DateTime? since = TimelineLast30DaysOnly ? DateTime.Now.AddDays(-30) : null;
            var result = await DriverInstallLogService.ParseAsync(since);

            InstallTimeline.Clear();
            foreach (var e in result.Timeline) InstallTimeline.Add(e);

            InstallFailures.Clear();
            foreach (var f in result.Failures) InstallFailures.Add(f);

            TimelineStatusText = result.ErrorMessage ??
                $"{result.Timeline.Count} install/update event(s) found ({result.Failures.Count} failed)" +
                (TimelineLast30DaysOnly ? ", last 30 days." : ", entire log.");
        }
        catch (Exception ex)
        {
            TimelineStatusText = $"Couldn't parse setupapi.dev.log: {ex.Message}";
        }
        finally
        {
            IsLoadingTimeline = false;
        }
    }

    /// <summary>#465: starts the best-effort logman driver-load capture - no canExecute gating on
    /// the command itself (matching this app's existing AsyncRelayCommand convention, e.g.
    /// VerifyAllCommand above), just an early-out here plus the view swapping which of the Start/
    /// Stop buttons is visible via IsDriverLoadTraceRunning.</summary>
    private async Task StartDriverLoadTraceAsync()
    {
        if (IsDriverLoadTraceRunning) return;

        DriverLoadTraceStatusText = "Starting capture...";
        var result = await DriverLoadTraceService.StartAsync();
        if (result.Success)
        {
            IsDriverLoadTraceRunning = true;
            _driverLoadTraceFilePathPending = result.FilePath;
            DriverLoadTraceFilePath = null;
        }
        DriverLoadTraceStatusText = result.Message;
    }

    private async Task StopDriverLoadTraceAsync()
    {
        if (!IsDriverLoadTraceRunning) return;

        DriverLoadTraceStatusText = "Stopping capture...";
        var result = await DriverLoadTraceService.StopAsync();
        IsDriverLoadTraceRunning = false;
        DriverLoadTraceFilePath = result.Success ? _driverLoadTraceFilePathPending : null;
        DriverLoadTraceStatusText = result.Message;
    }

    /// <summary>#467 (class-wide half): the button-gated full Control\Class sweep.</summary>
    private async Task LoadClassFiltersAsync()
    {
        IsLoadingClassFilters = true;
        ClassFiltersStatusText = "Scanning device setup classes for filter drivers...";
        try
        {
            var filters = await ClassFilterDriverService.ScanClassWideAsync();
            ClassFilters.Clear();
            foreach (var f in filters) ClassFilters.Add(f);

            int missing = filters.Count(f => !f.ServiceExists);
            ClassFiltersStatusText = filters.Count == 0
                ? "No class-wide filter drivers found."
                : $"{filters.Count} filter driver entr{(filters.Count == 1 ? "y" : "ies")} found across every device class" +
                  (missing > 0 ? $" - {missing} reference a service that no longer exists." : ".");
        }
        catch (Exception ex)
        {
            ClassFiltersStatusText = $"Couldn't scan class filters: {ex.Message}";
        }
        finally
        {
            IsLoadingClassFilters = false;
        }
    }

    /// <summary>#468/#469: loads the present-device tree from Win32_PnPEntity, then reports the
    /// problem-device count to #470's Health Check bridge. Re-fetches #471's non-present list too
    /// when that toggle is already on, since a fresh Load should reflect the current present-device
    /// set either way.</summary>
    private async Task LoadDeviceTreeAsync()
    {
        IsLoadingDeviceTree = true;
        DeviceTreeStatusText = "Loading device tree...";
        try
        {
            _presentDeviceNodes = await PnpDeviceTreeService.ListPresentAsync();
            _nonPresentDeviceNodes = null;
            RebuildDeviceTree();

            if (ShowNonPresentDevices) await LoadNonPresentDevicesAsync();

            int problemCount = _presentDeviceNodes.Count(d => d.HasProblem);
            DeviceTreeStatusText = $"{_presentDeviceNodes.Count} present device(s) loaded - {problemCount} showing a problem code.";

            // #470: feed the problem-device count into the Summary tab's Health Check card - see
            // DeviceProblemSummaryState's remarks for why this is a static bridge, not a
            // ViewModel-to-ViewModel reference.
            DeviceProblemSummaryState.Report(problemCount);
        }
        catch (Exception ex)
        {
            DeviceTreeStatusText = $"Couldn't load the device tree: {ex.Message}";
        }
        finally
        {
            IsLoadingDeviceTree = false;
        }
    }

    /// <summary>#471: the one-time (per Load) SetupDiGetClassDevs sweep for non-present devices.</summary>
    private async Task LoadNonPresentDevicesAsync()
    {
        IsLoadingDeviceTree = true;
        try
        {
            _nonPresentDeviceNodes = await PnpDeviceTreeService.ListNonPresentAsync(_presentDeviceNodes.Select(n => n.DeviceId));
            RebuildDeviceTree();
        }
        catch (Exception ex)
        {
            DeviceTreeStatusText = $"Couldn't load non-present devices: {ex.Message}";
        }
        finally
        {
            IsLoadingDeviceTree = false;
        }
    }

    private void RebuildDeviceTree()
    {
        DeviceTree.Clear();
        foreach (var n in _presentDeviceNodes) DeviceTree.Add(n);
        if (ShowNonPresentDevices && _nonPresentDeviceNodes is not null)
            foreach (var n in _nonPresentDeviceNodes) DeviceTree.Add(n);
    }
}
