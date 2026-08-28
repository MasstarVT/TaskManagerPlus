using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>#519's flattened "one row per configured resolver IP" view of DnsResolverService's
/// per-adapter list - a mutable class (not a record), same "annotate after the fact" shape
/// RouteEntry/HostsFileEntry already use, since <see cref="IsFirstResponder"/> is only known once a
/// #517 comparison run has actually completed.</summary>
public sealed class DnsAdapterResolverRow
{
    public string AdapterName { get; init; } = string.Empty;
    public string ResolverIp { get; init; } = string.Empty;
    public bool IsFirstResponder { get; set; }
}

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

    // ---- suggestions.md #501-509: always-on latency/jitter/packet-loss monitor -----------------
    // Distinct from the connectivity check above (a single gateway/DNS ping every 15s): this is
    // a continuous probe ring with its own start/stop toggle and its own rolling window, so a
    // user can leave it running and catch the drop that only happens every twenty minutes. See
    // LatencyMonitorService's class remarks for the full design.
    private readonly LatencyMonitorService _latencyMonitor = new();
    private const int LatencyHistoryLength = 60;
    private const double LatencyHistoryFlushMinutes = 5.0;
    private const float LatencyCoreStrokeWidth = 2f;
    private const float LatencyGlowStrokeWidth = 7f;
    private DateTime _lastLatencyFlushUtc = DateTime.MinValue;
    private readonly LatencyBaselineFile _latencyBaseline = LatencyBaselineService.Load();

    private bool _isLatencyMonitoring;
    public bool IsLatencyMonitoring { get => _isLatencyMonitoring; private set => SetProperty(ref _isLatencyMonitoring, value); }

    public RelayCommand ToggleLatencyMonitorCommand { get; }

    private double _latencyIntervalSeconds = 2.0;

    /// <summary>#501: configurable probe interval - restarts the ring immediately if it's
    /// currently running, rather than waiting for the next manual Start.</summary>
    public double LatencyIntervalSeconds
    {
        get => _latencyIntervalSeconds;
        set
        {
            double clamped = Math.Clamp(value, 1.0, 30.0);
            if (!SetProperty(ref _latencyIntervalSeconds, clamped)) return;
            if (IsLatencyMonitoring)
            {
                _latencyMonitor.Stop();
                _latencyMonitor.Start(clamped);
            }
        }
    }

    // #501: rolling chart history per charted tier (Nic is matrix-only - a near-zero control row,
    // not worth its own chart line). Shares the glow/core LineSeries pairing PerformanceViewModel.
    // LineOf already establishes for every other history chart in the app - see this class's
    // private LineOf below.
    public ObservableCollection<double> GatewayLatencyHistory { get; } = NewLatencyHistory();
    public ObservableCollection<double> FirstHopLatencyHistory { get; } = NewLatencyHistory();
    public ObservableCollection<double> ResolverLatencyHistory { get; } = NewLatencyHistory();

    private readonly LineSeries<double> _gatewayGlow, _gatewayCore;
    private readonly LineSeries<double> _firstHopGlow, _firstHopCore;
    private readonly LineSeries<double> _resolverGlow, _resolverCore;
    public ISeries[] LatencySeries { get; }
    public Axis[] LatencyHiddenXAxes { get; }
    public Axis[] LatencyMsYAxes { get; }

    // #505: latency-under-load overlay - the shared PerformanceViewModel's existing throughput
    // series plus a second Gateway-latency line scaled to its own (right-hand) axis, on one
    // shared time axis, so a ping spike that lines up with a throughput spike reads as
    // self-inflicted congestion instead of an ISP fault. The two source collections tick on
    // different clocks (Performance's 1s sampler vs. this ring's configurable interval), so the
    // alignment is approximate, not frame-exact - close enough for the "did these two things
    // happen around the same time" question this overlay exists to answer.
    private readonly LineSeries<double> _overlayLatencyGlow, _overlayLatencyCore;
    public ISeries[] LatencyOverlaySeries { get; }
    public Axis[] OverlayYAxes { get; }

    // #503: LAN/ISP/internet matrix - four fixed tiers, read live from the same rolling window
    // the charts above are built from (see LatencyMonitorService's remarks), not a second probe.
    public ObservableCollection<LatencyTierStats> LatencyMatrix { get; } = new();

    // #502: packet-loss timeline per charted tier, plus one combined "quick flag, not a verdict" caption.
    public ObservableCollection<LatencyLossTick> GatewayLossStrip { get; } = new();
    public ObservableCollection<LatencyLossTick> FirstHopLossStrip { get; } = new();
    public ObservableCollection<LatencyLossTick> ResolverLossStrip { get; } = new();

    private string _lossBreakdownText = "Not monitoring - start the latency monitor to see loss data.";
    public string LossBreakdownText { get => _lossBreakdownText; private set => SetProperty(ref _lossBreakdownText, value); }

    // #504: baseline deviation badges - null hides the badge (no deviation, or no baseline yet).
    private string? _gatewayDeviationText;
    public string? GatewayDeviationText { get => _gatewayDeviationText; private set => SetProperty(ref _gatewayDeviationText, value); }

    private string? _firstHopDeviationText;
    public string? FirstHopDeviationText { get => _firstHopDeviationText; private set => SetProperty(ref _firstHopDeviationText, value); }

    private string? _resolverDeviationText;
    public string? ResolverDeviationText { get => _resolverDeviationText; private set => SetProperty(ref _resolverDeviationText, value); }

    private double _deviationMultiplier;

    /// <summary>#504: "a configurable multiple" - how many times over baseline counts as worth
    /// flagging. Persisted straight into latency-baseline.json alongside the baselines it judges.</summary>
    public double DeviationMultiplier
    {
        get => _deviationMultiplier;
        set
        {
            double clamped = Math.Clamp(value, 1.5, 20.0);
            if (!SetProperty(ref _deviationMultiplier, clamped)) return;
            _latencyBaseline.DeviationMultiplier = clamped;
            LatencyBaselineService.Save(_latencyBaseline);
        }
    }

    // #508: route-flap (TTL-change) summary line.
    private string _routeFlapText = "No data yet.";
    public string RouteFlapText { get => _routeFlapText; private set => SetProperty(ref _routeFlapText, value); }

    // #506: persisted 24h/7d history view for the Gateway tier - the closest, most diagnostic link.
    public ObservableCollection<LatencyHistoryPoint> LatencyHistoryPoints { get; } = new();

    private bool _latencyHistoryShowWeek;
    public bool LatencyHistoryShowWeek
    {
        get => _latencyHistoryShowWeek;
        private set { if (SetProperty(ref _latencyHistoryShowWeek, value)) LoadLatencyHistoryView(); }
    }

    public RelayCommand ShowLatencyHistoryDayCommand { get; }
    public RelayCommand ShowLatencyHistoryWeekCommand { get; }

    // ---- suggestions.md #510-516: path MTU, routing and hop-level diagnostics ------------------

    // #510/#512: on-demand path MTU discovery, plus the derived PMTUD black-hole verdict from the
    // same sweep - see PathMtuService's remarks for why this is a manual button, never a tick.
    private string _pathMtuTargetHost = "1.1.1.1";
    public string PathMtuTargetHost { get => _pathMtuTargetHost; set => SetProperty(ref _pathMtuTargetHost, value); }

    private string _pathMtuResultText = "Not run yet.";
    public string PathMtuResultText { get => _pathMtuResultText; private set => SetProperty(ref _pathMtuResultText, value); }

    private string? _pathMtuBlackHoleText;
    public string? PathMtuBlackHoleText { get => _pathMtuBlackHoleText; private set => SetProperty(ref _pathMtuBlackHoleText, value); }

    private bool _isRunningPathMtu;
    public bool IsRunningPathMtu { get => _isRunningPathMtu; private set => SetProperty(ref _isRunningPathMtu, value); }

    public AsyncRelayCommand RunPathMtuCommand { get; }

    private PathMtuResult? _lastPathMtuResult;

    // #511: per-adapter configured MTU inventory, cross-referenced against the discovered path
    // MTU above once one exists.
    public ObservableCollection<InterfaceMtuInfo> InterfaceMtus { get; } = new();
    public AsyncRelayCommand RefreshInterfaceMtusCommand { get; }

    // #513/#514: routing table viewer with conflict flags, plus the separate persistent-route
    // section - see RoutingTableService's remarks.
    public ObservableCollection<RouteEntry> Routes { get; } = new();
    public ObservableCollection<PersistentRouteEntry> PersistentRoutes { get; } = new();
    private bool _isRefreshingRoutes;
    public bool IsRefreshingRoutes { get => _isRefreshingRoutes; private set => SetProperty(ref _isRefreshingRoutes, value); }
    public AsyncRelayCommand RefreshRoutingCommand { get; }

    // #515: MTR-style continuous hop monitor - its own explicit start/stop toggle, distinct from
    // the #501 latency ring and the shared connectivity timer - see MtrService's remarks.
    private readonly MtrService _mtr = new();

    private string _mtrHost = "1.1.1.1";
    public string MtrHost { get => _mtrHost; set => SetProperty(ref _mtrHost, value); }

    public ObservableCollection<MtrHopStats> MtrHops { get; } = new();

    private bool _isMtrRunning;
    public bool IsMtrRunning { get => _isMtrRunning; private set => SetProperty(ref _isMtrRunning, value); }

    public RelayCommand ToggleMtrCommand { get; }

    // #516: traceroute baseline save/diff - built on top of the existing #49 traceroute card's
    // output, parsed into hops via TracerouteService.ParseHops.
    private List<TracerouteHop> _lastTracerouteHops = new();

    private string _tracerouteBaselineName = string.Empty;
    public string TracerouteBaselineName { get => _tracerouteBaselineName; set => SetProperty(ref _tracerouteBaselineName, value); }

    public ObservableCollection<string> TracerouteBaselineNames { get; } = new();

    private string? _selectedTracerouteBaseline;
    public string? SelectedTracerouteBaseline { get => _selectedTracerouteBaseline; set => SetProperty(ref _selectedTracerouteBaseline, value); }

    public ObservableCollection<TracerouteDiffEntry> TracerouteDiff { get; } = new();

    private string? _tracerouteDiffSummaryText;
    public string? TracerouteDiffSummaryText { get => _tracerouteDiffSummaryText; private set => SetProperty(ref _tracerouteDiffSummaryText, value); }

    public RelayCommand SaveTracerouteBaselineCommand { get; }
    public RelayCommand CompareTracerouteBaselineCommand { get; }

    // #525: on-demand reverse-DNS enrichment for the existing #21 connections grid above.
    private bool _isResolvingConnectionNames;
    public bool IsResolvingConnectionNames { get => _isResolvingConnectionNames; private set => SetProperty(ref _isResolvingConnectionNames, value); }

    public AsyncRelayCommand ResolveConnectionNamesCommand { get; }

    // ---- suggestions.md #517-526: DNS resolution, cache and configuration ----------------------
    // New "DNS" card. #517/#519 share one underlying test run (CompareAsync already queries every
    // adapter-configured resolver, so #519's "who answered first" is read straight off that run's
    // timings rather than a second probe); #520/#521/#523 are three independent read-only
    // configuration snapshots; #518/#522/#524 are three independent on-demand scans; #526 shares
    // the Latency card's own start/stop toggle rather than getting a new one - see
    // ToggleLatencyMonitor's remarks below.

    // #517/#519: multi-resolver comparison + configured-resolvers-per-adapter/"who answered".
    private string _dnsCompareHostname = string.Empty;
    public string DnsCompareHostname { get => _dnsCompareHostname; set => SetProperty(ref _dnsCompareHostname, value); }

    private bool _isComparingDns;
    public bool IsComparingDns { get => _isComparingDns; private set => SetProperty(ref _isComparingDns, value); }

    private string _dnsCompareStatusText = "Enter a host name above and click Compare.";
    public string DnsCompareStatusText { get => _dnsCompareStatusText; private set => SetProperty(ref _dnsCompareStatusText, value); }

    public ObservableCollection<DnsResolverAnswer> DnsCompareAnswers { get; } = new();
    public ObservableCollection<DnsAdapterResolverRow> ConfiguredResolvers { get; } = new();

    public AsyncRelayCommand RunDnsCompareCommand { get; }

    // #518: DNS cache viewer with flush.
    private string _dnsCacheSearchText = string.Empty;
    public string DnsCacheSearchText
    {
        get => _dnsCacheSearchText;
        set { if (SetProperty(ref _dnsCacheSearchText, value)) ApplyDnsCacheFilter(); }
    }

    public ObservableCollection<DnsCacheEntry> DnsCacheEntries { get; } = new();
    private List<DnsCacheEntry> _allDnsCacheEntries = new();

    private bool _isLoadingDnsCache;
    public bool IsLoadingDnsCache { get => _isLoadingDnsCache; private set => SetProperty(ref _isLoadingDnsCache, value); }

    private string _dnsCacheStatusText = "Not loaded yet.";
    public string DnsCacheStatusText { get => _dnsCacheStatusText; private set => SetProperty(ref _dnsCacheStatusText, value); }

    public AsyncRelayCommand RefreshDnsCacheCommand { get; }
    public AsyncRelayCommand FlushDnsCacheCommand { get; }

    // #520: DNS-over-HTTPS configuration read - read-only.
    public ObservableCollection<DohServerStatus> DohStatuses { get; } = new();

    private string _dohRawOutput = string.Empty;
    public string DohRawOutput { get => _dohRawOutput; private set => SetProperty(ref _dohRawOutput, value); }

    private string _dohSummaryText = "Not loaded yet.";
    public string DohSummaryText { get => _dohSummaryText; private set => SetProperty(ref _dohSummaryText, value); }

    public AsyncRelayCommand RefreshDohConfigCommand { get; }

    // #521: NRPT rules - expander hidden entirely (view-side DataTrigger) when this is empty.
    public ObservableCollection<NrptRule> NrptRules { get; } = new();

    // #522: hosts-file parser with shadowing flags.
    public ObservableCollection<HostsFileEntry> HostsEntries { get; } = new();

    private bool _isLoadingHostsFile;
    public bool IsLoadingHostsFile { get => _isLoadingHostsFile; private set => SetProperty(ref _isLoadingHostsFile, value); }

    public AsyncRelayCommand RefreshHostsFileCommand { get; }

    // #523: suffix/search list - collapsed by default (view-side Expander).
    private string _primaryDnsSuffixText = "(none)";
    public string PrimaryDnsSuffixText { get => _primaryDnsSuffixText; private set => SetProperty(ref _primaryDnsSuffixText, value); }

    public ObservableCollection<string> DnsSearchList { get; } = new();
    public ObservableCollection<AdapterSuffixInfo> AdapterDnsSuffixes { get; } = new();

    // #524: DNS-Client event log failure/timeout scan - behind an explicit Scan button per the
    // on-demand-for-event-logs convention.
    private double _dnsScanWindowHours = 24.0;
    public double DnsScanWindowHours { get => _dnsScanWindowHours; set => SetProperty(ref _dnsScanWindowHours, Math.Clamp(value, 1.0, 720.0)); }

    private bool _isScanningDnsFailures;
    public bool IsScanningDnsFailures { get => _isScanningDnsFailures; private set => SetProperty(ref _isScanningDnsFailures, value); }

    private string _dnsScanStatusText = "Not scanned yet - the DNS-Client Operational log is disabled by default on most machines.";
    public string DnsScanStatusText { get => _dnsScanStatusText; private set => SetProperty(ref _dnsScanStatusText, value); }

    public ObservableCollection<DnsFailureGroup> DnsFailuresByName { get; } = new();
    public ObservableCollection<DnsFailureGroup> DnsFailuresByResolver { get; } = new();

    public AsyncRelayCommand ScanDnsFailuresCommand { get; }

    // #526: per-resolver DNS response-time chart, sharing the #501 Latency card's start/stop
    // toggle - see ToggleLatencyMonitor's remarks.
    private readonly DnsResponseTimeMonitorService _dnsResponseMonitor = new();
    private const int DnsResponseHistoryLength = 60;
    private readonly List<(string ResolverIp, ObservableCollection<double> History)> _dnsResponseLines = new();

    private static readonly SKColor[] DnsResponsePalette =
    {
        SKColors.Gold, SKColors.Orchid, SKColors.Turquoise, SKColors.LimeGreen, SKColors.DeepPink, SKColors.DodgerBlue,
    };

    public ISeries[] DnsResponseSeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] DnsResponseHiddenXAxes { get; }
    public Axis[] DnsResponseMsYAxes { get; }

    // ---- suggestions.md #527-535: DHCP, addressing, ARP and gateway ------------------------------
    // Two new cards - "Addressing" (#527/#528/#529/#530/#531/#534) and "ARP / neighbours"
    // (#532/#533) - plus a new section on the existing #513 Routing card (#535). #527/#528/#534 all
    // read one Win32_NetworkAdapterConfiguration sweep (DhcpAddressingService.ReadAll); #529 shells
    // out to ipconfig.exe scoped to whichever adapter is selected below; #530/#531's event-log scans
    // and #531's gratuitous-ARP probe are on-demand, per CLAUDE.md's event-log-scan convention.

    // #527/#528/#529/#534: per-adapter lease detail, APIPA flag, and the addressing sanity checklist.
    public ObservableCollection<AdapterAddressInfo> Addressing { get; } = new();

    private bool _isRefreshingAddressing;
    public bool IsRefreshingAddressing { get => _isRefreshingAddressing; private set => SetProperty(ref _isRefreshingAddressing, value); }

    public AsyncRelayCommand RefreshAddressingCommand { get; }

    // #529: which adapter Release/Renew/Register DNS act on.
    private AdapterAddressInfo? _selectedAddressingAdapter;
    public AdapterAddressInfo? SelectedAddressingAdapter { get => _selectedAddressingAdapter; set => SetProperty(ref _selectedAddressingAdapter, value); }

    private bool _isRunningDhcpAction;
    public bool IsRunningDhcpAction { get => _isRunningDhcpAction; private set => SetProperty(ref _isRunningDhcpAction, value); }

    private string _dhcpActionStatusText = "Select an adapter above, then Release, Renew, or Register DNS.";
    public string DhcpActionStatusText { get => _dhcpActionStatusText; private set => SetProperty(ref _dhcpActionStatusText, value); }

    public RelayCommand ReleaseAddressCommand { get; }
    public RelayCommand RenewAddressCommand { get; }
    public RelayCommand RegisterDnsCommand { get; }

    // #528: the APIPA banner's own Renew button - scoped to whichever adapter it's shown on
    // (via CommandParameter), independent of whatever's picked in the Actions section's adapter
    // selector above.
    public RelayCommand RenewApipaAdapterCommand { get; }

    // #530: DHCP client event timeline - its own lookback window, mirroring #524's DNS scan.
    private double _dhcpScanWindowHours = 24.0;
    public double DhcpScanWindowHours { get => _dhcpScanWindowHours; set => SetProperty(ref _dhcpScanWindowHours, Math.Clamp(value, 1.0, 720.0)); }

    private bool _isScanningDhcpEvents;
    public bool IsScanningDhcpEvents { get => _isScanningDhcpEvents; private set => SetProperty(ref _isScanningDhcpEvents, value); }

    private string _dhcpScanStatusText = "Not scanned yet - the DHCP-Client Operational log is disabled by default on most machines.";
    public string DhcpScanStatusText { get => _dhcpScanStatusText; private set => SetProperty(ref _dhcpScanStatusText, value); }

    public ObservableCollection<DhcpClientEvent> DhcpEvents { get; } = new();
    public AsyncRelayCommand ScanDhcpEventsCommand { get; }

    // #531: System-log Tcpip 4198/4199 correlation, plus the active gratuitous-ARP probe.
    private double _ipConflictScanWindowHours = 24.0;
    public double IpConflictScanWindowHours { get => _ipConflictScanWindowHours; set => SetProperty(ref _ipConflictScanWindowHours, Math.Clamp(value, 1.0, 720.0)); }

    private bool _isScanningIpConflicts;
    public bool IsScanningIpConflicts { get => _isScanningIpConflicts; private set => SetProperty(ref _isScanningIpConflicts, value); }

    private string _ipConflictScanStatusText = "Not scanned yet.";
    public string IpConflictScanStatusText { get => _ipConflictScanStatusText; private set => SetProperty(ref _ipConflictScanStatusText, value); }

    public ObservableCollection<IpConflictLogEntry> IpConflictEvents { get; } = new();
    public AsyncRelayCommand ScanIpConflictsCommand { get; }

    private bool _isProbingGratuitousArp;
    public bool IsProbingGratuitousArp { get => _isProbingGratuitousArp; private set => SetProperty(ref _isProbingGratuitousArp, value); }

    private string _gratuitousArpResultText = "Not probed yet.";
    public string GratuitousArpResultText { get => _gratuitousArpResultText; private set => SetProperty(ref _gratuitousArpResultText, value); }

    public AsyncRelayCommand ProbeGratuitousArpCommand { get; }

    // #532/#533: new "ARP / neighbours" card.
    public ObservableCollection<ArpEntry> ArpEntries { get; } = new();

    private bool _isRefreshingArp;
    public bool IsRefreshingArp { get => _isRefreshingArp; private set => SetProperty(ref _isRefreshingArp, value); }

    private string _arpStatusText = "Not loaded yet.";
    public string ArpStatusText { get => _arpStatusText; private set => SetProperty(ref _arpStatusText, value); }

    public AsyncRelayCommand RefreshArpCommand { get; }

    // #533: gateway-MAC-change alert - null hides the banner (no baseline yet, or nothing changed).
    private string? _gatewayMacChangeText;
    public string? GatewayMacChangeText { get => _gatewayMacChangeText; private set => SetProperty(ref _gatewayMacChangeText, value); }

    private readonly GatewayFingerprintFile _gatewayFingerprint = GatewayFingerprintService.Load();

    // #535: interface-metric section on the existing #513 Routing card.
    public ObservableCollection<InterfaceMetricInfo> InterfaceMetrics { get; } = new();

    private string _adapterWinnerText = "Refresh routing to see which adapter Windows currently prefers for outbound traffic.";
    public string AdapterWinnerText { get => _adapterWinnerText; private set => SetProperty(ref _adapterWinnerText, value); }

    public NetworkViewModel(PerformanceViewModel performance)
    {
        Performance = performance;

        CheckConnectivityCommand = new RelayCommand(_ => _ = CheckConnectivityAsync());
        LookupPublicIpCommand = new AsyncRelayCommand(LookupPublicIpAsync);
        OpenHostsFileCommand = new RelayCommand(_ => OpenHostsFile());
        RunTracerouteCommand = new AsyncRelayCommand(RunTracerouteAsync, () => !IsTracerouting && !string.IsNullOrWhiteSpace(TracerouteHost));
        RunJitterTestCommand = new AsyncRelayCommand(RunJitterTestAsync, () => !IsJitterTesting && !string.IsNullOrWhiteSpace(JitterTestHost));

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

        // #501-509: latency monitor wiring.
        _deviationMultiplier = _latencyBaseline.DeviationMultiplier;

        ToggleLatencyMonitorCommand = new RelayCommand(_ => ToggleLatencyMonitor());
        ShowLatencyHistoryDayCommand = new RelayCommand(_ => LatencyHistoryShowWeek = false);
        ShowLatencyHistoryWeekCommand = new RelayCommand(_ => LatencyHistoryShowWeek = true);

        LatencyHiddenXAxes = new[]
        {
            new Axis { IsVisible = false, MinLimit = 0, MaxLimit = LatencyHistoryLength - 1, ShowSeparatorLines = false },
        };
        LatencyMsYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} ms",
                LabelsPaint = LatencyAxisTextPaint(),
                SeparatorsPaint = LatencyAxisSeparatorPaint(),
            },
        };
        OverlayYAxes = new[]
        {
            new Axis // [0] throughput bytes/sec - left, shared with the plain Throughput chart's scale
            {
                MinLimit = 0,
                Labeler = v => Formatting.FormatByteRate(v),
                LabelsPaint = LatencyAxisTextPaint(),
                SeparatorsPaint = LatencyAxisSeparatorPaint(),
                Position = AxisPosition.Start,
            },
            new Axis // [1] Gateway latency ms - right, its own scale so it doesn't get flattened by byte-rate magnitudes
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} ms",
                LabelsPaint = new SolidColorPaint(SKColors.Gold),
                Position = AxisPosition.End,
            },
        };

        (_gatewayGlow, _gatewayCore) = LatencyLineOf(GatewayLatencyHistory, SKColors.Gold, "Gateway");
        (_firstHopGlow, _firstHopCore) = LatencyLineOf(FirstHopLatencyHistory, SKColors.Orchid, "First hop");
        (_resolverGlow, _resolverCore) = LatencyLineOf(ResolverLatencyHistory, SKColors.Turquoise, "Resolver");
        LatencySeries = new ISeries[] { _gatewayGlow, _gatewayCore, _firstHopGlow, _firstHopCore, _resolverGlow, _resolverCore };

        // Overlay's latency line shares GatewayLatencyHistory's data with the card's own chart
        // above (the same "two series, one Values collection" sharing the glow/core pair itself
        // already does) - no fill, so it doesn't obscure the throughput area fill beneath it.
        (_overlayLatencyGlow, _overlayLatencyCore) = LatencyLineOf(GatewayLatencyHistory, SKColors.Gold, "Gateway latency", scalesYAt: 1);
        LatencyOverlaySeries = Performance.NetworkSeries.Concat(new ISeries[] { _overlayLatencyGlow, _overlayLatencyCore }).ToArray();

        _latencyMonitor.CycleCompleted += OnLatencyCycleCompleted;
        LoadLatencyHistoryView();

        // #510-516: path MTU / routing / MTR / traceroute baseline wiring.
        RunPathMtuCommand = new AsyncRelayCommand(RunPathMtuAsync, () => !IsRunningPathMtu && !string.IsNullOrWhiteSpace(PathMtuTargetHost));
        RefreshInterfaceMtusCommand = new AsyncRelayCommand(RefreshInterfaceMtusAsync);
        RefreshRoutingCommand = new AsyncRelayCommand(RefreshRoutingAsync, () => !IsRefreshingRoutes);
        ToggleMtrCommand = new RelayCommand(_ => ToggleMtr());
        SaveTracerouteBaselineCommand = new RelayCommand(SaveTracerouteBaseline,
            () => _lastTracerouteHops.Count > 0 && !string.IsNullOrWhiteSpace(TracerouteBaselineName));
        CompareTracerouteBaselineCommand = new RelayCommand(CompareTracerouteBaseline,
            () => _lastTracerouteHops.Count > 0 && !string.IsNullOrWhiteSpace(SelectedTracerouteBaseline));

        _mtr.CycleCompleted += OnMtrCycleCompleted;

        LoadTracerouteBaselineNames();
        _ = RefreshInterfaceMtusAsync();
        _ = RefreshRoutingAsync();

        // #525: reverse-DNS enrichment for the existing #21 connections grid.
        ResolveConnectionNamesCommand = new AsyncRelayCommand(ResolveConnectionNamesAsync, () => !IsResolvingConnectionNames && Connections.Count > 0);

        // #517-526: DNS card wiring.
        RunDnsCompareCommand = new AsyncRelayCommand(RunDnsCompareAsync, () => !IsComparingDns && !string.IsNullOrWhiteSpace(DnsCompareHostname));
        RefreshDnsCacheCommand = new AsyncRelayCommand(RefreshDnsCacheAsync, () => !IsLoadingDnsCache);
        FlushDnsCacheCommand = new AsyncRelayCommand(FlushDnsCacheAsync, () => !IsLoadingDnsCache);
        RefreshDohConfigCommand = new AsyncRelayCommand(RefreshDohConfigAsync);
        RefreshHostsFileCommand = new AsyncRelayCommand(RefreshHostsFileAsync, () => !IsLoadingHostsFile);
        ScanDnsFailuresCommand = new AsyncRelayCommand(ScanDnsFailuresAsync, () => !IsScanningDnsFailures);

        DnsResponseHiddenXAxes = new[]
        {
            new Axis { IsVisible = false, MinLimit = 0, MaxLimit = DnsResponseHistoryLength - 1, ShowSeparatorLines = false },
        };
        DnsResponseMsYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} ms",
                LabelsPaint = LatencyAxisTextPaint(),
                SeparatorsPaint = LatencyAxisSeparatorPaint(),
            },
        };
        _dnsResponseMonitor.CycleCompleted += OnDnsResponseCycleCompleted;

        // #519: configured resolvers per adapter - cheap NetworkInterface enumeration, loaded
        // immediately (no test resolution yet, so IsFirstResponder starts false everywhere).
        foreach (var adapter in DnsResolverService.ReadConfiguredResolvers())
            foreach (var ip in adapter.ResolverIps)
                ConfiguredResolvers.Add(new DnsAdapterResolverRow { AdapterName = adapter.AdapterName, ResolverIp = ip });

        // #521/#523: one-time-per-launch registry/API reads, same "queried once" tradeoff
        // AdapterDrivers above already takes - neither can change without an external action
        // (group policy refresh, network reconfiguration) this app would need a restart to see
        // anyway.
        _ = Task.Run(() =>
        {
            var nrpt = DnsConfigService.ReadNrptRules();
            var suffixInfo = DnsConfigService.ReadSuffixInfo();
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var r in nrpt) NrptRules.Add(r);

                PrimaryDnsSuffixText = string.IsNullOrWhiteSpace(suffixInfo.PrimarySuffix) ? "(none)" : suffixInfo.PrimarySuffix;
                foreach (var s in suffixInfo.SearchList) DnsSearchList.Add(s);
                foreach (var a in suffixInfo.AdapterSuffixes) AdapterDnsSuffixes.Add(a);
            });
        });

        _ = RefreshDnsCacheAsync();
        _ = RefreshDohConfigAsync();
        _ = RefreshHostsFileAsync();

        // #527-535: Addressing / ARP-neighbours card wiring.
        RefreshAddressingCommand = new AsyncRelayCommand(RefreshAddressingAsync, () => !IsRefreshingAddressing);
        ReleaseAddressCommand = new RelayCommand(() => ReleaseSelectedAdapter(), () => SelectedAddressingAdapter is not null && !IsRunningDhcpAction);
        RenewAddressCommand = new RelayCommand(() => RenewAdapter(SelectedAddressingAdapter), () => SelectedAddressingAdapter is not null && !IsRunningDhcpAction);
        RegisterDnsCommand = new RelayCommand(() => RegisterDnsForSelectedAdapter(), () => !IsRunningDhcpAction);
        RenewApipaAdapterCommand = new RelayCommand(param => RenewAdapter(param as AdapterAddressInfo), _ => !IsRunningDhcpAction);
        ScanDhcpEventsCommand = new AsyncRelayCommand(ScanDhcpEventsAsync, () => !IsScanningDhcpEvents);
        ScanIpConflictsCommand = new AsyncRelayCommand(ScanIpConflictsAsync, () => !IsScanningIpConflicts);
        ProbeGratuitousArpCommand = new AsyncRelayCommand(ProbeGratuitousArpAsync, () => !IsProbingGratuitousArp);
        RefreshArpCommand = new AsyncRelayCommand(RefreshArpAsync, () => !IsRefreshingArp);

        _ = RefreshAddressingAsync();
        _ = RefreshArpAsync();
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
            // #525: reapply whatever's already cached from a previous "Resolve names" click - no
            // I/O, so this is safe on every 15s refresh even though the fresh names themselves
            // only come from an explicit user action.
            ReverseDnsService.ApplyCached(connections);
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

            // #516: parse this run's hops so Save/Compare-to-baseline have something to work
            // with - a fresh run always replaces whatever the buttons were pointed at before.
            _lastTracerouteHops = TracerouteService.ParseHops(TracerouteOutput);
            TracerouteDiff.Clear();
            TracerouteDiffSummaryText = null;
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

    // ---- #501-509 helpers -------------------------------------------------------------------

    private static ObservableCollection<double> NewLatencyHistory()
    {
        var col = new ObservableCollection<double>();
        for (int i = 0; i < LatencyHistoryLength; i++) col.Add(0);
        return col;
    }

    // Same colors PerformanceViewModel's own axis theming defaults to (TextSecondary/Border) -
    // duplicated rather than shared since these are a different ViewModel's chart axes, and
    // (unlike PerformanceViewModel's) aren't currently wired into MainViewModel's theme-switch
    // repaint - a known limitation, not an oversight: these axes keep their initial dark-theme
    // colors across a theme-family switch until the app restarts.
    private static SolidColorPaint LatencyAxisTextPaint() => new(new SKColor(0x9A, 0x9A, 0xA2));
    private static SolidColorPaint LatencyAxisSeparatorPaint() => new(new SKColor(0x33, 0x33, 0x3A, 160)) { StrokeThickness = 1 };

    /// <summary>Same glow+core LineSeries pairing PerformanceViewModel.LineOf builds for every
    /// other history chart in the app (see CLAUDE.md's "Chart styling" notes) - duplicated here
    /// rather than shared since PerformanceViewModel's version is private to that class.</summary>
    private static (LineSeries<double> Glow, LineSeries<double> Core) LatencyLineOf(
        ObservableCollection<double> values, SKColor color, string? name = null, int scalesYAt = 0)
    {
        var glow = new LineSeries<double>
        {
            Values = values,
            Stroke = new SolidColorPaint(color.WithAlpha(70), LatencyGlowStrokeWidth),
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
            IsHoverable = false,
            IsVisibleAtLegend = false,
            ScalesYAt = scalesYAt,
        };
        var core = new LineSeries<double>
        {
            Values = values,
            Name = name,
            Stroke = new SolidColorPaint(color, LatencyCoreStrokeWidth),
            Fill = scalesYAt == 0 ? new LinearGradientPaint(color.WithAlpha(90), color.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)) : null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
            ScalesYAt = scalesYAt,
        };
        return (glow, core);
    }

    private void ToggleLatencyMonitor()
    {
        if (IsLatencyMonitoring)
        {
            _latencyMonitor.Stop();
            _dnsResponseMonitor.Stop(); // #526: shares this toggle rather than getting its own - see StartDnsResponseMonitor's remarks.
            IsLatencyMonitoring = false;
        }
        else
        {
            _latencyMonitor.Start(LatencyIntervalSeconds);
            StartDnsResponseMonitor();
            IsLatencyMonitoring = true;
        }
    }

    /// <summary>#526: (re)builds the per-resolver glow/core chart series for whichever resolvers
    /// are configured right now (the OS-configured ones plus the same three fixed public resolvers
    /// #517/#519 use, deduplicated, capped so the chart's legend stays readable) and starts the
    /// probe loop against them. Re-picking the resolver set on every Start (rather than caching it
    /// once) means a VPN connect/disconnect between monitoring sessions is picked up for free.</summary>
    private void StartDnsResponseMonitor()
    {
        var resolvers = DnsResolverService.ReadConfiguredResolvers()
            .SelectMany(a => a.ResolverIps)
            .Concat(DnsResolverService.FixedPublicResolvers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(DnsResponsePalette.Length)
            .ToList();

        _dnsResponseLines.Clear();
        var series = new List<ISeries>();
        for (int i = 0; i < resolvers.Count; i++)
        {
            var history = NewLatencyHistory(); // zero-filled, same length/shape helper the Latency card's own charts already use
            var (glow, core) = LatencyLineOf(history, DnsResponsePalette[i % DnsResponsePalette.Length], resolvers[i]);
            _dnsResponseLines.Add((resolvers[i], history));
            series.Add(glow);
            series.Add(core);
        }
        DnsResponseSeries = series.ToArray();
        OnPropertyChanged(nameof(DnsResponseSeries));

        _dnsResponseMonitor.Start(resolvers, LatencyIntervalSeconds);
    }

    /// <summary>Fired on DnsResponseTimeMonitorService's own background probe-loop thread - marshal
    /// to the UI thread before touching any bound collection, same pattern OnLatencyCycleCompleted/
    /// OnMtrCycleCompleted already use.</summary>
    private void OnDnsResponseCycleCompleted() => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
    {
        foreach (var (resolverIp, history) in _dnsResponseLines)
        {
            double value = _dnsResponseMonitor.TryGetLatest(resolverIp, out var latest) && latest.Success ? latest.Ms : 0;
            history.Add(value);
            if (history.Count > DnsResponseHistoryLength) history.RemoveAt(0);
        }
    });

    /// <summary>Fired on LatencyMonitorService's own background probe-loop thread - marshal to
    /// the UI thread before touching any bound property/collection.</summary>
    private void OnLatencyCycleCompleted() => System.Windows.Application.Current?.Dispatcher.Invoke(RefreshLatencyDisplay);

    private void RefreshLatencyDisplay()
    {
        // #501: push each charted tier's latest reading - 0 on a failed probe, so a drop shows as
        // a visible dip in the chart rather than silently freezing the line at its last good value.
        PushLatency(GatewayLatencyHistory, LatencyTier.Gateway);
        PushLatency(FirstHopLatencyHistory, LatencyTier.FirstHop);
        PushLatency(ResolverLatencyHistory, LatencyTier.Resolver);

        // #503: matrix - all four tiers, from the same rolling window the charts above read.
        LatencyMatrix.Clear();
        foreach (var tier in AllLatencyTiers) LatencyMatrix.Add(_latencyMonitor.GetStats(tier));

        // #502: loss strips + one combined breakdown caption.
        RefreshLossStrip(GatewayLossStrip, LatencyTier.Gateway);
        RefreshLossStrip(FirstHopLossStrip, LatencyTier.FirstHop);
        RefreshLossStrip(ResolverLossStrip, LatencyTier.Resolver);

        var gw = _latencyMonitor.GetLossBreakdown(LatencyTier.Gateway);
        var fh = _latencyMonitor.GetLossBreakdown(LatencyTier.FirstHop);
        var rs = _latencyMonitor.GetLossBreakdown(LatencyTier.Resolver);
        int isolated = gw.IsolatedLosses + fh.IsolatedLosses + rs.IsolatedLosses;
        int burstCount = gw.BurstCount + fh.BurstCount + rs.BurstCount;
        LossBreakdownText = isolated == 0 && burstCount == 0
            ? "No packet loss observed in the current window."
            : $"{isolated} isolated drop(s), {burstCount} burst(s) of consecutive loss across Gateway/First hop/Resolver in the current window — " +
              "bursts point at a link/radio problem, scattered singles more often mean congestion or a rate-limited ICMP responder. Quick flag, not a verdict.";

        // #508: route-flap - TTL changes in the last hour per tier.
        int gwFlaps = _latencyMonitor.GetTtlChangeCountLastHour(LatencyTier.Gateway);
        int fhFlaps = _latencyMonitor.GetTtlChangeCountLastHour(LatencyTier.FirstHop);
        int rsFlaps = _latencyMonitor.GetTtlChangeCountLastHour(LatencyTier.Resolver);
        RouteFlapText = $"Path changed {gwFlaps} time(s) to the gateway, {fhFlaps} to the first hop, {rsFlaps} to the resolver in the last hour.";

        // #504: baseline deviation badges.
        GatewayDeviationText = LatencyBaselineService.GetDeviationMessage(_latencyBaseline, nameof(LatencyTier.Gateway), "Gateway", _latencyMonitor.GetStats(LatencyTier.Gateway).AvgMs);
        FirstHopDeviationText = LatencyBaselineService.GetDeviationMessage(_latencyBaseline, nameof(LatencyTier.FirstHop), "First hop", _latencyMonitor.GetStats(LatencyTier.FirstHop).AvgMs);
        ResolverDeviationText = LatencyBaselineService.GetDeviationMessage(_latencyBaseline, nameof(LatencyTier.Resolver), "Resolver", _latencyMonitor.GetStats(LatencyTier.Resolver).AvgMs);

        // #504/#506: periodically blend the window into the persisted baseline and flush a
        // history point, rather than a read-modify-write JSON file every probe cycle.
        if (DateTime.UtcNow - _lastLatencyFlushUtc >= TimeSpan.FromMinutes(LatencyHistoryFlushMinutes))
        {
            _lastLatencyFlushUtc = DateTime.UtcNow;
            FlushLatencyBaselineAndHistory();
        }
    }

    private static readonly LatencyTier[] AllLatencyTiers = { LatencyTier.Nic, LatencyTier.Gateway, LatencyTier.FirstHop, LatencyTier.Resolver };
    private static readonly LatencyTier[] ChartedLatencyTiers = { LatencyTier.Gateway, LatencyTier.FirstHop, LatencyTier.Resolver };

    private void PushLatency(ObservableCollection<double> history, LatencyTier tier)
    {
        double value = _latencyMonitor.TryGetLatest(tier, out var latest) && latest.Success ? latest.RoundtripMs : 0;
        history.Add(value);
        if (history.Count > LatencyHistoryLength) history.RemoveAt(0);
    }

    private void RefreshLossStrip(ObservableCollection<LatencyLossTick> strip, LatencyTier tier)
    {
        var ticks = _latencyMonitor.GetLossStrip(tier, 60);
        strip.Clear();
        foreach (var t in ticks) strip.Add(t);
    }

    /// <summary>#504/#506: snapshots the current rolling-window stats for each charted tier on
    /// this (UI) thread - GetStats/GetSuccessfulRoundtrips just lock briefly and copy, cheap -
    /// then does the actual JSON file I/O off it via Task.Run, same "keep file I/O off the UI
    /// thread" convention this app uses everywhere else.</summary>
    private void FlushLatencyBaselineAndHistory()
    {
        var snapshots = ChartedLatencyTiers
            .Select(tier => (Tier: tier, Stats: _latencyMonitor.GetStats(tier), Roundtrips: _latencyMonitor.GetSuccessfulRoundtrips(tier)))
            .ToArray();

        _ = Task.Run(() =>
        {
            foreach (var (tier, stats, roundtrips) in snapshots)
            {
                if (stats.SampleCount == 0) continue;
                string key = tier.ToString();
                LatencyHistoryService.RecordWindow(key, stats.MinMs, stats.AvgMs, stats.MaxMs, stats.LossPercent);
                LatencyBaselineService.UpdateBaseline(_latencyBaseline, key, roundtrips);
            }
            LatencyBaselineService.Save(_latencyBaseline);

            System.Windows.Application.Current?.Dispatcher.Invoke(LoadLatencyHistoryView);
        });
    }

    /// <summary>#506: loads the 24h (hour-bucketed) or 7d (day-bucketed) aggregated view for the
    /// Gateway tier - the closest, most diagnostic link - depending on <see cref="LatencyHistoryShowWeek"/>.</summary>
    private void LoadLatencyHistoryView()
    {
        var points = LatencyHistoryShowWeek
            ? LatencyHistoryService.GetLast7Days(nameof(LatencyTier.Gateway))
            : LatencyHistoryService.GetLast24Hours(nameof(LatencyTier.Gateway));
        LatencyHistoryPoints.Clear();
        foreach (var p in points) LatencyHistoryPoints.Add(p);
    }

    // ---- #510-516 helpers ---------------------------------------------------------------------

    /// <summary>#510/#512: on-demand path MTU discovery to <see cref="PathMtuTargetHost"/> - a
    /// dozen-plus round trips, so this only ever runs from the button.</summary>
    private async Task RunPathMtuAsync()
    {
        if (IsRunningPathMtu) return;
        IsRunningPathMtu = true;
        PathMtuResultText = "Discovering path MTU (a dozen-plus round trips, this can take several seconds)...";
        PathMtuBlackHoleText = null;
        try
        {
            var result = await PathMtuService.DiscoverAsync(PathMtuTargetHost);
            _lastPathMtuResult = result;
            PathMtuResultText = result.Message;
            PathMtuBlackHoleText = result.BlackHoleMessage;
        }
        catch (Exception ex)
        {
            _lastPathMtuResult = null;
            PathMtuResultText = $"Path MTU discovery failed: {ex.Message}";
        }
        finally
        {
            IsRunningPathMtu = false;
            RecomputeInterfaceMtuMismatches();
        }
    }

    /// <summary>#511: per-adapter configured MTU inventory - shells out via netsh, so this only
    /// runs on load and from an explicit refresh, never on a tick.</summary>
    private async Task RefreshInterfaceMtusAsync()
    {
        try
        {
            var infos = await InterfaceMtuService.ReadAllAsync();
            InterfaceMtus.Clear();
            foreach (var info in infos) InterfaceMtus.Add(info);
            RecomputeInterfaceMtuMismatches();
        }
        catch
        {
            // Best-effort - leave whatever was already loaded.
        }
    }

    /// <summary>#511: flags an interface configured with a larger MTU than the most recent #510
    /// path MTU discovery found - a no-op (all flags cleared) until a path MTU has actually been
    /// discovered.</summary>
    private void RecomputeInterfaceMtuMismatches()
    {
        int? pathMtu = _lastPathMtuResult?.DiscoveredMtu;
        foreach (var iface in InterfaceMtus)
        {
            if (pathMtu is { } mtu && iface.Mtu > mtu)
            {
                iface.IsMismatched = true;
                iface.MismatchReason = $"Configured MTU {iface.Mtu} is larger than the {mtu}-byte path MTU discovered to {_lastPathMtuResult!.Host} - packets this size toward that host may be dropped or fragmented.";
            }
            else
            {
                iface.IsMismatched = false;
                iface.MismatchReason = null;
            }
        }
        // ObservableCollection<T> doesn't raise a change notification for a mutated element's own
        // properties - force the bound ItemsControl to re-evaluate its DataTriggers by touching
        // the collection itself, the same "the objects aren't INotifyPropertyChanged" tradeoff
        // AdapterLinks/InterfaceMtus (a clear+rebuild display list) already accepts elsewhere.
        var snapshot = InterfaceMtus.ToList();
        InterfaceMtus.Clear();
        foreach (var i in snapshot) InterfaceMtus.Add(i);
    }

    /// <summary>#513/#514: routing table + persistent routes - both shell out/read the registry,
    /// so this only runs on load and from an explicit refresh, never on a tick.</summary>
    private async Task RefreshRoutingAsync()
    {
        if (IsRefreshingRoutes) return;
        IsRefreshingRoutes = true;
        try
        {
            var routes = await RoutingTableService.GetActiveRoutesAsync();
            Routes.Clear();
            foreach (var r in routes) Routes.Add(r);

            var persistent = await Task.Run(RoutingTableService.ReadPersistentRoutes);
            PersistentRoutes.Clear();
            foreach (var p in persistent) PersistentRoutes.Add(p);

            // #535: per-adapter routing metric + "which adapter wins" - shares this card's refresh
            // since it's another netsh/route shell-out, not a trivial local read.
            var metrics = await InterfaceMetricService.ReadAllAsync();
            InterfaceMetrics.Clear();
            foreach (var m in metrics) InterfaceMetrics.Add(m);
            AdapterWinnerText = InterfaceMetricService.DescribeWinner(metrics);
        }
        catch
        {
            // Best-effort - leave whatever was already loaded.
        }
        finally
        {
            IsRefreshingRoutes = false;
        }
    }

    /// <summary>#515: start/stop toggle for the MTR-style continuous hop monitor - a no-op start
    /// with an empty host, same "silently do nothing" guard ToggleLatencyMonitor's callers get
    /// from their own text-box binding.</summary>
    private void ToggleMtr()
    {
        if (IsMtrRunning)
        {
            _mtr.Stop();
            IsMtrRunning = false;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(MtrHost)) return;
            MtrHops.Clear();
            _mtr.Start(MtrHost.Trim(), 1.0);
            IsMtrRunning = true;
        }
    }

    /// <summary>Fired on MtrService's own background probe-loop thread - marshal to the UI thread
    /// before touching any bound collection, same pattern OnLatencyCycleCompleted already uses.</summary>
    private void OnMtrCycleCompleted() => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
    {
        var snapshot = _mtr.GetSnapshot();
        MtrHops.Clear();
        foreach (var hop in snapshot) MtrHops.Add(hop);
    });

    /// <summary>#516: saves the most recently completed traceroute run as a named baseline.</summary>
    private void SaveTracerouteBaseline()
    {
        if (_lastTracerouteHops.Count == 0 || string.IsNullOrWhiteSpace(TracerouteBaselineName)) return;

        TracerouteBaselineService.SaveBaseline(TracerouteBaselineName.Trim(), TracerouteHost, _lastTracerouteHops);
        LoadTracerouteBaselineNames();
        SelectedTracerouteBaseline = TracerouteBaselineName.Trim();
    }

    /// <summary>#516: diffs the most recently completed traceroute run against the selected saved
    /// baseline, highlighting inserted/removed/reordered hops.</summary>
    private void CompareTracerouteBaseline()
    {
        if (_lastTracerouteHops.Count == 0 || string.IsNullOrWhiteSpace(SelectedTracerouteBaseline)) return;

        var file = TracerouteBaselineService.Load();
        var baseline = file.Baselines.FirstOrDefault(b => b.Name.Equals(SelectedTracerouteBaseline, StringComparison.OrdinalIgnoreCase));
        if (baseline is null)
        {
            TracerouteDiff.Clear();
            TracerouteDiffSummaryText = $"Baseline '{SelectedTracerouteBaseline}' no longer exists.";
            return;
        }

        var diff = TracerouteBaselineService.Diff(baseline, _lastTracerouteHops);
        TracerouteDiff.Clear();
        foreach (var entry in diff) TracerouteDiff.Add(entry);

        int inserted = diff.Count(d => d.Kind == TracerouteDiffKind.Inserted);
        int removed = diff.Count(d => d.Kind == TracerouteDiffKind.Removed);
        int reordered = diff.Count(d => d.Kind == TracerouteDiffKind.Reordered);
        TracerouteDiffSummaryText = inserted == 0 && removed == 0 && reordered == 0
            ? $"Path matches baseline '{baseline.Name}' (saved {baseline.SavedUtc.ToLocalTime():g}) - same hops, same order."
            : $"{inserted} inserted, {removed} removed, {reordered} reordered hop(s) vs baseline '{baseline.Name}' (saved {baseline.SavedUtc.ToLocalTime():g}) - " +
              "your ISP may have rerouted you, or a hop's address simply changed. Quick flag, not a verdict.";
    }

    private void LoadTracerouteBaselineNames()
    {
        var file = TracerouteBaselineService.Load();
        TracerouteBaselineNames.Clear();
        foreach (var b in file.Baselines.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
            TracerouteBaselineNames.Add(b.Name);
    }

    // ---- #525 helper -----------------------------------------------------------------------

    /// <summary>#525: PTR-resolves every not-yet-cached remote address currently in the
    /// connections grid, then force-refreshes the grid so the newly filled-in
    /// <see cref="TcpConnectionInfo.RemoteHostName"/> values actually show - same clear+rebuild
    /// trick RecomputeInterfaceMtuMismatches already uses, since TcpConnectionInfo isn't
    /// INotifyPropertyChanged.</summary>
    private async Task ResolveConnectionNamesAsync()
    {
        if (IsResolvingConnectionNames || Connections.Count == 0) return;
        IsResolvingConnectionNames = true;
        try
        {
            await ReverseDnsService.ResolveNamesAsync(Connections.ToList());

            var snapshot = Connections.ToList();
            Connections.Clear();
            foreach (var c in snapshot) Connections.Add(c);
        }
        finally
        {
            IsResolvingConnectionNames = false;
        }
    }

    // ---- #517-526 helpers -------------------------------------------------------------------

    /// <summary>#517/#519: resolves DnsCompareHostname against every configured + fixed public
    /// resolver, populates the comparison table, and marks #519's "who answered first" among the
    /// configured resolvers from the same run.</summary>
    private async Task RunDnsCompareAsync()
    {
        if (IsComparingDns || string.IsNullOrWhiteSpace(DnsCompareHostname)) return;
        IsComparingDns = true;
        DnsCompareStatusText = "Comparing (querying every configured + public resolver)...";
        try
        {
            var result = await DnsResolverService.CompareAsync(DnsCompareHostname);

            if (result.ValidationError is not null)
            {
                DnsCompareAnswers.Clear();
                DnsCompareStatusText = result.ValidationError;
                return;
            }

            DnsCompareAnswers.Clear();
            foreach (var r in result.Resolvers.OrderByDescending(r => r.IsConfiguredResolver).ThenBy(r => r.ElapsedMs))
                DnsCompareAnswers.Add(r);

            int successCount = result.Resolvers.Count(r => r.Success);
            DnsCompareStatusText = result.Resolvers.Count == 0
                ? "No resolvers to query - no adapter has a configured resolver, and the fixed public resolvers weren't reachable."
                : $"{successCount}/{result.Resolvers.Count} resolver(s) answered for '{result.Hostname}'. " +
                  (result.AnswersDiverge
                      ? "Answers diverge between resolvers - could be a hijacking/filtering resolver, a stale cache, or just normal GeoDNS/CDN load-balancing. Quick flag, not a verdict."
                      : "All answering resolvers agree.");

            // #519: mark which configured resolver answered first (fastest successful reply) -
            // clear+rebuild so the DataGrid's IsFirstResponder DataTrigger re-evaluates, same
            // "not INotifyPropertyChanged" tradeoff RecomputeInterfaceMtuMismatches already takes.
            var rows = ConfiguredResolvers.ToList();
            foreach (var row in rows)
                row.IsFirstResponder = result.FirstRespondingConfiguredResolver is not null &&
                    row.ResolverIp.Equals(result.FirstRespondingConfiguredResolver, StringComparison.OrdinalIgnoreCase);
            ConfiguredResolvers.Clear();
            foreach (var row in rows) ConfiguredResolvers.Add(row);
        }
        catch (Exception ex)
        {
            DnsCompareStatusText = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            IsComparingDns = false;

            // #522: the hosts-file card flags any entry that shadows the hostname the user just
            // tried to resolve here - refresh it now that there's a new "just resolved" target.
            _ = RefreshHostsFileAsync();
        }
    }

    /// <summary>#518: loads/reloads the full DNS cache, then reapplies whatever search filter is
    /// currently active.</summary>
    private async Task RefreshDnsCacheAsync()
    {
        if (IsLoadingDnsCache) return;
        IsLoadingDnsCache = true;
        try
        {
            _allDnsCacheEntries = await DnsCacheService.ReadCacheAsync();
            ApplyDnsCacheFilter();
            DnsCacheStatusText = _allDnsCacheEntries.Count == 0
                ? "Cache is empty (or couldn't be read)."
                : $"{_allDnsCacheEntries.Count} cached record(s).";
        }
        catch (Exception ex)
        {
            DnsCacheStatusText = $"Couldn't read the DNS cache: {ex.Message}";
        }
        finally
        {
            IsLoadingDnsCache = false;
        }
    }

    /// <summary>#518: case-insensitive substring filter over record name/type/data - runs entirely
    /// in memory against the already-loaded snapshot, no re-read of the cache itself.</summary>
    private void ApplyDnsCacheFilter()
    {
        DnsCacheEntries.Clear();
        var matches = string.IsNullOrWhiteSpace(DnsCacheSearchText)
            ? _allDnsCacheEntries
            : _allDnsCacheEntries.Where(e =>
                e.RecordName.Contains(DnsCacheSearchText, StringComparison.OrdinalIgnoreCase) ||
                e.RecordType.Contains(DnsCacheSearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains(DnsCacheSearchText, StringComparison.OrdinalIgnoreCase));
        foreach (var e in matches) DnsCacheEntries.Add(e);
    }

    /// <summary>#518: flushes the resolver cache, then reloads it (should come back empty) so the
    /// grid reflects the flush immediately rather than waiting for the next manual refresh.</summary>
    private async Task FlushDnsCacheAsync()
    {
        if (IsLoadingDnsCache) return;
        IsLoadingDnsCache = true;
        try
        {
            bool ok = await DnsCacheService.FlushAsync();
            DnsCacheStatusText = ok ? "Cache flushed." : "Flush may not have succeeded - check the cache below.";
        }
        catch (Exception ex)
        {
            DnsCacheStatusText = $"Flush failed: {ex.Message}";
        }
        finally
        {
            IsLoadingDnsCache = false;
        }
        await RefreshDnsCacheAsync();
    }

    /// <summary>#520: reloads the DoH encryption status table + raw netsh output.</summary>
    private async Task RefreshDohConfigAsync()
    {
        try
        {
            var result = await DnsConfigService.ReadDohConfigAsync();
            DohStatuses.Clear();
            foreach (var s in result.Parsed) DohStatuses.Add(s);
            DohRawOutput = result.RawOutput;

            DohSummaryText = !result.CommandSupported
                ? "`netsh dns show encryption` isn't available on this Windows version - encrypted-DNS status can't be read."
                : result.Parsed.Count == 0
                    ? (result.RegistryKeyPresent
                        ? "No servers reported by netsh, though at least one interface has a DoH registry key present."
                        : "No DNS-over-HTTPS configuration found - resolvers are used in plaintext.")
                    : $"{result.Parsed.Count(s => s.EncryptionStatus.Contains("Yes", StringComparison.OrdinalIgnoreCase))}/{result.Parsed.Count} server(s) reporting encrypted DNS.";
        }
        catch (Exception ex)
        {
            DohSummaryText = $"Couldn't read DoH configuration: {ex.Message}";
        }
    }

    /// <summary>#522: reloads the parsed hosts file, flagging any entry that shadows the hostname
    /// most recently looked up via the #517 compare box above.</summary>
    private async Task RefreshHostsFileAsync()
    {
        if (IsLoadingHostsFile) return;
        IsLoadingHostsFile = true;
        try
        {
            string? recentLookup = DnsCompareHostname;
            var entries = await Task.Run(() => HostsFileService.Parse(recentLookup));
            HostsEntries.Clear();
            foreach (var e in entries) HostsEntries.Add(e);
        }
        finally
        {
            IsLoadingHostsFile = false;
        }
    }

    /// <summary>#524: scans the DNS-Client Operational + System/Dnscache logs over the chosen
    /// lookback window - shelled off the UI thread via Task.Run since EventLogReader is
    /// synchronous, same pattern this app's other event-log reads already take.</summary>
    private async Task ScanDnsFailuresAsync()
    {
        if (IsScanningDnsFailures) return;
        IsScanningDnsFailures = true;
        DnsScanStatusText = "Scanning...";
        try
        {
            var window = TimeSpan.FromHours(DnsScanWindowHours);
            var result = await Task.Run(() => DnsEventLogService.Scan(window));

            DnsFailuresByName.Clear();
            foreach (var g in result.ByName) DnsFailuresByName.Add(g);
            DnsFailuresByResolver.Clear();
            foreach (var g in result.ByResolver) DnsFailuresByResolver.Add(g);

            DnsScanStatusText = !result.OperationalChannelAvailable
                ? $"DNS-Client Operational log unavailable (it's disabled by default - enable it in Event Viewer's \"Show Analytic and Debug Logs\" to get full results). Found {result.Events.Count} event(s) from the System log only."
                : result.Events.Count == 0
                    ? $"No DNS timeout/failure events in the last {DnsScanWindowHours:0.#}h."
                    : $"{result.Events.Count} event(s) in the last {DnsScanWindowHours:0.#}h across {result.ByName.Count} name(s) and {result.ByResolver.Count} resolver bucket(s).";
        }
        catch (Exception ex)
        {
            DnsScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningDnsFailures = false;
        }
    }

    // ---- #527-535 helpers -------------------------------------------------------------------

    /// <summary>#527/#528/#534: reloads the Addressing card's per-adapter DHCP lease/APIPA/sanity
    /// data from one Win32_NetworkAdapterConfiguration sweep - shelled off the UI thread via
    /// Task.Run since WMI queries are synchronous.</summary>
    private async Task RefreshAddressingAsync()
    {
        if (IsRefreshingAddressing) return;
        IsRefreshingAddressing = true;
        try
        {
            var infos = await Task.Run(DhcpAddressingService.ReadAll);
            string? previouslySelected = SelectedAddressingAdapter?.AdapterName;

            Addressing.Clear();
            foreach (var i in infos) Addressing.Add(i);

            SelectedAddressingAdapter = (previouslySelected is null ? null : Addressing.FirstOrDefault(a => a.AdapterName == previouslySelected))
                ?? Addressing.FirstOrDefault();
        }
        catch
        {
            // Best-effort - leave whatever was already loaded.
        }
        finally
        {
            IsRefreshingAddressing = false;
        }
    }

    /// <summary>#529: release - behind an explicit Yes/No confirmation since it briefly drops
    /// connectivity on the selected adapter, the same MessageBox.Show confirm pattern
    /// ProcessesViewModel.EndSelected already uses for its own disruptive action.</summary>
    private void ReleaseSelectedAdapter()
    {
        var target = SelectedAddressingAdapter;
        if (target is null) return;

        var confirm = MessageBox.Show(
            $"Release the IP address on \"{target.AdapterName}\"?\nThis briefly drops connectivity on this adapter until it's renewed or reconnected.",
            "Release IP address", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunDhcpActionAsync(() => DhcpAddressingService.ReleaseAsync(target.AdapterName), "Release");
    }

    /// <summary>#529: renew - same confirm-first pattern as Release above.</summary>
    /// <summary>#529 (also used directly by #528's own per-adapter banner button via
    /// <see cref="RenewApipaAdapterCommand"/>) - same confirm-first pattern as Release.</summary>
    private void RenewAdapter(AdapterAddressInfo? target)
    {
        if (target is null) return;

        var confirm = MessageBox.Show(
            $"Renew the IP address on \"{target.AdapterName}\"?\nThis briefly drops connectivity while a new lease is negotiated.",
            "Renew IP address", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunDhcpActionAsync(() => DhcpAddressingService.RenewAsync(target.AdapterName), "Renew");
    }

    /// <summary>#529: register DNS - ipconfig.exe has no per-adapter scope for this (see
    /// DhcpAddressingService.RegisterDnsAsync's remarks), so the confirmation says so rather than
    /// implying it only touches the selected adapter.</summary>
    private void RegisterDnsForSelectedAdapter()
    {
        var confirm = MessageBox.Show(
            "Re-register this machine's DNS records with its configured DNS server(s)?\nipconfig has no per-adapter scope for this - it re-registers every adapter, not just the one selected above. Normally harmless, but it does generate DNS traffic.",
            "Register DNS", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunDhcpActionAsync(DhcpAddressingService.RegisterDnsAsync, "Register DNS");
    }

    private async Task RunDhcpActionAsync(Func<Task<string>> action, string label)
    {
        if (IsRunningDhcpAction) return;
        IsRunningDhcpAction = true;
        DhcpActionStatusText = $"Running {label}...";
        try
        {
            string output = await action();
            DhcpActionStatusText = $"{label}: {output}";
        }
        catch (Exception ex)
        {
            DhcpActionStatusText = $"{label} failed: {ex.Message}";
        }
        finally
        {
            IsRunningDhcpAction = false;
            await RefreshAddressingAsync();
        }
    }

    /// <summary>#530: scans the DHCP-Client Admin + Operational logs over the chosen lookback
    /// window - shelled off the UI thread via Task.Run since EventLogReader is synchronous, same
    /// pattern ScanDnsFailuresAsync (#524) already uses.</summary>
    private async Task ScanDhcpEventsAsync()
    {
        if (IsScanningDhcpEvents) return;
        IsScanningDhcpEvents = true;
        DhcpScanStatusText = "Scanning...";
        try
        {
            var window = TimeSpan.FromHours(DhcpScanWindowHours);
            var result = await Task.Run(() => DhcpEventLogService.Scan(window));

            DhcpEvents.Clear();
            foreach (var e in result.Events) DhcpEvents.Add(e);

            DhcpScanStatusText = !result.AdminChannelAvailable && !result.OperationalChannelAvailable
                ? "Neither the DHCP-Client Admin nor Operational log could be read."
                : !result.OperationalChannelAvailable
                    ? $"DHCP-Client Operational log unavailable (it's disabled by default - enable it in Event Viewer's \"Show Analytic and Debug Logs\" for full results). Found {result.Events.Count} event(s) from the Admin log only."
                    : result.Events.Count == 0
                        ? $"No DHCP lease events in the last {DhcpScanWindowHours:0.#}h."
                        : $"{result.Events.Count} event(s) in the last {DhcpScanWindowHours:0.#}h.";
        }
        catch (Exception ex)
        {
            DhcpScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningDhcpEvents = false;
        }
    }

    /// <summary>#531: correlates System-log Tcpip 4198/4199 address-conflict events against this
    /// machine's currently configured addresses.</summary>
    private async Task ScanIpConflictsAsync()
    {
        if (IsScanningIpConflicts) return;
        IsScanningIpConflicts = true;
        IpConflictScanStatusText = "Scanning...";
        try
        {
            var currentIps = Addressing.Select(a => a.IpAddress).Where(ip => ip.Length > 0).ToList();
            var window = TimeSpan.FromHours(IpConflictScanWindowHours);
            var result = await Task.Run(() => IpConflictService.ScanSystemLog(window, currentIps));

            IpConflictEvents.Clear();
            foreach (var e in result.Events) IpConflictEvents.Add(e);

            IpConflictScanStatusText = !result.ChannelAvailable
                ? "The System log's Tcpip provider couldn't be read."
                : result.Events.Count == 0
                    ? $"No address-conflict events in the last {IpConflictScanWindowHours:0.#}h."
                    : $"{result.Events.Count} conflict event(s) in the last {IpConflictScanWindowHours:0.#}h.";
        }
        catch (Exception ex)
        {
            IpConflictScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningIpConflicts = false;
        }
    }

    /// <summary>#531's active check: a gratuitous-ARP-style probe of the selected adapter's own
    /// address - see IpConflictService.ProbeOwnAddressAsync's remarks.</summary>
    private async Task ProbeGratuitousArpAsync()
    {
        if (IsProbingGratuitousArp) return;
        var target = SelectedAddressingAdapter ?? Addressing.FirstOrDefault();
        if (target is null)
        {
            GratuitousArpResultText = "No adapter with an IPv4 address to probe - refresh Addressing first.";
            return;
        }

        IsProbingGratuitousArp = true;
        GratuitousArpResultText = $"Probing {target.IpAddress}...";
        try
        {
            var (_, message) = await IpConflictService.ProbeOwnAddressAsync(target.IpAddress, target.MacAddress);
            GratuitousArpResultText = message;
        }
        catch (Exception ex)
        {
            GratuitousArpResultText = $"Probe failed: {ex.Message}";
        }
        finally
        {
            IsProbingGratuitousArp = false;
        }
    }

    /// <summary>#532/#533: reloads the ARP/neighbour cache, then (once the gateway's own MAC is
    /// actually known from this snapshot) runs #533's gateway-MAC-fingerprint check against it.</summary>
    private async Task RefreshArpAsync()
    {
        if (IsRefreshingArp) return;
        IsRefreshingArp = true;
        try
        {
            string? gatewayIp = NetworkDiagnosticsService.FindDefaultGateway();
            var result = await ArpCacheService.ReadAsync(gatewayIp);

            ArpEntries.Clear();
            foreach (var e in result.Entries.OrderBy(e => e.IsGateway ? 0 : 1).ThenBy(e => e.IpAddress, StringComparer.OrdinalIgnoreCase))
                ArpEntries.Add(e);

            ArpStatusText = result.Entries.Count == 0
                ? "ARP cache is empty (or couldn't be read)."
                : $"{result.Entries.Count} neighbour(s)." + (result.GatewayEntryMissing ? " No ARP entry for the gateway yet - try pinging it first (the Connectivity card above does this every 15s)." : string.Empty);

            // #533: only meaningful once the gateway's own MAC actually resolved from this snapshot.
            if (gatewayIp is not null && result.GatewayMac is not null)
            {
                string profileKey = Wifi?.Ssid ?? "Wired";
                GatewayMacChangeText = GatewayFingerprintService.CheckAndUpdate(_gatewayFingerprint, profileKey, gatewayIp, result.GatewayMac);
                await Task.Run(() => GatewayFingerprintService.Save(_gatewayFingerprint));
            }
            else
            {
                GatewayMacChangeText = null;
            }
        }
        catch (Exception ex)
        {
            ArpStatusText = $"Couldn't read the ARP cache: {ex.Message}";
        }
        finally
        {
            IsRefreshingArp = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _latencyMonitor.CycleCompleted -= OnLatencyCycleCompleted;
        _latencyMonitor.Dispose();
        _mtr.CycleCompleted -= OnMtrCycleCompleted;
        _mtr.Dispose();
        _dnsResponseMonitor.CycleCompleted -= OnDnsResponseCycleCompleted;
        _dnsResponseMonitor.Dispose();
    }
}
