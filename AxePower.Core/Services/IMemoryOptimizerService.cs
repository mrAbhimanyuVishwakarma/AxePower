using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface IMemoryOptimizerService
{
    MemoryStatusInfo GetMemoryStatus();
    Task<double> PurgeStandbyMemoryAsync();
}
