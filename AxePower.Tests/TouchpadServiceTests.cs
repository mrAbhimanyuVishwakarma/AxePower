using System.Threading.Tasks;
using AxePower.Core.Services;

namespace AxePower.Tests;

public class TouchpadServiceTests
{
    [Fact]
    public async Task AlreadyInTheWantedState_PressesNothing()
    {
        var pad = new FakeTouchpad(enabled: true);
        Assert.True(await new TouchpadService(pad).SetEnabledAsync(true));
        Assert.Equal(0, pad.Presses);
    }

    [Fact]
    public async Task TurnsItOff_AndConfirmsWindowsDidIt()
    {
        var pad = new FakeTouchpad(enabled: true);
        Assert.True(await new TouchpadService(pad).SetEnabledAsync(false));
        Assert.Equal(1, pad.Presses);
        Assert.False(pad.Enabled);
    }

    /// <summary>Seen on real hardware: a toggle too soon after another is ignored.</summary>
    [Fact]
    public async Task AnIgnoredPress_IsRetriedOnceAfterAPause()
    {
        var pad = new FakeTouchpad(enabled: true) { IgnoreNextPresses = 1 };
        Assert.True(await new TouchpadService(pad).SetEnabledAsync(false));
        Assert.Equal(2, pad.Presses);
        Assert.False(pad.Enabled);
    }

    [Fact]
    public async Task WhenWindowsNeverChangesIt_ItSaysSo()
    {
        var pad = new FakeTouchpad(enabled: true) { IgnoreNextPresses = 99 };
        Assert.False(await new TouchpadService(pad).SetEnabledAsync(false));
        Assert.Equal(2, pad.Presses);   // one retry, not a loop
    }

    [Fact]
    public async Task NoPrecisionTouchpad_IsNotAvailable_AndNothingIsPressed()
    {
        var pad = new FakeTouchpad(enabled: null);
        var service = new TouchpadService(pad);
        Assert.Null(service.IsEnabled());
        Assert.False(await service.SetEnabledAsync(false));
        Assert.Equal(0, pad.Presses);
    }

    private sealed class FakeTouchpad(bool? enabled) : ITouchpadPlatform
    {
        public bool? Enabled { get; private set; } = enabled;
        public int Presses { get; private set; }
        public int IgnoreNextPresses { get; set; }

        public bool? ReadEnabled() => Enabled;

        public void Toggle()
        {
            Presses++;
            if (IgnoreNextPresses > 0) { IgnoreNextPresses--; return; }
            if (Enabled is bool on) Enabled = !on;
        }
    }
}
