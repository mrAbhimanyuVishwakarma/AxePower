using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Diagnostics;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>Settings › Diagnostics: delete needs a yes, export needs a chosen file, and nothing else happens on its own.</summary>
public class DiagnosticsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_DiagVm_" + Guid.NewGuid().ToString("N"));
    private readonly DiagnosticsStore _store;
    private readonly LogService _log;
    private readonly FakePrompts _prompts = new();

    public DiagnosticsViewModelTests()
    {
        _store = new DiagnosticsStore(Path.Combine(_dir, "diag"));
        _log = new LogService(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DiagnosticsViewModel Create() =>
        new(_log, new SettingsService(new LogService(), Path.Combine(_dir, "settings")), _prompts, Path.Combine(_dir, "UninstallLogs"));

    [Fact]
    public void TheSummary_SaysWhatIsStored_AndForHowLong()
    {
        _log.LogInfo("[Vm] one");
        var vm = Create();

        Assert.True(vm.HasFiles);
        Assert.StartsWith("1 file(s)", vm.SummaryText);
        Assert.Contains("14 days or 20 MB", vm.RetentionText);
        Assert.Contains("Never uploaded", vm.RetentionText);
    }

    [Fact]
    public void DeleteAll_AskedAndRefused_KeepsEverything()
    {
        _log.LogInfo("[Vm] keep me");
        var vm = Create();
        _prompts.ConfirmAnswer = false;

        vm.DeleteAllCommand.Execute(null);

        Assert.Equal(1, _prompts.ConfirmCalls);
        Assert.Single(_store.Files());
    }

    [Fact]
    public void DeleteAll_Confirmed_RemovesEveryFile_AndWritesNoTraceOfItself()
    {
        for (int i = 0; i < 3; i++) _log.LogInfo($"[Vm] event {i}");
        var vm = Create();
        _prompts.ConfirmAnswer = true;

        vm.DeleteAllCommand.Execute(null);

        Assert.Empty(_store.Files());
        Assert.False(vm.HasFiles);
        Assert.Equal("Nothing stored", vm.SummaryText);
        Assert.StartsWith("Deleted 1 file(s)", vm.StatusText);
    }

    [Fact]
    public void Export_IsOnlyWritten_WhenTheUserChoosesWhere()
    {
        _log.LogInfo("[Vm] for the bundle");
        var bundle = Create().CreateBundle()!;

        _prompts.SavePath = null; // the user cancelled the Save dialog
        bundle.SaveCommand.Execute(null);
        Assert.Empty(Directory.GetFiles(_dir, "*.zip"));

        _prompts.SavePath = Path.Combine(_dir, "chosen.zip");
        bundle.SaveCommand.Execute(null);

        using var zip = ZipFile.OpenRead(_prompts.SavePath);
        Assert.Equal(bundle.Items.Select(i => i.Name), zip.Entries.Select(e => e.FullName));
        Assert.Contains(_store.ReadEntries(), e => e.EventId == LogEvents.DiagnosticsExported.Id);
    }

    [Fact]
    public void TickingAnOption_ChangesThePreview_BeforeAnythingIsSaved()
    {
        _log.LogInfo("[Vm] preview");
        var bundle = Create().CreateBundle()!;
        Assert.DoesNotContain(bundle.Items, i => i.Name == "settings.json");

        bundle.IncludeSettings = true;

        Assert.Contains(bundle.Items, i => i.Name == "settings.json");
        Assert.Contains("settings.json", bundle.Items[0].Content); // the README lists it too
        Assert.Null(_prompts.SavePath);
    }

    [Fact]
    public void WithoutAStore_NothingCanBeInspectedOrDeleted()
    {
        var vm = new DiagnosticsViewModel(new LogService(), new SettingsService(new LogService(), Path.Combine(_dir, "settings")), _prompts);

        Assert.False(vm.IsStored);
        Assert.Null(vm.CreateBundle());
        vm.DeleteAllCommand.Execute(null);
        Assert.Equal(0, _prompts.ConfirmCalls);
    }

    private sealed class FakePrompts : IDiagnosticsPrompts
    {
        public bool ConfirmAnswer { get; set; }
        public int ConfirmCalls { get; private set; }
        public string? SavePath { get; set; }

        public bool ConfirmDeleteAll(DiagnosticsSummary summary)
        {
            ConfirmCalls++;
            return ConfirmAnswer;
        }

        public string? ChooseBundlePath(string suggestedName) => SavePath;

        public void ShowBundle(DiagnosticsBundleViewModel bundle) { }
    }
}
