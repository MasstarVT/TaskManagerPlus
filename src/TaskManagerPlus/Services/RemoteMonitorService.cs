using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Remote/read-only metrics endpoint (#101) - so a second device (phone/tablet) can watch CPU/
/// RAM/Disk/Network/temp while the troubleshooter is elsewhere physically, e.g. watching temps
/// while stress-testing without hovering over the desk. Built on the BCL's own HttpListener (no
/// new dependency) rather than a web framework, since it only ever needs to serve one static page
/// and one small JSON snapshot. Deliberately read-only and opt-in/off-by-default: it exposes no
/// process list, no file paths, no control actions - just the same handful of headline numbers
/// already visible on the Summary tab - and the Settings drawer's toggle carries an explicit
/// "unauthenticated, LAN-visible" warning, since HttpListener has no built-in auth and this app
/// takes no dependency that would add one.
/// </summary>
public sealed class RemoteMonitorService : IDisposable
{
    private readonly Func<RemoteMetricsSnapshot> _sample;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }

    /// <summary>Round 12, #97: optional shared token - see RemoteMonitorSettings.Token's remarks.
    /// Null/empty (the default) means every request is served exactly as before this feature
    /// existed. Settable live so a token change in the Settings drawer applies without needing
    /// to stop/restart the listener.</summary>
    public string? RequiredToken { get; set; }

    public RemoteMonitorService(Func<RemoteMetricsSnapshot> sample)
    {
        _sample = sample;
    }

    /// <summary>Starts listening on every local interface (http://+:port/) - this app already
    /// runs elevated, which HttpListener accepts in place of a separate URL-ACL reservation.</summary>
    public (bool Success, string? Error) Start(int port)
    {
        Stop();
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{port}/");
            listener.Start();
            _listener = listener;
            Port = port;

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            return (true, null);
        }
        catch (Exception ex)
        {
            _listener = null;
            return (false, ex.Message);
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* best-effort */ }
        try { _listener?.Stop(); _listener?.Close(); } catch { /* best-effort */ }
        _listener = null;
    }

    /// <summary>Non-loopback IPv4 addresses of this machine (#101) - shown in the UI as the
    /// "connect from your phone" address list, since which adapter is the "real" LAN one isn't
    /// knowable in general (Wi-Fi vs. Ethernet vs. a VPN adapter all look the same here).</summary>
    public static List<string> LocalIPv4Addresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        addresses.Add(addr.Address.ToString());
                }
            }
        }
        catch
        {
            // Best-effort - an empty list just means the UI can't suggest an address to try.
        }
        return addresses;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                break; // listener stopped/disposed
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // #97: optional shared-token check - see RemoteMonitorSettings.Token's remarks. A
            // missing/mismatched token gets a bare 401 (no body, no page) rather than a "wrong
            // token" message, so a scan doesn't get free confirmation the endpoint even exists.
            if (!string.IsNullOrEmpty(RequiredToken))
            {
                string? providedToken = ctx.Request.QueryString["token"];
                if (!string.Equals(providedToken, RequiredToken, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.OutputStream.Close();
                    return;
                }
            }

            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            byte[] body;

            if (path.Equals("/metrics.json", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "application/json";
                body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_sample()));
            }
            else
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                body = Encoding.UTF8.GetBytes(DashboardHtml);
            }

            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
        }
        catch
        {
            // Best-effort per request - a dropped connection shouldn't affect the listener loop.
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { /* already closed by the client */ }
        }
    }

    // A single self-contained page (inline CSS/JS, no external references) that polls
    // /metrics.json every 2 seconds - simple enough not to need a templating engine for one page.
    private const string DashboardHtml = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Task Manager Plus - Remote</title>
        <style>
          body{font-family:Segoe UI,Arial,sans-serif;background:#1c1c1f;color:#e4e4e7;margin:0;padding:24px}
          h1{font-size:18px;margin:0 0 4px}
          .sub{color:#9a9aa2;font-size:12px;margin-bottom:20px}
          .row{display:flex;justify-content:space-between;padding:10px 0;border-bottom:1px solid #2c2c33;font-size:15px}
          .label{color:#9a9aa2}
        </style></head><body>
        <h1 id="machine">Task Manager Plus</h1>
        <div class="sub" id="ts">Connecting...</div>
        <div class="row"><span class="label">CPU</span><span id="cpu">-</span></div>
        <div class="row"><span class="label">CPU temp</span><span id="temp">-</span></div>
        <div class="row"><span class="label">Memory</span><span id="ram">-</span></div>
        <div class="row"><span class="label">Disk</span><span id="disk">-</span></div>
        <div class="row"><span class="label">Network</span><span id="net">-</span></div>
        <div class="row"><span class="label">Uptime</span><span id="uptime">-</span></div>
        <script>
        function fmtRate(v){if(v>1048576)return (v/1048576).toFixed(1)+' MB/s';if(v>1024)return (v/1024).toFixed(1)+' KB/s';return v.toFixed(0)+' B/s';}
        async function poll(){
          try{
            // #97: forwards this page's own query string (including ?token=... when the optional
            // shared token is set) onto every /metrics.json poll, so the page keeps working once
            // it's been opened once with the right token - no separate client-side token entry.
            const r=await fetch('/metrics.json'+location.search,{cache:'no-store'});
            const m=await r.json();
            document.getElementById('machine').textContent=m.machineName;
            document.getElementById('ts').textContent='Updated '+new Date().toLocaleTimeString();
            document.getElementById('cpu').textContent=m.cpuPercent.toFixed(1)+'%';
            document.getElementById('temp').textContent=m.hasCpuTemp?m.cpuTempC.toFixed(1)+'°C':'-';
            document.getElementById('ram').textContent=m.ramPercent.toFixed(1)+'%';
            document.getElementById('disk').textContent=m.diskPercent.toFixed(1)+'%';
            document.getElementById('net').textContent='↓'+fmtRate(m.networkReceiveBps)+'  ↑'+fmtRate(m.networkSendBps);
            document.getElementById('uptime').textContent=m.uptime;
          }catch(e){document.getElementById('ts').textContent='Connection lost - retrying...';}
        }
        poll(); setInterval(poll,2000);
        </script>
        </body></html>
        """;

    public void Dispose() => Stop();
}
