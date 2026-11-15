using NAudio.Wave;

namespace RadioDeck.Services.Audio;

/// <summary>
/// Holds received audio between the network and the sound device.
/// <para>
/// Packets arrive every 20 ms while the device asks for larger blocks at its own pace.
/// Without a reserve the device would run dry in the middle of the first word of every
/// reception and insert silence. So playback of a reception starts only after a small
/// reserve was collected, and starts over the same way after the buffer ran empty.
/// </para>
/// <para>
/// The radio's clock and the PC's clock differ slightly. When the reserve has grown
/// too large, single packets are dropped; the delay stays small without an audible gap.
/// </para>
/// </summary>
public sealed class PlaybackJitterBuffer : IWaveProvider
{
    private readonly object _sync = new();
    private readonly byte[] _ring;
    private readonly int _startBytes;
    private readonly int _maxBytes;
    private int _read;
    private int _count;
    private bool _collecting = true;

    /// <param name="format">PCM format of the audio that is added and read.</param>
    /// <param name="startMs">Reserve collected before a reception is played.</param>
    /// <param name="maxMs">Largest reserve; packets beyond it are dropped.</param>
    public PlaybackJitterBuffer(WaveFormat format, int startMs = 60, int maxMs = 240)
    {
        WaveFormat = format;
        _startBytes = Align(format.AverageBytesPerSecond * Math.Max(0, startMs) / 1000);
        _maxBytes = Math.Max(_startBytes, Align(format.AverageBytesPerSecond * Math.Max(startMs, maxMs) / 1000));
        _ring = new byte[_maxBytes + format.AverageBytesPerSecond]; // room for one late burst
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Audio waiting to be played, in milliseconds.</summary>
    public int BufferedMs
    {
        get { lock (_sync) return (int)(_count * 1000L / WaveFormat.AverageBytesPerSecond); }
    }

    /// <summary>Packets dropped because the reserve was full.</summary>
    public int Dropped { get; private set; }

    /// <summary>Times the buffer ran empty while playing (this also happens at the end of every reception).</summary>
    public int Underruns { get; private set; }

    /// <summary>Adds one packet; returns false when it was dropped.</summary>
    public bool Add(byte[] pcm, int offset, int count)
    {
        lock (_sync)
        {
            if (_count + count > _maxBytes || count > _ring.Length - _count)
            {
                Dropped++;
                return false;
            }

            var write = (_read + _count) % _ring.Length;
            var first = Math.Min(count, _ring.Length - write);
            Buffer.BlockCopy(pcm, offset, _ring, write, first);
            Buffer.BlockCopy(pcm, offset + first, _ring, 0, count - first);
            _count += count;
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _read = 0;
            _count = 0;
            _collecting = true;
        }
    }

    /// <summary>Always fills the whole request; silence where no audio is ready.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            if (_collecting && _count < _startBytes)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            _collecting = false;

            var take = Math.Min(count, _count);
            var first = Math.Min(take, _ring.Length - _read);
            Buffer.BlockCopy(_ring, _read, buffer, offset, first);
            Buffer.BlockCopy(_ring, 0, buffer, offset + first, take - first);
            _read = (_read + take) % _ring.Length;
            _count -= take;

            if (take < count)
            {
                // Ran dry: the reception ended, or packets are late. Collect a reserve again.
                Array.Clear(buffer, offset + take, count - take);
                _collecting = true;
                if (take > 0) Underruns++;
            }
            return count;
        }
    }

    private int Align(int bytes) => bytes - bytes % WaveFormat.BlockAlign;
}
