using System.Net;
using NAudio.Wave;
using SIPSorceryMedia.Abstractions;

namespace RadioDeck.Services.Audio;

/// <summary>
/// Plays the received G.711 A-law audio of one radio session on the selected output
/// device, with a per-radio volume and left/right balance.
/// </summary>
public sealed class RadioAudioSink : IAudioSink, IDisposable
{
    private const int SampleRate = 8000;

    // A reception is played once 80 ms were collected, so the device (40 ms blocks) does
    // not run dry in the first word. Above 280 ms single packets are dropped: clock drift
    // between radio and PC must not build up a growing delay.
    private const int StartReserveMs = 80;
    private const int MaxReserveMs = 280;
    private const int DeviceLatencyMs = 120;
    private const int DeviceBuffers = 3;

    // Above this level the volume control rounds peaks off instead of cutting them.
    private const float SoftClipKnee = 0.8f * short.MaxValue;

    private static readonly AudioFormat Pcma = new(SDPWellKnownMediaFormatsEnum.PCMA);

    private readonly object _sync = new();
    private readonly Func<string?> _deviceName;
    private WaveOutEvent? _output;
    private PlaybackJitterBuffer? _buffer;
    private volatile float _gainLeft = 1f;
    private volatile float _gainRight = 1f;
    private bool _started;
    private bool _closed;

    public event SourceErrorDelegate? OnAudioSinkError;

    public RadioAudioSink(Func<string?> deviceName)
    {
        _deviceName = deviceName;
    }

    /// <param name="volumePercent">0-200.</param>
    /// <param name="pan">-100 (left only) to 100 (right only).</param>
    public void SetVolume(int volumePercent, int pan)
    {
        var volume = Math.Clamp(volumePercent, 0, 200) / 100f;
        var balance = Math.Clamp(pan, -100, 100) / 100f;
        _gainLeft = volume * (balance <= 0 ? 1f : 1f - balance);
        _gainRight = volume * (balance >= 0 ? 1f : 1f + balance);
    }

    public List<AudioFormat> GetAudioSinkFormats() => new() { Pcma };
    public void SetAudioSinkFormat(AudioFormat format) { }
    public void RestrictFormats(Func<AudioFormat, bool> filter) { }

    public Task StartAudioSink()
    {
        lock (_sync)
        {
            if (_started || _closed) return Task.CompletedTask;
            _started = true;
            try
            {
                var name = _deviceName();
                var device = AudioDevices.ResolveOutput(name, out var found);
                if (!found)
                    OnAudioSinkError?.Invoke($"Output device '{name}' is not present; using the Windows default.");

                var buffer = new PlaybackJitterBuffer(new WaveFormat(SampleRate, 16, 2), StartReserveMs, MaxReserveMs);
                var output = new WaveOutEvent { DeviceNumber = device, DesiredLatency = DeviceLatencyMs, NumberOfBuffers = DeviceBuffers };
                output.Init(buffer);
                output.Play();
                _buffer = buffer;
                _output = output;
            }
            catch (Exception ex)
            {
                _buffer = null;
                try { _output?.Dispose(); } catch { }
                _output = null;
                OnAudioSinkError?.Invoke($"Playback device could not be opened: {ex.Message}");
            }
        }
        return Task.CompletedTask;
    }

    public Task PauseAudioSink()
    {
        lock (_sync) { try { _output?.Pause(); } catch { } }
        return Task.CompletedTask;
    }

    public Task ResumeAudioSink()
    {
        lock (_sync) { try { if (!_closed) _output?.Play(); } catch { } }
        return Task.CompletedTask;
    }

    public Task CloseAudioSink()
    {
        lock (_sync)
        {
            if (_closed) return Task.CompletedTask;
            _closed = true;
            var output = _output;
            _output = null;
            _buffer = null;
            if (output != null)
            {
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp,
        int payloadID, bool marker, byte[] payload)
    {
        if (payloadID == 8) Play(payload);
    }

    public void GotEncodedMediaFrame(EncodedAudioFrame frame)
    {
        if (frame.AudioFormat.Codec == AudioCodecsEnum.PCMA) Play(frame.EncodedAudio);
    }

    private void Play(byte[]? alaw)
    {
        if (alaw == null || alaw.Length == 0) return;
        var left = _gainLeft;
        var right = _gainRight;
        var pcm = new byte[alaw.Length * 4];
        for (var i = 0; i < alaw.Length; i++)
        {
            int sample = G711.Decode(alaw[i]);
            var l = SoftClip(sample * left);
            var r = SoftClip(sample * right);
            pcm[i * 4] = (byte)l;
            pcm[i * 4 + 1] = (byte)(l >> 8);
            pcm[i * 4 + 2] = (byte)r;
            pcm[i * 4 + 3] = (byte)(r >> 8);
        }

        lock (_sync)
        {
            var buffer = _buffer;
            if (buffer == null) return;
            buffer.Add(pcm, 0, pcm.Length);
        }
    }

    // Unchanged at normal levels; with the volume above 100 % loud passages are
    // rounded off instead of being cut flat, which sounds far less harsh.
    private static short SoftClip(float sample)
    {
        var magnitude = Math.Abs(sample);
        if (magnitude > SoftClipKnee)
        {
            var room = short.MaxValue - SoftClipKnee;
            magnitude = SoftClipKnee + room * MathF.Tanh((magnitude - SoftClipKnee) / room);
        }
        var value = Math.Min((int)MathF.Round(magnitude), short.MaxValue);
        return (short)(sample < 0 ? -value : value);
    }

    public void Dispose() => CloseAudioSink();
}

/// <summary>
/// Declares a PCMA send track for a media session without touching any device.
/// Microphone frames reach the engine through <see cref="MicrophoneHub"/> instead.
/// </summary>
public sealed class SilentAudioSource : IAudioSource
{
    private static readonly AudioFormat Pcma = new(SDPWellKnownMediaFormatsEnum.PCMA);

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample { add { } remove { } }
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady { add { } remove { } }
    public event RawAudioSampleDelegate? OnAudioSourceRawSample { add { } remove { } }
    public event SourceErrorDelegate? OnAudioSourceError { add { } remove { } }

    public List<AudioFormat> GetAudioSourceFormats() => new() { Pcma };
    public void SetAudioSourceFormat(AudioFormat format) { }
    public void RestrictFormats(Func<AudioFormat, bool> filter) { }
    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) { }
    public bool HasEncodedAudioSubscribers() => false;
    public bool IsAudioSourcePaused() => false;
    public Task PauseAudio() => Task.CompletedTask;
    public Task ResumeAudio() => Task.CompletedTask;
    public Task StartAudio() => Task.CompletedTask;
    public Task CloseAudio() => Task.CompletedTask;
}
