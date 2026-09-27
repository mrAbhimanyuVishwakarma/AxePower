using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AxePower.App.Views;

namespace AxePower.Tests;

/// <summary>
/// The About page carries the Privacy Policy and Terms dialogs, and the website carries the same
/// policies. These pin the parts that are easy to break without noticing: the page must load
/// (every style it references resolves), and the site must never load analytics before consent.
/// </summary>
[Collection(WpfCollection.Name)]
public class LegalPagesTests(WpfApplicationFixture wpf)
{
    /// <summary>The repo's AppWebsite folder, found from this source file so it works from any build output.</summary>
    private static string WebsiteFolder([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "AppWebsite"));
        Assert.True(Directory.Exists(folder), $"Website folder not found at {folder}");
        return folder;
    }

    [Fact]
    public void AboutPage_LoadsWithItsPrivacyAndTermsDialogs() =>
        wpf.Invoke(() =>
        {
            var view = new AboutView();
            var text = string.Join("\n", Texts(view));
            Assert.Contains("Privacy Policy & Credits", text);
            Assert.Contains("Terms of Service", text);
            Assert.Contains("THIRD_PARTY_NOTICES.md", text);
        });

    [Fact]
    public void EveryWebPage_LoadsAnalyticsOnlyThroughTheConsentScript_AndLinksThePolicies()
    {
        var pages = Directory.GetFiles(WebsiteFolder(), "*.html");
        Assert.NotEmpty(pages);
        foreach (var page in pages)
        {
            string html = File.ReadAllText(page);
            string name = Path.GetFileName(page);
            Assert.False(html.Contains("googletagmanager", StringComparison.OrdinalIgnoreCase), $"{name} loads analytics directly");
            Assert.Contains("<script src=\"consent.js\"></script>", html);
            Assert.Contains("href=\"privacy.html\"", html);
            Assert.Contains("href=\"terms.html\"", html);
            Assert.Contains("data-cookie-settings", html);
        }
    }

    [Fact]
    public void TheWebsite_MakesNoAbsoluteNoNetworkClaim()
    {
        foreach (var page in Directory.GetFiles(WebsiteFolder(), "*.html"))
        {
            string text = File.ReadAllText(page);
            Assert.DoesNotMatch(new Regex("zero external network calls", RegexOptions.IgnoreCase), text);
            Assert.DoesNotMatch(new Regex("website do not collect", RegexOptions.IgnoreCase), text);
        }
    }

    /// <summary>Accounts exist since 2.0.8 (optional). No page may still promise there are none.</summary>
    [Fact]
    public void NothingClaimsThereAreNoAccounts_AndThePolicyDescribesThem() =>
        wpf.Invoke(() =>
        {
            var app = string.Join("\n", Texts(new AboutView()));
            var noAccounts = new Regex(@"\bno accounts\b", RegexOptions.IgnoreCase);
            Assert.DoesNotMatch(noAccounts, app);
            Assert.Contains("Your AxePower account (optional)", app);
            Assert.Contains("Supabase", app);
            Assert.Contains("Your account and friends", app);

            foreach (var page in Directory.GetFiles(WebsiteFolder(), "*.html"))
            {
                Assert.DoesNotMatch(noAccounts, File.ReadAllText(page));
            }
            var privacy = File.ReadAllText(Path.Combine(WebsiteFolder(), "privacy.html"));
            Assert.Contains("Your AxePower account (optional)", privacy);
            Assert.Contains("Supabase", privacy);
        });

    /// <summary>The app's policy and terms are the website's; their "Last updated" dates must match.</summary>
    [Fact]
    public void TheAppAndWebsitePolicies_ShareTheSameDate() =>
        wpf.Invoke(() =>
        {
            var dateText = new Regex(@"Last updated: (\d{1,2} \w+ \d{4})");
            var appDates = Texts(new AboutView()).Select(t => dateText.Match(t)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
            var siteDates = new[] { "privacy.html", "terms.html" }
                .Select(f => dateText.Match(File.ReadAllText(Path.Combine(WebsiteFolder(), f))).Groups[1].Value).Distinct().ToList();

            Assert.Single(appDates);
            Assert.Equal(appDates, siteDates);
        });

    private static System.Collections.Generic.IEnumerable<string> Texts(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is System.Windows.Controls.TextBlock block) yield return block.Text;
            foreach (var nested in Texts(child)) yield return nested;
        }
    }
}
