using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxePower.Core.Helpers;
using AxePower.Core.Models;
using System.Diagnostics.CodeAnalysis;

namespace AxePower.Core.Services;

public class SettingsService
{
    private readonly string _settingsFilePath;
    private readonly string _settingsBackupFilePath;
    private readonly string _settingsTempFilePath;
    private readonly LogService _logService;
    private AppSettings _currentSettings = new();

    public string SettingsDirectory { get; }

    public SettingsService(LogService logService)
        : this(logService, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower"))
    {
    }

    /// <summary>Keeps settings in <paramref name="settingsDirectory"/>; tests use a temp folder so they never touch the real file.</summary>
    public SettingsService(LogService logService, string settingsDirectory)
    {
        _logService = logService;
        SettingsDirectory = settingsDirectory;
        Directory.CreateDirectory(SettingsDirectory);
        
        _settingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
        _settingsBackupFilePath = Path.Combine(SettingsDirectory, "settings.backup.json");
        _settingsTempFilePath = Path.Combine(SettingsDirectory, "settings.temp.json");
    }

    public AppSettings GetSettings()
    {
        return _currentSettings.Clone();
    }

    public void LoadSettings()
    {
        if (TryLoadFile(_settingsFilePath, out var settings))
        {
            _currentSettings = settings;
            _logService.LogInfo("Settings loaded successfully.");
            return;
        }

        if (TryLoadFile(_settingsBackupFilePath, out var backupSettings))
        {
            _currentSettings = backupSettings;
            _logService.LogWarning("Settings loaded from backup.");
            SaveSettings(_currentSettings);
            return;
        }

        _logService.LogInfo("Settings file not found or invalid. Using default settings.");
        SaveSettings(new AppSettings());
    }

    private bool TryLoadFile(string path, [NotNullWhen(true)] out AppSettings? settings)
    {
        settings = null;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                if (loaded != null)
                {
                    // A null in the file ("AppRules": null) would otherwise reach Clone() and every
                    // GetSettings() call would throw.
                    var repair = JsonNullRepair.Repair(loaded);
                    if (!repair.IsComplete)
                    {
                        _logService.LogWarning($"Settings in {path} are incomplete ({repair.Describe()}).");
                        return false;
                    }
                    if (repair.Changed)
                        _logService.LogWarning($"Settings in {path} had empty values ({repair.Describe()}).");

                    settings = loaded;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"Failed to load settings from {path}.", ex);
        }
        return false;
    }

    /// <param name="notifyListeners">
    /// False for UI-only state (e.g. sidebar group expansion) that no service reacts to, so a
    /// click doesn't re-evaluate the power mode or rewrite the startup task.
    /// </param>
    public void SaveSettings(AppSettings settings, bool notifyListeners = true)
    {
        try
        {
            _currentSettings = settings.Clone();
            var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });
            
            // Atomic write
            File.WriteAllText(_settingsTempFilePath, json);
            
            if (File.Exists(_settingsFilePath))
            {
                File.Replace(_settingsTempFilePath, _settingsFilePath, _settingsBackupFilePath, true);
            }
            else
            {
                File.Move(_settingsTempFilePath, _settingsFilePath);
            }
            
            _logService.LogInfo("Settings saved successfully.");
            
            if (notifyListeners)
            {
                OnSettingsChanged?.Invoke(this, _currentSettings);
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to save settings.", ex);
        }
    }

    public event EventHandler<AppSettings>? OnSettingsChanged;
}
