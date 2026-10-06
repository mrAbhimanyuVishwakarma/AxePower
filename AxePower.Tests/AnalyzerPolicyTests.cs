using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AxePower.Tests;

public class AnalyzerPolicyTests
{
    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Directory.Build.props")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Directory.Build.props.");
    }

    [Fact]
    public void DirectoryBuildProps_EnablesNetAnalyzersAndTreatWarningsAsErrors()
    {
        var repoRoot = FindRepoRoot();
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist.");

        var content = File.ReadAllText(propsPath);
        Assert.Contains("<EnableNETAnalyzers>true</EnableNETAnalyzers>", content);
        Assert.Contains("<AnalysisLevel>latest</AnalysisLevel>", content);
        Assert.Contains("TreatWarningsAsErrors", content);
    }

    [Fact]
    public void EditorConfig_ContainsSecurityAndReliabilityAnalyzerRules()
    {
        var repoRoot = FindRepoRoot();
        var editorConfigPath = Path.Combine(repoRoot, ".editorconfig");
        Assert.True(File.Exists(editorConfigPath), ".editorconfig must exist.");

        var content = File.ReadAllText(editorConfigPath);
        Assert.Contains("dotnet_diagnostic.CA2000.severity", content);
        Assert.Contains("dotnet_diagnostic.CA2100.severity", content);
        Assert.Contains("dotnet_diagnostic.CA5350.severity", content);
    }

    [Fact]
    public void NoNewGlobalSuppressions_Exist()
    {
        var repoRoot = FindRepoRoot();

        // 1. Directory.Build.props must not introduce blanket NoWarn
        var propsContent = File.ReadAllText(Path.Combine(repoRoot, "Directory.Build.props"));
        Assert.DoesNotContain("<NoWarn>", propsContent);

        // 2. AxePower.App must have zero NoWarn
        var appCsproj = File.ReadAllText(Path.Combine(repoRoot, "AxePower.App", "AxePower.App.csproj"));
        Assert.DoesNotContain("<NoWarn>", appCsproj);

        // 3. AxePower.Tests must have zero NoWarn
        var testsCsproj = File.ReadAllText(Path.Combine(repoRoot, "AxePower.Tests", "AxePower.Tests.csproj"));
        Assert.DoesNotContain("<NoWarn>", testsCsproj);

        // 4. AxePower.Core has none either: the CS8600/CS8602/CS8604 suppression was removed in Prompt 22
        var coreCsproj = File.ReadAllText(Path.Combine(repoRoot, "AxePower.Core", "AxePower.Core.csproj"));
        Assert.DoesNotContain("<NoWarn>", coreCsproj);
    }

    [Fact]
    public void Core_DoesNotSilenceNullabilityWithNullForgivingDefaults()
    {
        // Prompt 22 replaced every "null!" and "default!" with an honest annotation
        // ([NotNullWhen], string?) or restructured control flow. Keep it that way.
        var core = Path.Combine(FindRepoRoot(), "AxePower.Core");
        var offenders = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
            .Where(x => Regex.IsMatch(x.line, @"\b(null|default)!"))
            .Select(x => $"{Path.GetRelativePath(core, x.f)}:{x.i + 1}")
            .ToList();
        Assert.True(offenders.Count == 0, "null!/default! in Core: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Core_HasNoNullablePragmasOrFileLevelOptOuts()
    {
        var core = Path.Combine(FindRepoRoot(), "AxePower.Core");
        var offenders = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"#nullable\s+disable|#pragma\s+warning\s+disable\s+CS86"))
            .Select(f => Path.GetRelativePath(core, f))
            .ToList();
        Assert.True(offenders.Count == 0, "Nullable opt-outs in Core: " + string.Join(", ", offenders));
    }

    [Fact]
    public void WarningsDebtLedger_ExistsAndDocumentsLegacyDebt()
    {
        var repoRoot = FindRepoRoot();
        var ledgerPath = Path.Combine(repoRoot, "docs", "engineering", "WARNINGS_DEBT.md");
        Assert.True(File.Exists(ledgerPath), "docs/engineering/WARNINGS_DEBT.md must exist.");

        var content = File.ReadAllText(ledgerPath);
        Assert.Contains("CS8600", content);
        Assert.Contains("CS8602", content);
        Assert.Contains("CS8604", content);
        Assert.Contains("Prompt 22", content);
    }
}
