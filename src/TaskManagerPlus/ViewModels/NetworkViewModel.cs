using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
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

/// <summary>#547-#554's per-adapter composite row for the new Adapter health card - assembled from
/// several independent services (AdapterErrorCounterService, AdapterAdvancedPropertyService,
/// AdapterPowerManagementService, AdapterDriverStoreService, ...) the same way AdapterAddressInfo
/// composes DhcpAddressingService's output for the Addressing card. Extends ObservableObject (unlike
/// AdapterAddressInfo's plain mutable class) because this card's per-adapter "Advanced properties"
/// Expander needs to keep its expanded/collapsed UI state across the 15s tick that refreshes #547's
/// error counters - see NetworkViewModel.RefreshAdapterHealth's remarks for why only the
/// per-tick fields below are ever reassigned after construction.</summary>
public sealed class AdapterHealthRow : ObservableObject
{
    public string AdapterName { get; init; } = string.Empty;

    // #549/#550/#551/#552/#553: read once when this row is first created - none of these change
    // without a driver reinstall/reconfiguration this app would need a restart to see anyway, the
    // same "queried once" tradeoff the existing #48 AdapterDrivers list already takes. (#556's
    // Driver Store match lives on the existing AdapterDrivers list itself, not here - see
    // NetworkViewModel's constructor remarks.)
    public List<AdapterAdvancedProperty> AdvancedProperties { get; init; } = new();
    public List<AdapterAdvancedProperty> OffloadProperties { get; init; } = new();
    public List<AdapterProblemFlag> ProblemFlags { get; init; } = new();
    public AdapterPowerManagementInfo PowerManagement { get; init; } = new(null, null, "Unknown", null, null);

    // #551: plain Yes/No/Unknown text for the view - avoids a bool?-to-text converter/multi-trigger
    // for two fields that never change after construction (PowerManagement itself is never
    // reassigned), same "compute it once in C#" tradeoff GatewayStatusText etc. already take above.
    public string ArpOffloadText => PowerManagement.ArpOffloadEnabled switch { true => "On", false => "Off", null => "Unknown" };
    public string WakeOnMagicPacketText => PowerManagement.WakeOnMagicPacketEnabled switch { true => "On", false => "Off", null => "Unknown" };

    // #547: per-tick error/discard deltas - the only fields mutated after construction, updated in
    // place every 15s tick so this row's object identity (and any expanded Expander bound to it)
    // survives the refresh instead of being torn down and rebuilt.
    private long _rxErrorsDelta;
    public long RxErrorsDelta { get => _rxErrorsDelta; set => SetProperty(ref _rxErrorsDelta, value); }

    private long _txErrorsDelta;
    public long TxErrorsDelta { get => _txErrorsDelta; set => SetProperty(ref _txErrorsDelta, value); }

    private long _rxDiscardsDelta;
    public long RxDiscardsDelta { get => _rxDiscardsDelta; set => SetProperty(ref _rxDiscardsDelta, value); }

    private long _txDiscardsDelta;
    public long TxDiscardsDelta { get => _txDiscardsDelta; set => SetProperty(ref _txDiscardsDelta, value); }

    private bool _hasCurrentErrorRate;
    public bool HasCurrentErrorRate { get => _hasCurrentErrorRate; set => SetProperty(ref _hasCurrentErrorRate, value); }

    // Sticky for the rest of this app session - unlike HasCurrentErrorRate (only the latest tick),
    // this stays true once any error/discard has ever been seen, so a sporadic error that's since
    // cleared still counts toward #554's quality score below.
    private bool _hasEverHadErrorsThisSession;
    public bool HasEverHadErrorsThisSession { get => _hasEverHadErrorsThisSession; set => SetProperty(ref _hasEverHadErrorsThisSession, value); }

    // #554: the card's headline - recomputed by NetworkViewModel.RecomputeLinkQuality whenever
    // either input (a fresh #547 sample, or a completed #548 scan) changes. Deliberately labelled a
    // heuristic, same as every other "quick flag, not a verdict" indicator in this app.
    private string _linkQualityLabel = "Unknown";
    public string LinkQualityLabel { get => _linkQualityLabel; set => SetProperty(ref _linkQualityLabel, value); }

    private string _linkQualityReason = "Not enough data yet.";
    public string LinkQualityReason { get => _linkQualityReason; set => SetProperty(ref _linkQualityReason, value); }
}

/// <summary>#570's "This app can't reach the network" wizard result - a guided assembly of signals
/// this tab otherwise scatters across the Firewall/Proxy/Adapters/Connections cards, gathered fresh
/// for one specific process/executable query at the moment the wizard runs (never cached - the
/// whole point is "what does this look like right now"). A plain mutable class rather than a
/// record, matching AdapterHealthRow's own "composite row assembled from several independent
/// services" shape above.</summary>
public sealed class NetworkTroubleshootReport
{
    public string Query { get; init; } = string.Empty;
    public List<FirewallRuleInfo> MatchingFirewallRules { get; init; } = new();
    public List<WfpDropEvent> MatchingWfpDrops { get; init; } = new();
    public bool WfpChannelAvailable { get; init; }
    public string ProxyApplicability { get; init; } = string.Empty;
    public string VpnRouteSummary { get; init; } = string.Empty;
    public List<TcpConnectionInfo> MatchingConnections { get; init; } = new();
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

    // ---- suggestions.md #536-546: Wi-Fi diagnostics -----------------------------------------------
    // Extends the existing #23 Wi-Fi card (Wifi/WifiInfo above stays exactly as-is - it's still the
    // card's visibility gate and the SSID GatewayFingerprintService keys profiles by) rather than
    // replacing it. #536/#540/#545 are native-WLAN-API/registry readouts refreshed on the same 2s
    // cadence WifiSignalMonitorService already samples RSSI on for #537 (see that class's remarks
    // for why it's safe to poll continuously, unlike the #538 scan); #538/#539/#546 are one on-demand
    // netsh neighbour scan and three views over its result; #541/#542 are an on-demand event-log
    // scan; #543 is a one-shot report generator; #544 is an on-demand profile audit with a per-row
    // delete action.
    private readonly WlanNativeService _wlan = new();
    private readonly WifiSignalMonitorService _wifiSignalMonitor;

    // #536/#540/#545: headline readout - null whenever WifiSignalMonitorService isn't running (no
    // live Wi-Fi association) or the native API had nothing to report; the view hides these rows
    // rather than showing a stale/guessed value.
    private WifiRadioSnapshot? _wifiRadio;
    public WifiRadioSnapshot? WifiRadio { get => _wifiRadio; private set => SetProperty(ref _wifiRadio, value); }

    /// <summary>#536: "good / marginal / poor" band for the dBm readout - informational thresholds
    /// (roughly the same -65/-75 dBm break points commonly cited for reliable data vs. marginal vs.
    /// poor Wi-Fi), not a hard spec.</summary>
    public string WifiRssiBandText => WifiRadio?.RssiDbm switch
    {
        null => "Unknown",
        >= -65 => "Good",
        >= -75 => "Marginal",
        _ => "Poor",
    };

    /// <summary>#536: SNR - only ever non-null once <see cref="WifiRadioSnapshot.NoiseDbm"/> has a
    /// real driver-supplied reading (it doesn't today; see that field's remarks), so this stays
    /// hidden rather than shown as "Unknown" on every machine.</summary>
    public string? WifiSnrText => WifiRadio?.RssiDbm is { } rssi && WifiRadio?.NoiseDbm is { } noise ? $"{rssi - noise}" : null;

    private WifiPowerSavingInfo? _wifiPowerSaving;
    public WifiPowerSavingInfo? WifiPowerSaving { get => _wifiPowerSaving; private set => SetProperty(ref _wifiPowerSaving, value); }

    // #537: RSSI + link-rate history, glow/core paired like every other chart in the app (see
    // LatencyLineOf's remarks) - rebuilt in full from WifiSignalMonitorService.GetWindow() every
    // cycle rather than pushed-and-trimmed incrementally, so the #537 roam markers' X-indices always
    // line up 1:1 with the chart data they're drawn against (the same "clear+rebuild is simpler for
    // a read-only display list" tradeoff CLAUDE.md documents for Energy & Thermals' sensor lists).
    public ObservableCollection<double> WifiRssiHistory { get; } = new();
    public ObservableCollection<double> WifiRxRateHistory { get; } = new();
    public ObservableCollection<RectangularSection> WifiRoamMarkers { get; } = new();

    private readonly LineSeries<double> _wifiRssiGlow, _wifiRssiCore;
    private readonly LineSeries<double> _wifiRateGlow, _wifiRateCore;
    public ISeries[] WifiRssiSeries { get; }
    public ISeries[] WifiRateSeries { get; }
    public Axis[] WifiHiddenXAxes { get; }
    public Axis[] WifiRssiYAxes { get; }
    public Axis[] WifiRateYAxes { get; }

    // #538/#539/#546: on-demand neighbour/channel scan ("Airspace" expander) - a fresh active scan,
    // distinct from #537's passive continuous sampling (see WifiChannelScanService's remarks on why
    // this one needs an explicit button).
    private bool _isScanningAirspace;
    public bool IsScanningAirspace { get => _isScanningAirspace; private set => SetProperty(ref _isScanningAirspace, value); }

    private string _airspaceStatusText = "Not scanned yet - scanning briefly interrupts your own connection's data flow, so this is on-demand rather than continuous.";
    public string AirspaceStatusText { get => _airspaceStatusText; private set => SetProperty(ref _airspaceStatusText, value); }

    public ObservableCollection<WifiChannelOccupancy> AirspaceOccupancy24 { get; } = new();
    public ObservableCollection<WifiChannelOccupancy> AirspaceOccupancy5 { get; } = new();
    public ObservableCollection<WifiChannelOccupancy> AirspaceOccupancy6 { get; } = new();

    private string? _airspaceRecommendation24;
    public string? AirspaceRecommendation24 { get => _airspaceRecommendation24; private set => SetProperty(ref _airspaceRecommendation24, value); }
    private string? _airspaceRecommendation5;
    public string? AirspaceRecommendation5 { get => _airspaceRecommendation5; private set => SetProperty(ref _airspaceRecommendation5, value); }
    private string? _airspaceRecommendation6;
    public string? AirspaceRecommendation6 { get => _airspaceRecommendation6; private set => SetProperty(ref _airspaceRecommendation6, value); }

    // #539: 2.4 GHz overlap verdict against the current channel - null when not on 2.4 GHz or no
    // scan has run yet.
    private string? _wifiOverlapText;
    public string? WifiOverlapText { get => _wifiOverlapText; private set => SetProperty(ref _wifiOverlapText, value); }

    // #540: band-steering suggestion - null (hidden) unless a scan has actually seen the same SSID
    // with a usable signal on a different band while this machine sits on 2.4 GHz.
    private string? _wifiBandSteeringHintText;
    public string? WifiBandSteeringHintText { get => _wifiBandSteeringHintText; private set => SetProperty(ref _wifiBandSteeringHintText, value); }

    // #546: same-SSID BSSID groups from the same scan - the mesh/extender "sticky client" view.
    public ObservableCollection<WifiSsidGroup> WifiMeshGroups { get; } = new();

    public AsyncRelayCommand RunAirspaceScanCommand { get; }

    // #541/#542: Wi-Fi events timeline - its own lookback window, mirroring #524/#530's on-demand
    // event-log scans.
    private double _wifiEventScanWindowHours = 24.0;
    public double WifiEventScanWindowHours { get => _wifiEventScanWindowHours; set => SetProperty(ref _wifiEventScanWindowHours, Math.Clamp(value, 1.0, 720.0)); }

    private bool _isScanningWifiEvents;
    public bool IsScanningWifiEvents { get => _isScanningWifiEvents; private set => SetProperty(ref _isScanningWifiEvents, value); }

    private string _wifiEventStatusText = "Not scanned yet.";
    public string WifiEventStatusText { get => _wifiEventStatusText; private set => SetProperty(ref _wifiEventStatusText, value); }

    public ObservableCollection<WifiConnectionEvent> WifiEvents { get; } = new();
    public AsyncRelayCommand ScanWifiEventsCommand { get; }

    // #543: one-click wireless report.
    private bool _isRunningWlanReport;
    public bool IsRunningWlanReport { get => _isRunningWlanReport; private set => SetProperty(ref _isRunningWlanReport, value); }

    private string _wlanReportStatusText = "Generates Windows' own built-in wireless diagnostics report and opens it in your browser.";
    public string WlanReportStatusText { get => _wlanReportStatusText; private set => SetProperty(ref _wlanReportStatusText, value); }

    public AsyncRelayCommand RunWlanReportCommand { get; }

    // #544: saved-profile audit.
    private bool _isLoadingWifiProfiles;
    public bool IsLoadingWifiProfiles { get => _isLoadingWifiProfiles; private set => SetProperty(ref _isLoadingWifiProfiles, value); }

    private string _wifiProfilesStatusText = "Not loaded yet.";
    public string WifiProfilesStatusText { get => _wifiProfilesStatusText; private set => SetProperty(ref _wifiProfilesStatusText, value); }

    public ObservableCollection<WifiProfileAudit> WifiProfiles { get; } = new();
    public AsyncRelayCommand RefreshWifiProfilesCommand { get; }
    public AsyncRelayCommand DeleteWifiProfileCommand { get; }

    // ---- suggestions.md #547-556: NIC driver, offload, power management and link health --------
    // New "Adapter health" card. #547 (per-adapter error/discard deltas) and the registry-derived
    // facts #549-553 need ride the same 15s CheckConnectivityAsync tick AdapterLinks/AdapterDrivers
    // already use above - #547 explicitly wants a per-tick delta, and a handful of small registry
    // reads per adapter is no heavier than the WMI/netsh calls that tick already makes elsewhere
    // (ReadProxyConfig, WifiDiagnosticsService.ReadCurrentWifiAsync, ...). #548 (event-log scan) and
    // #555 (restart) are on-demand only, per CLAUDE.md's event-log-scan and disruptive-action
    // conventions - #555 sits behind the same MessageBox.Show confirm-first pattern
    // ReleaseSelectedAdapter/RenewAdapter above already use for their own connection-dropping actions.
    private readonly AdapterErrorCounterService _adapterErrorCounters = new();

    // #548: reset counts attributed to an adapter by the most recent scan - empty until a scan has
    // actually run, in which case #554 simply has one less input to work from.
    private readonly Dictionary<string, int> _linkFlapCountsByAdapter = new(StringComparer.OrdinalIgnoreCase);

    // #556: every Net-class package currently staged in the Driver Store - read once at startup,
    // matched against each adapter's own driver record when its AdapterHealthRow is first built.
    private List<StagedDriverPackage> _allStagedNetDrivers = new();

    public ObservableCollection<AdapterHealthRow> AdapterHealth { get; } = new();

    // #552: machine-wide TCP offload settings - read once at startup, like AdapterDrivers, since
    // these don't change without an explicit `netsh int tcp set global` command this app itself
    // never issues.
    private TcpGlobalSettings _tcpGlobalSettings = new("Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown");
    public TcpGlobalSettings TcpGlobalSettings { get => _tcpGlobalSettings; private set => SetProperty(ref _tcpGlobalSettings, value); }

    // #548: on-demand link-flap/reset scan - its own lookback window, mirroring #524/#530/#541's
    // on-demand event-log scans elsewhere on this tab.
    private double _linkFlapScanWindowHours = 24.0;
    public double LinkFlapScanWindowHours { get => _linkFlapScanWindowHours; set => SetProperty(ref _linkFlapScanWindowHours, Math.Clamp(value, 1.0, 720.0)); }

    private bool _isScanningLinkFlaps;
    public bool IsScanningLinkFlaps { get => _isScanningLinkFlaps; private set => SetProperty(ref _isScanningLinkFlaps, value); }

    private string _linkFlapScanStatusText = "Not scanned yet.";
    public string LinkFlapScanStatusText { get => _linkFlapScanStatusText; private set => SetProperty(ref _linkFlapScanStatusText, value); }

    public ObservableCollection<LinkFlapEvent> LinkFlapEvents { get; } = new();
    public AsyncRelayCommand ScanLinkFlapsCommand { get; }

    // #555: restart action - target adapter picked by name from the same set AdapterHealth/
    // AdapterLinks already list.
    private string? _selectedHealthAdapterName;
    public string? SelectedHealthAdapterName { get => _selectedHealthAdapterName; set => SetProperty(ref _selectedHealthAdapterName, value); }

    private bool _isRestartingAdapter;
    public bool IsRestartingAdapter { get => _isRestartingAdapter; private set => SetProperty(ref _isRestartingAdapter, value); }

    private string _restartAdapterStatusText = "Pick an adapter above, then Restart. This briefly drops its connection.";
    public string RestartAdapterStatusText { get => _restartAdapterStatusText; private set => SetProperty(ref _restartAdapterStatusText, value); }

    public RelayCommand RestartAdapterCommand { get; }

    // #551: "the exact Device Manager location to change it" - Windows has no documented way to
    // deep-link straight to one device's Power Management tab, so this opens Device Manager itself
    // (same ShellExecute-a-known-tool shape #46's OpenHostsFileCommand already uses) rather than
    // leaving the guidance text as a dead end.
    public RelayCommand OpenDeviceManagerCommand { get; }

    // ---- suggestions.md #557-565: TCP stack, connections and ports ------------------------------
    // Two new cards - "TCP health" (#557, #565) and "Ports" (#563, #564) - plus four extensions to
    // the existing #21 connections grid (#558 state histogram, #559 stalled-SYN age column, #560
    // port lookup, #561/#562 UDP + IPv6 rows). All of it rides the same 15s CheckConnectivityAsync
    // tick the grid itself already refreshes on (per CLAUDE.md's on-demand-vs-polled guidance, these
    // are all cheap reads on top of data already sampled) except #563/#565, which are read once at
    // startup plus an explicit Refresh button, like #552's own one-time TcpGlobalSettings read above.

    // #557: TCP-layer retransmit/reset health (IPv4 + IPv6) - see TcpHealthCounterService's remarks.
    private readonly TcpHealthCounterService _tcpHealthCounters = new();
    private const double RetransmitRateFlagPercent = 2.0;

    public ObservableCollection<TcpHealthSample> TcpHealthSamples { get; } = new();

    // #557: IPv4 retransmit-rate-as-percent-of-segments-sent history - glow/core paired like every
    // other chart in the app, reusing LatencyLineOf/NewLatencyHistory below rather than duplicating
    // the pairing helper for one more chart. IPv6 isn't separately charted (most machines carry
    // little-to-no IPv6 TCP traffic today), but its own numbers are still in TcpHealthSamples above.
    public ObservableCollection<double> TcpRetransmitRateHistory { get; } = NewLatencyHistory();
    private readonly LineSeries<double> _tcpRetransmitGlow, _tcpRetransmitCore;
    public ISeries[] TcpRetransmitSeries { get; }
    public Axis[] TcpRetransmitPercentYAxes { get; }

    private string _tcpHealthStatusText = "Not sampled yet.";
    public string TcpHealthStatusText { get => _tcpHealthStatusText; private set => SetProperty(ref _tcpHealthStatusText, value); }

    // #558: connection-state histogram, derived entirely from the connections table already sampled
    // below - no extra I/O.
    public ObservableCollection<ConnectionStateHistogramEntry> ConnectionStateHistogram { get; } = new();

    // #559: stalled-SYN_SENT tracker + its one combined status line - the per-connection age itself
    // lives on each TcpConnectionInfo row in Connections (see that class's remarks), refreshed via
    // SynSentStallTracker.Annotate before Connections is rebuilt each tick.
    private readonly SynSentStallTracker _synSentTracker = new();

    private string _stalledSynStatusText = "No SYN_SENT connection has been stuck long enough to flag.";
    public string StalledSynStatusText { get => _stalledSynStatusText; private set => SetProperty(ref _stalledSynStatusText, value); }

    // #560: "what is using port N" - looks up whatever's currently in Connections/UdpConnections
    // below (no fresh sample of its own), so results always reflect the tables as of the last 15s
    // tick, not a separate live query.
    private string _portLookupQuery = string.Empty;
    public string PortLookupQuery { get => _portLookupQuery; set => SetProperty(ref _portLookupQuery, value); }

    public ObservableCollection<PortLookupResult> PortLookupResults { get; } = new();

    private bool _isLookingUpPort;
    public bool IsLookingUpPort { get => _isLookingUpPort; private set => SetProperty(ref _isLookingUpPort, value); }

    private string _portLookupStatusText = "Enter a port number above and click Look up.";
    public string PortLookupStatusText { get => _portLookupStatusText; private set => SetProperty(ref _portLookupStatusText, value); }

    public AsyncRelayCommand LookupPortCommand { get; }

    /// <summary>#560's "End process" action - reuses ProcessMonitorService.EndProcess, the same
    /// disruptive action ProcessesViewModel's own "End process" button already calls, behind the
    /// same MessageBox.Show confirm-first pattern used throughout this ViewModel.</summary>
    public AsyncRelayCommand EndPortLookupProcessCommand { get; }

    // #561: UDP table alongside the existing TCP-only Connections grid - its own small
    // TCP/UDP toggle rather than merging two differently-shaped row types into one DataGrid (UDP
    // has no remote endpoint or state to show).
    public ObservableCollection<UdpConnectionInfo> UdpConnections { get; } = new();

    private bool _showUdpConnections;
    public bool ShowUdpConnections { get => _showUdpConnections; private set => SetProperty(ref _showUdpConnections, value); }

    public RelayCommand ShowTcpConnectionsCommand { get; }
    public RelayCommand ShowUdpConnectionsCommand { get; }

    // #563: port exclusion ranges + the TCP dynamic port range, plus which excluded ranges overlap
    // it - read once at startup and from an explicit Refresh button, like #513's routing table.
    private PortReservationInfo _portReservation = PortReservationInfo.Empty;
    public PortReservationInfo PortReservation { get => _portReservation; private set => SetProperty(ref _portReservation, value); }

    private bool _isLoadingPortReservation;
    public bool IsLoadingPortReservation { get => _isLoadingPortReservation; private set => SetProperty(ref _isLoadingPortReservation, value); }

    private string _portReservationStatusText = "Not loaded yet.";
    public string PortReservationStatusText { get => _portReservationStatusText; private set => SetProperty(ref _portReservationStatusText, value); }

    public AsyncRelayCommand RefreshPortReservationCommand { get; }

    // #564: ephemeral-port utilization - recomputed (no extra I/O) whenever either input changes,
    // i.e. after every connections refresh and after every #563 reload. Null until a dynamic port
    // range has actually been read once.
    private PortExhaustionInfo? _portExhaustion;
    public PortExhaustionInfo? PortExhaustion { get => _portExhaustion; private set => SetProperty(ref _portExhaustion, value); }

    // ---- suggestions.md #566-571: Firewall rules and blocked connections -------------------------
    // New "Firewall" card. Everything here is on-demand only (per CLAUDE.md's on-demand-vs-polled
    // convention - a COM/netsh sweep of hundreds of rules, an event-log scan, or an XML dump of the
    // WFP engine state are all far too heavy for a tick), and every action that changes system state
    // (#568's logging toggle, #569's audit-policy change) sits behind an explicit MessageBox.Show
    // confirm, the same pattern RestartSelectedAdapter/DeleteWifiProfileAsync above already use.

    // #566: profile status.
    public ObservableCollection<FirewallProfileStatus> FirewallProfiles { get; } = new();
    private bool _isLoadingFirewallProfiles;
    public bool IsLoadingFirewallProfiles { get => _isLoadingFirewallProfiles; private set => SetProperty(ref _isLoadingFirewallProfiles, value); }
    public AsyncRelayCommand RefreshFirewallProfilesCommand { get; }

    // #567: full rule browser, filter box, and "this executable only" mode.
    private List<FirewallRuleInfo> _allFirewallRules = new();
    public ObservableCollection<FirewallRuleInfo> FilteredFirewallRules { get; } = new();
    private bool _isLoadingFirewallRules;
    public bool IsLoadingFirewallRules { get => _isLoadingFirewallRules; private set => SetProperty(ref _isLoadingFirewallRules, value); }
    private string _firewallRulesStatusText = "Not loaded yet - this reads every configured firewall rule, which can take a few seconds.";
    public string FirewallRulesStatusText { get => _firewallRulesStatusText; private set => SetProperty(ref _firewallRulesStatusText, value); }

    private string _firewallRuleFilterText = string.Empty;
    public string FirewallRuleFilterText
    {
        get => _firewallRuleFilterText;
        set { if (SetProperty(ref _firewallRuleFilterText, value)) ApplyFirewallRuleFilter(); }
    }

    private bool _firewallFilterExecutableOnly;
    public bool FirewallFilterExecutableOnly
    {
        get => _firewallFilterExecutableOnly;
        set { if (SetProperty(ref _firewallFilterExecutableOnly, value)) ApplyFirewallRuleFilter(); }
    }

    public AsyncRelayCommand RefreshFirewallRulesCommand { get; }

    // #568: blocked-connection log reader + the "enable dropped-packet logging" action.
    public ObservableCollection<FirewallLogEntry> FirewallLogEntries { get; } = new();
    private bool _isLoadingFirewallLog;
    public bool IsLoadingFirewallLog { get => _isLoadingFirewallLog; private set => SetProperty(ref _isLoadingFirewallLog, value); }
    private string _firewallLogStatusText = "Not loaded yet.";
    public string FirewallLogStatusText { get => _firewallLogStatusText; private set => SetProperty(ref _firewallLogStatusText, value); }
    public AsyncRelayCommand RefreshFirewallLogCommand { get; }
    public AsyncRelayCommand EnableDroppedLoggingCommand { get; }

    // #569: WFP drop auditing - enable action, then an on-demand scan with its own lookback window
    // (mirroring #524/#530/#541's own on-demand event-log scans elsewhere on this tab).
    private bool _isEnablingWfpAuditing;
    public bool IsEnablingWfpAuditing { get => _isEnablingWfpAuditing; private set => SetProperty(ref _isEnablingWfpAuditing, value); }
    private string _wfpAuditStatusText = "Not enabled yet - click \"Enable auditing\", then Scan after some blocked traffic occurs.";
    public string WfpAuditStatusText { get => _wfpAuditStatusText; private set => SetProperty(ref _wfpAuditStatusText, value); }
    public AsyncRelayCommand EnableWfpAuditingCommand { get; }

    private double _wfpScanWindowHours = 24.0;
    public double WfpScanWindowHours { get => _wfpScanWindowHours; set => SetProperty(ref _wfpScanWindowHours, Math.Clamp(value, 1.0, 720.0)); }
    private bool _isScanningWfpDrops;
    public bool IsScanningWfpDrops { get => _isScanningWfpDrops; private set => SetProperty(ref _isScanningWfpDrops, value); }
    private string _wfpScanStatusText = "Not scanned yet.";
    public string WfpScanStatusText { get => _wfpScanStatusText; private set => SetProperty(ref _wfpScanStatusText, value); }
    public ObservableCollection<WfpDropEvent> WfpDropEvents { get; } = new();
    public AsyncRelayCommand ScanWfpDropsCommand { get; }

    // #571: read-only filter-driver/security-product stack inventory.
    public ObservableCollection<WfpProviderInfo> WfpProviders { get; } = new();
    public ObservableCollection<WfpCalloutInfo> WfpCallouts { get; } = new();
    public ObservableCollection<BoundNetworkFilterDriver> BoundFilterDrivers { get; } = new();
    private bool _isLoadingStackInventory;
    public bool IsLoadingStackInventory { get => _isLoadingStackInventory; private set => SetProperty(ref _isLoadingStackInventory, value); }
    private string _stackInventoryStatusText = "Not loaded yet.";
    public string StackInventoryStatusText { get => _stackInventoryStatusText; private set => SetProperty(ref _stackInventoryStatusText, value); }
    public AsyncRelayCommand RefreshStackInventoryCommand { get; }

    // #570: "This app can't reach the network" wizard - a guided combination of #567/#569's own
    // last-run data (freshly re-gathered, scoped to the query) plus the existing proxy/VPN readouts
    // and the Connections grid. Wiring an actual right-click "jump here" from the Processes tab
    // context menu would need new cross-ViewModel plumbing this app doesn't otherwise have (every
    // existing cross-tab reach is the thin theme/accent-color wiring MainViewModel already owns, per
    // CLAUDE.md's "Cross-tab coupling is deliberately thin" note) - out of scope for this pass, so
    // this wizard is reached from its own button on this tab instead, with a free-text query field
    // (process name or full executable path both work, since #567's own filter already matches
    // either).
    private string _wizardQuery = string.Empty;
    public string WizardQuery { get => _wizardQuery; set => SetProperty(ref _wizardQuery, value); }
    private bool _isRunningWizard;
    public bool IsRunningWizard { get => _isRunningWizard; private set => SetProperty(ref _isRunningWizard, value); }
    private string _wizardStatusText = "Enter a process name or executable path (e.g. \"chrome.exe\") and click Diagnose.";
    public string WizardStatusText { get => _wizardStatusText; private set => SetProperty(ref _wizardStatusText, value); }
    private NetworkTroubleshootReport? _wizardReport;
    public NetworkTroubleshootReport? WizardReport { get => _wizardReport; private set => SetProperty(ref _wizardReport, value); }
    public AsyncRelayCommand RunWizardCommand { get; }

    // ---- suggestions.md #572-575: Proxy, PAC and Winsock ------------------------------------------
    // Extends the existing read-only proxy readout (ProxyStatusText above, #47) into a full "Proxy"
    // card (#572/#573/#574) plus a new "Stack" card for Winsock (#575) and the reset toolkit (#576).
    // One combined Refresh loads all three proxy checks together since they all read from the same
    // already-fetched ProxyConfigInfo; the bypass tester (#574) needs no I/O of its own once that's
    // loaded.

    private bool _isLoadingProxyCard;
    public bool IsLoadingProxyCard { get => _isLoadingProxyCard; private set => SetProperty(ref _isLoadingProxyCard, value); }
    public AsyncRelayCommand RefreshProxyCardCommand { get; }

    // #572: PAC fetch/health.
    private string _pacStatusText = "Not checked yet.";
    public string PacStatusText { get => _pacStatusText; private set => SetProperty(ref _pacStatusText, value); }
    private string? _pacBody;
    public string? PacBody { get => _pacBody; private set => SetProperty(ref _pacBody, value); }

    // "No PAC configured" is a normal, non-problem state, distinct from an attempted fetch that
    // failed or came back slow - a separate flag rather than inferring a warning color off
    // PacBody == null in the view (which would also match "not configured").
    private bool _pacLooksProblematic;
    public bool PacLooksProblematic { get => _pacLooksProblematic; private set => SetProperty(ref _pacLooksProblematic, value); }

    // #573: per-user vs. machine-wide WinHTTP proxy divergence.
    private ProxyDivergenceInfo? _proxyDivergence;
    public ProxyDivergenceInfo? ProxyDivergence { get => _proxyDivergence; private set => SetProperty(ref _proxyDivergence, value); }

    // #574: bypass list + "does this host bypass" tester.
    public ObservableCollection<string> ProxyBypassEntries { get; } = new();
    private string _bypassTestHostname = string.Empty;
    public string BypassTestHostname { get => _bypassTestHostname; set => SetProperty(ref _bypassTestHostname, value); }
    private string _bypassTestResultText = "Enter a hostname above and click Test.";
    public string BypassTestResultText { get => _bypassTestResultText; private set => SetProperty(ref _bypassTestResultText, value); }
    public RelayCommand TestBypassCommand { get; }

    // #575: new "Stack" card - Winsock LSP catalog + reset (behind a reboot-required confirm).
    public ObservableCollection<WinsockProviderEntry> WinsockProviders { get; } = new();
    private int _winsockNonMicrosoftCount;
    public int WinsockNonMicrosoftCount { get => _winsockNonMicrosoftCount; private set => SetProperty(ref _winsockNonMicrosoftCount, value); }
    private bool _isLoadingWinsockCatalog;
    public bool IsLoadingWinsockCatalog { get => _isLoadingWinsockCatalog; private set => SetProperty(ref _isLoadingWinsockCatalog, value); }
    private string _winsockStatusText = "Not loaded yet.";
    public string WinsockStatusText { get => _winsockStatusText; private set => SetProperty(ref _winsockStatusText, value); }
    public AsyncRelayCommand RefreshWinsockCatalogCommand { get; }

    private bool _isResettingWinsock;
    public bool IsResettingWinsock { get => _isResettingWinsock; private set => SetProperty(ref _isResettingWinsock, value); }
    private string _winsockResetStatusText = string.Empty;
    public string WinsockResetStatusText { get => _winsockResetStatusText; private set => SetProperty(ref _winsockResetStatusText, value); }
    public RelayCommand ResetWinsockCommand { get; }

    // ---- suggestions.md #576: network stack reset toolkit ------------------------------------------
    // Stack card. Each action is individually confirmed (RunStackResetAction below owns the shared
    // confirm-then-run-then-log plumbing) rather than one "reset everything" button, since each one
    // breaks something different and the user should be able to run just the one they need.
    private bool _isRunningStackReset;
    public bool IsRunningStackReset { get => _isRunningStackReset; private set => SetProperty(ref _isRunningStackReset, value); }
    private string _stackResetStatusText = string.Empty;
    public string StackResetStatusText { get => _stackResetStatusText; private set => SetProperty(ref _stackResetStatusText, value); }
    public RelayCommand ResetIpStackCommand { get; }
    public RelayCommand FlushDnsResolverCacheCommand { get; }
    public RelayCommand ClearArpCacheCommand { get; }
    public RelayCommand ResetNetBiosCacheCommand { get; }

    // ---- suggestions.md #577: VPN default-route and DNS-leak check ---------------------------------
    // Lands beside the existing #37 VPN presence indicator (HasActiveVpn/VpnStatusText above) in the
    // Adapters card - a real behavioural check, on-demand (both a routing-table read and a live DNS
    // query), unlike that heuristic's cheap NetworkInterface enumeration.
    private bool _isCheckingVpnTunnel;
    public bool IsCheckingVpnTunnel { get => _isCheckingVpnTunnel; private set => SetProperty(ref _isCheckingVpnTunnel, value); }
    private VpnTunnelCheckResult? _vpnTunnelCheck;
    public VpnTunnelCheckResult? VpnTunnelCheck { get => _vpnTunnelCheck; private set => SetProperty(ref _vpnTunnelCheck, value); }
    public AsyncRelayCommand RunVpnTunnelCheckCommand { get; }

    // ---- suggestions.md #578: orphaned virtual adapter detection -----------------------------------
    // A new section on the existing Adapter health card (#547-556 above), not a separate card, per
    // this item's own text.
    public ObservableCollection<OrphanedAdapterInfo> OrphanedAdapters { get; } = new();
    private bool _isScanningOrphanedAdapters;
    public bool IsScanningOrphanedAdapters { get => _isScanningOrphanedAdapters; private set => SetProperty(ref _isScanningOrphanedAdapters, value); }
    private string _orphanedAdapterStatusText = "Not scanned yet.";
    public string OrphanedAdapterStatusText { get => _orphanedAdapterStatusText; private set => SetProperty(ref _orphanedAdapterStatusText, value); }
    public AsyncRelayCommand ScanOrphanedAdaptersCommand { get; }

    // ---- suggestions.md #579-585: throughput, bufferbloat and per-process bandwidth --------------
    // #579/#580/#581/#582 are all explicit user-initiated on-demand actions with their own visible
    // "running" state, per CLAUDE.md's on-demand-vs-polled convention - none of these ever run on a
    // timer. #584/#585 are cheap NetworkInterface reads derived from data already sampled every
    // tick, so they ride existing tick cadences instead (#584 the shared PerformanceViewModel's own
    // 1s sampler, via a PropertyChanged subscription wired in the constructor; #585 this ViewModel's
    // own 15s CheckConnectivityAsync tick).

    // #579: built-in HTTP speed test - a single-stream download then upload, deliberately captioned
    // as a rough floor, not a certified figure (see SpeedTestService's remarks).
    private string _speedTestDownloadUrl = SpeedTestService.DefaultDownloadUrl;
    public string SpeedTestDownloadUrl { get => _speedTestDownloadUrl; set => SetProperty(ref _speedTestDownloadUrl, value); }

    private string _speedTestUploadUrl = SpeedTestService.DefaultUploadUrl;
    public string SpeedTestUploadUrl { get => _speedTestUploadUrl; set => SetProperty(ref _speedTestUploadUrl, value); }

    private bool _isRunningSpeedTest;
    public bool IsRunningSpeedTest { get => _isRunningSpeedTest; private set => SetProperty(ref _isRunningSpeedTest, value); }

    private string _speedTestStatusText = "A single-stream HTTP test - a rough floor on your link's real capacity, not a certified/ISP-comparable figure.";
    public string SpeedTestStatusText { get => _speedTestStatusText; private set => SetProperty(ref _speedTestStatusText, value); }

    private SpeedTestResult? _lastDownloadResult;
    public SpeedTestResult? LastDownloadResult { get => _lastDownloadResult; private set => SetProperty(ref _lastDownloadResult, value); }

    private SpeedTestResult? _lastUploadResult;
    public SpeedTestResult? LastUploadResult { get => _lastUploadResult; private set => SetProperty(ref _lastUploadResult, value); }

    // #579's "small history list" - persisted across restarts (NetworkTestHistoryService), also
    // where #580/#581's own results land, since all three are the same "on-demand network test"
    // family sharing one small feed.
    public ObservableCollection<NetworkTestHistoryEntry> NetworkTestHistory { get; } = new();

    public AsyncRelayCommand RunDownloadTestCommand { get; }
    public AsyncRelayCommand RunUploadTestCommand { get; }

    // #580: bufferbloat / latency-under-load grade - runs the same ICMP ping #501's monitor uses,
    // continuously, first idle then while #579's download saturates the link.
    private string _bufferbloatPingHost = "1.1.1.1";
    public string BufferbloatPingHost { get => _bufferbloatPingHost; set => SetProperty(ref _bufferbloatPingHost, value); }

    private bool _isRunningBufferbloatTest;
    public bool IsRunningBufferbloatTest { get => _isRunningBufferbloatTest; private set => SetProperty(ref _isRunningBufferbloatTest, value); }

    private string _bufferbloatStatusText = "Measures how much your ping to a public host rises while a download saturates the link - the classic \"video calls stutter when someone else is downloading\" test.";
    public string BufferbloatStatusText { get => _bufferbloatStatusText; private set => SetProperty(ref _bufferbloatStatusText, value); }

    private BufferbloatResult? _bufferbloatResult;
    public BufferbloatResult? BufferbloatResult { get => _bufferbloatResult; private set => SetProperty(ref _bufferbloatResult, value); }

    public AsyncRelayCommand RunBufferbloatTestCommand { get; }

    // #581: LAN throughput test - either a large SMB-share read or a raw TCP stream to a listener
    // on another machine, so a slow #579 internet result can be separated from a slow LAN/Wi-Fi
    // link.
    private string _lanTestSmbPath = string.Empty;
    public string LanTestSmbPath { get => _lanTestSmbPath; set => SetProperty(ref _lanTestSmbPath, value); }

    private string _lanTestTcpTarget = string.Empty;
    public string LanTestTcpTarget { get => _lanTestTcpTarget; set => SetProperty(ref _lanTestTcpTarget, value); }

    private bool _isRunningLanTest;
    public bool IsRunningLanTest { get => _isRunningLanTest; private set => SetProperty(ref _isRunningLanTest, value); }

    private string _lanTestStatusText = "Tests throughput to something on your own local network, separate from the internet-facing speed test above.";
    public string LanTestStatusText { get => _lanTestStatusText; private set => SetProperty(ref _lanTestStatusText, value); }

    private LanThroughputResult? _lastLanTestResult;
    public LanThroughputResult? LastLanTestResult { get => _lastLanTestResult; private set => SetProperty(ref _lastLanTestResult, value); }

    public AsyncRelayCommand RunLanSmbTestCommand { get; }
    public AsyncRelayCommand RunLanTcpTestCommand { get; }

    // #582: true per-process bandwidth via a short, explicit ETW capture on the
    // Microsoft-Windows-Kernel-Network provider - see ProcessBandwidthEtwService's remarks for why
    // this is the one place on this tab a raw ETW dependency is justified. New section above the
    // existing #87 top-processes-by-connection-count list.
    private readonly ProcessBandwidthEtwService _etwBandwidth = new();
    private readonly DispatcherTimer _etwElapsedTimer;

    private bool _isEtwCapturing;
    public bool IsEtwCapturing { get => _isEtwCapturing; private set => SetProperty(ref _isEtwCapturing, value); }

    private string _etwCaptureStatusText = "Not capturing. Real per-process byte counts, unlike the connection-count list below - see that card's own caveat.";
    public string EtwCaptureStatusText { get => _etwCaptureStatusText; private set => SetProperty(ref _etwCaptureStatusText, value); }

    public ObservableCollection<EtwProcessBandwidth> EtwBandwidthResults { get; } = new();

    public RelayCommand StartEtwCaptureCommand { get; }
    public RelayCommand StopEtwCaptureCommand { get; }

    // #584: link-utilization gauge - current combined throughput as a percent of the primary
    // adapter's negotiated link speed, recomputed every time the shared Performance sampler
    // produces a fresh throughput figure (its own 1s tick) rather than this tab's own slower one.
    private AdapterUtilizationInfo? _linkUtilization;
    public AdapterUtilizationInfo? LinkUtilization { get => _linkUtilization; private set => SetProperty(ref _linkUtilization, value); }

    // #585: adapter-throughput reconciliation - flags traffic flowing over an adapter other than
    // the one the user believes is active. Recomputed on this tab's own 15s tick (see
    // AdapterTrafficService's remarks for why it needs a previous-sample delta rather than being a
    // pure function like #584 above).
    private readonly AdapterTrafficService _adapterTraffic = new();

    private string? _adapterTrafficFlagText;
    public string? AdapterTrafficFlagText { get => _adapterTrafficFlagText; private set => SetProperty(ref _adapterTrafficFlagText, value); }

    // ---- suggestions.md #586-590: SMB and network drives -------------------------------------------
    // New "Network drives" card - "the PC is slow" very often means one unresponsive network share
    // silently stalling Explorer, a file dialog, or a save. #586 rides the existing 15s tick (cheap
    // perf-counter read); #587/#588/#590 load together behind one manual Refresh (a `net use` shell,
    // a second WMI namespace, and a service-status read are all more than a per-tick-cheap read, but
    // still far lighter than an event-log scan) plus once at startup, the same "queried once, plus
    // an explicit Refresh" shape #563's port-reservation read already uses; #589 is its own on-demand
    // scan with its own lookback window, mirroring #524/#530/#541's own event-log scans elsewhere on
    // this tab.

    // #586: per-connected-share latency/queue-depth/throughput.
    public ObservableCollection<SmbShareLatency> SmbShareLatencies { get; } = new();

    // #587: mapped-drive inventory (HKCU\Network + live `net use`), with an on-demand reachability
    // test and a confirm-first Disconnect action.
    public ObservableCollection<MappedDriveInfo> MappedDrives { get; } = new();

    private bool _isLoadingNetworkDrives;
    public bool IsLoadingNetworkDrives { get => _isLoadingNetworkDrives; private set => SetProperty(ref _isLoadingNetworkDrives, value); }

    private string _networkDrivesStatusText = "Not loaded yet.";
    public string NetworkDrivesStatusText { get => _networkDrivesStatusText; private set => SetProperty(ref _networkDrivesStatusText, value); }

    public AsyncRelayCommand RefreshNetworkDrivesCommand { get; }
    public AsyncRelayCommand TestDriveReachabilityCommand { get; }
    public AsyncRelayCommand DisconnectMappedDriveCommand { get; }

    // #588: negotiated dialect/signing/encryption per server connection - loaded together with
    // #587's own Refresh above (same MSFT_SmbConnection-namespace read, no extra button).
    public ObservableCollection<SmbConnectionInfo> SmbConnections { get; } = new();

    // #589: on-demand SMBClient event scan (Connectivity + Operational channels) - its own lookback
    // window, mirroring this tab's other on-demand event-log scans.
    private double _smbEventScanWindowHours = 24.0;
    public double SmbEventScanWindowHours { get => _smbEventScanWindowHours; set => SetProperty(ref _smbEventScanWindowHours, Math.Clamp(value, 1.0, 720.0)); }

    private bool _isScanningSmbEvents;
    public bool IsScanningSmbEvents { get => _isScanningSmbEvents; private set => SetProperty(ref _isScanningSmbEvents, value); }

    private string _smbEventScanStatusText = "Not scanned yet - the SMBClient Connectivity/Operational logs are disabled by default on most machines.";
    public string SmbEventScanStatusText { get => _smbEventScanStatusText; private set => SetProperty(ref _smbEventScanStatusText, value); }

    public ObservableCollection<SmbClientEvent> SmbClientEvents { get; } = new();
    public AsyncRelayCommand ScanSmbEventsCommand { get; }

    // #590: Offline Files (CSC) state - null hides the whole section (OfflineFilesState.IsInUse is
    // false), per this item's own "hidden when Offline Files is not in use" text. Property named
    // OfflineFiles (not OfflineFilesState) to avoid shadowing the OfflineFilesState type itself
    // inside this class's own method bodies.
    private OfflineFilesState? _offlineFiles;
    public OfflineFilesState? OfflineFiles { get => _offlineFiles; private set => SetProperty(ref _offlineFiles, value); }

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

        // #48/#556: one-time driver read plus a Driver Store sweep, matched together - neither can
        // change without a reinstall/reboot this app would need a restart to see anyway, same
        // "queried once" tradeoff AdapterDrivers already took before #556 extended it.
        _ = Task.Run(async () =>
        {
            var drivers = NetworkDiagnosticsService.ReadAdapterDriverInfo();
            _allStagedNetDrivers = await AdapterDriverStoreService.ReadNetDriverPackagesAsync();
            var annotated = drivers.Select(d => d with { StagedPackages = AdapterDriverStoreService.MatchToAdapter(_allStagedNetDrivers, d) }).ToList();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var d in annotated) AdapterDrivers.Add(d);
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

        // #536-546: Wi-Fi diagnostics wiring. _wifiSignalMonitor itself is started/stopped from
        // CheckConnectivityAsync as the existing Wifi (netsh) association check comes and goes -
        // see RefreshWifiRadioMonitorState's remarks.
        _wifiSignalMonitor = new WifiSignalMonitorService(_wlan);
        _wifiSignalMonitor.CycleCompleted += OnWifiSignalCycleCompleted;

        WifiHiddenXAxes = new[] { new Axis { IsVisible = false, ShowSeparatorLines = false } };
        WifiRssiYAxes = new[]
        {
            new Axis { Labeler = v => $"{v:0} dBm", LabelsPaint = LatencyAxisTextPaint(), SeparatorsPaint = LatencyAxisSeparatorPaint() },
        };
        WifiRateYAxes = new[]
        {
            new Axis { MinLimit = 0, Labeler = v => $"{v:0} Mbps", LabelsPaint = LatencyAxisTextPaint(), SeparatorsPaint = LatencyAxisSeparatorPaint() },
        };

        (_wifiRssiGlow, _wifiRssiCore) = LatencyLineOf(WifiRssiHistory, SKColors.DeepSkyBlue, "RSSI (dBm)");
        WifiRssiSeries = new ISeries[] { _wifiRssiGlow, _wifiRssiCore };
        (_wifiRateGlow, _wifiRateCore) = LatencyLineOf(WifiRxRateHistory, SKColors.LimeGreen, "Rx rate (Mbps)");
        WifiRateSeries = new ISeries[] { _wifiRateGlow, _wifiRateCore };

        RunAirspaceScanCommand = new AsyncRelayCommand(RunAirspaceScanAsync, () => !IsScanningAirspace);
        ScanWifiEventsCommand = new AsyncRelayCommand(ScanWifiEventsAsync, () => !IsScanningWifiEvents);
        RunWlanReportCommand = new AsyncRelayCommand(RunWlanReportAsync, () => !IsRunningWlanReport);
        RefreshWifiProfilesCommand = new AsyncRelayCommand(RefreshWifiProfilesAsync, () => !IsLoadingWifiProfiles);
        DeleteWifiProfileCommand = new AsyncRelayCommand(param => DeleteWifiProfileAsync(param as WifiProfileAudit), _ => !IsLoadingWifiProfiles);

        // #547-556: Adapter health card wiring.
        ScanLinkFlapsCommand = new AsyncRelayCommand(ScanLinkFlapsAsync, () => !IsScanningLinkFlaps);
        RestartAdapterCommand = new RelayCommand(RestartSelectedAdapter, () => !string.IsNullOrWhiteSpace(SelectedHealthAdapterName) && !IsRestartingAdapter);
        OpenDeviceManagerCommand = new RelayCommand(_ => OpenDeviceManager());

        // #552/#565: one-time machine-wide TCP offload/tuning read - see TcpGlobalSettings' remarks.
        _ = Task.Run(async () =>
        {
            var tcp = await TcpGlobalSettingsService.ReadAsync();
            System.Windows.Application.Current?.Dispatcher.Invoke(() => TcpGlobalSettings = tcp);
        });

        // #557-565: TCP health / Ports card wiring.
        (_tcpRetransmitGlow, _tcpRetransmitCore) = LatencyLineOf(TcpRetransmitRateHistory, SKColors.OrangeRed, "Retransmit rate (IPv4)");
        TcpRetransmitSeries = new ISeries[] { _tcpRetransmitGlow, _tcpRetransmitCore };
        TcpRetransmitPercentYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0.0}%",
                LabelsPaint = LatencyAxisTextPaint(),
                SeparatorsPaint = LatencyAxisSeparatorPaint(),
            },
        };

        LookupPortCommand = new AsyncRelayCommand(LookupPortAsync, () => !IsLookingUpPort && !string.IsNullOrWhiteSpace(PortLookupQuery));
        EndPortLookupProcessCommand = new AsyncRelayCommand(param => EndPortLookupProcessAsync(param as PortLookupResult), _ => !IsLookingUpPort);

        ShowTcpConnectionsCommand = new RelayCommand(_ => ShowUdpConnections = false);
        ShowUdpConnectionsCommand = new RelayCommand(_ => ShowUdpConnections = true);

        RefreshPortReservationCommand = new AsyncRelayCommand(RefreshPortReservationAsync, () => !IsLoadingPortReservation);
        _ = RefreshPortReservationAsync();

        // #566-571: Firewall card wiring. All on-demand - none of this runs until the user opens
        // the card and clicks something, per CLAUDE.md's on-demand-vs-polled convention.
        RefreshFirewallProfilesCommand = new AsyncRelayCommand(RefreshFirewallProfilesAsync, () => !IsLoadingFirewallProfiles);
        RefreshFirewallRulesCommand = new AsyncRelayCommand(RefreshFirewallRulesAsync, () => !IsLoadingFirewallRules);
        RefreshFirewallLogCommand = new AsyncRelayCommand(RefreshFirewallLogAsync, () => !IsLoadingFirewallLog);
        EnableDroppedLoggingCommand = new AsyncRelayCommand(EnableDroppedLoggingAsync, () => !IsLoadingFirewallLog);
        EnableWfpAuditingCommand = new AsyncRelayCommand(EnableWfpAuditingAsync, () => !IsEnablingWfpAuditing);
        ScanWfpDropsCommand = new AsyncRelayCommand(ScanWfpDropsAsync, () => !IsScanningWfpDrops);
        RefreshStackInventoryCommand = new AsyncRelayCommand(RefreshStackInventoryAsync, () => !IsLoadingStackInventory);
        RunWizardCommand = new AsyncRelayCommand(RunWizardAsync, () => !IsRunningWizard && !string.IsNullOrWhiteSpace(WizardQuery));
        _ = RefreshFirewallProfilesAsync();

        // #572-575: Proxy and Stack card wiring.
        RefreshProxyCardCommand = new AsyncRelayCommand(RefreshProxyCardAsync, () => !IsLoadingProxyCard);
        TestBypassCommand = new RelayCommand(_ => TestBypass());
        RefreshWinsockCatalogCommand = new AsyncRelayCommand(RefreshWinsockCatalogAsync, () => !IsLoadingWinsockCatalog);
        ResetWinsockCommand = new RelayCommand(ConfirmAndResetWinsock, () => !IsResettingWinsock);
        _ = RefreshProxyCardAsync();
        _ = RefreshWinsockCatalogAsync();

        // #576: reset toolkit - each command routes through RunStackResetAction's shared
        // confirm-then-run-then-log plumbing with its own action-specific warning text.
        ResetIpStackCommand = new RelayCommand(_ => RunStackResetAction(
            "Reset TCP/IP stack",
            "Resets the TCP/IP stack to its installation defaults (`netsh int ip reset`). Fixes a corrupted Winsock/TCP-IP registry configuration, but wipes any custom IP settings, static routes, and some third-party network filter registrations. A restart is recommended afterward.",
            NetworkStackResetService.ResetIpStackAsync));
        FlushDnsResolverCacheCommand = new RelayCommand(_ => RunStackResetAction(
            "Flush DNS resolver cache",
            "Clears the local DNS resolver cache (`ipconfig /flushdns`). Safe and low-impact - the next lookup for any host is just slightly slower while the cache refills. Fixes a stale/poisoned cached DNS record.",
            NetworkStackResetService.FlushDnsAsync));
        ClearArpCacheCommand = new RelayCommand(_ => RunStackResetAction(
            "Clear ARP cache",
            "Clears the cached IP-to-MAC address mappings for the local network (`arp -d *`). Safe - Windows rebuilds it automatically as needed, with a brief delay on the next connection to each host. Fixes a stale ARP entry (e.g. after a device's network card was replaced).",
            NetworkStackResetService.ClearArpCacheAsync));
        ResetNetBiosCacheCommand = new RelayCommand(_ => RunStackResetAction(
            "Reset NetBIOS name cache",
            "Purges and reloads the NetBIOS name cache (`nbtstat -R`). Safe - only affects NetBIOS name resolution (mostly legacy Windows file-sharing/`\\\\computername` lookups), rebuilt automatically. Fixes a stale cached NetBIOS name-to-address mapping.",
            NetworkStackResetService.ResetNetBiosCacheAsync));

        // #577: VPN default-route / DNS-leak check wiring.
        RunVpnTunnelCheckCommand = new AsyncRelayCommand(RunVpnTunnelCheckAsync, () => !IsCheckingVpnTunnel);

        // #578: orphaned virtual adapter scan wiring.
        ScanOrphanedAdaptersCommand = new AsyncRelayCommand(ScanOrphanedAdaptersAsync, () => !IsScanningOrphanedAdapters);

        // #579-581: on-demand speed/bufferbloat/LAN test wiring.
        RunDownloadTestCommand = new AsyncRelayCommand(RunDownloadTestAsync, () => !IsRunningSpeedTest && !string.IsNullOrWhiteSpace(SpeedTestDownloadUrl));
        RunUploadTestCommand = new AsyncRelayCommand(RunUploadTestAsync, () => !IsRunningSpeedTest && !string.IsNullOrWhiteSpace(SpeedTestUploadUrl));
        RunBufferbloatTestCommand = new AsyncRelayCommand(RunBufferbloatTestAsync, () => !IsRunningBufferbloatTest);
        RunLanSmbTestCommand = new AsyncRelayCommand(RunLanSmbTestAsync, () => !IsRunningLanTest && !string.IsNullOrWhiteSpace(LanTestSmbPath));
        RunLanTcpTestCommand = new AsyncRelayCommand(RunLanTcpTestAsync, () => !IsRunningLanTest && !string.IsNullOrWhiteSpace(LanTestTcpTarget));

        foreach (var entry in NetworkTestHistoryService.Load().Take(20)) NetworkTestHistory.Add(entry);

        // #582: ETW capture start/stop wiring, plus a 1s "N seconds elapsed" ticker that only runs
        // while a capture is actually in progress - a lightweight, dedicated timer rather than
        // reusing this class's own 15s tick, since a capture-in-progress indicator needs to feel
        // live.
        StartEtwCaptureCommand = new RelayCommand(StartEtwCapture, () => !IsEtwCapturing);
        StopEtwCaptureCommand = new RelayCommand(StopEtwCapture, () => IsEtwCapturing);
        _etwElapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _etwElapsedTimer.Tick += (_, _) =>
        {
            if (_etwBandwidth.CaptureStartedUtc is { } startedAt)
                EtwCaptureStatusText = $"Capture running - {(DateTime.UtcNow - startedAt).TotalSeconds:0}s elapsed. Click Stop to see per-process results.";
        };

        // #584: recompute the link-utilization gauge every time the shared sampler produces a
        // fresh throughput figure, rather than on this tab's own slower tick.
        Performance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PerformanceViewModel.NetworkReceiveBps) or nameof(PerformanceViewModel.NetworkSendBps))
                RecomputeLinkUtilization();
        };
        RecomputeLinkUtilization();

        // #586-590: Network drives card wiring - one manual Refresh for #587/#588/#590 (also run
        // once at startup, the same "queried once, plus an explicit Refresh" shape #563's
        // port-reservation read already uses), plus #589's own on-demand event scan.
        RefreshNetworkDrivesCommand = new AsyncRelayCommand(RefreshNetworkDrivesAsync, () => !IsLoadingNetworkDrives);
        TestDriveReachabilityCommand = new AsyncRelayCommand(TestDriveReachabilityAsync);
        DisconnectMappedDriveCommand = new AsyncRelayCommand(DisconnectMappedDriveAsync);
        ScanSmbEventsCommand = new AsyncRelayCommand(ScanSmbEventsAsync, () => !IsScanningSmbEvents);
        _ = RefreshNetworkDrivesAsync();
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

            // #547-556: Adapter health card - see RefreshAdapterHealth's remarks for why this
            // only does registry/driver-store work for adapters this session hasn't already built a
            // row for, and always refreshes #547's error-delta fields regardless.
            await Task.Run(RefreshAdapterHealth);

            var vpnAdapters = NetworkDiagnosticsService.ReadActiveVpnAdapterNames();
            HasActiveVpn = vpnAdapters.Count > 0;
            VpnStatusText = HasActiveVpn ? string.Join(", ", vpnAdapters) : "None detected";

            var connections = await Task.Run(() => NetworkConnectionsService.Sample());
            // #525: reapply whatever's already cached from a previous "Resolve names" click - no
            // I/O, so this is safe on every 15s refresh even though the fresh names themselves
            // only come from an explicit user action.
            ReverseDnsService.ApplyCached(connections);
            // #559: annotate SYN_SENT rows with how long they've persisted across polls, before
            // the snapshot is sorted and handed to the grid below.
            _synSentTracker.Annotate(connections);
            Connections.Clear();
            foreach (var c in connections.OrderByDescending(c => c.State == "ESTABLISHED").ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
                Connections.Add(c);

            // #558: state histogram - derived entirely from the sample just taken, no extra I/O.
            var histogram = NetworkConnectionsService.BuildStateHistogram(connections);
            ConnectionStateHistogram.Clear();
            foreach (var h in histogram) ConnectionStateHistogram.Add(h);

            // #559: one combined status line for whatever's currently stuck.
            var stalled = connections.Where(c => c.IsStalledSynSent).ToList();
            StalledSynStatusText = stalled.Count == 0
                ? "No SYN_SENT connection has been stuck long enough to flag."
                : $"{stalled.Count} outbound connection(s) stuck in SYN_SENT for {stalled.Min(c => c.SynSentAgeSeconds).GetValueOrDefault():0}s+ - " +
                  string.Join(", ", stalled.Take(3).Select(c => $"{c.ProcessName} → {c.RemoteAddress}:{c.RemotePort}")) +
                  (stalled.Count > 3 ? $", and {stalled.Count - 3} more" : string.Empty) +
                  ". Often a firewall, proxy, or dead route silently swallowing outbound traffic.";

            // #561: UDP table alongside the existing TCP-only table above.
            var udpConnections = await Task.Run(() => NetworkConnectionsService.SampleUdp());
            UdpConnections.Clear();
            foreach (var u2 in udpConnections.OrderBy(u2 => u2.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(u2 => u2.LocalPort))
                UdpConnections.Add(u2);

            // #557: TCP-layer retransmit/reset health (IPv4 + IPv6).
            var tcpHealth = await Task.Run(() => _tcpHealthCounters.Sample());
            TcpHealthSamples.Clear();
            foreach (var h in tcpHealth) TcpHealthSamples.Add(h);

            var ipv4Health = tcpHealth.FirstOrDefault(h => h.AddressFamily == "IPv4");
            double retransmitPercent = ipv4Health is { IsAvailable: true } ? ipv4Health.RetransmitRatePercent : 0;
            TcpRetransmitRateHistory.Add(retransmitPercent);
            if (TcpRetransmitRateHistory.Count > LatencyHistoryLength) TcpRetransmitRateHistory.RemoveAt(0);
            TcpHealthStatusText = ipv4Health is not { IsAvailable: true }
                ? "TCP health counters unavailable on this machine."
                : retransmitPercent > RetransmitRateFlagPercent
                    ? $"IPv4 retransmit rate {retransmitPercent:0.0}% of segments sent - above a couple of percent is the cleanest objective evidence of a lossy path."
                    : $"IPv4 retransmit rate {retransmitPercent:0.0}% of segments sent - within normal range.";

            // #564: recompute now that the TCP/UDP tables above are fresh - a no-op (stays null)
            // until #563 has read a dynamic port range at least once.
            RecomputePortExhaustion();

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
            RefreshWifiRadioMonitorState();

            // #585: adapter-throughput reconciliation - cheap NetworkInterface reads, rides this
            // tick rather than getting its own timer.
            var reconciliation = await Task.Run(() => _adapterTraffic.ComputeReconciliation());
            AdapterTrafficFlagText = reconciliation.FlagText;

            // #586: per-connected-share latency/queue-depth - a single perf-counter WMI query,
            // cheap enough to ride this tick alongside everything else above.
            var shareLatencies = await Task.Run(SmbShareService.ReadShareLatencies);
            SmbShareLatencies.Clear();
            foreach (var s in shareLatencies) SmbShareLatencies.Add(s);
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

    /// <summary>
    /// #547-556: builds/refreshes the Adapter health card. Called via Task.Run from
    /// CheckConnectivityAsync above, so this runs on a background thread - every
    /// ObservableCollection/property mutation below is marshaled back to the UI thread through
    /// Dispatcher.Invoke, the same pattern the one-time #48 AdapterDrivers read already uses.
    ///
    /// A brand-new adapter (not yet represented by an AdapterHealthRow) gets its one-time
    /// registry/USB-cross-reference facts (#549-553) read here, once - see AdapterHealthRow's
    /// remarks for why those fields are never reassigned afterward, which is what keeps this card's
    /// per-adapter "Advanced properties" Expander from collapsing every 15s. Every adapter, new or
    /// already-known, gets its #547 error-delta fields refreshed on every call (the one genuinely
    /// per-tick part of this card), and #554's quality score is recomputed from whatever's now known.
    /// </summary>
    private void RefreshAdapterHealth()
    {
        // #547: always sample - cheap NetworkInterface counter reads, no registry/shell-out involved.
        var errorSamples = _adapterErrorCounters.Sample();
        var errorsByName = errorSamples.ToDictionary(e => e.AdapterName, e => e, StringComparer.OrdinalIgnoreCase);

        var existingNames = new HashSet<string>(
            System.Windows.Application.Current?.Dispatcher.Invoke(() => AdapterHealth.Select(r => r.AdapterName).ToList())
                ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var activeAdapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();

        var newAdapters = activeAdapters.Where(ni => !existingNames.Contains(ni.Name)).ToList();
        var newRows = new List<AdapterHealthRow>();
        if (newAdapters.Count > 0)
        {
            // Only newly-seen adapters need these heavier one-time reads - a WMI sweep plus a
            // handful of registry key opens per adapter.
            var pnpIdsByGuid = AdapterUsbLookupService.ReadPnpDeviceIdsByGuid();
            var usbDevices = UsbPowerService.ReadUsbSelectiveSuspend();
            foreach (var ni in newAdapters)
                newRows.Add(BuildAdapterHealthRow(ni, pnpIdsByGuid, usbDevices));
        }

        var currentNames = new HashSet<string>(activeAdapters.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            // Drop rows for adapters that disappeared (renamed, unplugged, disabled).
            for (int i = AdapterHealth.Count - 1; i >= 0; i--)
                if (!currentNames.Contains(AdapterHealth[i].AdapterName)) AdapterHealth.RemoveAt(i);

            foreach (var row in newRows) AdapterHealth.Add(row);

            // #547: refresh every row's error-delta fields in place - this is what keeps each row's
            // object identity (and any expanded Expander) intact across this 15s tick.
            foreach (var row in AdapterHealth)
            {
                if (!errorsByName.TryGetValue(row.AdapterName, out var e)) continue;
                row.RxErrorsDelta = e.ReceivedErrorsDelta;
                row.TxErrorsDelta = e.OutboundErrorsDelta;
                row.RxDiscardsDelta = e.ReceivedDiscardsDelta;
                row.TxDiscardsDelta = e.OutboundDiscardsDelta;
                row.HasCurrentErrorRate = e.HasNonZeroRate;
                if (e.HasNonZeroRate) row.HasEverHadErrorsThisSession = true;
            }

            RecomputeLinkQuality();
        });
    }

    /// <summary>The one-time (per adapter, per session) half of RefreshAdapterHealth -
    /// everything a freshly-discovered adapter needs read once: #549's full advanced-property
    /// enumeration, #552's offload subset of it, #550's known-problem flags (including the USB
    /// Selective Suspend cross-reference via AdapterUsbLookupService, only attempted when the
    /// adapter's own PNPDeviceID actually starts with "USB"), and #551's power-management read.</summary>
    private static AdapterHealthRow BuildAdapterHealthRow(
        NetworkInterface ni, Dictionary<string, string> pnpIdsByGuid, List<UsbDevicePowerInfo> usbDevices)
    {
        var properties = AdapterAdvancedPropertyService.ReadAll(ni.Id);
        var offload = AdapterAdvancedPropertyService.FilterOffloadRelated(properties);

        string wantedGuid = ni.Id.Trim('{', '}');
        pnpIdsByGuid.TryGetValue(wantedGuid, out var pnpDeviceId);
        bool isUsbAdapter = pnpDeviceId is not null && pnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase);
        bool? selectiveSuspend = isUsbAdapter ? AdapterUsbLookupService.FindSelectiveSuspend(usbDevices, pnpDeviceId) : null;

        var problems = AdapterAdvancedPropertyService.DetectKnownProblems(properties, selectiveSuspend);
        var power = AdapterPowerManagementService.Read(ni.Id);

        return new AdapterHealthRow
        {
            AdapterName = ni.Name,
            AdvancedProperties = properties,
            OffloadProperties = offload,
            ProblemFlags = problems,
            PowerManagement = power,
        };
    }

    /// <summary>
    /// #554: combines #548's link-state transitions with #547's error-counter rate into one
    /// per-adapter "clean / marginal / bad" headline, over a rolling window - deliberately labelled
    /// a heuristic (the same "quick flag, not a verdict" framing this app's other pattern-matched
    /// indicators use), not a hard measurement. Called both after every #547 sample
    /// (RefreshAdapterHealth above) and after a #548 scan completes (ScanLinkFlapsAsync below),
    /// since either input changing should move the badge. Must run on the UI thread (it touches
    /// AdapterHealth's items directly) - both call sites already guarantee that via
    /// Dispatcher.Invoke.
    /// </summary>
    private void RecomputeLinkQuality()
    {
        foreach (var row in AdapterHealth)
        {
            _linkFlapCountsByAdapter.TryGetValue(row.AdapterName, out int flapCount);

            if (row.HasCurrentErrorRate || flapCount >= 3)
            {
                row.LinkQualityLabel = "Bad";
                row.LinkQualityReason = row.HasCurrentErrorRate
                    ? "Currently seeing non-zero packet errors/discards."
                    : $"{flapCount} link-state change(s) in the last scanned window.";
            }
            else if (row.HasEverHadErrorsThisSession || flapCount >= 1)
            {
                row.LinkQualityLabel = "Marginal";
                row.LinkQualityReason = row.HasEverHadErrorsThisSession
                    ? "A packet error/discard was seen earlier this session (currently clear)."
                    : $"{flapCount} link-state change in the last scanned window.";
            }
            else
            {
                row.LinkQualityLabel = "Clean";
                row.LinkQualityReason = _linkFlapCountsByAdapter.Count == 0
                    ? "No errors seen so far. Run a link-flap scan below for the full picture."
                    : "No errors or link-state changes seen.";
            }
        }
    }

    /// <summary>#548: on-demand System-log link-state/reset scan - see LinkFlapEventLogService's
    /// remarks for why this is EventID-filtered rather than provider-filtered. After a successful
    /// scan, best-effort attributes each event to an adapter by matching its AdapterHint (or, when
    /// that field wasn't present in the event's own data, its raw message text) against every known
    /// adapter's Name/Description, then feeds those per-adapter counts into #554's quality score.
    /// Unattributed events still count toward the total shown in the status text, just not toward
    /// any specific adapter's headline - honest under-attribution rather than a guessed owner.</summary>
    private async Task ScanLinkFlapsAsync()
    {
        if (IsScanningLinkFlaps) return;
        IsScanningLinkFlaps = true;
        LinkFlapScanStatusText = "Scanning...";
        try
        {
            var window = TimeSpan.FromHours(LinkFlapScanWindowHours);
            var result = await Task.Run(() => LinkFlapEventLogService.Scan(window));

            var adapterDescriptions = NetworkInterface.GetAllNetworkInterfaces()
                .Select(ni => (ni.Name, ni.Description))
                .ToList();

            _linkFlapCountsByAdapter.Clear();
            foreach (var e in result.Events)
            {
                string? matchedName = AttributeEventToAdapter(e, adapterDescriptions);
                if (matchedName is null) continue;
                _linkFlapCountsByAdapter[matchedName] = _linkFlapCountsByAdapter.GetValueOrDefault(matchedName) + 1;
            }

            LinkFlapEvents.Clear();
            foreach (var e in result.Events) LinkFlapEvents.Add(e);

            LinkFlapScanStatusText = !result.ChannelAvailable
                ? "The System log couldn't be read."
                : result.Events.Count == 0
                    ? $"No link-state events in the last {LinkFlapScanWindowHours:0.#}h - clean."
                    : $"{result.Events.Count} event(s) ({result.ResetCount} look like a reset/disconnect) in the last {LinkFlapScanWindowHours:0.#}h.";

            RecomputeLinkQuality();
        }
        catch (Exception ex)
        {
            LinkFlapScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningLinkFlaps = false;
        }
    }

    private static string? AttributeEventToAdapter(LinkFlapEvent e, List<(string Name, string Description)> adapters)
    {
        string haystack = $"{e.AdapterHint} {e.Message}";
        foreach (var (name, description) in adapters)
        {
            if (description.Length > 3 && haystack.Contains(description, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    /// <summary>#555: "Restart this adapter" - behind the same MessageBox.Show confirm-first
    /// pattern ReleaseSelectedAdapter/RenewAdapter already use for their own connection-dropping
    /// actions, since bouncing admin state briefly drops whatever's using this adapter.</summary>
    private void RestartSelectedAdapter()
    {
        string? target = SelectedHealthAdapterName;
        if (string.IsNullOrWhiteSpace(target)) return;

        var confirm = MessageBox.Show(
            $"Restart the adapter \"{target}\"?\nThis disables then re-enables it, which briefly drops its connection.",
            "Restart adapter", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunRestartAdapterAsync(target);
    }

    private async Task RunRestartAdapterAsync(string adapterName)
    {
        if (IsRestartingAdapter) return;
        IsRestartingAdapter = true;
        RestartAdapterStatusText = $"Restarting \"{adapterName}\"...";
        try
        {
            string output = await AdapterRestartService.RestartAsync(adapterName);
            RestartAdapterStatusText = output;
        }
        catch (Exception ex)
        {
            RestartAdapterStatusText = $"Restart failed: {ex.Message}";
        }
        finally
        {
            IsRestartingAdapter = false;
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

    /// <summary>#551: opens Device Manager - there's no documented switch/URI to deep-link straight
    /// to a specific device's Power Management tab, so this just gets the user to the right tool
    /// (they still pick the adapter and tab themselves), the same "known tool over nothing" shape
    /// OpenHostsFile above already takes for a different destination.</summary>
    private static void OpenDeviceManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - if it can't launch there's nothing more useful this app can do here.
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

    // ---- #557-565 helpers ---------------------------------------------------------------------

    /// <summary>#560: looks up whatever's currently in Connections/UdpConnections (no fresh
    /// network sample of its own - see this property group's remarks) for the entered port,
    /// enriching each match with its process path/start time off the UI thread since reading
    /// MainModule can briefly block.</summary>
    private async Task LookupPortAsync()
    {
        if (IsLookingUpPort) return;

        if (!int.TryParse(PortLookupQuery.Trim(), out int port) || port is <= 0 or > 65535)
        {
            PortLookupResults.Clear();
            PortLookupStatusText = "Enter a valid port number (1-65535).";
            return;
        }

        IsLookingUpPort = true;
        PortLookupStatusText = "Looking up...";
        try
        {
            var tcpSnapshot = Connections.ToList();
            var udpSnapshot = UdpConnections.ToList();
            var results = await Task.Run(() => NetworkConnectionsService.FindByPort(port, tcpSnapshot, udpSnapshot));

            PortLookupResults.Clear();
            foreach (var r in results) PortLookupResults.Add(r);

            PortLookupStatusText = results.Count == 0
                ? $"Nothing is using port {port} right now (checked TCP + UDP, both address families)."
                : $"{results.Count} match(es) for port {port}.";
        }
        catch (Exception ex)
        {
            PortLookupStatusText = $"Lookup failed: {ex.Message}";
        }
        finally
        {
            IsLookingUpPort = false;
        }
    }

    /// <summary>#560's "End process" action - same MessageBox.Show confirm-first pattern
    /// ProcessesViewModel.EndSelected/ReleaseSelectedAdapter already use for their own disruptive
    /// actions, reusing ProcessMonitorService.EndProcess rather than a second kill implementation.</summary>
    private async Task EndPortLookupProcessAsync(PortLookupResult? target)
    {
        if (target is null) return;

        var confirm = MessageBox.Show(
            $"End \"{target.ProcessName}\" (PID {target.Pid})?\nAny unsaved data in this process will be lost.",
            "End process", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = ProcessMonitorService.EndProcess(target.Pid);
        PortLookupStatusText = success
            ? $"Ended {target.ProcessName} (PID {target.Pid})."
            : $"Couldn't end {target.ProcessName}: {error}";

        if (success) await LookupPortAsync();
    }

    /// <summary>#563: reloads the excluded/dynamic port ranges via netsh, then recomputes #564's
    /// exhaustion figure (its dynamic-range input may have just changed).</summary>
    private async Task RefreshPortReservationAsync()
    {
        if (IsLoadingPortReservation) return;
        IsLoadingPortReservation = true;
        PortReservationStatusText = "Reading port ranges (netsh)...";
        try
        {
            PortReservation = await PortReservationService.ReadAsync();
            PortReservationStatusText = !PortReservation.CommandsSucceeded
                ? "Couldn't read port ranges from netsh."
                : PortReservation.OverlappingExclusions.Count == 0
                    ? $"{PortReservation.ExcludedRanges.Count} excluded range(s)" +
                      (PortReservation.DynamicRange is { } r ? $"; dynamic port range {r.StartPort}-{r.EndPort}." : ".") +
                      " No overlap with the dynamic range found."
                    : $"{PortReservation.OverlappingExclusions.Count} excluded range(s) overlap the dynamic port range - " +
                      "a common Hyper-V/WinNAT reservation bug that can make a port look free in netstat while an app still can't bind it.";
        }
        catch (Exception ex)
        {
            PortReservationStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsLoadingPortReservation = false;
            RecomputePortExhaustion();
        }
    }

    /// <summary>#564: recomputes the ephemeral-port utilization figure from whatever's currently in
    /// Connections/UdpConnections and PortReservation.DynamicRange - no I/O, safe to call after
    /// either input changes.</summary>
    private void RecomputePortExhaustion() =>
        PortExhaustion = PortReservationService.ComputeExhaustion(Connections, UdpConnections, PortReservation.DynamicRange);

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

    // ---- #536-546 helpers --------------------------------------------------------------------

    /// <summary>Starts/stops the #537 continuous sampler in step with the existing #23 Wifi
    /// (netsh) association check, and refreshes the #536/#540/#545 headline readouts from whatever
    /// the sampler has (or, on the very first tick after a fresh association, doesn't yet have -
    /// the row just stays hidden until the next cycle). Called once per 15s connectivity tick,
    /// which only gates start/stop; the readouts themselves update every ~2s via
    /// OnWifiSignalCycleCompleted.</summary>
    private void RefreshWifiRadioMonitorState()
    {
        if (Wifi is null)
        {
            if (_wifiSignalMonitor.IsRunning) _wifiSignalMonitor.Stop();
            WifiRadio = null;
            WifiPowerSaving = null;
            return;
        }

        if (!_wifiSignalMonitor.IsRunning) _wifiSignalMonitor.Start();
    }

    /// <summary>Fired on WifiSignalMonitorService's own background-loop thread - marshal to the UI
    /// thread before touching any bound property/collection, same contract every other
    /// CycleCompleted handler in this class follows.</summary>
    private void OnWifiSignalCycleCompleted() => System.Windows.Application.Current?.Dispatcher.Invoke(RefreshWifiSignalDisplay);

    private void RefreshWifiSignalDisplay()
    {
        var snapshot = _wifiSignalMonitor.GetLatestSnapshot();
        WifiRadio = snapshot;
        OnPropertyChanged(nameof(WifiRssiBandText));
        OnPropertyChanged(nameof(WifiSnrText));

        // #545: refreshed alongside the radio snapshot rather than on its own timer - a cheap
        // registry read, and the adapter it's scoped to only changes when the association itself
        // does.
        WifiPowerSaving = WifiPowerSavingService.Read(_wlan.GetConnectedInterfaceGuid());

        // #537: full rebuild from the rolling window rather than push-and-trim, so the roam
        // markers' X-indices always match the chart data 1:1 - see WifiRssiHistory's remarks.
        var window = _wifiSignalMonitor.GetWindow();
        WifiRssiHistory.Clear();
        WifiRxRateHistory.Clear();
        WifiRoamMarkers.Clear();
        for (int i = 0; i < window.Count; i++)
        {
            var sample = window[i];
            WifiRssiHistory.Add(sample.RssiDbm ?? -100); // -100 dBm reads as "no signal" - a floor, not a fabricated real reading
            WifiRxRateHistory.Add(sample.RxRateMbps ?? 0);
            if (sample.RoamedFromPrevious)
            {
                WifiRoamMarkers.Add(new RectangularSection
                {
                    Xi = i,
                    Xj = i,
                    Stroke = new SolidColorPaint(SKColors.OrangeRed, 2),
                    Label = "Roam",
                    LabelSize = 10,
                    LabelPaint = new SolidColorPaint(SKColors.OrangeRed),
                });
            }
        }
    }

    /// <summary>#538/#539/#546: on-demand active neighbour scan via WifiChannelScanService, plus
    /// the #539 overlap verdict and #540 band-steering hint derived from the same scan result -
    /// see that service's remarks for why this can't run on a timer.</summary>
    private async Task RunAirspaceScanAsync()
    {
        if (IsScanningAirspace) return;
        IsScanningAirspace = true;
        AirspaceStatusText = "Scanning - this briefly interrupts your own connection...";
        try
        {
            string? myBssid = WifiRadio?.Bssid;
            var result = await WifiChannelScanService.ScanAsync(myBssid);

            void Fill(ObservableCollection<WifiChannelOccupancy> target, string band)
            {
                target.Clear();
                foreach (var o in result.Occupancy.Where(o => o.Band == band)) target.Add(o);
            }
            Fill(AirspaceOccupancy24, "2.4 GHz");
            Fill(AirspaceOccupancy5, "5 GHz");
            Fill(AirspaceOccupancy6, "6 GHz");

            AirspaceRecommendation24 = WifiChannelScanService.RecommendChannelText(result.Occupancy, "2.4 GHz");
            AirspaceRecommendation5 = result.Occupancy.Any(o => o.Band == "5 GHz") ? WifiChannelScanService.RecommendChannelText(result.Occupancy, "5 GHz") : null;
            AirspaceRecommendation6 = result.Occupancy.Any(o => o.Band == "6 GHz") ? WifiChannelScanService.RecommendChannelText(result.Occupancy, "6 GHz") : null;

            // #539: overlap verdict for whatever channel this machine is actually on.
            WifiOverlapText = WifiChannelScanService.ComputeOverlapVerdict(result.Networks, (int?)WifiRadio?.Channel, myBssid);

            // #546: same-SSID BSSID groups.
            WifiMeshGroups.Clear();
            foreach (var g in WifiChannelScanService.GroupBySsid(result.Networks)) WifiMeshGroups.Add(g);

            // #540: band-steering suggestion - only meaningful when this machine is currently on
            // 2.4 GHz and the scan actually saw the same SSID with a usable signal elsewhere.
            WifiBandSteeringHintText = ComputeBandSteeringHint(result.Networks);

            AirspaceStatusText = $"Scanned {result.Networks.Count} BSSID(s) across {result.Networks.Select(n => n.Ssid).Distinct(StringComparer.OrdinalIgnoreCase).Count()} network(s) at {result.ScannedAtUtc:t} UTC.";
        }
        catch (Exception ex)
        {
            AirspaceStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningAirspace = false;
        }
    }

    /// <summary>#540: flags the common "stuck on a crowded 2.4 GHz channel while the same SSID is
    /// available on a cleaner band" case - worded as a suggestion to check band steering, not a
    /// verdict, since this app has no way to tell whether the client's own band preference,
    /// capability, or the AP's own steering policy is what actually kept it on 2.4 GHz.</summary>
    private static string? ComputeBandSteeringHint(List<WifiScanNetwork> networks)
    {
        var mine = networks.FirstOrDefault(n => n.IsCurrentBssid);
        if (mine is null || mine.Band != "2.4 GHz") return null;

        var betterBand = networks.FirstOrDefault(n =>
            string.Equals(n.Ssid, mine.Ssid, StringComparison.OrdinalIgnoreCase)
            && n.Band is "5 GHz" or "6 GHz"
            && (n.SignalPercent ?? 0) >= 50);
        if (betterBand is null) return null;

        return $"\"{mine.Ssid}\" is also visible on {betterBand.Band} (channel {betterBand.Channel}, {betterBand.SignalPercent}% signal) while this machine is associated on 2.4 GHz "
             + $"(channel {mine.Channel}, {mine.SignalPercent}% signal) - worth checking whether band steering is enabled on the AP. Quick flag, not a verdict.";
    }

    /// <summary>#541/#542: on-demand WLAN-AutoConfig event-log scan - see WifiEventLogService's
    /// remarks for the field-extraction/reason-decoding approach.</summary>
    private async Task ScanWifiEventsAsync()
    {
        if (IsScanningWifiEvents) return;
        IsScanningWifiEvents = true;
        WifiEventStatusText = "Scanning...";
        try
        {
            var window = TimeSpan.FromHours(WifiEventScanWindowHours);
            var result = await Task.Run(() => WifiEventLogService.Scan(window));

            WifiEvents.Clear();
            foreach (var e in result.Events) WifiEvents.Add(e);

            WifiEventStatusText = !result.ChannelAvailable
                ? "The WLAN-AutoConfig Operational log couldn't be read (access denied, or the channel is disabled/absent on this machine)."
                : result.Events.Count == 0
                    ? $"No connect/disconnect/roam events in the last {WifiEventScanWindowHours:0.#}h."
                    : $"{result.Events.Count} event(s) in the last {WifiEventScanWindowHours:0.#}h.";
        }
        catch (Exception ex)
        {
            WifiEventStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningWifiEvents = false;
        }
    }

    /// <summary>#543: one-click `netsh wlan show wlanreport`, then open the result.</summary>
    private async Task RunWlanReportAsync()
    {
        if (IsRunningWlanReport) return;
        IsRunningWlanReport = true;
        WlanReportStatusText = "Generating report...";
        try
        {
            var (_, message, _) = await WifiProfileService.RunWlanReportAsync();
            WlanReportStatusText = message;
        }
        catch (Exception ex)
        {
            WlanReportStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunningWlanReport = false;
        }
    }

    /// <summary>#544: on-demand saved-profile audit.</summary>
    private async Task RefreshWifiProfilesAsync()
    {
        if (IsLoadingWifiProfiles) return;
        IsLoadingWifiProfiles = true;
        WifiProfilesStatusText = "Loading...";
        try
        {
            var profiles = await WifiProfileService.ListProfilesAsync();
            WifiProfiles.Clear();
            foreach (var p in profiles) WifiProfiles.Add(p);

            int flagged = profiles.Count(p => p.IsHiddenSsid || p.IsWeakSecurity || p.AutoConnectsToOpenNetwork);
            WifiProfilesStatusText = profiles.Count == 0
                ? "No saved profiles found."
                : $"{profiles.Count} saved profile(s), {flagged} flagged.";
        }
        catch (Exception ex)
        {
            WifiProfilesStatusText = $"Couldn't load saved profiles: {ex.Message}";
        }
        finally
        {
            IsLoadingWifiProfiles = false;
        }
    }

    /// <summary>#544: delete one saved profile, behind an explicit Yes/No confirmation - same
    /// MessageBox.Show confirm-first pattern ReleaseSelectedAdapter/ProcessesViewModel.EndSelected
    /// already use for their own irreversible/disruptive actions.</summary>
    private async Task DeleteWifiProfileAsync(WifiProfileAudit? target)
    {
        if (target is null || IsLoadingWifiProfiles) return;

        var confirm = MessageBox.Show(
            $"Delete the saved Wi-Fi profile \"{target.Name}\"?\nWindows will forget this network's password and settings - you'll need to reconnect manually next time it's in range.",
            "Delete Wi-Fi profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsLoadingWifiProfiles = true;
        WifiProfilesStatusText = $"Deleting \"{target.Name}\"...";
        try
        {
            await WifiProfileService.DeleteProfileAsync(target.Name, target.InterfaceName);
        }
        catch (Exception ex)
        {
            WifiProfilesStatusText = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsLoadingWifiProfiles = false;
        }
        await RefreshWifiProfilesAsync();
    }

    // ---- #566-571 helpers (Firewall card) ------------------------------------------------------

    private async Task RefreshFirewallProfilesAsync()
    {
        if (IsLoadingFirewallProfiles) return;
        IsLoadingFirewallProfiles = true;
        try
        {
            var profiles = await FirewallService.ReadProfileStatusAsync();
            FirewallProfiles.Clear();
            foreach (var p in profiles) FirewallProfiles.Add(p);
        }
        catch
        {
            // Best-effort - leave whatever was already loaded.
        }
        finally
        {
            IsLoadingFirewallProfiles = false;
        }
    }

    /// <summary>#567: the on-demand rule sweep - hundreds of rules is common, so this is never
    /// called automatically, only from the card's own "Load rules" button.</summary>
    private async Task RefreshFirewallRulesAsync()
    {
        if (IsLoadingFirewallRules) return;
        IsLoadingFirewallRules = true;
        FirewallRulesStatusText = "Loading every firewall rule - this can take a few seconds...";
        try
        {
            _allFirewallRules = await FirewallService.ReadRulesAsync();
            ApplyFirewallRuleFilter();
            FirewallRulesStatusText = $"{_allFirewallRules.Count} rule(s) loaded.";
        }
        catch (Exception ex)
        {
            FirewallRulesStatusText = $"Couldn't load firewall rules: {ex.Message}";
        }
        finally
        {
            IsLoadingFirewallRules = false;
        }
    }

    private void ApplyFirewallRuleFilter()
    {
        var filtered = FirewallService.FilterRules(_allFirewallRules, FirewallRuleFilterText, FirewallFilterExecutableOnly);
        FilteredFirewallRules.Clear();
        foreach (var r in filtered) FilteredFirewallRules.Add(r);
    }

    /// <summary>#568: reads pfirewall.log's DROP entries.</summary>
    private async Task RefreshFirewallLogAsync()
    {
        if (IsLoadingFirewallLog) return;
        IsLoadingFirewallLog = true;
        FirewallLogStatusText = "Loading...";
        try
        {
            var result = await FirewallLogService.ReadDropEntriesAsync();
            FirewallLogEntries.Clear();
            foreach (var e in result.Entries) FirewallLogEntries.Add(e);
            FirewallLogStatusText = result.Message ?? $"{result.Entries.Count} DROP entr{(result.Entries.Count == 1 ? "y" : "ies")}.";
        }
        catch (Exception ex)
        {
            FirewallLogStatusText = $"Couldn't read the firewall log: {ex.Message}";
        }
        finally
        {
            IsLoadingFirewallLog = false;
        }
    }

    /// <summary>#568: behind an explicit Yes/No confirm since it's a persistent firewall
    /// configuration change - same MessageBox.Show confirm-first pattern used throughout this
    /// ViewModel.</summary>
    private async Task EnableDroppedLoggingAsync()
    {
        var confirm = MessageBox.Show(
            "Enable dropped-packet logging across all firewall profiles?\nWindows Firewall will begin writing every dropped packet to pfirewall.log - useful for diagnosis, but the log grows continuously and adds some disk I/O on a busy or heavily-filtered machine.",
            "Enable dropped-packet logging", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsLoadingFirewallLog = true;
        FirewallLogStatusText = "Enabling...";
        try
        {
            FirewallLogStatusText = await FirewallLogService.EnableDroppedConnectionLoggingAsync();
        }
        catch (Exception ex)
        {
            FirewallLogStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsLoadingFirewallLog = false;
        }
    }

    /// <summary>#569: behind an explicit Yes/No confirm since this is a persistent audit-policy
    /// change with a real (if usually small) log-volume cost.</summary>
    private async Task EnableWfpAuditingAsync()
    {
        var confirm = MessageBox.Show(
            "Enable Windows Filtering Platform drop auditing?\nThis turns on two Security-log audit subcategories (\"Filtering Platform Packet Drop\" and \"Filtering Platform Connection\") so blocked traffic gets logged with the filter that blocked it. Adds Security-log volume on a busy machine - this app never turns it back off automatically.",
            "Enable WFP drop auditing", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsEnablingWfpAuditing = true;
        WfpAuditStatusText = "Enabling...";
        try
        {
            WfpAuditStatusText = await WfpAuditService.EnableAuditingAsync();
        }
        catch (Exception ex)
        {
            WfpAuditStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsEnablingWfpAuditing = false;
        }
    }

    private async Task ScanWfpDropsAsync()
    {
        if (IsScanningWfpDrops) return;
        IsScanningWfpDrops = true;
        WfpScanStatusText = "Scanning...";
        try
        {
            var window = TimeSpan.FromHours(WfpScanWindowHours);
            var result = await WfpAuditService.ScanAsync(window);

            WfpDropEvents.Clear();
            foreach (var e in result.Events) WfpDropEvents.Add(e);

            WfpScanStatusText = !result.ChannelAvailable
                ? "The Security log couldn't be read."
                : result.Events.Count == 0
                    ? $"No WFP drop events in the last {WfpScanWindowHours:0.#}h - either nothing was blocked, or auditing isn't enabled yet."
                    : $"{result.Events.Count} event(s) in the last {WfpScanWindowHours:0.#}h." +
                      (result.FilterNamesResolved ? string.Empty : " Rule names couldn't be resolved (netsh wfp show filters failed) - showing raw filter IDs.");
        }
        catch (Exception ex)
        {
            WfpScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningWfpDrops = false;
        }
    }

    /// <summary>#571: read-only, informational stack inventory.</summary>
    private async Task RefreshStackInventoryAsync()
    {
        if (IsLoadingStackInventory) return;
        IsLoadingStackInventory = true;
        StackInventoryStatusText = "Loading...";
        try
        {
            var result = await NetworkStackInventoryService.ReadAsync();

            WfpProviders.Clear();
            foreach (var p in result.Providers) WfpProviders.Add(p);
            WfpCallouts.Clear();
            foreach (var c in result.Callouts) WfpCallouts.Add(c);
            BoundFilterDrivers.Clear();
            foreach (var d in result.BoundDrivers) BoundFilterDrivers.Add(d);

            int nonMs = result.BoundDrivers.Count(d => d.LooksNonMicrosoft);
            StackInventoryStatusText = !result.WfpStateAvailable
                ? $"WFP provider/callout list unavailable on this machine (netsh wfp show state failed to parse). {result.BoundDrivers.Count} bound filter driver(s) found ({nonMs} non-Microsoft)."
                : $"{result.Providers.Count} provider(s), {result.Callouts.Count} callout(s), {result.BoundDrivers.Count} bound filter driver(s) ({nonMs} non-Microsoft).";
        }
        catch (Exception ex)
        {
            StackInventoryStatusText = $"Couldn't load the stack inventory: {ex.Message}";
        }
        finally
        {
            IsLoadingStackInventory = false;
        }
    }

    /// <summary>#570: the guided wizard - gathers #567's rule filter, a fresh #569-style 24h WFP
    /// scan, the existing proxy readout, a fresh #577 VPN/route check, and the existing Connections
    /// grid, all scoped to one process-name/executable query.</summary>
    private async Task RunWizardAsync()
    {
        string query = WizardQuery.Trim();
        if (query.Length == 0 || IsRunningWizard) return;

        IsRunningWizard = true;
        WizardStatusText = "Gathering signals...";
        try
        {
            var rules = await FirewallService.ReadRulesAsync();
            var matchingRules = FirewallService.FilterRules(rules, query, executableOnly: true);

            var wfpScan = await WfpAuditService.ScanAsync(TimeSpan.FromHours(24));
            var matchingDrops = wfpScan.Events
                .Where(e => e.ApplicationPath is not null && e.ApplicationPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var proxy = NetworkDiagnosticsService.ReadProxyConfig();
            string proxyText = proxy.Enabled
                ? $"A system-wide proxy is configured ({(proxy.ProxyServer.Length > 0 ? proxy.ProxyServer : "no server set")}) - most apps using default WinHTTP/WinINet settings will route through it unless they implement their own network stack."
                : "No system-wide proxy is configured - this app most likely connects directly.";

            var vpnCheck = await VpnTunnelCheckService.CheckAsync();
            string vpnText = vpnCheck.HasVpn
                ? $"VPN adapter: {vpnCheck.VpnAdapterName}. {vpnCheck.DefaultRouteExplanation}"
                : "No VPN adapter is currently active.";

            var matchingConnections = Connections.Where(c => c.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            WizardReport = new NetworkTroubleshootReport
            {
                Query = query,
                MatchingFirewallRules = matchingRules,
                MatchingWfpDrops = matchingDrops,
                WfpChannelAvailable = wfpScan.ChannelAvailable,
                ProxyApplicability = proxyText,
                VpnRouteSummary = vpnText,
                MatchingConnections = matchingConnections,
            };

            WizardStatusText = $"{matchingRules.Count} firewall rule(s), {matchingDrops.Count} WFP drop(s), {matchingConnections.Count} current connection(s) matching \"{query}\".";
        }
        catch (Exception ex)
        {
            WizardStatusText = $"Diagnosis failed: {ex.Message}";
        }
        finally
        {
            IsRunningWizard = false;
        }
    }

    // ---- #572-575 helpers (Proxy and Stack cards) ------------------------------------------------

    /// <summary>#572/#573/#574: one combined refresh - all three read from the same
    /// already-fetched ProxyConfigInfo, so there's no benefit to three separate round trips.</summary>
    private async Task RefreshProxyCardAsync()
    {
        if (IsLoadingProxyCard) return;
        IsLoadingProxyCard = true;
        try
        {
            var proxy = NetworkDiagnosticsService.ReadProxyConfig();

            ProxyBypassEntries.Clear();
            foreach (var e in ProxyDiagnosticsService.ParseBypassList(proxy.ProxyOverride)) ProxyBypassEntries.Add(e);

            if (string.IsNullOrWhiteSpace(proxy.AutoConfigUrl))
            {
                PacStatusText = "No PAC/auto-config URL configured.";
                PacBody = null;
                PacLooksProblematic = false;
            }
            else
            {
                PacStatusText = "Fetching...";
                var pac = await ProxyDiagnosticsService.FetchPacAsync(proxy.AutoConfigUrl);
                PacBody = pac.Body;
                PacLooksProblematic = !pac.Success || pac.IsSlow;
                PacStatusText = pac.Success
                    ? $"Fetched in {pac.ElapsedMs} ms" + (pac.IsSlow ? " - slow enough to cause a noticeable multi-second hang before every new connection." : ".")
                    : $"Couldn't fetch the PAC script ({pac.ElapsedMs} ms): {pac.ErrorMessage}. An unreachable or slow PAC server causes the same multi-second per-connection hang.";
            }

            ProxyDivergence = await ProxyDiagnosticsService.ReadDivergenceAsync(proxy);
        }
        catch (Exception ex)
        {
            PacStatusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoadingProxyCard = false;
        }
    }

    private void TestBypass()
    {
        string host = BypassTestHostname.Trim();
        if (host.Length == 0)
        {
            BypassTestResultText = "Enter a hostname above and click Test.";
            return;
        }

        var proxy = NetworkDiagnosticsService.ReadProxyConfig();
        bool bypasses = ProxyDiagnosticsService.TestBypasses(proxy.ProxyOverride, host);
        BypassTestResultText = bypasses
            ? $"\"{host}\" would bypass the proxy (connect directly) under the current rules."
            : $"\"{host}\" would go through the proxy under the current rules.";
    }

    private async Task RefreshWinsockCatalogAsync()
    {
        if (IsLoadingWinsockCatalog) return;
        IsLoadingWinsockCatalog = true;
        WinsockStatusText = "Loading...";
        try
        {
            var result = await WinsockService.ReadCatalogAsync();
            WinsockProviders.Clear();
            foreach (var p in result.Entries) WinsockProviders.Add(p);
            WinsockNonMicrosoftCount = result.NonMicrosoftCount;

            WinsockStatusText = result.NonMicrosoftCount == 0
                ? $"{result.Entries.Count} catalog entr{(result.Entries.Count == 1 ? "y" : "ies")}, all under the Windows System32 folder."
                : $"{result.Entries.Count} catalog entries - {result.NonMicrosoftCount} non-Microsoft provider(s) flagged below. Quick flag, not a verdict - a legitimate third-party provider (a VPN client, a firewall/AV suite) looks identical from here to a leftover corrupted one.";
        }
        catch (Exception ex)
        {
            WinsockStatusText = $"Couldn't load the Winsock catalog: {ex.Message}";
        }
        finally
        {
            IsLoadingWinsockCatalog = false;
        }
    }

    /// <summary>#575: `netsh winsock reset` - behind an explicit confirm with a clear
    /// reboot-required warning, per this item's own text.</summary>
    private void ConfirmAndResetWinsock()
    {
        if (IsResettingWinsock) return;

        var confirm = MessageBox.Show(
            "Reset the Winsock catalog?\nThis removes every registered Layered Service Provider (including any flagged above) and restores Windows' default Winsock configuration.\n\nA RESTART IS REQUIRED for this to fully take effect - networking can behave oddly until you reboot.",
            "Reset Winsock catalog", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunWinsockResetAsync();
    }

    private async Task RunWinsockResetAsync()
    {
        IsResettingWinsock = true;
        WinsockResetStatusText = "Resetting...";
        try
        {
            WinsockResetStatusText = await WinsockService.ResetAsync();
        }
        catch (Exception ex)
        {
            WinsockResetStatusText = $"Reset failed: {ex.Message}";
        }
        finally
        {
            IsResettingWinsock = false;
        }
    }

    // ---- #576 helper (reset toolkit) ---------------------------------------------------------

    /// <summary>Shared confirm-then-run-then-log plumbing for every #576 toolkit action - shows
    /// <paramref name="warningText"/> (the plain-English "what it does and what it will break"
    /// description) behind an explicit Yes/No confirm, same MessageBox.Show pattern used throughout
    /// this ViewModel, then runs <paramref name="action"/> and reports its result. Each action logs
    /// itself via NetworkStackResetService's own AppendToActionLog, not duplicated here.</summary>
    private void RunStackResetAction(string actionName, string warningText, Func<Task<StackResetActionResult>> action)
    {
        if (IsRunningStackReset) return;

        var confirm = MessageBox.Show($"{warningText}\n\nRun this now?", actionName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunStackResetActionAsync(actionName, action);
    }

    private async Task RunStackResetActionAsync(string actionName, Func<Task<StackResetActionResult>> action)
    {
        IsRunningStackReset = true;
        StackResetStatusText = $"Running \"{actionName}\"...";
        try
        {
            var result = await action();
            StackResetStatusText = $"{result.ActionName}: {(result.Success ? "done" : "failed")} — {result.Output}";
        }
        catch (Exception ex)
        {
            StackResetStatusText = $"{actionName} failed: {ex.Message}";
        }
        finally
        {
            IsRunningStackReset = false;
        }
    }

    // ---- #577 helper (VPN tunnel/DNS-leak check) -----------------------------------------------

    private async Task RunVpnTunnelCheckAsync()
    {
        if (IsCheckingVpnTunnel) return;
        IsCheckingVpnTunnel = true;
        try
        {
            VpnTunnelCheck = await VpnTunnelCheckService.CheckAsync();
        }
        catch
        {
            VpnTunnelCheck = null; // best-effort - the view just hides the result panel
        }
        finally
        {
            IsCheckingVpnTunnel = false;
        }
    }

    // ---- #578 helper (orphaned virtual adapters) -----------------------------------------------

    private async Task ScanOrphanedAdaptersAsync()
    {
        if (IsScanningOrphanedAdapters) return;
        IsScanningOrphanedAdapters = true;
        OrphanedAdapterStatusText = "Scanning...";
        try
        {
            var orphans = await OrphanedAdapterService.FindOrphansAsync();
            OrphanedAdapters.Clear();
            foreach (var o in orphans) OrphanedAdapters.Add(o);

            OrphanedAdapterStatusText = orphans.Count == 0
                ? "No orphaned virtual/VPN adapters found."
                : $"{orphans.Count} adapter(s) flagged - no matching installed service or driver package found by name.";
        }
        catch (Exception ex)
        {
            OrphanedAdapterStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningOrphanedAdapters = false;
        }
    }

    // ---- #579-585 helpers (throughput, bufferbloat, per-process bandwidth) -----------------------

    private async Task RunDownloadTestAsync()
    {
        if (IsRunningSpeedTest || string.IsNullOrWhiteSpace(SpeedTestDownloadUrl)) return;
        IsRunningSpeedTest = true;
        SpeedTestStatusText = "Downloading a fixed-size payload...";
        try
        {
            var result = await SpeedTestService.RunDownloadAsync(SpeedTestDownloadUrl);
            LastDownloadResult = result;
            SpeedTestStatusText = result.Succeeded
                ? $"Download: {result.Mbps:0.#} Mbps ({Formatting.FormatBytes(result.BytesTransferred)} in {result.DurationSeconds:0.#}s) - a single-stream HTTP test, a rough floor, not a certified figure."
                : $"Download test failed: {result.ErrorMessage}";
            RecordTestHistory("Download", SpeedTestDownloadUrl, result.Mbps, SpeedTestStatusText, result.Succeeded);
        }
        finally
        {
            IsRunningSpeedTest = false;
        }
    }

    private async Task RunUploadTestAsync()
    {
        if (IsRunningSpeedTest || string.IsNullOrWhiteSpace(SpeedTestUploadUrl)) return;
        IsRunningSpeedTest = true;
        SpeedTestStatusText = "Uploading a fixed-size payload...";
        try
        {
            var result = await SpeedTestService.RunUploadAsync(SpeedTestUploadUrl);
            LastUploadResult = result;
            SpeedTestStatusText = result.Succeeded
                ? $"Upload: {result.Mbps:0.#} Mbps ({Formatting.FormatBytes(result.BytesTransferred)} in {result.DurationSeconds:0.#}s) - a single-stream HTTP test, a rough floor, not a certified figure."
                : $"Upload test failed: {result.ErrorMessage}";
            RecordTestHistory("Upload", SpeedTestUploadUrl, result.Mbps, SpeedTestStatusText, result.Succeeded);
        }
        finally
        {
            IsRunningSpeedTest = false;
        }
    }

    /// <summary>#579's "small history list", shared by #580/#581 - see NetworkTestHistoryService's
    /// remarks for why one small feed covers all three test kinds.</summary>
    private void RecordTestHistory(string kind, string target, double mbps, string summary, bool succeeded)
    {
        var entry = new NetworkTestHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            TestKind = kind,
            Target = target,
            Mbps = mbps,
            Summary = summary,
            Succeeded = succeeded,
        };
        NetworkTestHistoryService.Add(entry);
        NetworkTestHistory.Insert(0, entry);
        while (NetworkTestHistory.Count > 20) NetworkTestHistory.RemoveAt(NetworkTestHistory.Count - 1);
    }

    /// <summary>#580: idle-then-loaded latency, graded A-F - see BufferbloatTestService's remarks
    /// for why this is split into two awaited phases with a status-text update between them.</summary>
    private async Task RunBufferbloatTestAsync()
    {
        if (IsRunningBufferbloatTest) return;
        IsRunningBufferbloatTest = true;
        string host = string.IsNullOrWhiteSpace(BufferbloatPingHost) ? "1.1.1.1" : BufferbloatPingHost.Trim();
        try
        {
            BufferbloatStatusText = "Measuring idle latency (a few seconds)...";
            double idle = await BufferbloatTestService.MeasureIdleLatencyAsync(host, TimeSpan.FromSeconds(4));

            BufferbloatStatusText = "Saturating the link (downloading) and measuring loaded latency...";
            var (loaded, download) = await BufferbloatTestService.MeasureLoadedLatencyAsync(host, SpeedTestDownloadUrl);

            double delta = loaded - idle;
            string grade = BufferbloatTestService.GradeFor(delta);
            BufferbloatResult = new BufferbloatResult(idle, loaded, delta, grade, null);
            BufferbloatStatusText = $"Idle {idle:0.#} ms → loaded {loaded:0.#} ms (+{delta:0.#} ms) - grade {grade}." +
                (download.Succeeded ? $" Download during the test: {download.Mbps:0.#} Mbps." : string.Empty);
            RecordTestHistory("Bufferbloat", host, download.Mbps, BufferbloatStatusText, true);
        }
        catch (Exception ex)
        {
            BufferbloatResult = new BufferbloatResult(0, 0, 0, "N/A", ex.Message);
            BufferbloatStatusText = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsRunningBufferbloatTest = false;
        }
    }

    private async Task RunLanSmbTestAsync()
    {
        if (IsRunningLanTest || string.IsNullOrWhiteSpace(LanTestSmbPath)) return;
        IsRunningLanTest = true;
        LanTestStatusText = "Reading from the SMB share...";
        try
        {
            var result = await LanThroughputService.MeasureSmbReadAsync(LanTestSmbPath);
            LastLanTestResult = result;
            LanTestStatusText = result.Succeeded
                ? $"SMB read: {result.Mbps:0.#} Mbps ({Formatting.FormatBytes(result.BytesTransferred)} in {result.DurationSeconds:0.#}s)."
                : $"SMB read test failed: {result.ErrorMessage}";
            RecordTestHistory("LAN (SMB)", LanTestSmbPath, result.Mbps, LanTestStatusText, result.Succeeded);
        }
        finally
        {
            IsRunningLanTest = false;
        }
    }

    private async Task RunLanTcpTestAsync()
    {
        if (IsRunningLanTest || string.IsNullOrWhiteSpace(LanTestTcpTarget)) return;
        IsRunningLanTest = true;
        LanTestStatusText = "Connecting and reading from the TCP listener (10s)...";
        try
        {
            var result = await LanThroughputService.MeasureTcpAsync(LanTestTcpTarget, TimeSpan.FromSeconds(10));
            LastLanTestResult = result;
            LanTestStatusText = result.Succeeded
                ? $"TCP stream: {result.Mbps:0.#} Mbps ({Formatting.FormatBytes(result.BytesTransferred)} in {result.DurationSeconds:0.#}s)."
                : $"TCP stream test failed: {result.ErrorMessage}";
            RecordTestHistory("LAN (TCP)", LanTestTcpTarget, result.Mbps, LanTestStatusText, result.Succeeded);
        }
        finally
        {
            IsRunningLanTest = false;
        }
    }

    /// <summary>#582: starts a capture - see ProcessBandwidthEtwService's remarks for the session
    /// lifecycle. Failure (permission, stale session) degrades to a status message, never a
    /// crash.</summary>
    private void StartEtwCapture()
    {
        if (IsEtwCapturing) return;
        EtwBandwidthResults.Clear();
        string? error = _etwBandwidth.Start();
        if (error is not null)
        {
            EtwCaptureStatusText = $"Couldn't start capture: {error}";
            return;
        }
        IsEtwCapturing = true;
        _etwElapsedTimer.Start();
        EtwCaptureStatusText = "Capture running - 0s elapsed. Click Stop to see per-process results.";
    }

    private void StopEtwCapture()
    {
        if (!IsEtwCapturing) return;
        _etwElapsedTimer.Stop();
        var results = _etwBandwidth.Stop();
        IsEtwCapturing = false;

        EtwBandwidthResults.Clear();
        foreach (var r in results) EtwBandwidthResults.Add(r);

        EtwCaptureStatusText = results.Count == 0
            ? "Capture stopped - no TCP/UDP traffic was seen during the capture window."
            : $"Capture stopped - {results.Count} process(es) seen, {Formatting.FormatBytes(results.Sum(r => r.TotalBytes))} total.";

        // #583: persist this capture's totals into the byte-level history series.
        NetworkHistoryService.RecordCaptureSample(results);
    }

    private void RecomputeLinkUtilization()
    {
        LinkUtilization = AdapterTrafficService.ComputeUtilization(Performance.NetworkReceiveBps, Performance.NetworkSendBps);
    }

    // ---- #586-590 helpers (SMB and network drives) --------------------------------------------

    private async Task RefreshNetworkDrivesAsync()
    {
        if (IsLoadingNetworkDrives) return;
        IsLoadingNetworkDrives = true;
        NetworkDrivesStatusText = "Loading...";
        try
        {
            var drives = await SmbShareService.ReadMappedDrivesAsync();
            var connections = await Task.Run(SmbShareService.ReadConnections);
            var offlineFiles = await Task.Run(OfflineFilesService.Read);

            MappedDrives.Clear();
            foreach (var d in drives) MappedDrives.Add(d);

            SmbConnections.Clear();
            foreach (var c in connections) SmbConnections.Add(c);

            OfflineFiles = offlineFiles;

            NetworkDrivesStatusText = $"{drives.Count} mapped drive(s), {connections.Count} active SMB connection(s).";
        }
        catch (Exception ex)
        {
            NetworkDrivesStatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoadingNetworkDrives = false;
        }
    }

    /// <summary>#587: on-demand reachability test for one row - see SmbShareService.TestReachabilityAsync's
    /// remarks for why this races Directory.Exists against a timeout rather than calling it
    /// directly.</summary>
    private static async Task TestDriveReachabilityAsync(object? parameter)
    {
        if (parameter is not MappedDriveInfo drive) return;
        drive.ReachabilityText = "Testing...";
        var (reachable, text) = await SmbShareService.TestReachabilityAsync(drive.RemotePath, TimeSpan.FromSeconds(5));
        drive.IsReachable = reachable;
        drive.ReachabilityText = text;
    }

    /// <summary>#587's "Disconnect" action - same MessageBox.Show confirm-first pattern used
    /// throughout this ViewModel (e.g. RestartSelectedAdapter above) for any connection-dropping
    /// action.</summary>
    private async Task DisconnectMappedDriveAsync(object? parameter)
    {
        if (parameter is not MappedDriveInfo drive) return;

        var confirm = MessageBox.Show(
            $"Disconnect {drive.DriveLetter} ({drive.RemotePath})?\nAny unsaved work relying on this mapping may be lost.",
            "Disconnect network drive", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        NetworkDrivesStatusText = $"Disconnecting {drive.DriveLetter}...";
        string output = await SmbShareService.DisconnectAsync(drive.DriveLetter);
        NetworkDrivesStatusText = output;
        await RefreshNetworkDrivesAsync();
    }

    private async Task ScanSmbEventsAsync()
    {
        if (IsScanningSmbEvents) return;
        IsScanningSmbEvents = true;
        SmbEventScanStatusText = "Scanning...";
        try
        {
            var result = await Task.Run(() => SmbClientEventLogService.Scan(TimeSpan.FromHours(SmbEventScanWindowHours)));
            SmbClientEvents.Clear();
            foreach (var e in result.Events) SmbClientEvents.Add(e);

            SmbEventScanStatusText = !result.ConnectivityChannelAvailable && !result.OperationalChannelAvailable
                ? "Neither SMBClient log channel could be read (both are disabled by default on most machines, or access was denied)."
                : $"{result.Events.Count} event(s) in the last {SmbEventScanWindowHours:0.#}h.";
        }
        catch (Exception ex)
        {
            SmbEventScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningSmbEvents = false;
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
        _wifiSignalMonitor.CycleCompleted -= OnWifiSignalCycleCompleted;
        _wifiSignalMonitor.Dispose();
        _wlan.Dispose();
        _etwElapsedTimer.Stop();
        _etwBandwidth.Dispose();
    }
}
