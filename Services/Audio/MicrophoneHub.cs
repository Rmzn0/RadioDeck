using NAudio.Wave;

namespace RadioDeck.Services.Audio;

/// <summary>Delivers 20 ms G.711 A-law microphone frames to its subscribers.</summary>
public interface IMicrophoneSource
{
    /// <summary>Starts delivering frames; dispose the result to stop.</summary>
    IDisposable Subscribe(Action<byte[]> onFrame);
}

/// <summary>
/// One microphone capture shared by every radio. It records for as long as at least
/// one transmitter session is connected, so the device is already running when PTT
/// is pressed, and it restarts itself if the device stalls or is re-plugged.
/// The capture is cleaned by <see cref="TxAudioProcessor"/>; subscribers receive
/// 20 ms G.711 A-law frames on the capture thread.
/// </summary>
public sealed class MicrophoneHub : IMicrophoneSource, IDisposable
{
    // Captured at 48 kHz and converted here: the conversion Windows does for an
    // 8 kHz capture lets sounds above the voice band fold back into it.
    private const int CaptureRate = 48000;
    private const int FallbackRate = TxAudioProcessor.OutputRate;
    private const int FrameMs = 20;
    private const int StallMs = 3000;
    private const int RetryMs = 3000;

    private readonly object _sync = new();
    private readonly List<Action<byte[]>> _subscribers = new();
    private readonly Timer _watchdog;
    private Action<byte[]>[] _snapshot = Array.Empty<Action<byte[]>>();
    private readonly Action<short[]> _deliver;
    private WaveInEvent? _waveIn;
    private TxAudioProcessor? _processor;
    private string? _deviceName;
    private int _noiseReduction;
    private long _lastFrameMs;
    private long _startedMs;
    private long _lastFailureLogMs;
    private bool _disposed;

    public event Action<string>? Log;

    public MicrophoneHub()
    {
        _deliver = Deliver;
        _watchdog = new Timer(_ => CheckCapture(), null, 1000, 1000);
    }

    /// <summary>Background noise reduction: 0 = off, 1 = normal, 2 = strong.</summary>
    public void SetNoiseReduction(int level)
    {
        lock (_sync)
        {
            _noiseReduction = Math.Clamp(level, 0, 2);
            if (_processor != null) _processor.NoiseReduction = _noiseReduction;
        }
    }

    /// <summary>Capture device name; null or empty selects the Windows default device.</summary>
    public void SetDevice(string? name)
    {
        lock (_sync)
        {
            name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (string.Equals(_deviceName, name, StringComparison.OrdinalIgnoreCase)) return;
            _deviceName = name;
            if (_waveIn == null) return;
            StopCapture();
            StartCapture();
        }
    }

    /// <summary>Starts delivering frames; dispose the result to stop.</summary>
    public IDisposable Subscribe(Action<byte[]> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers.Add(onFrame);
            _snapshot = _subscribers.ToArray();
            if (_waveIn == null) StartCapture();
        }
        return new Subscription(this, onFrame);
    }

    private void Unsubscribe(Action<byte[]> onFrame)
    {
        lock (_sync)
        {
            if (!_subscribers.Remove(onFrame)) return;
            _snapshot = _subscribers.ToArray();
            if (_subscribers.Count == 0) StopCapture();
        }
    }

    // Caller holds _sync.
    private void StartCapture()
    {
        if (_disposed || _waveIn != null) return;
        try
        {
            var device = AudioDevices.ResolveInput(_deviceName, out var found);
            if (!found)
                Log?.Invoke($"[AUDIO][MIC][WARN] Input device '{_deviceName}' is not present; using the Windows default.");

            WaveInEvent waveIn;
            int rate;
            try
            {
                waveIn = Open(device, rate = CaptureRate);
            }
            catch (Exception ex) when (ex is NAudio.MmException or ArgumentException or InvalidOperationException)
            {
                // A device that refuses 48 kHz: let Windows deliver 8 kHz as before.
                waveIn = Open(device, rate = FallbackRate);
            }

            _processor = new TxAudioProcessor(rate, _noiseReduction);
            _waveIn = waveIn;
            _startedMs = Environment.TickCount64;
            Interlocked.Exchange(ref _lastFrameMs, 0);
            Log?.Invoke($"[AUDIO][MIC] Capture started on {(_deviceName ?? "the default input device")} at {rate} Hz, noise reduction {NoiseReductionName(_noiseReduction)}.");
        }
        catch (Exception ex)
        {
            _waveIn = null;
            _processor = null;
            LogFailure($"[AUDIO][MIC][ERR] Microphone could not be opened: {ex.Message}");
        }
    }

    private WaveInEvent Open(int device, int rate)
    {
        var waveIn = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(rate, 16, 1),
            BufferMilliseconds = FrameMs,
            NumberOfBuffers = 3
        };
        try
        {
            waveIn.DataAvailable += OnData;
            waveIn.RecordingStopped += OnStopped;
            waveIn.StartRecording();
            return waveIn;
        }
        catch
        {
            waveIn.DataAvailable -= OnData;
            waveIn.RecordingStopped -= OnStopped;
            try { waveIn.Dispose(); } catch { }
            throw;
        }
    }

    private static string NoiseReductionName(int level) => level switch { 1 => "normal", 2 => "strong", _ => "off" };

    // Caller holds _sync.
    private void StopCapture()
    {
        var waveIn = _waveIn;
        _waveIn = null;
        _processor = null;
        if (waveIn == null) return;
        waveIn.DataAvailable -= OnData;
        waveIn.RecordingStopped -= OnStopped;
        try { waveIn.StopRecording(); } catch { }
        try { waveIn.Dispose(); } catch { }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded < 2) return;
        Interlocked.Exchange(ref _lastFrameMs, Environment.TickCount64);

        // Runs even while nobody transmits, so the noise estimate is ready when PTT is pressed.
        var processor = Volatile.Read(ref _processor);
        processor?.Process(e.Buffer.AsSpan(0, e.BytesRecorded & ~1), _deliver);
    }

    private void Deliver(short[] pcm)
    {
        var subscribers = Volatile.Read(ref _snapshot);
        if (subscribers.Length == 0) return;

        var frame = G711.Encode((ReadOnlySpan<short>)pcm);
        foreach (var subscriber in subscribers)
        {
            try { subscriber(frame); }
            catch (Exception ex) { LogFailure($"[AUDIO][MIC][ERR] Frame handler failed: {ex.Message}"); }
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            LogFailure($"[AUDIO][MIC][ERR] Capture stopped: {e.Exception.Message}");
    }

    // Restart a capture that failed to open, was unplugged, or silently stopped delivering frames.
    private void CheckCapture()
    {
        if (!Monitor.TryEnter(_sync)) return;
        try
        {
            if (_disposed || _subscribers.Count == 0) return;
            var now = Environment.TickCount64;
            if (_waveIn == null)
            {
                if (now - _startedMs >= RetryMs) { _startedMs = now; StartCapture(); }
                return;
            }

            var last = Interlocked.Read(ref _lastFrameMs);
            if (now - Math.Max(last, _startedMs) < StallMs) return;
            LogFailure("[AUDIO][MIC][WARN] No microphone frames for 3 s; restarting capture.");
            StopCapture();
            _startedMs = now;
            StartCapture();
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    // A missing microphone would otherwise repeat the same line every few seconds.
    private void LogFailure(string message)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFailureLogMs) < 30_000) return;
        Interlocked.Exchange(ref _lastFailureLogMs, now);
        Log?.Invoke(message);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _subscribers.Clear();
            _snapshot = Array.Empty<Action<byte[]>>();
            StopCapture();
        }
        _watchdog.Dispose();
    }

    private sealed class Subscription(MicrophoneHub hub, Action<byte[]> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) hub.Unsubscribe(handler);
        }
    }
}
