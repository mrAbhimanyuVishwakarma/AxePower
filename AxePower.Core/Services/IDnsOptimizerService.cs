using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface IDnsOptimizerService
{
    IReadOnlyList<DnsProviderItem> GetAvailableProviders();
    Task<List<DnsProviderItem>> BenchmarkDnsProvidersAsync();
    Task<bool> ApplyDnsProviderAsync(string providerId);
    Task<bool> ResetToDhcpAsync();
    Task<string> GetActiveAdapterNameAsync();
}
