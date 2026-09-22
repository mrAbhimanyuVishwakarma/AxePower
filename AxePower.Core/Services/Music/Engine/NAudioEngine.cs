using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AxePower.Core.Services.Music;

/// <summary>
/// The playback engine: NAudio decoders and WASAPI shared-mode output.
///
/// Threading: every decoder and COM object lives on one MTA <see cref="EngineWorker"/> thread;
/// WASAPI pulls samples on its own render thread; events are raised in order on a
/// <see cref="SerialEventQueue"/>, never on the render thread and never under a lock.
///
/// The device is opened lazily on the first Play, so restoring a paused session at startup
/// does not create an audio session. Transport operations (load, seek, pause, stop) are
/// serialised and use short fades so they never click.
/// </summary>
public sealed class NAudioEngine : IAudioEngine, IChainListener
{
    private static readonly TimeSpan TransportFade = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Preroll = TimeSpan.FromMilliseconds(500);

    private readonly LogService _log;
    private readonly EngineWorker _worker = new("AxePower Music engine");
    private readonly SerialEventQueue _events = new();
    private readonly SemaphoreSlim _transport = new(1, 1);

    // Owned by the worker thread
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiOut? _output;
    private WaveFormat? _format;
    private GaplessSourceChain? _chain;
    private VolumeStage? _volume;
    private SoftLimiter? _limiter;
    private PcmOutputConverter? _converter;
    private QueueItem? _nextItem;
    private Guid? _preparedNextKey;
    private OutputSettings _outputSettings = new(null, false, false, 60);
    private bool _outputSettingsChanged;
    private bool _pipelineInvalid;
    private bool _disposed;

    // Read from any thread
    private volatile bool _isPlaying;
    private readonly object _levelLock = new();
    private double _volumeSetting = 1.0;
    private bool _muted;
    private DspSettings _dsp = DspSettings.Neutral;

    public NAudioEngine(LogService log)
    {
        _log = log;
    }

    public event EventHandler<Guid>? TrackStarted;
    public event EventHandler<Guid>? TrackEnded;
    public event EventHandler<EngineError>? Error;
    public event EventHandler<OutputInfo>? OutputChanged;
    public event EventHandler? DeviceLost;

    public bool IsPlaying => _isPlaying;

    public TimeSpan Position
    {
        get
        {
            var chain = _chain;
            if (chain == null) return TimeSpan.Zero;

            var (start, rendered, duration) = chain.Progress;
            // What was handed to the device is ahead of what is audible by the buffer length
            var latency = TimeSpan.FromMilliseconds(_outputSettings.SharedLatencyMs);
            var heard = rendered > latency ? rendered - latency : TimeSpan.Zero;
            var position = start + heard;
            return duration > TimeSpan.Zero && position > duration ? duration : position;
        }
    }

    public TimeSpan Duration => _chain?.Progress.Duration ?? TimeSpan.Zero;

    public bool SpectrumEnabled { get; set; }

    public float[]? GetSpectrum() => null; // Phase 5

    // ── Transport ───────────────────────────────────────────────────────────

    public async Task LoadAsync(QueueItem item, TimeSpan startAt, bool play, CancellationToken ct)
    {
        await _transport.WaitAsync(ct);
        try
        {
            await FadeTransportAsync(0);

            try
            {
                await _worker.RunAsync(() =>
                {
                    ct.ThrowIfCancellationRequested();

                    var decoder = DecoderFactory.Open(item.Track.Path);
                    try
                    {
                        if (startAt > TimeSpan.Zero) decoder.Seek(startAt);
                        EnsurePipeline();

                        var source = CreateSource(item.Key, decoder, startAt);
                        Retire(_chain!.SetCurrent(source));
                        _preparedNextKey = null;
                        if (_nextItem?.Key == item.Key) _nextItem = null;

                        if (play) StartOutput();
                        else PauseOutput();
                    }
                    catch
                    {
                        decoder.Dispose();
                        throw;
                    }
                });
            }
            finally
            {
                await FadeTransportAsync(1);
            }

            _events.Enqueue(() => TrackStarted?.Invoke(this, item.Key));
        }
        finally
        {
            _transport.Release();
        }
    }

    public void SetNext(QueueItem? next)
    {
        _worker.Post(() =>
        {
            _nextItem = next;

            if (next != null && _preparedNextKey == next.Key) return;

            // A different (or no) next track: drop whatever was preloaded
            if (_chain != null)
            {
                var old = _chain.SetNext(null);
                if (old != null) old.Decoder.Dispose();
            }
            _preparedNextKey = null;

            if (next != null && _chain?.IsNearEnd == true) PrepareNext();
        });
    }

    public void Play() => _ = PlayCoreAsync();

    public void Pause() => _ = PauseCoreAsync();

    private async Task PlayCoreAsync()
    {
        await _transport.WaitAsync();
        try
        {
            await _worker.RunAsync(StartOutput);
        }
        catch (Exception ex)
        {
            ReportOutputFailure(ex);
        }
        finally
        {
            _transport.Release();
        }
    }

    private async Task PauseCoreAsync()
    {
        await _transport.WaitAsync();
        try
        {
            if (!_isPlaying) return;
            await FadeTransportAsync(0);
            await _worker.RunAsync(PauseOutput);
            await FadeTransportAsync(1);
        }
        finally
        {
            _transport.Release();
        }
    }

    public async Task StopAsync()
    {
        await _transport.WaitAsync();
        try
        {
            await FadeTransportAsync(0);
            await _worker.RunAsync(() =>
            {
                PauseOutput();
                if (_chain != null) Retire(_chain.SetCurrent(null));
                _nextItem = null;
                _preparedNextKey = null;
            });
            await FadeTransportAsync(1);
        }
        finally
        {
            _transport.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        await _transport.WaitAsync(ct);
        try
        {
            await FadeTransportAsync(0);
            try
            {
                await _worker.RunAsync(() =>
                {
                    var current = _chain?.Current;
                    if (current == null) return;

                    if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                    if (position > current.Decoder.Duration) position = current.Decoder.Duration;

                    current.Decoder.Seek(position);
                    // A fresh adapter also flushes the resampler's buffered samples
                    _chain!.ReplaceCurrent(CreateSource(current.Key, current.Decoder, position));
                });
            }
            finally
            {
                await FadeTransportAsync(1);
            }
        }
        finally
        {
            _transport.Release();
        }
    }

    // ── Levels ──────────────────────────────────────────────────────────────

    public void SetVolume(double volume, bool muted)
    {
        lock (_levelLock)
        {
            _volumeSetting = Math.Clamp(volume, 0.0, 2.0);
            _muted = muted;
        }
        ApplyLevels();
    }

    public void SetDsp(DspSettings dsp)
    {
        lock (_levelLock) _dsp = dsp;
        ApplyLevels();
    }

    public async Task FadeToAsync(double gain, TimeSpan duration, CancellationToken ct)
    {
        var volume = _volume;
        if (volume == null) return;

        var fade = volume.FadeToAsync(Math.Clamp(gain, 0.0, 1.0), duration, immediate: !_isPlaying);
        // Never hang if the device stalls: give up a little after the fade should have ended
        await Task.WhenAny(fade, Task.Delay(duration + TimeSpan.FromMilliseconds(500), ct));
    }

    private void ApplyLevels()
    {
        double amplitude;
        lock (_levelLock) amplitude = VolumeStage.ToAmplitude(_volumeSetting, _muted, _dsp.AllowBoost);

        _volume?.SetAmplitude(amplitude);
        if (_limiter != null) _limiter.Active = amplitude > 1.0;
        if (_converter != null) _converter.DspActive = amplitude != 1.0;
    }

    private async Task FadeTransportAsync(double gain)
    {
        var volume = _volume;
        if (volume == null) return;

        var fade = volume.TransportFadeAsync(gain, TransportFade, immediate: !_isPlaying);
        await Task.WhenAny(fade, Task.Delay(TransportFade + TimeSpan.FromMilliseconds(250)));
    }

    // ── Output ──────────────────────────────────────────────────────────────

    public Task ConfigureOutputAsync(OutputSettings output, CancellationToken ct)
    {
        // Applied at the next safe point: the next time the output is (re)opened
        return _worker.RunAsync(() =>
        {
            if (output == _outputSettings) return;
            _outputSettings = output;
            _outputSettingsChanged = true;
        });
    }

    /// <summary>
    /// Worker thread. After the device was lost the pipeline is rebuilt for the current default
    /// device; the current track is moved over at its position, still paused.
    /// </summary>
    private void RebuildPipelineIfInvalid()
    {
        if (!_pipelineInvalid || _chain == null) return;

        var current = _chain.Current;
        var position = Position;
        var next = _chain.SetNext(null);
        if (next != null) next.Decoder.Dispose();
        _preparedNextKey = null;

        DisposeOutput();
        _device?.Dispose();
        _device = null;
        _chain = null;
        _pipelineInvalid = false;

        EnsurePipeline();

        if (current != null)
        {
            current.Decoder.Seek(position);
            _chain!.SetCurrent(CreateSource(current.Key, current.Decoder, position));
        }
    }

    /// <summary>Worker thread. Resolves the device and builds the sample pipeline in its mix format.</summary>
    private void EnsurePipeline()
    {
        if (_chain != null && _pipelineInvalid)
        {
            // Called from LoadAsync: the old track is being replaced anyway
            Retire(_chain.SetCurrent(null));
            DisposeOutput();
            _device?.Dispose();
            _device = null;
            _chain = null;
            _pipelineInvalid = false;
        }

        if (_chain != null) return;

        _enumerator ??= new MMDeviceEnumerator();
        _device = ResolveDevice();

        using (var client = _device.AudioClient)
            _format = client.MixFormat;

        _chain = new GaplessSourceChain(_format.SampleRate, _format.Channels, this);
        _volume = new VolumeStage(_chain);
        _limiter = new SoftLimiter(_volume);
        _converter = new PcmOutputConverter(_limiter, _format);
        ApplyLevels();

        var info = new OutputInfo(_device.FriendlyName, false, _format.SampleRate, _format.BitsPerSample, _format.Channels, false, null);
        _events.Enqueue(() => OutputChanged?.Invoke(this, info));
        _log.LogInfo($"[Music] Output: {_device.FriendlyName}, shared, {_format.SampleRate} Hz, {_format.Channels} ch.");
    }

    private MMDevice ResolveDevice()
    {
        if (!string.IsNullOrEmpty(_outputSettings.DeviceId))
        {
            try
            {
                var device = _enumerator!.GetDevice(_outputSettings.DeviceId);
                if (device.State == DeviceState.Active) return device;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Music] Selected output device unavailable, using the Windows default: {ex.Message}");
            }
        }

        return _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>Worker thread.</summary>
    private void StartOutput()
    {
        RebuildPipelineIfInvalid();
        if (_chain?.Current == null) return;

        if (_outputSettingsChanged && _output != null)
        {
            // Latency changes need a fresh client; the pipeline itself is unchanged
            DisposeOutput();
        }
        _outputSettingsChanged = false;

        if (_output == null)
        {
            _output = new WasapiOut(_device!, AudioClientShareMode.Shared, true, _outputSettings.SharedLatencyMs);
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_converter!);
        }

        _output.Play();
        _isPlaying = true;
    }

    /// <summary>Worker thread.</summary>
    private void PauseOutput()
    {
        if (_output != null && _output.PlaybackState == PlaybackState.Playing)
            _output.Pause();
        _isPlaying = false;
    }

    private void DisposeOutput()
    {
        if (_output == null) return;
        _output.PlaybackStopped -= OnPlaybackStopped;
        try { _output.Stop(); } catch { }
        try { _output.Dispose(); } catch { }
        _output = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return; // our own Stop

        // Device unplugged, disabled or invalidated: drop the output and let the player decide
        _log.LogWarning($"[Music] Output device lost: {e.Exception.Message}");
        _worker.Post(() =>
        {
            DisposeOutput();
            _isPlaying = false;
            // Rebuilt against the (possibly new) default device on the next play or load
            _pipelineInvalid = true;
        });
        _events.Enqueue(() => DeviceLost?.Invoke(this, EventArgs.Empty));
    }

    private void ReportOutputFailure(Exception ex)
    {
        _log.LogError("[Music] Could not start audio output.", ex);
        var key = _chain?.CurrentKey ?? Guid.Empty;
        _events.Enqueue(() => Error?.Invoke(this, new EngineError(key, "Couldn't open the audio device.", ex)));
    }

    // ── Sources ─────────────────────────────────────────────────────────────

    /// <summary>Worker thread.</summary>
    private ChainSource CreateSource(Guid key, IDecoder decoder, TimeSpan startOffset)
    {
        var adapted = FormatAdapter.Adapt(decoder, _format!.SampleRate, _format.Channels);
        return new ChainSource(key, decoder, new PrerollSampleProvider(adapted, Preroll), startOffset);
    }

    /// <summary>Worker thread. Opens and pre-rolls the next track for a gapless join.</summary>
    private void PrepareNext()
    {
        var item = _nextItem;
        if (_chain == null || item == null || _preparedNextKey == item.Key) return;

        IDecoder? decoder = null;
        try
        {
            decoder = DecoderFactory.Open(item.Track.Path);
            var old = _chain.SetNext(CreateSource(item.Key, decoder, TimeSpan.Zero));
            if (old != null) old.Decoder.Dispose();
            _preparedNextKey = item.Key;
        }
        catch (Exception ex)
        {
            // Not fatal: without a preloaded track the current one simply ends, and the player
            // loads (and reports) the next one itself
            decoder?.Dispose();
            _log.LogWarning($"[Music] Could not preload the next track: {ex.Message}");
        }
    }

    private void Retire(IReadOnlyList<ChainSource> sources)
    {
        foreach (var source in sources)
        {
            try { source.Decoder.Dispose(); } catch { }
        }
    }

    // ── Render-thread callbacks (queue work only) ───────────────────────────

    void IChainListener.OnTrackStarted(Guid key)
    {
        _worker.Post(() =>
        {
            if (_preparedNextKey == key) _preparedNextKey = null;
            if (_nextItem?.Key == key) _nextItem = null;
        });
        _events.Enqueue(() => TrackStarted?.Invoke(this, key));
    }

    void IChainListener.OnTrackEnded(Guid key) =>
        _events.Enqueue(() => TrackEnded?.Invoke(this, key));

    void IChainListener.OnTrackFailed(Guid key, Exception error)
    {
        _log.LogWarning($"[Music] Decoding failed mid-track: {error.Message}");
        _events.Enqueue(() => Error?.Invoke(this, new EngineError(key, error.Message, error)));
    }

    void IChainListener.OnNearEnd(Guid key) => _worker.Post(PrepareNext);

    void IChainListener.OnRetired(ChainSource source) =>
        _worker.Post(() => { try { source.Decoder.Dispose(); } catch { } });

    // ── Lifetime ────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await _worker.RunAsync(() =>
            {
                DisposeOutput();
                if (_chain != null)
                {
                    Retire(_chain.SetCurrent(null));
                }
                _device?.Dispose();
                _enumerator?.Dispose();
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Engine shutdown: {ex.Message}");
        }

        _worker.Dispose();
        _transport.Dispose();
    }
}
