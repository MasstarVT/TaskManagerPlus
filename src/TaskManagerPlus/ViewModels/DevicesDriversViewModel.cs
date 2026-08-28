using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
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

    // --- #473: "disabled devices only" filter chip on the device-tree view - toggles the
    // predicate rather than rebuilding DeviceTree, matching #461's ThirdPartyOnly pattern above. ---
    private bool _showDisabledOnly;
    public bool ShowDisabledOnly
    {
        get => _showDisabledOnly;
        set
        {
            if (!SetProperty(ref _showDisabledOnly, value)) return;
            DeviceTreeView.Refresh();
        }
    }

    private bool FilterDeviceTree(object obj) => !ShowDisabledOnly || (obj is PnpDeviceNode n && n.IsDisabledByUser);

    // --- #472/#474/#475: pnputil-backed device-tree actions. Enable/Disable/Restart/Remove(single)
    // act on SelectedDeviceNode via a ListBox-level ContextMenu (right-click selects the row first,
    // same as every other Selector-derived control in this app - matches the
    // ContextMenu-at-the-control-level, SelectedX-driven pattern ProcessesView/ServicesView already
    // use rather than a per-row CommandParameter). RemoveCheckedDevicesCommand is the #472 MULTI-
    // select path, driven by each non-present row's own IsCheckedForRemoval checkbox instead. ---
    public AsyncRelayCommand EnableDeviceCommand { get; }
    public AsyncRelayCommand DisableDeviceCommand { get; }
    public AsyncRelayCommand RestartDeviceCommand { get; }
    public AsyncRelayCommand RemoveDeviceCommand { get; }
    public AsyncRelayCommand RemoveCheckedDevicesCommand { get; }

    private bool _isPerformingDeviceAction;
    public bool IsPerformingDeviceAction { get => _isPerformingDeviceAction; private set => SetProperty(ref _isPerformingDeviceAction, value); }

    private string _deviceActionStatusText = string.Empty;
    public string DeviceActionStatusText { get => _deviceActionStatusText; private set => SetProperty(ref _deviceActionStatusText, value); }

    /// <summary>#474: pragmatic safety block, not a precise boot-critical-controller check - see
    /// the suggestion text's own "a simple heuristic ... is acceptable" guidance. Blocks disabling
    /// (but not restarting - see RestartSelectedDeviceAsync) any device in the Display class or in
    /// one of the classic storage host-controller classes, rather than trying to identify the
    /// *specific* boot volume's controller or the *specific* active display adapter.</summary>
    private static readonly HashSet<string> BlockedDisableClassGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{4d36e968-e325-11ce-bfc1-08002be10318}", // Display
        "{4d36e96a-e325-11ce-bfc1-08002be10318}", // HDC - IDE/ATA/ATAPI controllers
        "{4d36e97b-e325-11ce-bfc1-08002be10318}", // SCSIAdapter - SCSI/RAID controllers
        "{a0a588a4-c46f-4b37-b7ea-c82fe89870c6}", // SDHost - SD host controllers
    };

    // --- #476/#477: resource map + interrupt mode, this device-tree view's own "Resources"
    // section - both are cheap WMI/registry pulls, gated behind one shared Load button rather than
    // the tab's Refresh timer per CLAUDE.md's on-demand-for-sweeps convention. ---
    public ObservableCollection<DeviceResourceRow> DeviceResources { get; } = new();
    public ObservableCollection<InterruptModeInfo> InterruptModes { get; } = new();
    public ObservableCollection<PnpDeviceNode> InsufficientResourceDevices { get; } = new();

    private bool _isLoadingResources;
    public bool IsLoadingResources { get => _isLoadingResources; private set => SetProperty(ref _isLoadingResources, value); }

    private string _resourcesStatusText =
        "Not loaded - click Load to read IRQ/I-O/memory/DMA assignments and each device's MSI vs. line-based interrupt mode.";
    public string ResourcesStatusText { get => _resourcesStatusText; private set => SetProperty(ref _resourcesStatusText, value); }

    public AsyncRelayCommand LoadResourcesCommand { get; }

    // --- #478: wake-armed / selective-suspend, this device-tree view's own "Power" section. ---
    public ObservableCollection<WakeDeviceInfo> WakeDevices { get; } = new();

    private bool _isLoadingWakePower;
    public bool IsLoadingWakePower { get => _isLoadingWakePower; private set => SetProperty(ref _isLoadingWakePower, value); }

    private string _wakePowerStatusText =
        "Not loaded - click Load to read which devices can currently wake the system (powercfg) and whether Windows is allowed to power each one down.";
    public string WakePowerStatusText { get => _wakePowerStatusText; private set => SetProperty(ref _wakePowerStatusText, value); }

    public AsyncRelayCommand LoadWakePowerCommand { get; }

    public DevicesDriversViewModel()
    {
        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;

        DeviceTreeView = CollectionViewSource.GetDefaultView(DeviceTree);
        DeviceTreeView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PnpDeviceNode.ClassName)));
        DeviceTreeView.SortDescriptions.Add(new SortDescription(nameof(PnpDeviceNode.ClassName), ListSortDirection.Ascending));
        DeviceTreeView.SortDescriptions.Add(new SortDescription(nameof(PnpDeviceNode.Name), ListSortDirection.Ascending));
        DeviceTreeView.Filter = FilterDeviceTree;

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

        EnableDeviceCommand = new AsyncRelayCommand(() => SetSelectedDeviceEnabledAsync(true));
        DisableDeviceCommand = new AsyncRelayCommand(() => SetSelectedDeviceEnabledAsync(false));
        RestartDeviceCommand = new AsyncRelayCommand(RestartSelectedDeviceAsync);
        RemoveDeviceCommand = new AsyncRelayCommand(RemoveSelectedDeviceAsync);
        RemoveCheckedDevicesCommand = new AsyncRelayCommand(RemoveCheckedDevicesAsync);

        LoadResourcesCommand = new AsyncRelayCommand(LoadResourcesAsync);
        LoadWakePowerCommand = new AsyncRelayCommand(LoadWakePowerAsync);

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

    // ------------------------------------------------------------------------------------------
    // #474: enable/disable the selected device.
    // ------------------------------------------------------------------------------------------

    private async Task SetSelectedDeviceEnabledAsync(bool enable)
    {
        var node = SelectedDeviceNode;
        if (node is null) return;

        if (!enable && BlockedDisableClassGuids.Contains(node.ClassGuid))
        {
            MessageBox.Show(
                $"\"{node.Name}\" is in the {node.ClassName} class. Disabling a display adapter or a storage host " +
                "controller can leave the system unusable (or unbootable, if it's the boot controller) until it's " +
                "re-enabled from Device Manager or Safe Mode - this app blocks disabling devices in that class.\n\n" +
                "Use Device Manager directly if you're certain this specific device is safe to disable.",
                "Can't disable this device",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            enable
                ? $"Enable \"{node.Name}\"?"
                : $"Disable \"{node.Name}\"?\nWindows will stop using this device until it's re-enabled.",
            enable ? "Enable device" : "Disable device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsPerformingDeviceAction = true;
        DeviceActionStatusText = $"{(enable ? "Enabling" : "Disabling")} \"{node.Name}\"...";
        try
        {
            var (success, message) = enable
                ? await PnpUtilService.EnableDeviceAsync(node.DeviceId)
                : await PnpUtilService.DisableDeviceAsync(node.DeviceId);
            DeviceActionStatusText = success
                ? $"{(enable ? "Enabled" : "Disabled")} \"{node.Name}\"."
                : $"Couldn't {(enable ? "enable" : "disable")} \"{node.Name}\": {message}";
            if (success) await LoadDeviceTreeAsync();
        }
        finally
        {
            IsPerformingDeviceAction = false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // #475: restart (reload the driver for) the selected device - no reboot needed. Not subject
    // to the #474 hard block (Device Manager itself allows restarting these classes; it's the
    // *disabled* state that's the persistent hazard), just a softer heads-up in the confirmation
    // text when the device is in one of those sensitive classes.
    // ------------------------------------------------------------------------------------------

    private async Task RestartSelectedDeviceAsync()
    {
        var node = SelectedDeviceNode;
        if (node is null) return;

        bool isSensitiveClass = BlockedDisableClassGuids.Contains(node.ClassGuid);
        var confirm = MessageBox.Show(
            isSensitiveClass
                ? $"Restart \"{node.Name}\"?\nThis reloads its driver without a reboot. It's in the {node.ClassName} " +
                  "class, so display or storage access may briefly interrupt while it reloads."
                : $"Restart \"{node.Name}\"?\nThis reloads its driver without a reboot.",
            "Restart device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsPerformingDeviceAction = true;
        DeviceActionStatusText = $"Restarting \"{node.Name}\"...";
        try
        {
            var (success, message) = await PnpUtilService.RestartDeviceAsync(node.DeviceId);
            DeviceActionStatusText = success ? $"Restarted \"{node.Name}\"." : $"Couldn't restart \"{node.Name}\": {message}";
            if (success) await LoadDeviceTreeAsync();
        }
        finally
        {
            IsPerformingDeviceAction = false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // #472: remove non-present ("ghost") device entries - a single one (context menu, acting on
    // SelectedDeviceNode) or every checked one (the "Remove checked" button below the tree).
    // ------------------------------------------------------------------------------------------

    private Task RemoveSelectedDeviceAsync() =>
        RemoveDevicesAsync(SelectedDeviceNode is { IsPresent: false } node ? new[] { node } : Array.Empty<PnpDeviceNode>());

    private Task RemoveCheckedDevicesAsync() =>
        RemoveDevicesAsync(DeviceTree.Where(n => !n.IsPresent && n.IsCheckedForRemoval).ToList());

    private async Task RemoveDevicesAsync(IReadOnlyList<PnpDeviceNode> targets)
    {
        if (targets.Count == 0)
        {
            MessageBox.Show(
                "Check one or more non-present devices below (or right-click a single non-present device) to remove.",
                "No devices selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string list = string.Join("\n", targets.Select(t => $"  • {t.Name}  ({t.DeviceId})"));
        var confirm = MessageBox.Show(
            $"Remove {targets.Count} non-present device entr{(targets.Count == 1 ? "y" : "ies")}?\n\n{list}\n\n" +
            "Removing a present-but-disconnected device's leftover registry entry is harmless - it comes back the " +
            "next time it's plugged in and rescanned. Removing the WRONG entry (a device class Windows still " +
            "relies on) is not - double-check the names above before continuing.",
            "Remove device(s)",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsPerformingDeviceAction = true;
        int done = 0, failed = 0;
        try
        {
            foreach (var t in targets)
            {
                DeviceActionStatusText = $"Removing \"{t.Name}\"... ({done + failed + 1}/{targets.Count})";
                var (success, _) = await PnpUtilService.RemoveDeviceAsync(t.DeviceId);
                if (success) done++; else failed++;
            }
            DeviceActionStatusText = failed == 0
                ? $"Removed {done} device entr{(done == 1 ? "y" : "ies")}."
                : $"Removed {done}, {failed} failed - click Load above to see what's left.";
            await LoadDeviceTreeAsync();
        }
        finally
        {
            IsPerformingDeviceAction = false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // #476/#477: resource map (IRQ/I-O/memory/DMA + conflict flags) and MSI-vs-line-based
    // interrupt mode, both gated behind one shared Load button.
    // ------------------------------------------------------------------------------------------

    private async Task LoadResourcesAsync()
    {
        IsLoadingResources = true;
        ResourcesStatusText = "Reading resource assignments...";
        try
        {
            var resources = await ResourceMapService.ScanAsync();

            var presentNodes = _presentDeviceNodes.Count > 0 ? _presentDeviceNodes : await PnpDeviceTreeService.ListPresentAsync();
            if (_presentDeviceNodes.Count == 0) _presentDeviceNodes = presentNodes;

            var problemCodesByDevice = presentNodes.ToDictionary(d => d.DeviceId, d => d.ConfigManagerErrorCode, StringComparer.OrdinalIgnoreCase);
            foreach (var r in resources)
                r.HasInsufficientResourcesProblem = problemCodesByDevice.TryGetValue(r.DeviceId, out int code) && code == 12;

            DeviceResources.Clear();
            foreach (var r in resources) DeviceResources.Add(r);

            InsufficientResourceDevices.Clear();
            foreach (var d in presentNodes.Where(d => d.ConfigManagerErrorCode == 12)) InsufficientResourceDevices.Add(d);

            // #477: candidate device set for the interrupt-mode registry scan - every present
            // device plus any device the resource scan above found (covers a device WMI's PnP
            // sweep might have missed resource-wise but still holds a legacy IRQ), deduped by ID.
            var candidateDevices = presentNodes.Select(d => (DeviceId: d.DeviceId, DeviceName: d.Name))
                .Concat(resources.Select(r => (DeviceId: r.DeviceId, DeviceName: r.DeviceName)))
                .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var interruptModes = await InterruptModeService.ScanAsync(candidateDevices);

            var irqCountsByNumber = resources.Where(r => r.Kind == DeviceResourceKind.Irq && r.IrqNumber.HasValue)
                .GroupBy(r => r.IrqNumber!.Value)
                .ToDictionary(g => g.Key, g => g.Select(r => r.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var irqByDevice = resources.Where(r => r.Kind == DeviceResourceKind.Irq && r.IrqNumber.HasValue)
                .GroupBy(r => r.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().IrqNumber!.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var info in interruptModes)
            {
                if (!irqByDevice.TryGetValue(info.DeviceId, out int irq)) continue;
                info.IrqNumber = irq;
                info.SharesLineWithCount = Math.Max(0, irqCountsByNumber.GetValueOrDefault(irq, 1) - 1);
            }

            InterruptModes.Clear();
            foreach (var m in interruptModes) InterruptModes.Add(m);

            int flagged = DeviceResources.Count(r => r.IsFlagged);
            ResourcesStatusText = $"{DeviceResources.Count} resource assignment(s) across " +
                $"{DeviceResources.Select(r => r.DeviceId).Distinct().Count()} device(s)" +
                (flagged > 0 ? $" - {flagged} flagged (overlapping/non-shareable)." : ".") +
                (InsufficientResourceDevices.Count > 0
                    ? $" {InsufficientResourceDevices.Count} device(s) report insufficient resources (problem code 12)."
                    : string.Empty);
        }
        catch (Exception ex)
        {
            ResourcesStatusText = $"Couldn't read resource assignments: {ex.Message}";
        }
        finally
        {
            IsLoadingResources = false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // #478: wake-armed / wake-capable devices (powercfg) combined with a best-effort per-device
    // "Windows allowed to power this off" read.
    // ------------------------------------------------------------------------------------------

    private async Task LoadWakePowerAsync()
    {
        IsLoadingWakePower = true;
        WakePowerStatusText = "Reading wake/power settings...";
        try
        {
            var query = await PowerWakeQueryService.ScanAsync();

            var presentNodes = _presentDeviceNodes.Count > 0 ? _presentDeviceNodes : await PnpDeviceTreeService.ListPresentAsync();
            if (_presentDeviceNodes.Count == 0) _presentDeviceNodes = presentNodes;

            var allowOffByDeviceId = DevicePowerCapabilityService.ReadAllowTurnOff(presentNodes.Select(d => d.DeviceId));

            // powercfg reports plain friendly names only - match back to a device ID only when the
            // name is unambiguous among present devices; leave AllowComputerToTurnOff Unknown otherwise.
            var nodesByName = presentNodes
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First() : null, StringComparer.OrdinalIgnoreCase);

            var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(query.WakeArmed);
            allNames.UnionWith(query.WakeFromAny);
            allNames.UnionWith(query.WakeProgrammable);

            var rows = new List<WakeDeviceInfo>();
            foreach (var name in allNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                nodesByName.TryGetValue(name, out var matched);
                bool? allowOff = matched is not null && allowOffByDeviceId.TryGetValue(matched.DeviceId, out var v) ? v : null;
                rows.Add(new WakeDeviceInfo
                {
                    DeviceName = name,
                    MatchedDeviceId = matched?.DeviceId,
                    IsWakeArmed = query.WakeArmed.Contains(name),
                    CanWakeFromAny = query.WakeFromAny.Contains(name),
                    CanWakeProgrammable = query.WakeProgrammable.Contains(name),
                    AllowComputerToTurnOff = allowOff,
                });
            }

            WakeDevices.Clear();
            foreach (var r in rows) WakeDevices.Add(r);

            WakePowerStatusText = rows.Count == 0
                ? "No wake-capable devices reported by powercfg."
                : $"{rows.Count} device(s) reported by powercfg /devicequery - {rows.Count(r => r.IsWakeArmed)} currently armed to wake the system.";
        }
        catch (Exception ex)
        {
            WakePowerStatusText = $"Couldn't read wake/power settings: {ex.Message}";
        }
        finally
        {
            IsLoadingWakePower = false;
        }
    }
}
