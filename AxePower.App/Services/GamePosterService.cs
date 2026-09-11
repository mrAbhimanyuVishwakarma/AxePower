using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AxePower.App.Services;

public static class GamePosterService
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxePower", "CoverArt");

    static GamePosterService()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
            {
                Directory.CreateDirectory(CacheDir);
            }
        }
        catch { }
    }

    public static string? TryGetSteamLibraryCachePoster(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        try
        {
            string? steamPath = null;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                steamPath = key?.GetValue("SteamPath") as string;
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                steamPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            }

            if (!Directory.Exists(steamPath)) return null;

            string libCache = Path.Combine(steamPath, "appcache", "librarycache");
            if (!Directory.Exists(libCache)) return null;

            // 1. Direct file: <appId>_header.jpg
            string direct = Path.Combine(libCache, $"{appId}_header.jpg");
            if (File.Exists(direct)) return direct;

            // 2. Subfolder: <appId>\*\library_header.jpg or *header*.jpg or *hero*.jpg
            string appDir = Path.Combine(libCache, appId);
            if (Directory.Exists(appDir))
            {
                var headers = Directory.GetFiles(appDir, "*header*.jpg", SearchOption.AllDirectories);
                if (headers.Length > 0) return headers[0];

                var heroes = Directory.GetFiles(appDir, "*hero*.jpg", SearchOption.AllDirectories);
                if (heroes.Length > 0) return heroes[0];

                var anyJpg = Directory.GetFiles(appDir, "*.jpg", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("600x900", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (anyJpg.Length > 0) return anyJpg[0];
            }
        }
        catch { }

        return null;
    }

    public static string? ResolvePosterPath(string exeName, string? steamAppId = null)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return null;

        // 0. Check local Steam librarycache for instant 16:9 banner
        if (!string.IsNullOrEmpty(steamAppId))
        {
            string? localCache = TryGetSteamLibraryCachePoster(steamAppId);
            if (!string.IsNullOrEmpty(localCache)) return localCache;
        }

        string clean = Path.GetFileNameWithoutExtension(exeName).ToLowerInvariant();

        // 1. Check custom / downloaded 16:9 landscape header banners
        string headerFile = Regex.Replace(clean, @"[^a-z0-9_-]", "_") + "_header.jpg";
        string headerPath = Path.Combine(CacheDir, headerFile);
        if (File.Exists(headerPath)) return headerPath;

        if (clean.Contains("gta") || clean.Contains("grand_theft") || clean.Contains("grandtheft"))
        {
            string gtaHeader = Path.Combine(CacheDir, "gta5_enhanced_header.jpg");
            if (File.Exists(gtaHeader)) return gtaHeader;
        }

        if (clean.Contains("cyberpunk"))
        {
            string cpHeader = Path.Combine(CacheDir, "cyberpunk2077_header.jpg");
            if (File.Exists(cpHeader)) return cpHeader;
            return "pack://application:,,,/Resources/Posters/cyberpunk.jpg";
        }

        if (clean.Contains("delta"))
        {
            string dfHeader = Path.Combine(CacheDir, "delta_force_header.jpg");
            if (File.Exists(dfHeader)) return dfHeader;
        }

        // 2. Check bundled posters
        if (clean.Contains("forza")) return "pack://application:,,,/Resources/Posters/forza.jpg";
        if (clean.Contains("valorant")) return "pack://application:,,,/Resources/Posters/valorant.jpg";

        // 3. Check regular cached poster
        string safeFileName = Regex.Replace(clean, @"[^a-z0-9_-]", "_") + ".jpg";
        string cachedPath = Path.Combine(CacheDir, safeFileName);
        if (File.Exists(cachedPath))
        {
            return cachedPath;
        }

        return null;
    }

    public static async Task<string?> FetchCoverArtAsync(string exeName, string? steamAppId = null)
    {
        string? existing = ResolvePosterPath(exeName, steamAppId);
        if (!string.IsNullOrEmpty(existing)) return existing;

        try
        {
            string clean = Path.GetFileNameWithoutExtension(exeName).ToLowerInvariant();
            string safeFileName = Regex.Replace(clean, @"[^a-z0-9_-]", "_") + "_header.jpg";
            string cachedPath = Path.Combine(CacheDir, safeFileName);

            // Direct Steam AppId download across multiple CDNs
            if (!string.IsNullOrEmpty(steamAppId))
            {
                string[] cdnUrls = {
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/header.jpg",
                    $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{steamAppId}/header.jpg",
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steamAppId}/capsule_616x353.jpg",
                    $"https://steamcdn-a.akamaihd.net/steam/apps/{steamAppId}/header.jpg"
                };

                foreach (var headerUrl in cdnUrls)
                {
                    try
                    {
                        var resp = await _httpClient.GetAsync(headerUrl);
                        if (resp.IsSuccessStatusCode)
                        {
                            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                            if (bytes.Length > 1000)
                            {
                                await File.WriteAllBytesAsync(cachedPath, bytes);
                                return cachedPath;
                            }
                        }
                    }
                    catch { }
                }
            }

            string searchTerm = GetCleanTitle(exeName);
            string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(searchTerm)}&l=english&cc=US";

            string json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var first = items[0];
                if (first.TryGetProperty("id", out var idElem))
                {
                    long appId = idElem.GetInt64();
                    // Prioritize 16:9 landscape header.jpg
                    string coverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                    var resp = await _httpClient.GetAsync(coverUrl);
                    if (!resp.IsSuccessStatusCode)
                    {
                        coverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg";
                        resp = await _httpClient.GetAsync(coverUrl);
                    }

                    if (resp.IsSuccessStatusCode)
                    {
                        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(cachedPath, bytes);
                        return cachedPath;
                    }
                }
            }
        }
        catch
        {
            // Graceful offline fallback
        }

        return null;
    }

    public static string GetCleanTitle(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return "Game";

        string name = Path.GetFileNameWithoutExtension(exeName);

        // Predefined popular names
        if (name.Equals("Cyberpunk2077", StringComparison.OrdinalIgnoreCase)) return "Cyberpunk 2077";
        if (name.Equals("ForzaHorizon5", StringComparison.OrdinalIgnoreCase)) return "Forza Horizon 5";
        if (name.Contains("valorant", StringComparison.OrdinalIgnoreCase)) return "Valorant";
        if (name.Equals("cs2", StringComparison.OrdinalIgnoreCase)) return "Counter-Strike 2";
        if (name.Equals("GTA5", StringComparison.OrdinalIgnoreCase)) return "Grand Theft Auto V";
        if (name.Equals("Witcher3", StringComparison.OrdinalIgnoreCase)) return "The Witcher 3";
        if (name.Equals("EldenRing", StringComparison.OrdinalIgnoreCase)) return "Elden Ring";
        if (name.Equals("Apex", StringComparison.OrdinalIgnoreCase)) return "Apex Legends";

        // Separate camelCase / PascalCase and clean up underscores
        name = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        name = Regex.Replace(name, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        name = name.Replace('_', ' ').Replace('-', ' ');

        return name.Trim();
    }
}
