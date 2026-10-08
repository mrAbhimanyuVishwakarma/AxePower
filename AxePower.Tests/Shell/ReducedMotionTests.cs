using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using AxePower.App.Helpers;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>
/// Reduced motion (Prompt 28): with Windows' animation effects off, nothing in AxePower slides or
/// spins. Every animation in the app's XAML takes its duration from <see cref="MotionExtension"/>,
/// and every busy spinner hides through <see cref="MotionOnlyExtension"/>.
/// </summary>
[Collection(WpfCollection.Name)]
public class ReducedMotionTests
{
    private static string AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found"), "AxePower.App");
    }

    private static string[] AppFiles(string pattern) =>
        Directory.GetFiles(AppFolder(), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

    [Fact]
    public void Motion_IsInstant_WhenReduced_AndNormalOtherwise()
    {
        try
        {
            Motion.ReducedOverride = true;
            Assert.Equal(new Duration(TimeSpan.Zero), new MotionExtension("0:0:0.16").ProvideValue(null!));
            Assert.Equal(Visibility.Collapsed, new MotionOnlyExtension().ProvideValue(null!));

            Motion.ReducedOverride = false;
            Assert.Equal(new Duration(TimeSpan.FromMilliseconds(160)), new MotionExtension("0:0:0.16").ProvideValue(null!));
            Assert.Equal(Visibility.Visible, new MotionOnlyExtension().ProvideValue(null!));
        }
        finally
        {
            Motion.ReducedOverride = null;
        }
    }

    [Fact]
    public void EveryXamlAnimation_TakesItsDurationFromMotion()
    {
        var animation = new Regex(@"<(\w+Animation(UsingKeyFrames)?)\b[^>]*>", RegexOptions.Singleline);
        var offenders = AppFiles("*.xaml")
            .SelectMany(file => animation.Matches(File.ReadAllText(file))
                .Where(m => !m.Value.Contains("{a11y:Motion"))
                .Select(m => $"{Path.GetFileName(file)}: {m.Value.Split('\n')[0].Trim()}"))
            .ToList();
        Assert.True(offenders.Count == 0, "Animations that ignore reduced motion (use Duration=\"{a11y:Motion …}\"):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryBusySpinner_HidesWhenMotionIsReduced()
    {
        var spinner = new Regex(@"<ProgressBar\b[^>]*IsIndeterminate=""True""[^>]*>", RegexOptions.Singleline);
        var offenders = AppFiles("*.xaml")
            .SelectMany(file => spinner.Matches(File.ReadAllText(file))
                .Where(m => !m.Value.Contains("{a11y:MotionOnly}"))
                .Select(m => Path.GetFileName(file)))
            .ToList();
        Assert.True(offenders.Count == 0, "Indeterminate progress bars without Visibility=\"{a11y:MotionOnly}\":\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void CodeThatAnimates_ChecksMotionFirst()
    {
        var animates = new Regex(@"\bBeginAnimation\(|new\s+\w+Animation\(|CompositionTarget\.Rendering");
        var offenders = AppFiles("*.cs")
            .Where(file => animates.IsMatch(File.ReadAllText(file)) && !File.ReadAllText(file).Contains("Motion.IsReduced"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0, "Code that animates without checking Motion.IsReduced:\n" + string.Join("\n", offenders));
    }
}
