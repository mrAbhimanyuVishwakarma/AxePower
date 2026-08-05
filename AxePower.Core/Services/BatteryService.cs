using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class BatteryService : IDisposable
{
    private readonly LogService _logService;
    private readonly string _historyFilePath;
    private readonly List<BatteryHistoryPoint> _history = new();
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private CancellationTokenSource? _pollingCts;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    public BatteryService(LogService logService)
    {
        _logService = logService;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(localAppData, "AxePower");
        Directory.CreateDirectory(appFolder);
        var historyFolder = Path.Combine(appFolder, "BatteryHistory");
        Directory.CreateDirectory(historyFolder);
        
        _historyFilePath = Path.Combine(historyFolder, "history.json");
        LoadHistoryAsync().ConfigureAwait(false);
    }
    
    public void StartHistoryCollection()
    {
        if (_pollingCts != null) return;
        _pollingCts = new CancellationTokenSource();
        Task.Run(() => HistoryPollingLoop(_pollingCts.Token));
    }

    public void StopHistoryCollection()
    {
        _pollingCts?.Cancel();
        _pollingCts = null;
    }

    private async Task HistoryPollingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var status = GetCurrentStatus();
                if (status.HasBattery)
                {
                    await AddHistoryPointAsync(new BatteryHistoryPoint
                    {
                        Timestamp = DateTime.UtcNow,
                        Percentage = status.BatteryPercentage,
                        IsPluggedIn = status.IsPluggedIn
                    });
                }
                
                // Poll every 60 seconds
                await Task.Delay(TimeSpan.FromSeconds(60), token);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logService.LogError("Error in battery history polling loop.", ex);
                await Task.Delay(TimeSpan.FromSeconds(60), token);
            }
        }
    }

    private async Task AddHistoryPointAsync(BatteryHistoryPoint point)
    {
        await _historyLock.WaitAsync();
        try
        {
            _history.Add(point);
            
            // Keep bounded history (last 24 hours). 24 hours * 60 minutes = 1440 points
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _history.RemoveAll(h => h.Timestamp < cutoff);

            await SaveHistoryInternalAsync();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    public async Task<List<BatteryHistoryPoint>> GetHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            return _history.ToList();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    public async Task ClearHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            _history.Clear();
            await SaveHistoryInternalAsync();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task LoadHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = await File.ReadAllTextAsync(_historyFilePath);
                var loaded = JsonSerializer.Deserialize<List<BatteryHistoryPoint>>(json);
                if (loaded != null)
                {
                    _history.AddRange(loaded);
                    
                    var cutoff = DateTime.UtcNow.AddHours(-24);
                    _history.RemoveAll(h => h.Timestamp < cutoff);
                    
                    _logService.LogInfo($"Loaded {_history.Count} battery history points.");
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to load battery history. Creating new.", ex);
            _history.Clear();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task SaveHistoryInternalAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = false });
            
            // Atomic write
            var tempFile = _historyFilePath + ".tmp";
            await File.WriteAllTextAsync(tempFile, json);
            File.Move(tempFile, _historyFilePath, true);
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to save battery history.", ex);
        }
    }

    public PowerStatusInfo GetCurrentStatus()
    {
        if (GetSystemPowerStatus(out var sps))
        {
            bool hasBattery = sps.BatteryFlag != 128 && sps.BatteryFlag != 255;
            return new PowerStatusInfo
            {
                IsPluggedIn = sps.ACLineStatus == 1,
                BatteryPercentage = sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : 0,
                HasBattery = hasBattery,
                IsCharging = hasBattery && sps.ACLineStatus == 1 && sps.BatteryFlag == 8
            };
        }

        return new PowerStatusInfo
        {
            IsPluggedIn = true,
            BatteryPercentage = 100,
            HasBattery = false,
            IsCharging = false
        };
    }

    public void Dispose()
    {
        StopHistoryCollection();
        _historyLock.Dispose();
    }
}
