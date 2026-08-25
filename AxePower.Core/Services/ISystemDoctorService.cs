using System;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface ISystemDoctorService
{
    Task<bool> RunDiagnosticTaskAsync(
        DiagnosticTaskType taskType, 
        IProgress<string> outputProgress, 
        CancellationToken cancellationToken = default);
}
