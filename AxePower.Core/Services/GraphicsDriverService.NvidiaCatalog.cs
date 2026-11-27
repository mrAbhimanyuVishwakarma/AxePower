using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AxePower.Core.Services.SoftwareHub;

namespace AxePower.Core.Services;

public partial class GraphicsDriverService
{
    private const string NvidiaLookup = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx";
    private const string NvidiaDriverLookup = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

    public async Task<IReadOnlyList<NvidiaDriverRelease>> GetNvidiaDriversAsync(
        GraphicsAdapterInfo adapter, CancellationToken ct = default)
    {
        if (!adapter.IsNvidia || RuntimeInformation.OSArchitecture != Architecture.X64)
            return Array.Empty<NvidiaDriverRelease>();

        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(TimeSpan.FromSeconds(20));
        var series = await ReadNvidiaOptionsAsync(2, GetNvidiaProductType(adapter.Name), lookup.Token);
        var candidates = PrioritizeNvidiaSeries(series, adapter).ToArray();
        var model = NormalizeNvidiaModel(adapter.Name);
        var products = await Task.WhenAll(candidates.Select(async candidate =>
        {
            var families = await ReadNvidiaOptionsAsync(3, candidate.Id, lookup.Token);
            return families.Where(product => NormalizeNvidiaModel(product.Name) == model)
                .Select(product => (Series: candidate.Id, Product: product.Id)).ToArray();
        }));
        var matchingProducts = products.SelectMany(matches => matches).Distinct().ToArray();
        if (matchingProducts.Length != 1) return Array.Empty<NvidiaDriverRelease>();

        var (seriesId, productId) = matchingProducts[0];
        var systems = await ReadNvidiaOptionsAsync(4, seriesId, lookup.Token);
        string osName = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "Windows 11" : "Windows 10 64-bit";
        int osId = systems.FirstOrDefault(os => os.Name.Equals(osName, StringComparison.OrdinalIgnoreCase)).Id;
        if (osId == 0) return Array.Empty<NvidiaDriverRelease>();

        bool geforce = GetNvidiaProductType(adapter.Name) == 1;
        var channels = geforce ? new[] { "0", "1" } : new[] { "null" };
        var responses = await Task.WhenAll(channels.Select(channel => HttpClient.GetStringAsync(
            FormattableString.Invariant($"{NvidiaDriverLookup}?func=DriverManualLookup&psid={seriesId}&pfid={productId}&osID={osId}&languageCode=1033&isWHQL={(channel == "1" ? 0 : 1)}&beta=0&dltype=-1&dch=1&upCRD={channel}&qnf=null&ctk=0&sort1=0&numberOfResults=12"),
            lookup.Token)));

        return responses.SelectMany(json => ReadNvidiaReleases(json, adapter.IsNotebook))
            .OrderByDescending(release => release.Date)
            .ThenByDescending(release => release.Version)
            .DistinctBy(release => (release.Driver.Version, release.Driver.Badge))
            .Select(release => release.Driver)
            .Take(24)
            .ToArray();
    }

    private static int GetNvidiaProductType(string name)
    {
        if (name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("TITAN", StringComparison.OrdinalIgnoreCase)) return 11;
        if (name.Contains("Tesla", StringComparison.OrdinalIgnoreCase)) return 7;
        if (name.Contains("NVS", StringComparison.OrdinalIgnoreCase)) return 8;
        return 3;
    }

    private static IEnumerable<(string Name, int Id)> PrioritizeNvidiaSeries(
        IReadOnlyList<(string Name, int Id)> series, GraphicsAdapterInfo adapter)
    {
        var compatible = adapter.IsNotebook ? series.Where(item => IsNotebookSeries(item.Name)) : series;
        var model = Regex.Match(adapter.Name, @"\b(?:RTX|GTX|GTS|MX|GT)\s*(\d{3,4})\b", RegexOptions.IgnoreCase);
        if (model.Success && GetNvidiaProductType(adapter.Name) == 1)
        {
            string number = model.Groups[1].Value;
            string generation = number[..(number.Length == 4 ? 2 : 1)];
            var matchingSeries = compatible.Where(item => Regex.IsMatch(item.Name, $@"\b{generation}\d*M?\b",
                RegexOptions.IgnoreCase)).ToArray();
            if (matchingSeries.Length > 0) return matchingSeries.Take(8);
        }
        return compatible.Take(8);
    }

    private static bool IsNotebookSeries(string name) =>
        name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mobile", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeNvidiaModel(string model)
    {
        string name = Regex.Replace(model, @"\bNVIDIA(?: Corporation)?\b|\(R\)|\(TM\)|[®™]",
            "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+with\s+Max-Q(?:\s+Design)?$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name.Trim(), @"\s+", " ").ToUpperInvariant();
    }

    private static async Task<IReadOnlyList<(string Name, int Id)>> ReadNvidiaOptionsAsync(
        int type, int parent, CancellationToken ct)
    {
        string xml = await HttpClient.GetStringAsync(
            FormattableString.Invariant($"{NvidiaLookup}?TypeID={type}&ParentID={parent}"), ct);
        var options = new List<(string Name, int Id)>();
        foreach (var element in XDocument.Parse(xml).Descendants("LookupValue"))
        {
            string? name = (string?)element.Element("Name");
            if (!string.IsNullOrWhiteSpace(name) && int.TryParse((string?)element.Element("Value"), out int id))
                options.Add((name, id));
        }
        return options;
    }

    internal static IReadOnlyList<(NvidiaDriverRelease Driver, DateTime Date, Version Version)> ReadNvidiaReleases(
        string json, bool notebook)
    {
        using var document = JsonDocument.Parse(json);
        var results = new List<(NvidiaDriverRelease, DateTime, Version)>();
        if (!document.RootElement.TryGetProperty("IDS", out var records) || records.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("downloadInfo", out var details) ||
                details.ValueKind != JsonValueKind.Object)
                continue;

            string number = ReadText(details, "Version");
            string dateText = ReadText(details, "ReleaseDateTime");
            string url = ReadText(details, "DownloadURL");
            string title = Uri.UnescapeDataString(ReadText(details, "Name"));
            bool studio = title.Contains("Studio", StringComparison.OrdinalIgnoreCase);
            if (ReadText(details, "IsBeta") != "0" || (!studio && ReadText(details, "IsWHQL") != "1") ||
                !Version.TryParse(number, out var version) ||
                !DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !PackageInput.TryDownloadUrl(url, NvidiaDownloadHosts, out var address) ||
                !address.AbsolutePath.StartsWith("/Windows/", StringComparison.OrdinalIgnoreCase) ||
                PackageInput.SafeDownloadFileName(address, ".exe") is not string fileName ||
                !fileName.StartsWith(number + "-", StringComparison.Ordinal))
                continue;

            string badge = studio ? "STUDIO" :
                title.Contains("Game Ready", StringComparison.OrdinalIgnoreCase) ? "GAME READY" : "WHQL";
            results.Add((new NvidiaDriverRelease
            {
                Version = number,
                ReleaseDate = date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                Badge = badge,
                Description = title,
                DownloadUrlDesktop = notebook ? string.Empty : address.AbsoluteUri,
                DownloadUrlNotebook = notebook ? address.AbsoluteUri : string.Empty
            }, date, version));
        }
        return results;
    }

    private static string ReadText(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
}
