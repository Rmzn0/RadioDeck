using System.Net;
using RadioDeck.Services.Audio;
using SIPSorceryMedia.Abstractions;

namespace RadioDeck.Services;

/// <summary>Mutes only local PCMA playback; capture, RTP and keepalives continue.</summary>
public sealed class RxPlaybackGate(IAudioSink sink, Func<bool> isMuted) : IAudioSink
{
    private readonly object _sync = new();
    private Task? _closeTask;
    private long _frames;

    /// <summary>Audio frames handed on for playback so far (diagnostics and checks).</summary>
    public long FramesReceived => Interlocked.Read(ref _frames);
    public event SourceErrorDelegate OnAudioSinkError
    {
        add => sink.OnAudioSinkError += value;
        remove => sink.OnAudioSinkError -= value;
    }

    public List<AudioFormat> GetAudioSinkFormats() => sink.GetAudioSinkFormats();
    public void SetAudioSinkFormat(AudioFormat format) => sink.SetAudioSinkFormat(format);
    public void RestrictFormats(Func<AudioFormat, bool> filter) => sink.RestrictFormats(filter);
    public Task PauseAudioSink() => sink.PauseAudioSink();
    public Task ResumeAudioSink()
    {
        lock (_sync) return _closeTask == null ? sink.ResumeAudioSink() : Task.CompletedTask;
    }
    public Task StartAudioSink()
    {
        lock (_sync) return _closeTask == null ? sink.StartAudioSink() : Task.CompletedTask;
    }
    public Task CloseAudioSink()
    {
        lock (_sync)
        {
            if (_closeTask != null) return _closeTask;
            _closeTask = Task.CompletedTask;
            try { _closeTask = sink.CloseAudioSink(); }
            catch (Exception ex) { _closeTask = Task.FromException(ex); }
            return _closeTask;
        }
    }

    public void GotAudioRtp(IPEndPoint remote, uint ssrc, uint sequence, uint timestamp,
        int payloadType, bool marker, byte[] payload)
    {
        lock (_sync)
        {
            if (_closeTask != null) return;
            Interlocked.Increment(ref _frames);
            if (isMuted())
            {
                if (payloadType != 8) return;
                payload = G711.SilenceFrame(payload.Length);
            }
            // Preserve the legacy sink entry point for callers that still use it.
#pragma warning disable CS0618
            sink.GotAudioRtp(remote, ssrc, sequence, timestamp, payloadType, marker, payload);
#pragma warning restore CS0618
        }
    }

    public void GotEncodedMediaFrame(EncodedAudioFrame frame)
    {
        lock (_sync)
        {
            if (_closeTask != null) return;
            Interlocked.Increment(ref _frames);
            if (isMuted())
            {
                if (frame.AudioFormat.Codec != AudioCodecsEnum.PCMA) return;
                frame = new EncodedAudioFrame(frame.MediaStreamIndex, frame.AudioFormat,
                    frame.DurationMilliSeconds, G711.SilenceFrame(frame.EncodedAudio.Length));
            }
            sink.GotEncodedMediaFrame(frame);
        }
    }
}
