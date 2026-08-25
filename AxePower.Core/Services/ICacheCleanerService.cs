using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface ICacheCleanerService
{
    Task<List<CacheCategorySummary>> AnalyzeCachesAsync();
    Task<CacheCleanResult> CleanCachesAsync(IEnumerable<CacheCategorySummary> categoriesToClean);
}
