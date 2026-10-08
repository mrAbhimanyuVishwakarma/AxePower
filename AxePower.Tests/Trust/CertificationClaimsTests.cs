using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AxePower.Tests.Trust;

/// <summary>
/// Prompt 34: no public surface claims an audit or certification that has not happened. The
/// reviewer packages in docs/review exist so a real review can take place; until its findings are
/// in REVIEW_FINDINGS.md, nothing may say AxePower was audited, certified or penetration-tested.
/// </summary>
public class CertificationClaimsTests
{
    private static readonly Regex Claim = new(
        @"\b(certified|certification|audited|security audit|third[- ]party audit|pen(etration)?[- ]?test(ed)?|military[- ]grade|bank[- ]grade|unhackable|SOC ?2|ISO ?27001|independently (verified|reviewed|tested))\b",
        RegexOptions.IgnoreCase);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void PublicPages_MakeNoAuditOrCertificationClaim()
    {
        var root = Root();
        var surfaces = new[] { "README.md", "RELEASE_README.md" }
            .Select(f => Path.Combine(root, f))
            .Concat(Directory.GetFiles(Path.Combine(root, "AppWebsite"), "*.html"))
            .Concat(Directory.GetFiles(Path.Combine(root, "AxePower.App", "Views"), "*.xaml", SearchOption.AllDirectories))
            .Where(File.Exists);

        var offenders = surfaces
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => Claim.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Unsupported audit/certification claims:\n" + string.Join("\n", offenders));
    }
}
