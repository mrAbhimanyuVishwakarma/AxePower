using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services;

public class TimeoutService
{
    private readonly LogService _logService;
    
    public TimeoutService(LogService logService)
    {
        _logService = logService;
    }

    public async Task<(int acMinutes, int dcMinutes)> GetMonitorTimeoutsAsync()
    {
        return await GetTimeoutsAsync("7516b95f-f776-4464-8c53-06167f40cc99", "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    }

    public async Task<(int acMinutes, int dcMinutes)> GetStandbyTimeoutsAsync()
    {
        return await GetTimeoutsAsync("238c9fa8-0aad-41ed-83f4-97be242c8f20", "29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    }

    private async Task<(int, int)> GetTimeoutsAsync(string subGroup, string setting)
    {
        var result = await CommandRunner.RunAsync("powercfg", $"/q SCHEME_CURRENT {subGroup} {setting}");
        if (!result.Success) 
        {
            _logService.LogError($"Failed to read timeout. Error: {result.Error}");
            return (0, 0);
        }

        int ac = ParseHexSecondsToMinutes(result.Output, "Current AC Power Setting Index");
        int dc = ParseHexSecondsToMinutes(result.Output, "Current DC Power Setting Index");
        return (ac, dc);
    }

    private int ParseHexSecondsToMinutes(string output, string key)
    {
        if (string.IsNullOrEmpty(output)) return 0;
        
        var match = Regex.Match(output, $@"{key}:\s*(0x[0-9a-fA-F]+)");
        if (match.Success)
        {
            var hex = match.Groups[1].Value.Replace("0x", "");
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int seconds))
            {
                return seconds / 60;
            }
        }
        return 0;
    }

    public async Task SetMonitorTimeoutAsync(int minutes, bool isAc)
    {
        string param = isAc ? "monitor-timeout-ac" : "monitor-timeout-dc";
        var res = await CommandRunner.RunAsync("powercfg", $"/change {param} {minutes}");
        if (!res.Success)
            _logService.LogError($"Failed to set monitor timeout. {res.Error}");
        else
            _logService.LogInfo($"Set monitor timeout {isAc} to {minutes}m");
    }

    public async Task SetStandbyTimeoutAsync(int minutes, bool isAc)
    {
        string param = isAc ? "standby-timeout-ac" : "standby-timeout-dc";
        var res = await CommandRunner.RunAsync("powercfg", $"/change {param} {minutes}");
        if (!res.Success)
            _logService.LogError($"Failed to set standby timeout. {res.Error}");
        else
            _logService.LogInfo($"Set standby timeout {isAc} to {minutes}m");
    }
}
