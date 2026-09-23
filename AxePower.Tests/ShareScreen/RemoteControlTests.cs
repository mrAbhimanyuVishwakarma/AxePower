using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// The gate around remote input.
///
/// These are the tests that matter most in the whole feature: everything else being wrong makes a
/// bad video, but this being wrong hands a stranger the keyboard. The rule under test is that
/// input is refused unless the host has explicitly opened the gate for the current session.
/// </summary>
public class RemoteInputGateTests
{
    private static RemoteInputService NewService() => new(new LogService());

    private static readonly CaptureDisplay Screen =
        new(0, @"\\.\DISPLAY1", "Display 1", 1920, 1080, 60, true);

    [Fact]
    public void StartsClosed()
    {
        // Nothing constructs itself into an accepting state.
        Assert.False(NewService().IsAccepting);
    }

    [Fact]
    public void OpensOnlyWhenTheHostAllowsIt()
    {
        var service = NewService();

        service.Allow(Screen);
        Assert.True(service.IsAccepting);
    }

    [Fact]
    public void RevokeClosesTheGate()
    {
        var service = NewService();
        service.Allow(Screen);

        service.RevokeAll();

        Assert.False(service.IsAccepting);
    }

    [Fact]
    public void ReleaseHeldKeepsTheGateOpen()
    {
        // A viewer losing window focus should drop stuck keys, not end their session.
        var service = NewService();
        service.Allow(Screen);

        service.ReleaseHeld();

        Assert.True(service.IsAccepting);
    }

    [Fact]
    public void InputBeforeAllowIsIgnored()
    {
        var service = NewService();
        var fired = false;
        service.InputReceived += (_, _) => fired = true;

        // Every entry point, with the gate shut.
        service.MoveMouse(0.5, 0.5);
        service.MouseButton(0, pressed: true);
        service.Scroll(120);
        service.Key(65, pressed: true);

        Assert.False(fired);
        Assert.False(service.IsAccepting);
    }

    [Fact]
    public void InputAfterRevokeIsIgnored()
    {
        var service = NewService();
        service.Allow(Screen);
        service.RevokeAll();

        var fired = false;
        service.InputReceived += (_, _) => fired = true;

        service.MoveMouse(0.5, 0.5);
        service.Key(65, pressed: true);

        Assert.False(fired);
    }

    [Fact]
    public void KeyCodeZeroIsIgnored()
    {
        // The browser sends 0 for keys it could not map; injecting it would be a blind guess.
        var service = NewService();
        service.Allow(Screen);

        var fired = false;
        service.InputReceived += (_, _) => fired = true;

        service.Key(0, pressed: true);

        Assert.False(fired);
    }
}

/// <summary>
/// Mapping a click in the viewer's video onto a pixel on the host's desktop.
/// </summary>
public class CaptureDisplayMappingTests
{
    [Fact]
    public void MapsCornersOntoTheScreen()
    {
        var screen = new CaptureDisplay(0, @"\\.\DISPLAY1", "Display 1", 1920, 1080, 60, true);

        Assert.Equal((0, 0), screen.FractionToDesktop(0, 0));
        Assert.Equal((1919, 1079), screen.FractionToDesktop(1, 1));
    }

    [Fact]
    public void MapsOntoAMonitorThatIsNotAtTheOrigin()
    {
        // A second monitor to the right of a 1920-wide primary.
        var screen = new CaptureDisplay(1, @"\\.\DISPLAY2", "Display 2", 2560, 1440, 60, false, Left: 1920, Top: 0);

        Assert.Equal((1920, 0), screen.FractionToDesktop(0, 0));
        Assert.Equal((1920 + 2559, 1439), screen.FractionToDesktop(1, 1));
    }

    [Fact]
    public void MapsOntoAMonitorAtNegativeCoordinates()
    {
        // Windows gives a screen placed left of the primary a negative origin.
        var screen = new CaptureDisplay(1, @"\\.\DISPLAY2", "Display 2", 1920, 1080, 60, false, Left: -1920, Top: -200);

        Assert.Equal((-1920, -200), screen.FractionToDesktop(0, 0));
    }

    [Fact]
    public void ClampsOutOfRangeFractions()
    {
        // The viewer controls these numbers, so they cannot be trusted to stay inside 0..1.
        var screen = new CaptureDisplay(0, @"\\.\DISPLAY1", "Display 1", 1920, 1080, 60, true);

        Assert.Equal((0, 0), screen.FractionToDesktop(-5, -5));
        Assert.Equal((1919, 1079), screen.FractionToDesktop(9, 9));
        Assert.Equal((0, 0), screen.FractionToDesktop(double.NegativeInfinity, double.NegativeInfinity));
    }
}

public class RemoteControlSettingsTests
{
    [Fact]
    public void RemoteControlIsOffByDefault()
    {
        Assert.False(new ShareScreenSettings().AllowRemoteControl);
    }

    [Fact]
    public void ControlSettingsSurviveClone()
    {
        var original = new ShareScreenSettings { AllowRemoteControl = true, ViewerPort = 9100 };
        var copy = original.Clone();

        Assert.True(copy.AllowRemoteControl);
        Assert.Equal(9100, copy.ViewerPort);
    }

    [Fact]
    public void SessionStatusStartsWithControlDisabled()
    {
        Assert.Equal(RemoteControlState.Disabled, ShareSessionStatus.Idle.Control);
        Assert.Equal(string.Empty, ShareSessionStatus.Idle.ControlCode);
    }
}
