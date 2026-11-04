using System.Net.NetworkInformation;

namespace SloomyTweaks.Core.Network;

public sealed record LatencyStats(
    string Target,
    int Sent,
    int Received,
    double LossPercent,
    double? MinMs,
    double? AverageMs,
    double? MaxMs,
    /// <summary>Mean absolute difference between consecutive round-trip times (RFC 3550-style smoothing not applied).</summary>
    double? JitterMs)
{
    /// <summary>Shown wherever results are displayed, because ICMP is not game traffic.</summary>
    public const string Disclaimer =
        "ICMP echo to the selected target measures the path to that host only. It is not a measurement of Fortnite or any other game's latency: game servers use different hosts, protocols (UDP) and routing, and may deprioritize ICMP.";
}

public sealed record AdapterInfo(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    long SpeedBitsPerSecond,
    IReadOnlyList<string> DnsServers,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> Addresses);

public static class LatencyCalculator
{
    /// <param name="roundTripsMs">Successful RTTs in order; failures are represented by null.</param>
    public static LatencyStats Compute(string target, IReadOnlyList<double?> roundTripsMs)
    {
        var ok = roundTripsMs.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        double loss = roundTripsMs.Count == 0 ? 0 : (roundTripsMs.Count - ok.Count) * 100.0 / roundTripsMs.Count;
        double? jitter = null;
        if (ok.Count >= 2)
        {
            double sum = 0;
            for (int i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
            jitter = sum / (ok.Count - 1);
        }
        return new LatencyStats(target, roundTripsMs.Count, ok.Count, loss,
            ok.Count > 0 ? ok.Min() : null,
            ok.Count > 0 ? ok.Average() : null,
            ok.Count > 0 ? ok.Max() : null,
            jitter);
    }
}

/// <summary>
/// Basic network diagnostics using documented .NET APIs (System.Net.NetworkInformation).
/// The gateway test isolates the local network; an internet host isolates ISP routing.
/// </summary>
public sealed class NetworkDiagnostics
{
    public static IReadOnlyList<AdapterInfo> GetActiveAdapters()
    {
        var result = new List<AdapterInfo>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props = nic.GetIPProperties();
            var gateways = props.GatewayAddresses.Select(g => g.Address.ToString()).Where(a => a != "0.0.0.0").ToList();
            result.Add(new AdapterInfo(
                nic.Name,
                nic.Description,
                nic.NetworkInterfaceType,
                nic.Speed,
                props.DnsAddresses.Select(d => d.ToString()).ToList(),
                gateways,
                props.UnicastAddresses.Select(u => u.Address.ToString()).ToList()));
        }
        // Adapters with a default gateway first: that is the one carrying internet traffic.
        return result.OrderByDescending(a => a.Gateways.Count > 0).ThenByDescending(a => a.SpeedBitsPerSecond).ToList();
    }

    public static async Task<LatencyStats> PingAsync(string target, int count, TimeSpan interval, CancellationToken ct = default)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        using var ping = new Ping();
        var rtts = new List<double?>(count);
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                PingReply reply = await ping.SendPingAsync(target, 2000).ConfigureAwait(false);
                rtts.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (PingException)
            {
                rtts.Add(null);
            }
            if (i < count - 1) await Task.Delay(interval, ct).ConfigureAwait(false);
        }
        return LatencyCalculator.Compute(target, rtts);
    }

    /// <summary>Interprets gateway vs. internet results to separate local problems from upstream ones.</summary>
    public static string Interpret(LatencyStats? gateway, LatencyStats? internet)
    {
        if (gateway is null) return "No default gateway was found; the device may not be connected to a router.";
        if (gateway.Received == 0) return "The gateway did not answer. Some routers block ICMP; otherwise check the local connection.";
        if (gateway.LossPercent > 1 || gateway.JitterMs > 5)
            return "Loss or jitter already appears between this PC and the router. This points to the local network (Wi-Fi signal, cabling, adapter driver), not the ISP or game server.";
        if (internet is null || internet.Received == 0)
            return "The local network looks stable, but the internet target did not answer. The target may block ICMP, or there is an upstream problem.";
        if (internet.LossPercent > 1 || internet.JitterMs > 10)
            return "The local network looks stable, but loss or jitter appears beyond the router. That points to ISP routing or the destination; Windows settings cannot fix it.";
        return "No local or upstream problem was detected on this path. Game-server routing can still differ from this test path.";
    }
}
