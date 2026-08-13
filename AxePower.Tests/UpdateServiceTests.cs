using System;
using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void UpdateService_InitializesProperly()
    {
        var logService = new LogService();
        var updateService = new UpdateService(logService);

        var currentVersion = updateService.GetCurrentVersion();
        Assert.NotNull(currentVersion);

        var versionString = updateService.GetCurrentVersionString();
        Assert.False(string.IsNullOrWhiteSpace(versionString));
    }
}
