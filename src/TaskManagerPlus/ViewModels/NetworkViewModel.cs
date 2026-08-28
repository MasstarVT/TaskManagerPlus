using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using TaskManagerPlus.Common;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Network tab. Mostly a thin composition over the shared PerformanceViewModel sampler
/// - see CpuViewModel's remarks - but also owns one deliberate exception: a gateway/DNS
/// reachability check. That needs actual network I/O (an ICMP ping, ~1s worst case), unlike
/// every other Network tab figure which is a local counter read, so it doesn't belong on the 1s
/// shared sampler tick - it gets its own slow (15s) timer instead, the same "genuinely different,
/// more expensive data source" exception EnergyThermalsViewModel documents for LibreHardwareMonitorLib.
/// </summary>
public sealed class NetworkViewModel : ObservableObject, IDisposable
{
    private readonly NetworkDiagnosticsService _diagnostics = new();
    private readonly DispatcherTimer _timer;
    private bool _isChecking;

    // #221: optional cross-link to the Responsiveness tab's DPC-by-driver table - the same
    // "reach a sibling ViewModel via an explicit constructor reference" pattern CpuViewModel
    // already takes for EnergyThermalsViewModel. Nullable/optional rather than a hard requirement:
    // NetworkViewModel is also constructed on its own wherever a future test/tool might want just
    // the Network tab, so the cross-link degrades to a plain one-line note (no cross-tab claim) when
    // it isn't wired.
    private readonly ResponsivenessViewModel? _responsiveness;

    public PerformanceViewModel Performance { get; }

    private bool? _gatewayReachable;
    public bool? GatewayReachable { get => _gatewayReachable; private set => SetProperty(ref _gatewayReachable, value); }

    private bool? _dnsReachable;
    public bool? DnsReachable { get => _dnsReachable; private set => SetProperty(ref _dnsReachable, value); }

    private string _gatewayStatusText = "Checking...";
    public string GatewayStatusText { get => _gatewayStatusText; private set => SetProperty(ref _gatewayStatusText, value); }

    private string _dnsStatusText = "Checking...";
    public string DnsStatusText { get => _dnsStatusText; private set => SetProperty(ref _dnsStatusText, value); }

    // #33: actual hostname resolution time, distinct from the ICMP ping to a resolver IP above.
    private string _dnsLookupText = "Checking...";
    public string DnsLookupText { get => _dnsLookupText; private set => SetProperty(ref _dnsLookupText, value); }

    // #31/#37: adapter link speeds and VPN presence - cheap NetworkInterface enumeration, so
    // it rides the same slow timer as the ping-based connectivity check rather than getting its
    // own.
    public ObservableCollection<AdapterLinkInfo> AdapterLinks { get; } = new();

    private bool _hasActiveVpn;
    public bool HasActiveVpn { get => _hasActiveVpn; private set => SetProperty(ref _hasActiveVpn, value); }

    private string _vpnStatusText = string.Empty;
    public string VpnStatusText { get => _vpnStatusText; private set => SetProperty(ref _vpnStatusText, value); }

    // #21: active TCP connections with owning process - refreshed on the same slow timer, since
    // reading the full connection table + resolving process names every 1s would be wasteful for
    // data that mostly matters when actively investigating something.
    public ObservableCollection<TcpConnectionInfo> Connections { get; } = new();

    // #87: top network processes by connection count - a "per-process bandwidth" proxy, since
    // Windows has no public API for true per-process byte attribution - see NetworkProcessUsage's
    // remarks. Refreshed on the same slow timer as Connections itself, since it's derived from
    // that same sample.
    public ObservableCollection<NetworkProcessUsage> TopNetworkProcesses { get; } = new();

    // #23: current Wi-Fi association (SSID/signal/channel) - null (and the view hides the card)
    // on a wired connection, no Wi-Fi adapter, or a non-English Windows install (netsh's text
    // output is parsed by English field labels - see WifiDiagnosticsService's remarks).
    private WifiInfo? _wifi;
    public WifiInfo? Wifi { get => _wifi; private set => SetProperty(ref _wifi, value); }

    // #24: public IP + ISP - deliberately not refreshed on the timer above, since it's a real
    // outbound call to a third-party service; only runs when the user clicks the button.
    private string _publicIpStatusText = "Not checked";
    public string PublicIpStatusText { get => _publicIpStatusText; private set => SetProperty(ref _publicIpStatusText, value); }

    // Round 9, #51: captive portal detection - distinct from a plain no-internet result. Null
    // means "couldn't tell" (see NetworkDiagnosticsService.CheckCaptivePortalAsync's remarks).
    private bool? _captivePortalDetected;
    public bool? CaptivePortalDetected { get => _captivePortalDetected; private set => SetProperty(ref _captivePortalDetected, value); }

    public string CaptivePortalStatusText => CaptivePortalDetected switch
    {
        true => "Detected — a login page is likely intercepting requests",
        false => "Not detected",
        null => "Unknown",
    };

    // Round 9, #47: read-only WinHTTP/IE proxy configuration - refreshed on the same slow timer.
    private string _proxyStatusText = "Checking...";
    public string ProxyStatusText { get => _proxyStatusText; private set => SetProperty(ref _proxyStatusText, value); }

    // Round 9, #48: network adapter driver version/date - queried once (like StorageSpacesService's
    // pool query), since a driver can't change without a reinstall/reboot this app would need a
    // restart to see anyway.
    public ObservableCollection<AdapterDriverInfo> AdapterDrivers { get; } = new();

    // Round 9, #52: metered-connection flag per network profile - refreshed on the slow timer
    // (cheap registry reads).
    public ObservableCollection<MeteredAdapterInfo> MeteredAdapters { get; } = new();

    // Round 9, #45: historical (daily/monthly) connection-count totals by process - see
    // NetworkHistoryService's remarks for why this is a connection-count history, not real
    // byte-level bandwidth history.
    public ObservableCollection<NetworkHistoryEntry> HistoryToday { get; } = new();
    public ObservableCollection<NetworkHistoryEntry> HistoryThisMonth { get; } = new();

    // Round 9, #49: on-demand traceroute.
    private string _tracerouteHost = string.Empty;
    public string TracerouteHost { get => _tracerouteHost; set => SetProperty(ref _tracerouteHost, value); }

    private string _tracerouteOutput = string.Empty;
    public string TracerouteOutput { get => _tracerouteOutput; private set => SetProperty(ref _tracerouteOutput, value); }

    private bool _isTracerouting;
    public bool IsTracerouting { get => _isTracerouting; private set => SetProperty(ref _isTracerouting, value); }

    public AsyncRelayCommand RunTracerouteCommand { get; }

    // Round 9, #50: on-demand jitter/packet-loss quick test, alongside the existing single-shot
    // gateway/DNS latency reading above.
    private string _jitterTestHost = "1.1.1.1";
    public string JitterTestHost { get => _jitterTestHost; set => SetProperty(ref _jitterTestHost, value); }

    private string _jitterTestResultText = "Not tested";
    public string JitterTestResultText { get => _jitterTestResultText; private set => SetProperty(ref _jitterTestResultText, value); }

    private bool _isJitterTesting;
    public bool IsJitterTesting { get => _isJitterTesting; private set => SetProperty(ref _isJitterTesting, value); }

    public AsyncRelayCommand RunJitterTestCommand { get; }

    public RelayCommand CheckConnectivityCommand { get; }
    public AsyncRelayCommand LookupPublicIpCommand { get; }

    // Round 9, #46: hosts-file quick-open shortcut for DNS override troubleshooting.
    public RelayCommand OpenHostsFileCommand { get; }

    // Round 19, #888: DNS posture - per-adapter configured DNS servers cross-referenced against
    // DHCP status, DoH template state, and NRPT policy rules. Deliberately its OWN on-demand
    // button, NOT folded into the 15s CheckConnectivityAsync timer above - the DoH/NRPT reads shell
    // out to PowerShell cmdlets, too heavy to repeat on a tick (see DnsPostureService's remarks).
    private DnsPostureService.DnsPostureResult? _dnsPosture;
    public DnsPostureService.DnsPostureResult? DnsPosture { get => _dnsPosture; private set => SetProperty(ref _dnsPosture, value); }

    private bool _isCheckingDnsPosture;
    public bool IsCheckingDnsPosture { get => _isCheckingDnsPosture; private set => SetProperty(ref _isCheckingDnsPosture, value); }

    private string? _dnsPostureStatusText;
    public string? DnsPostureStatusText { get => _dnsPostureStatusText; private set => SetProperty(ref _dnsPostureStatusText, value); }

    public AsyncRelayCommand CheckDnsPostureCommand { get; }

    // #221: NIC interrupt-moderation/RSS audit - see NicInterruptModerationService. Loaded once at
    // start-up plus a manual refresh (registry reads are cheap, but this rides the same "device
    // topology essentially never changes tick to tick" reasoning as the one-time #48 driver read
    // right below it, not the 15s connectivity timer).
    public ObservableCollection<NicInterruptModerationInfo> NicInterruptSettings { get; } = new();

    private string _nicAuditStatusText = "Not loaded yet.";
    public string NicAuditStatusText { get => _nicAuditStatusText; private set => SetProperty(ref _nicAuditStatusText, value); }

    public AsyncRelayCommand LoadNicInterruptSettingsCommand { get; }

    public NetworkViewModel(PerformanceViewModel performance, ResponsivenessViewModel? responsiveness = null)
    {
        Performance = performance;
        _responsiveness = responsiveness;

        // #221: keep the cross-link text live as either side's data arrives asynchronously,
        // rather than a one-shot computed property that could bind before either collection is
        // populated and then never update.
        if (_responsiveness is not null)
            _responsiveness.DriverDpcRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NdisDpcCrossLinkText));
        AdapterDrivers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NdisDpcCrossLinkText));

        LoadNicInterruptSettingsCommand = new AsyncRelayCommand(LoadNicInterruptSettingsAsync);

        CheckConnectivityCommand = new RelayCommand(_ => _ = CheckConnectivityAsync());
        LookupPublicIpCommand = new AsyncRelayCommand(LookupPublicIpAsync);
        OpenHostsFileCommand = new RelayCommand(_ => OpenHostsFile());
        RunTracerouteCommand = new AsyncRelayCommand(RunTracerouteAsync, () => !IsTracerouting && !string.IsNullOrWhiteSpace(TracerouteHost));
        RunJitterTestCommand = new AsyncRelayCommand(RunJitterTestAsync, () => !IsJitterTesting && !string.IsNullOrWhiteSpace(JitterTestHost));
        CheckDnsPostureCommand = new AsyncRelayCommand(CheckDnsPostureAsync);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) => await CheckConnectivityAsync();
        _timer.Start();

        _ = CheckConnectivityAsync();

        // #48: one-time driver read - see AdapterDrivers' remarks.
        _ = Task.Run(() =>
        {
            var drivers = NetworkDiagnosticsService.ReadAdapterDriverInfo();
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var d in drivers) AdapterDrivers.Add(d);
            });
        });

        // #221: one-time NIC interrupt-moderation/RSS audit - see NicInterruptSettings' remarks.
        _ = LoadNicInterruptSettingsAsync();
    }

    /// <summary>#221: reads each adapter's interrupt-moderation/RSS registry settings and flags an
    /// adapter with moderation disabled or RSS off on a multi-core machine - see
    /// NicInterruptModerationService's remarks for why some fields can legitimately come back
    /// Unknown rather than a guessed on/off state.</summary>
    private async Task LoadNicInterruptSettingsAsync()
    {
        NicAuditStatusText = "Reading NIC interrupt-moderation/RSS settings...";
        try
        {
            var results = await NicInterruptModerationService.LoadAsync();
            NicInterruptSettings.Clear();
            foreach (var r in results) NicInterruptSettings.Add(r);
            NicAuditStatusText = results.Count == 0
                ? "No physical adapters exposed these registry settings on this system."
                : $"{results.Count} adapter(s) read.";
        }
        catch (Exception ex)
        {
            NicAuditStatusText = $"Read failed: {ex.Message}";
        }
    }

    /// <summary>#221: plain-text cross-link to the Responsiveness tab's DPC-by-driver table - a
    /// simple string comparison against DriverDpcRows rather than new bidirectional plumbing, per
    /// the assignment's own "keep it a simpler one-way text note" fallback. Matches either the
    /// generic "ndis.sys" network-stack driver, or (using the #216 driver-to-device attribution
    /// already on each row) a DPC row whose attributed device matches one of this tab's known NIC
    /// adapters. Only meaningful when this NetworkViewModel was constructed with a
    /// ResponsivenessViewModel reference (see MainViewModel's construction order); returns null when
    /// it wasn't, so the view just hides the note instead of claiming "not a DPC offender" with no
    /// data behind that claim.</summary>
    public string? NdisDpcCrossLinkText
    {
        get
        {
            if (_responsiveness is null) return null;
            var ndisRow = _responsiveness.DriverDpcRows.FirstOrDefault(r =>
                r.DriverName.Equals("ndis.sys", StringComparison.OrdinalIgnoreCase) ||
                (r.DeviceName.Length > 0 && AdapterDrivers.Any(d =>
                    r.DeviceName.Contains(d.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                    d.DeviceName.Contains(r.DeviceName, StringComparison.OrdinalIgnoreCase))));
            return ndisRow is null
                ? null
                : $"Heads up: \"{ndisRow.DriverName}\" is also showing up as a top DPC-time offender on the Responsiveness tab ({ndisRow.TotalTimeUs:0} µs total) - this adapter's own driver may be contributing to system-wide stutter, not just network throughput.";
        }
    }

    private async Task CheckConnectivityAsync()
    {
        if (_isChecking) return;
        _isChecking = true;
        try
        {
            var result = await _diagnostics.CheckAsync();

            GatewayReachable = result.GatewayReachable;
            GatewayStatusText = result.GatewayReachable switch
            {
                true => $"{result.GatewayRoundtripMs} ms",
                false => "Unreachable",
                null => "No gateway found",
            };

            DnsReachable = result.DnsReachable;
            DnsStatusText = result.DnsReachable switch
            {
                true => $"{result.DnsRoundtripMs} ms",
                false => "Unreachable",
                null => "Check unavailable",
            };

            DnsLookupText = result.DnsLookupMs is { } lookupMs ? $"{lookupMs} ms" : "Failed";

            // #51: captive portal - a distinct signal from plain gateway/DNS unreachability.
            CaptivePortalDetected = result.CaptivePortalDetected;
            OnPropertyChanged(nameof(CaptivePortalStatusText));

            // #47: read-only proxy configuration.
            var proxy = NetworkDiagnosticsService.ReadProxyConfig();
            ProxyStatusText = proxy.Enabled
                ? $"Enabled — {(string.IsNullOrEmpty(proxy.ProxyServer) ? "(no server configured)" : proxy.ProxyServer)}"
                : string.IsNullOrEmpty(proxy.AutoConfigUrl) ? "Disabled" : $"Disabled (auto-config script set: {proxy.AutoConfigUrl})";

            // #52: metered-connection flag per network profile.
            var metered = await Task.Run(MeteredConnectionService.ReadMeteredStatus);
            MeteredAdapters.Clear();
            foreach (var m in metered) MeteredAdapters.Add(m);

            var links = NetworkDiagnosticsService.ReadAdapterLinks();
            AdapterLinks.Clear();
            foreach (var link in links) AdapterLinks.Add(link);

            var vpnAdapters = NetworkDiagnosticsService.ReadActiveVpnAdapterNames();
            HasActiveVpn = vpnAdapters.Count > 0;
            VpnStatusText = HasActiveVpn ? string.Join(", ", vpnAdapters) : "None detected";

            var connections = await Task.Run(() => NetworkConnectionsService.Sample());
            Connections.Clear();
            foreach (var c in connections.OrderByDescending(c => c.State == "ESTABLISHED").ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
                Connections.Add(c);

            var topProcesses = NetworkConnectionsService.SummarizeByProcess(connections);
            TopNetworkProcesses.Clear();
            foreach (var u in topProcesses.Take(8))
                TopNetworkProcesses.Add(u);

            // #45: persist this sample into the daily/monthly connection-count history - see
            // NetworkHistoryService's remarks for why this is a connection-count history, not
            // real byte-level bandwidth history.
            await Task.Run(() => NetworkHistoryService.RecordSample(topProcesses));
            var todayTotals = await Task.Run(() => NetworkHistoryService.GetDayTotals());
            var monthTotals = await Task.Run(NetworkHistoryService.GetMonthTotals);
            HistoryToday.Clear();
            foreach (var e in todayTotals.Take(8)) HistoryToday.Add(e);
            HistoryThisMonth.Clear();
            foreach (var e in monthTotals.Take(8)) HistoryThisMonth.Add(e);

            Wifi = await WifiDiagnosticsService.ReadCurrentWifiAsync();
        }
        catch
        {
            // Best-effort - a failed check shouldn't crash the timer loop.
        }
        finally
        {
            _isChecking = false;
        }
    }

    /// <summary>#46: opens the hosts file in Notepad for DNS override troubleshooting - explicitly
    /// launches notepad.exe rather than ShellExecute-ing the bare path, since the hosts file has
    /// no extension and so no reliably-registered default handler to fall back to.</summary>
    private static void OpenHostsFile()
    {
        try
        {
            string hostsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{hostsPath}\"") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - if Notepad can't launch there's nothing more useful this app can do here.
        }
    }

    /// <summary>#49: on-demand traceroute - see TracerouteService's remarks for why this shells
    /// out to tracert.exe rather than reimplementing it.</summary>
    private async Task RunTracerouteAsync()
    {
        if (IsTracerouting) return;
        IsTracerouting = true;
        TracerouteOutput = "Running traceroute (up to 20 hops, this can take several seconds)...";
        try
        {
            TracerouteOutput = await TracerouteService.RunAsync(TracerouteHost);
        }
        catch (Exception ex)
        {
            TracerouteOutput = $"Traceroute failed: {ex.Message}";
        }
        finally
        {
            IsTracerouting = false;
        }
    }

    /// <summary>#50: on-demand jitter/packet-loss quick test - see
    /// NetworkDiagnosticsService.RunJitterTestAsync's remarks.</summary>
    private async Task RunJitterTestAsync()
    {
        if (IsJitterTesting) return;
        IsJitterTesting = true;
        JitterTestResultText = "Testing (10 pings, ~2 seconds)...";
        try
        {
            var result = await NetworkDiagnosticsService.RunJitterTestAsync(JitterTestHost);
            JitterTestResultText = result.Message;
        }
        catch (Exception ex)
        {
            JitterTestResultText = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsJitterTesting = false;
        }
    }

    /// <summary>#24: only ever runs on an explicit button click - see PublicIpLookupService's
    /// remarks for why this doesn't ride the timer above like everything else on this tab.</summary>
    private async Task LookupPublicIpAsync()
    {
        PublicIpStatusText = "Looking up...";
        try
        {
            var info = await PublicIpLookupService.LookupAsync();
            PublicIpStatusText = info is null
                ? "Lookup failed (no internet, or the lookup service is unreachable)"
                : string.Join("  •  ", new[] { info.Ip, info.Isp, string.Join(", ", new[] { info.City, info.Region, info.Country }.Where(s => !string.IsNullOrWhiteSpace(s))) }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch (Exception ex)
        {
            PublicIpStatusText = $"Lookup failed: {ex.Message}";
        }
    }

    /// <summary>#888: on-demand DNS posture check - see this VM's own DnsPosture remarks for why
    /// this doesn't ride the 15s connectivity timer.</summary>
    private async Task CheckDnsPostureAsync()
    {
        IsCheckingDnsPosture = true;
        DnsPostureStatusText = "Checking...";
        try
        {
            var result = await Task.Run(DnsPostureService.ReadPosture);
            DnsPosture = result;
            int staticOnDhcp = result.Adapters.Count(a => a.StaticDnsOnDhcpAdapter);
            DnsPostureStatusText = $"{result.Adapters.Count} adapter(s) checked" +
                (staticOnDhcp > 0 ? $" - {staticOnDhcp} has static DNS set on an otherwise-DHCP adapter (see Security tab for the mirrored finding)." : ".") +
                (result.DohCmdletAvailable ? $" DoH servers configured: {result.DohServers.Count}." : " DoH state: cmdlet not available on this Windows build.") +
                (result.NrptCmdletAvailable ? $" NRPT rules: {result.NrptRules.Count}." : " NRPT state: cmdlet not available.");
        }
        catch (Exception ex)
        {
            DnsPostureStatusText = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingDnsPosture = false;
        }
    }

    public void Dispose() => _timer.Stop();
}
