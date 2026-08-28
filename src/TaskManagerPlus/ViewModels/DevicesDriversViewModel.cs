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

    public DevicesDriversViewModel()
    {
        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        VerifyCommand = new RelayCommand(param => _ = VerifyRowAsync(param as DriverInventoryRow));
        VerifyFileCommand = new RelayCommand(param => _ = VerifyFileAsync(param as DriverFileInfo));
        VerifyAllCommand = new AsyncRelayCommand(VerifyAllAsync);

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
}
