using System;
using System.Text;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using Xunit;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Deterministic fuzz and property tests for remote control JSON event parsing (ShareViewerHost.ApplyEvent).
/// Validates integer overflow resistance, type mismatch resilience, and malformed JSON safety.
/// </summary>
public class ControlEventParserFuzzTests
{
    private readonly ShareViewerHost _host;
    private readonly RemoteInputService _input = new(new LogService());

    public ControlEventParserFuzzTests()
    {
        _host = new ShareViewerHost(new LogService(), _input);
    }

    [Fact]
    public void MinimizedControlEventRegressions_NeverCrashOrThrow()
    {
        foreach (var regression in FuzzTestCorpus.MinimizedControlEventRegressions)
        {
            // Must gracefully ignore malformed/overflowing messages without crashing
            _host.ApplyEvent(regression);
        }
    }

    [Fact]
    public void IntegerBoundaryInputs_HandledSafely()
    {
        var boundaries = new[]
        {
            $"{{\"t\":\"k\",\"c\":{int.MaxValue},\"p\":1}}",
            $"{{\"t\":\"k\",\"c\":{int.MinValue},\"p\":1}}",
            $"{{\"t\":\"k\",\"c\":{ushort.MaxValue},\"p\":1}}",
            $"{{\"t\":\"k\",\"c\":-1,\"p\":1}}",
            $"{{\"t\":\"k\",\"c\":0,\"p\":0}}",
            $"{{\"t\":\"w\",\"d\":{int.MaxValue}}}",
            $"{{\"t\":\"w\",\"d\":{int.MinValue}}}",
            $"{{\"t\":\"d\",\"b\":{int.MaxValue}}}",
            $"{{\"t\":\"d\",\"b\":-100}}",
            $"{{\"t\":\"m\",\"x\":{double.MaxValue},\"y\":{double.MinValue}}}",
            $"{{\"t\":\"m\",\"x\":{double.NaN},\"y\":{double.PositiveInfinity}}}"
        };

        foreach (var boundary in boundaries)
        {
            _host.ApplyEvent(boundary);
        }
    }

    [Fact]
    public void RandomMutatedJson_NeverCrashes()
    {
        foreach (var randomBytes in FuzzTestCorpus.GenerateMutatedByteSequences(count: 300, maxLength: 1024))
        {
            var text = Encoding.UTF8.GetString(randomBytes);
            _host.ApplyEvent(text);
        }
    }
}
