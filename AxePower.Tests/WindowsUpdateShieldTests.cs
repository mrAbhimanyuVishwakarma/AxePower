using System;
using System.Collections.Generic;
using AxePower.Core.Services;

namespace AxePower.Tests;

public sealed class WindowsUpdateShieldTests
{
    [Fact]
    public void SecurityOnlyStatus_RequiresTheFull365DayPolicy()
    {
        var system = new FakeWindowsUpdateSystem();
        var service = Create(system);
        system.Values[(WindowsUpdateShieldService.UpdatePolicyKey, "DeferFeatureUpdates")] = 1;

        Assert.False(service.GetStatus().IsSecurityOnlyEnabled);

        system.Values[(WindowsUpdateShieldService.UpdatePolicyKey, "DeferFeatureUpdatesPeriodInDays")] = 365;
        Assert.True(service.GetStatus().IsSecurityOnlyEnabled);
    }

    [Fact]
    public async Task SecurityOnlyPolicy_IsWrittenAndVerified_ThenCleanlyRemoved()
    {
        var system = new FakeWindowsUpdateSystem();
        var service = Create(system);

        Assert.True(await service.SetSecurityOnlyUpdatesAsync(true));
        Assert.Equal(1, system.ReadDword(WindowsUpdateShieldService.UpdatePolicyKey, "DeferFeatureUpdates"));
        Assert.Equal(365, system.ReadDword(WindowsUpdateShieldService.UpdatePolicyKey, "DeferFeatureUpdatesPeriodInDays"));
        Assert.Null(system.ReadDword(WindowsUpdateShieldService.UpdatePolicyKey, "BranchReadinessLevel"));

        system.Values[(WindowsUpdateShieldService.UpdatePolicyKey, "BranchReadinessLevel")] = 16;
        Assert.True(await service.SetSecurityOnlyUpdatesAsync(false));
        Assert.False(service.GetStatus().IsSecurityOnlyEnabled);
        Assert.Null(system.ReadDword(WindowsUpdateShieldService.UpdatePolicyKey, "BranchReadinessLevel"));
    }

    [Fact]
    public async Task PolicySetter_ReturnsFalseWhenWindowsDoesNotPersistTheWrite()
    {
        var system = new FakeWindowsUpdateSystem { IgnoreWrites = true };

        Assert.False(await Create(system).SetSecurityOnlyUpdatesAsync(true));
    }

    [Fact]
    public async Task NoForcedReboot_DoesNotChangeDownloadPolicy_AndCleansTheLegacyValue()
    {
        var system = new FakeWindowsUpdateSystem();
        var service = Create(system);

        Assert.True(await service.SetNoForcedRebootAsync(true));
        Assert.Equal(1, system.ReadDword(WindowsUpdateShieldService.AuPolicyKey, "NoAutoRebootWithLoggedOnUsers"));
        Assert.Null(system.ReadDword(WindowsUpdateShieldService.AuPolicyKey, "AUOptions"));

        system.Values[(WindowsUpdateShieldService.AuPolicyKey, "AUOptions")] = 3;
        Assert.True(await service.SetNoForcedRebootAsync(false));
        Assert.Null(system.ReadDword(WindowsUpdateShieldService.AuPolicyKey, "AUOptions"));
    }

    [Fact]
    public async Task CacheReset_RestartsOnlyServicesThatWereRunning()
    {
        var system = new FakeWindowsUpdateSystem();
        system.Services["wuauserv"] = true;
        system.Services["bits"] = true;
        system.Services["cryptsvc"] = false;
        system.Services["msiserver"] = false;

        bool ok = await Create(system).ResetWindowsUpdateCacheAsync(new ImmediateProgress());

        Assert.True(ok);
        Assert.Equal(new[] { "wuauserv", "bits" }, system.Stopped);
        Assert.Equal(new[] { "wuauserv", "bits" }, system.Started);
        Assert.Equal(1, system.ClearCalls);
        Assert.False(system.Services["cryptsvc"]);
        Assert.False(system.Services["msiserver"]);
    }

    [Fact]
    public async Task CacheReset_DoesNotDeleteWhenAnActiveServiceCannotStop_AndRestoresEarlierServices()
    {
        var system = new FakeWindowsUpdateSystem();
        system.Services["wuauserv"] = true;
        system.Services["bits"] = true;
        system.Services["cryptsvc"] = false;
        system.Services["msiserver"] = false;
        system.StopFailures.Add("bits");

        bool ok = await Create(system).ResetWindowsUpdateCacheAsync(new ImmediateProgress());

        Assert.False(ok);
        Assert.Equal(0, system.ClearCalls);
        Assert.Contains("wuauserv", system.Started);
        Assert.True(system.Services["wuauserv"]);
    }

    [Fact]
    public void RestorePointScript_EscapesDescriptions_AndRestoresTheFrequencyPolicy()
    {
        string script = RestorePointService.BuildPowerShellScript("AxePower O'Brien; Remove-Item C:\\");

        Assert.Contains("'AxePower O''Brien; Remove-Item C:\\'", script);
        Assert.Contains("Checkpoint-Computer -Description 'AxePower O''Brien; Remove-Item C:\\'", script);
        Assert.Contains("finally", script);
        Assert.Contains("Set-ItemProperty", script);
        Assert.Contains("Remove-ItemProperty", script);
        Assert.DoesNotContain("-Command", script);
    }

    private static WindowsUpdateShieldService Create(FakeWindowsUpdateSystem system) =>
        new(new LogService(), system);

    private sealed class ImmediateProgress : IProgress<string>
    {
        public void Report(string value) { }
    }

    private sealed class FakeWindowsUpdateSystem : IWindowsUpdateSystem
    {
        public Dictionary<(string Key, string Name), int> Values { get; } = new();
        public Dictionary<string, bool> Services { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["wuauserv"] = false,
            ["bits"] = false,
            ["cryptsvc"] = false,
            ["msiserver"] = false
        };
        public List<string> Stopped { get; } = new();
        public List<string> Started { get; } = new();
        public HashSet<string> StopFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IgnoreWrites { get; set; }
        public int ClearCalls { get; private set; }

        public int? ReadDword(string keyPath, string valueName) =>
            Values.TryGetValue((keyPath, valueName), out int value) ? value : null;

        public void WriteDword(string keyPath, string valueName, int value)
        {
            if (!IgnoreWrites) Values[(keyPath, valueName)] = value;
        }

        public void DeleteValue(string keyPath, string valueName) => Values.Remove((keyPath, valueName));

        public WindowsServiceState GetServiceState(string serviceName) =>
            Services.TryGetValue(serviceName, out bool running)
                ? new WindowsServiceState(true, running, running ? "Running" : "Stopped")
                : new WindowsServiceState(false, false, "not installed");

        public WindowsUpdateOperationResult StopService(string serviceName, TimeSpan timeout)
        {
            if (StopFailures.Contains(serviceName))
                return new WindowsUpdateOperationResult(false, "simulated stop failure");
            Services[serviceName] = false;
            Stopped.Add(serviceName);
            return new WindowsUpdateOperationResult(true, "stopped");
        }

        public WindowsUpdateOperationResult StartService(string serviceName, TimeSpan timeout)
        {
            Services[serviceName] = true;
            Started.Add(serviceName);
            return new WindowsUpdateOperationResult(true, "running");
        }

        public WindowsUpdateOperationResult ClearUpdateDownloadCache()
        {
            ClearCalls++;
            return new WindowsUpdateOperationResult(true, "cleared");
        }
    }
}
