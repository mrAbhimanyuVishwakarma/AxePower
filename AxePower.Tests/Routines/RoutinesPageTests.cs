using System;
using System.IO;
using System.Linq;
using AxePower.App.ViewModels;
using AxePower.Core.Models;
using AxePower.Core.Models.Routines;
using AxePower.Core.Services;

namespace AxePower.Tests.Routines;

/// <summary>Routines as the owner makes them: the model's rules, saving to settings, and the page.</summary>
public sealed class RoutinesPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerRoutinesPage", Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;

    public RoutinesPageTests() => _settings = new SettingsService(new LogService(), _dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RoutinesSurviveASaveAndReload_AndCloneDeeply()
    {
        var s = _settings.GetSettings();
        s.Routines.Add(RoutineTemplates.All.Single(t => t.Id == "battery-saver").Create());
        _settings.SaveSettings(s);

        var reloaded = new SettingsService(new LogService(), _dir);
        reloaded.LoadSettings();
        var routine = Assert.Single(reloaded.GetSettings().Routines);
        Assert.Equal("Battery saver", routine.Name);
        Assert.Equal(new[] { RoutineConditionKind.OnBattery, RoutineConditionKind.BatteryAtOrBelow }, routine.Conditions.Select(c => c.Kind));
        Assert.Equal(PowerMode.Eco, routine.Actions.Single().Mode);

        var copy = reloaded.GetSettings();
        copy.Routines[0].Conditions[1].Percent = 5;
        Assert.Equal(20, reloaded.GetSettings().Routines[0].Conditions[1].Percent);
    }

    [Fact]
    public void AnOldSettingsFile_HasNoRoutines()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ \"autoSwitchEnabled\": true }");
        _settings.LoadSettings();
        Assert.Empty(_settings.GetSettings().Routines);
    }

    [Fact]
    public void Problems_SayWhatIsMissing()
    {
        var routine = new Routine
        {
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.TimeBetween, From = "25:00" }, new RoutineCondition { Kind = RoutineConditionKind.AppRunning } },
            Actions = { new RoutineAction { Kind = RoutineActionKind.StartPowerTimer, Minutes = 0 } }
        };
        Assert.Equal(new[]
        {
            "Give the routine a name.",
            "Write times as hours and minutes, like 23:00.",
            "Pick the app to watch for.",
            "The timer must be 1 minute to 24 hours."
        }, routine.Problems());

        foreach (var template in RoutineTemplates.All.Where(t => t.Id != "gaming"))
            Assert.Empty(template.Create().Problems());
        Assert.Equal(new[] { "Pick the app to watch for." }, RoutineTemplates.All.Single(t => t.Id == "gaming").Create().Problems());
    }

    [Fact]
    public void Summary_ReadsAsASentence()
    {
        var gaming = RoutineTemplates.All.Single(t => t.Id == "gaming").Create();
        gaming.Conditions[0].App = @"C:\Games\cs2.exe";
        Assert.Equal("If Cs2 is running → Beast mode, close Discord", RoutineText.Summary(gaming));

        var night = RoutineTemplates.All.Single(t => t.Id == "night").Create();
        night.Conditions[0].Days = RoutineDays.Weekdays;
        Assert.Equal("If 23:00–07:00 on weekdays → Eco mode", RoutineText.Summary(night));
    }

    [Fact]
    public void StartingFromATemplate_OpensTheEditor_AndSavingKeepsIt()
    {
        var page = new RoutinesViewModel(_settings);
        Assert.False(page.HasRoutines);

        page.UseTemplateCommand.Execute(RoutineTemplates.All.Single(t => t.Id == "gaming"));
        Assert.True(page.IsEditing);

        page.SaveEditorCommand.Execute(null);
        Assert.True(page.IsEditing);
        Assert.Equal("Pick the app to watch for.", page.Editor!.Error);
        Assert.Empty(_settings.GetSettings().Routines);

        page.Editor.Conditions[0].App = "cs2.exe";
        page.SaveEditorCommand.Execute(null);

        Assert.False(page.IsEditing);
        var saved = Assert.Single(_settings.GetSettings().Routines);
        Assert.Equal("cs2.exe", saved.Conditions[0].App);
        Assert.Equal("If Cs2 is running → Beast mode, close Discord", page.Routines.Single().Summary);
    }

    [Fact]
    public void TheEditor_ChangesKindsAndReadsFieldsBack()
    {
        var page = new RoutinesViewModel(_settings);
        page.NewRoutineCommand.Execute(null);
        var editor = page.Editor!;
        editor.Name = "  Work  ";
        editor.IsAnySelected = true;
        editor.IsAllSelected = false;   // what unchecking the other radio button writes back: ignored
        editor.Conditions[0].Kind = RoutineConditionKind.BatteryAtOrBelow;
        editor.Conditions[0].PercentText = "30%";
        editor.Actions[0].Kind = RoutineActionKind.CloseApps;
        editor.Actions[0].AppsText = "Discord, chrome.exe; ";
        editor.AddActionCommand.Execute(null);
        editor.Actions[1].Message = "Focus time";

        var routine = editor.ToRoutine();
        Assert.Equal("Work", routine.Name);
        Assert.Equal(RoutineMatch.Any, routine.Match);
        Assert.Equal(30, routine.Conditions[0].Percent);
        Assert.Equal(new[] { "Discord", "chrome.exe" }, routine.Actions[0].Apps);
        Assert.Equal("Focus time", routine.Actions[1].Message);
        Assert.True(editor.Conditions[0].ShowPercent);
        Assert.False(editor.Conditions[0].ShowTime);
    }

    [Fact]
    public void TurningOffAndDeleting_AreSaved()
    {
        var s = _settings.GetSettings();
        s.Routines.Add(RoutineTemplates.All.Single(t => t.Id == "night").Create());
        _settings.SaveSettings(s);
        var page = new RoutinesViewModel(_settings) { ConfirmDelete = _ => true };

        page.Routines[0].IsEnabled = false;
        Assert.False(_settings.GetSettings().Routines[0].Enabled);
        Assert.Equal("Off", page.Routines[0].StatusText);

        page.Routines[0].DeleteCommand.Execute(null);
        Assert.Empty(_settings.GetSettings().Routines);
        Assert.False(page.HasRoutines);
    }

    [Fact]
    public void ToolsCard_CountsRoutines_AndOpensThePage()
    {
        string? went = null;
        var tools = new ToolsViewModel(new ToolsService(), settings: _settings) { NavigateTo = t => went = t };
        Assert.Equal("None yet", tools.RoutinesCard.StatusText);

        var s = _settings.GetSettings();
        s.Routines.Add(RoutineTemplates.All.Single(t => t.Id == "night").Create());
        _settings.SaveSettings(s);
        tools.RoutinesCard.Refresh();
        Assert.Equal("1 on", tools.RoutinesCard.StatusText);

        tools.RoutinesCard.OpenCommand.Execute(null);
        Assert.Equal("Routines", went);
    }
}
