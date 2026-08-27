using System.Collections.ObjectModel;
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

    public RelayCommand CheckConnectivityCommand { get; }

    public NetworkViewModel(PerformanceViewModel performance)
    {
        Performance = performance;

        CheckConnectivityCommand = new RelayCommand(_ => _ = CheckConnectivityAsync());

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) => await CheckConnectivityAsync();
        _timer.Start();

        _ = CheckConnectivityAsync();
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

            var links = NetworkDiagnosticsService.ReadAdapterLinks();
            AdapterLinks.Clear();
            foreach (var link in links) AdapterLinks.Add(link);

            var vpnAdapters = NetworkDiagnosticsService.ReadActiveVpnAdapterNames();
            HasActiveVpn = vpnAdapters.Count > 0;
            VpnStatusText = HasActiveVpn ? string.Join(", ", vpnAdapters) : "None detected";
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

    public void Dispose() => _timer.Stop();
}
