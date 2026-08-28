using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
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
            IsLatencyMonitoring = false;
        }
        else
        {
            _latencyMonitor.Start(LatencyIntervalSeconds);
            IsLatencyMonitoring = true;
        }
    }

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

    public void Dispose()
    {
        _timer.Stop();
        _latencyMonitor.CycleCompleted -= OnLatencyCycleCompleted;
        _latencyMonitor.Dispose();
    }
}
