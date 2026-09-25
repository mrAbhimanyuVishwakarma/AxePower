using System;
using System.Linq;
using System.Xml.Linq;
using AxePower.App.ViewModels;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.Tests;

public class BatteryCapacityTests
{
    // Trimmed from a real `powercfg /batteryreport /xml` (2026-09-25): weekly entries, then daily ones
    private const string Report = """
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <History>
            <HistoryEntry StartDate="2026-08-27T23:12:17Z" EndDate="2026-09-03T23:12:17Z" DesignCapacity="60000" FullChargeCapacity="53649" CycleCount="194" BatteryChanged="0" />
            <HistoryEntry StartDate="2026-09-03T23:12:17Z" EndDate="2026-09-10T23:12:17Z" DesignCapacity="60000" FullChargeCapacity="53776" CycleCount="199" BatteryChanged="0" />
            <HistoryEntry StartDate="2026-09-18T18:30:00Z" EndDate="2026-09-19T18:29:59Z" DesignCapacity="60000" FullChargeCapacity="53298" CycleCount="205" BatteryChanged="0" />
            <HistoryEntry StartDate="2026-09-23T18:30:00Z" EndDate="2026-09-24T18:29:59Z" DesignCapacity="60000" FullChargeCapacity="52777" CycleCount="207" BatteryChanged="0" />
          </History>
        </BatteryReport>
        """;

    [Fact]
    public void ParsesTheReportHistory_OldestFirst_WithTheDesignCapacity()
    {
        var history = BatteryService.ParseCapacityHistory(XDocument.Parse(Report))!;

        Assert.Equal(60000, history.DesignMwh);
        Assert.Equal(new[] { 53649d, 53776, 53298, 52777 }, history.Points.Select(p => p.FullChargeMwh));
        Assert.Equal(new DateTime(2026, 9, 24, 18, 29, 59, DateTimeKind.Utc), history.Points[^1].EndUtc);
    }

    [Fact]
    public void AReplacedBattery_StartsANewHistory()
    {
        var xml = Report.Replace("FullChargeCapacity=\"53298\" CycleCount=\"205\" BatteryChanged=\"0\"",
                                 "FullChargeCapacity=\"59900\" CycleCount=\"1\" BatteryChanged=\"1\"");
        var history = BatteryService.ParseCapacityHistory(XDocument.Parse(xml))!;

        Assert.Equal(new[] { 59900d, 52777 }, history.Points.Select(p => p.FullChargeMwh));
    }

    [Fact]
    public void TooLittleHistory_IsNone()
    {
        var xml = """<BatteryReport><History><HistoryEntry EndDate="2026-09-24T18:29:59Z" DesignCapacity="60000" FullChargeCapacity="52777" /></History></BatteryReport>""";
        Assert.Null(BatteryService.ParseCapacityHistory(XDocument.Parse(xml)));
    }

    [Theory]
    [InlineData(95, "Excellent")]
    [InlineData(88, "Good")]
    [InlineData(70, "Fair")]
    [InlineData(55, "Worn")]
    public void HealthLabels(double health, string label) =>
        Assert.Equal(label, BatteryService.HealthLabel(health));

    [Fact]
    public void TheChart_SpansTheBox_AndDoesNotBlowUpSmallChanges()
    {
        var history = BatteryService.ParseCapacityHistory(XDocument.Parse(Report))!;

        var (line, area) = BatteryViewModel.BuildCapacityGeometry(history);

        // Pinned to the 100x100 box so Stretch=Fill maps it exactly
        Assert.Equal(new System.Windows.Rect(0, 0, 100, 100), line.Bounds);
        Assert.Equal(new System.Windows.Rect(0, 0, 100, 100), area.Bounds);

        // ~1,000 mWh of movement on a 60,000 mWh battery stays inside the middle of a 6,000 mWh
        // (10%) range, rather than being stretched from top to bottom
        var drawn = System.Windows.Media.PathGeometry.CreateFromGeometry(line).Figures[0];
        var ys = new[] { drawn.StartPoint.Y }.Concat(drawn.Segments.OfType<System.Windows.Media.PolyLineSegment>().SelectMany(s => s.Points).Select(p => p.Y)).ToList();
        Assert.Equal(4, ys.Count);
        Assert.True(ys.Max() - ys.Min() < 25, $"line spans {ys.Max() - ys.Min():F1}% of the box");
    }
}
