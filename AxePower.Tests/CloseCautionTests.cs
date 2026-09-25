using System.Collections.Generic;
using AxePower.Core.Services;

namespace AxePower.Tests;

/// <summary>Which apps and services Game Mode's booster warns about before closing them.</summary>
public class CloseCautionTests
{
    [Theory]
    [InlineData("RtkAudUService64")]
    [InlineData("NahimicSvc64")]
    [InlineData("LenovoVantageService")]
    [InlineData("NVIDIA Share")]
    [InlineData("SynTPEnh")]
    [InlineData("iCUE")]
    public void HardwareHelpers_AreCaution_EvenWithoutAWindow(string process)
    {
        Assert.Contains("Drives", CloseCaution.ForApp(process, hasOpenWindow: false));
    }

    [Fact]
    public void AnAppWithAnOpenWindow_IsCaution_ForUnsavedWork()
    {
        Assert.Contains("unsaved", CloseCaution.ForApp("Antigravity", hasOpenWindow: true));
    }

    [Theory]
    [InlineData("PhoneExperienceHost")]
    [InlineData("WmiApSrv")]
    [InlineData("Steam")]
    public void OrdinaryBackgroundApps_AreSafe(string process)
    {
        Assert.Null(CloseCaution.ForApp(process, hasOpenWindow: false));
    }

    [Fact]
    public void OnlyWindowsUpdateMedic_IsACautionService()
    {
        Assert.NotNull(CloseCaution.ForService("WaaSMedicSvc"));
        Assert.Null(CloseCaution.ForService("Spooler"));
        Assert.Null(CloseCaution.ForService("SysMain"));
    }

    [Fact]
    public void ServiceRows_NotifySelection_SoSelectAllUpdatesTheCheckboxes()
    {
        var row = new SystemServiceObject { ServiceName = "SysMain" };
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.IsSelected = true;

        Assert.Contains(nameof(SystemServiceObject.IsSelected), changed);
    }

    [Fact]
    public void ServicesToOptimize_SurvivesAClone()
    {
        var settings = new AxePower.Core.Models.AppSettings { ServicesToOptimize = new() { "SysMain" } };

        Assert.Equal(new[] { "SysMain" }, settings.Clone().ServicesToOptimize);
    }
}
