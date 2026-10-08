using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AxePower.Core.Services.Trust;
using Xunit;

namespace AxePower.Tests.Trust;

/// <summary>
/// Privacy claims are specific and testable (Prompt 26): no public surface makes an absolute claim,
/// the data-flow inventory (docs/security/PRIVACY.md) matches the app's own list, the surfaces agree
/// on the claims they do make, and the code really has no analytics or ads.
/// </summary>
public class PrivacyClaimsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>Everything a user reads about privacy: docs, release notes, website, in-app pages, installer.</summary>
    private static IEnumerable<string> PublicSurfaces()
    {
        var root = RepoRoot();
        yield return "README.md";
        yield return "RELEASE_README.md";
        foreach (var html in Directory.GetFiles(Path.Combine(root, "AppWebsite"), "*.html")) yield return Path.GetRelativePath(root, html);
        yield return Path.Combine("AxePower.App", "Views", "AboutView.xaml");
        yield return Path.Combine("AxePower.App", "Views", "SettingsView.xaml");
        yield return Path.Combine("AxePower.App", "Views", "TrustView.xaml");
        foreach (var iss in Directory.GetFiles(Path.Combine(root, "Installer"), "*.iss")) yield return Path.GetRelativePath(root, iss);
    }

    [Fact]
    public void NoPublicSurface_MakesAnAbsolutePrivacyClaim()
    {
        var banned = new Regex(
            @"100\s*%\s*local|zero\s+telemetry|zero\s+(remote\s+)?data\s+collection|zero\s+analytics|no\s+telemetry|completely\s+private|fully\s+offline|never\s+leaves?\s+your\s+(computer|pc|machine)(?!\s+on\s+(its|their)\s+own)",
            RegexOptions.IgnoreCase);
        var found = PublicSurfaces()
            .SelectMany(f => banned.Matches(Read(f)).Select(m => $"{f}: \"{m.Value}\""))
            .ToList();
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void TheDataFlowInventory_ListsEverythingTheAppDoes()
    {
        var doc = Read(Path.Combine("docs", "security", "PRIVACY.md"));
        var missing = new List<string>();
        missing.AddRange(TrustInventory.DataStores.Select(s => s.Name).Where(n => !doc.Contains(n, StringComparison.Ordinal)));
        missing.AddRange(TrustInventory.CloudData.Select(c => c.Name).Where(n => !doc.Contains(n, StringComparison.Ordinal)));
        missing.AddRange(TrustInventory.Connections.Select(c => c.Name).Where(n => !doc.Contains(n, StringComparison.Ordinal)));
        missing.AddRange(TrustInventory.Listeners.Select(l => l.Name).Where(n => !doc.Contains(n, StringComparison.Ordinal)));
        var hosts = TrustInventory.Connections
            .SelectMany(c => Regex.Matches(c.Hosts, @"\b[a-z0-9-]+(\.[a-z0-9-]+)+\.[a-z]{2,}\b|\b[a-z0-9-]+\.(com|net|dev|co)\b").Select(m => m.Value))
            .Distinct();
        missing.AddRange(hosts.Where(h => !doc.Contains(h, StringComparison.OrdinalIgnoreCase)).Select(h => "host " + h));
        Assert.True(missing.Count == 0, "Missing from PRIVACY.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void ThePublicSurfaces_AgreeOnWhatTheyClaim()
    {
        var website = Read(Path.Combine("AppWebsite", "privacy.html"));
        var inApp = Read(Path.Combine("AxePower.App", "Views", "AboutView.xaml"));
        foreach (var (name, text) in new[] { ("privacy.html", website), ("AboutView.xaml", inApp), ("README.md", Read("README.md")) })
        {
            Assert.Matches(new Regex("no (ads|advertising)", RegexOptions.IgnoreCase), text);
            Assert.Matches(new Regex("no (usage )?analytics", RegexOptions.IgnoreCase), text);
        }
        // The one thing that happens on its own is disclosed in both privacy texts.
        Assert.Contains("Check for updates automatically", website);
        Assert.Contains("Check for updates automatically", inApp);
        // Every kind of connection is named on the website's privacy page.
        foreach (var keyword in new[] { "Supabase", "YouTube", "lyrics", "Steam", "GitHub", "drivers", "WinGet", "Trust &amp; Permissions", "turned off" })
            Assert.Contains(keyword, website, StringComparison.OrdinalIgnoreCase);
        // Sign-in providers match the code's list.
        foreach (var provider in Enum.GetNames(typeof(AxePower.Core.Services.Accounts.AccountProvider)))
            Assert.Contains(provider, website, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCode_HasNoAnalyticsOrAdLibraries()
    {
        var root = RepoRoot();
        // Analytics and ad SDK package names and their collection hosts (not bare words, which match
        // ordinary code such as TrayMouseDoubleClick or a volume "amplitude").
        var forbidden = new Regex(
            @"google-analytics\.com|googletagmanager\.com|doubleclick\.net|app-measurement\.com|mixpanel\.com|api\.segment\.io|amplitude\.com|posthog\.com|sentry\.io|" +
            @"applicationinsights|Microsoft\.AppCenter|Firebase|AdMob|""Sentry""|""Mixpanel""|""PostHog""|Segment\.Analytics",
            RegexOptions.IgnoreCase);
        var files = new[] { "AxePower.Core", "AxePower.App" }
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".csproj") || f.EndsWith(".xaml") || f.EndsWith("packages.lock.json"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var hits = files.Where(f => forbidden.IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.True(hits.Count == 0, "Analytics or ad references in the desktop app: " + string.Join(", ", hits));
    }

    [Fact]
    public void WebsiteAnalytics_LoadOnlyThroughTheConsentScript()
    {
        var root = RepoRoot();
        foreach (var html in Directory.GetFiles(Path.Combine(root, "AppWebsite"), "*.html"))
            Assert.DoesNotContain("googletagmanager", File.ReadAllText(html), StringComparison.OrdinalIgnoreCase);
        var consent = Read(Path.Combine("AppWebsite", "consent.js"));
        var load = consent.IndexOf("function loadAnalytics", StringComparison.Ordinal);
        var accept = consent.IndexOf("function accept", StringComparison.Ordinal);
        Assert.True(load >= 0 && accept >= 0);
        Assert.Contains("allow_ad_personalization_signals: false", consent);
    }
}
