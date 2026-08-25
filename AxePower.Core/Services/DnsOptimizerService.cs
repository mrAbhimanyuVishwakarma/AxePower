using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class DnsOptimizerService : IDnsOptimizerService
{
    private readonly LogService _log;
    private readonly List<DnsProviderItem> _providers = new();

    public DnsOptimizerService(LogService log)
    {
        _log = log;
        InitializeProviders();
    }

    private void InitializeProviders()
    {
        _providers.Clear();

        _providers.Add(new DnsProviderItem
        {
            Id = "cloudflare",
            Name = "Cloudflare (1.1.1.1)",
            Description = "Lowest ping for online gaming, fast routing, and no data tracking.",
            PrimaryIpv4 = "1.1.1.1",
            SecondaryIpv4 = "1.0.0.1",
            PrimaryIpv6 = "2606:4700:4700::1111",
            SecondaryIpv6 = "2606:4700:4700::1001",
            IsDhcp = false
        });

        _providers.Add(new DnsProviderItem
        {
            Id = "quad9",
            Name = "Quad9 (9.9.9.9)",
            Description = "High security DNS with built-in malicious domain and phishing blocking.",
            PrimaryIpv4 = "9.9.9.9",
            SecondaryIpv4 = "149.112.112.112",
            PrimaryIpv6 = "2620:fe::fe",
            SecondaryIpv6 = "2620:fe::9",
            IsDhcp = false
        });

        _providers.Add(new DnsProviderItem
        {
            Id = "google",
            Name = "Google Public DNS (8.8.8.8)",
            Description = "Global coverage and maximum uptime with highly reliable infrastructure.",
            PrimaryIpv4 = "8.8.8.8",
            SecondaryIpv4 = "8.8.4.4",
            PrimaryIpv6 = "2001:4860:4860::8888",
            SecondaryIpv6 = "2001:4860:4860::8844",
            IsDhcp = false
        });

        _providers.Add(new DnsProviderItem
        {
            Id = "opendns",
            Name = "OpenDNS / Cisco (208.67.222.222)",
            Description = "Enterprise-grade filtering with content protection and reliable routing.",
            PrimaryIpv4 = "208.67.222.222",
            SecondaryIpv4 = "208.67.220.220",
            PrimaryIpv6 = "2620:119:35::35",
            SecondaryIpv6 = "2620:119:53::53",
            IsDhcp = false
        });

        _providers.Add(new DnsProviderItem
        {
            Id = "dhcp",
            Name = "Automatic (DHCP)",
            Description = "Default DNS servers provided by your local router / ISP.",
            PrimaryIpv4 = "Auto",
            SecondaryIpv4 = "Auto",
            IsDhcp = true
        });
    }

    public IReadOnlyList<DnsProviderItem> GetAvailableProviders() => _providers.AsReadOnly();

    public async Task<List<DnsProviderItem>> BenchmarkDnsProvidersAsync()
    {
        return await Task.Run(async () =>
        {
            var ping = new Ping();

            foreach (var provider in _providers.Where(p => !p.IsDhcp))
            {
                try
                {
                    long totalMs = 0;
                    int successfulPings = 0;

                    for (int i = 0; i < 3; i++)
                    {
                        var reply = await ping.SendPingAsync(provider.PrimaryIpv4, 1200);
                        if (reply.Status == IPStatus.Success)
                        {
                            totalMs += reply.RoundtripTime;
                            successfulPings++;
                        }
                    }

                    if (successfulPings > 0)
                    {
                        provider.LatencyMs = totalMs / successfulPings;
                    }
                    else
                    {
                        provider.LatencyMs = -1; // Unreachable
                    }
                }
                catch
                {
                    provider.LatencyMs = -1;
                }
            }

            // Find lowest positive latency
            var reachable = _providers.Where(p => !p.IsDhcp && p.LatencyMs >= 0).ToList();
            if (reachable.Any())
            {
                long minLatency = reachable.Min(p => p.LatencyMs);
                foreach (var p in _providers)
                {
                    p.IsFastest = (!p.IsDhcp && p.LatencyMs == minLatency);
                }
            }

            return _providers.ToList();
        });
    }

    public async Task<string> GetActiveAdapterNameAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var active = interfaces.FirstOrDefault(i =>
                    i.OperationalStatus == OperationalStatus.Up &&
                    (i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                     i.NetworkInterfaceType == NetworkInterfaceType.Ethernet) &&
                    !i.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                    !i.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                    !i.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                    !i.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase));

                return active?.Name ?? "Wi-Fi";
            }
            catch
            {
                return "Wi-Fi";
            }
        });
    }

    public async Task<bool> ApplyDnsProviderAsync(string providerId)
    {
        var provider = _providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider == null) return false;

        if (provider.IsDhcp)
        {
            return await ResetToDhcpAsync();
        }

        string adapterName = await GetActiveAdapterNameAsync();

        return await Task.Run(() =>
        {
            try
            {
                _log.LogInfo($"[DnsOptimizer] Setting DNS on adapter '{adapterName}' to '{provider.Name}' ({provider.PrimaryIpv4}, {provider.SecondaryIpv4})...");

                // Set Primary DNS
                var psiPrimary = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"interface ipv4 set dnsservers name=\"{adapterName}\" source=static address={provider.PrimaryIpv4} validate=no",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Verb = "runas"
                };
                using var p1 = Process.Start(psiPrimary);
                p1?.WaitForExit(4000);

                // Set Secondary DNS
                if (!string.IsNullOrWhiteSpace(provider.SecondaryIpv4))
                {
                    var psiSecondary = new ProcessStartInfo
                    {
                        FileName = "netsh.exe",
                        Arguments = $"interface ipv4 add dnsservers name=\"{adapterName}\" address={provider.SecondaryIpv4} index=2 validate=no",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        Verb = "runas"
                    };
                    using var p2 = Process.Start(psiSecondary);
                    p2?.WaitForExit(4000);
                }

                // Flush DNS cache
                using var pFlush = Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig.exe",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                pFlush?.WaitForExit(3000);

                foreach (var p in _providers)
                {
                    p.IsActive = (p.Id == provider.Id);
                }

                _log.LogInfo($"[DnsOptimizer] Successfully switched to {provider.Name} and flushed DNS cache.");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError($"[DnsOptimizer] Failed to apply DNS provider '{provider.Name}'", ex);
                return false;
            }
        });
    }

    public async Task<bool> ResetToDhcpAsync()
    {
        string adapterName = await GetActiveAdapterNameAsync();

        return await Task.Run(() =>
        {
            try
            {
                _log.LogInfo($"[DnsOptimizer] Resetting DNS on adapter '{adapterName}' to Automatic (DHCP)...");

                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"interface ipv4 set dnsservers name=\"{adapterName}\" source=dhcp",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Verb = "runas"
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(4000);

                // Flush DNS cache
                using var pFlush = Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig.exe",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                pFlush?.WaitForExit(3000);

                foreach (var p in _providers)
                {
                    p.IsActive = p.IsDhcp;
                }

                _log.LogInfo("[DnsOptimizer] Successfully reset DNS to Automatic (DHCP).");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError("[DnsOptimizer] Failed to reset DNS to DHCP", ex);
                return false;
            }
        });
    }
}
