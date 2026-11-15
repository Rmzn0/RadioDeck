namespace RadioDeck.Services.Audio;

/// <summary>
/// Turns raw microphone PCM into clean 8 kHz frames of 20 ms for the radio:
/// <list type="number">
/// <item>band-limits and converts the capture rate to 8 kHz, so sounds above the
/// voice band (hiss, sibilants, wind) do not fold back into it;</item>
/// <item>removes everything below the voice band (wind rumble, engine drone, mains hum);</item>
/// <item>optionally reduces steady background noise;</item>
/// <item>limits peaks softly instead of letting loud speech clip.</item>
/// </list>
/// Not thread-safe: call <see cref="Process"/> from one thread (the capture thread).
/// </summary>
public sealed class TxAudioProcessor
{
    public const int OutputRate = 8000;
    public const int FrameSamples = 160;

    private const double LowPassCutoffHz = 3700;   // -6 dB point; the stop band begins near 4 kHz
    private const double HighPassCutoffHz = 250;
    private const float LimiterKnee = 0.7f * short.MaxValue;

    private readonly int _factor;
    private readonly float[] _taps;
    private float[] _work = Array.Empty<float>();
    private readonly float[] _carry;
    private int _phase;

    private readonly Biquad _highPass1;
    private readonly Biquad _highPass2;

    private readonly SpectralNoiseReducer _reducer = new();
    private readonly float[] _reducerIn = new float[SpectralNoiseReducer.Hop];
    private readonly float[] _reducerOut = new float[SpectralNoiseReducer.Hop];
    private int _reducerCount;
    private int _activeNoiseReduction;
    private volatile int _noiseReduction;

    private readonly short[] _frame = new short[FrameSamples];
    private int _frameCount;

    /// <param name="inputRate">Capture rate: a multiple of 8000 Hz (8000, 16000, 48000).</param>
    /// <param name="noiseReduction">0 = off, 1 = normal, 2 = strong.</param>
    public TxAudioProcessor(int inputRate, int noiseReduction = 0)
    {
        if (inputRate < OutputRate || inputRate % OutputRate != 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate), "The capture rate must be a multiple of 8000 Hz.");

        InputRate = inputRate;
        _factor = inputRate / OutputRate;
        _taps = _factor == 1 ? Array.Empty<float>() : DesignLowPass(inputRate, LowPassCutoffHz, 40 * _factor + 1);
        _carry = new float[Math.Max(0, _taps.Length - 1)];
        _highPass1 = Biquad.HighPass(OutputRate, HighPassCutoffHz, 0.54119610);
        _highPass2 = Biquad.HighPass(OutputRate, HighPassCutoffHz, 1.30656296);
        NoiseReduction = noiseReduction;
    }

    public int InputRate { get; }

    /// <summary>0 = off, 1 = normal (about 12 dB), 2 = strong (about 18 dB). May be changed while running.</summary>
    public int NoiseReduction
    {
        get => _noiseReduction;
        set => _noiseReduction = Math.Clamp(value, 0, 2);
    }

    /// <summary>Delay added by the processing, in milliseconds.</summary>
    public int LatencyMs => (_taps.Length / 2 * 1000 / InputRate) + (_noiseReduction > 0 ? SpectralNoiseReducer.LatencyMs : 0);

    /// <summary>
    /// Feeds 16-bit little-endian mono PCM at the capture rate; <paramref name="onFrame"/> is
    /// called for every completed frame of 160 samples at 8 kHz. The array is reused: copy or
    /// encode it before returning.
    /// </summary>
    public void Process(ReadOnlySpan<byte> pcm16, Action<short[]> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        var count = pcm16.Length / 2;
        if (count == 0) return;

        if (_factor == 1)
        {
            for (var i = 0; i < count; i++)
                Emit((short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8)), onFrame);
            return;
        }

        // Low-pass and keep every n-th sample. The filter needs the end of the previous block.
        var history = _carry.Length;
        if (_work.Length < history + count) _work = new float[history + count];
        _carry.CopyTo(_work, 0);
        for (var i = 0; i < count; i++)
            _work[history + i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));

        var taps = _taps;
        for (var i = 0; i < count; i++)
        {
            if (++_phase < _factor) continue;
            _phase = 0;
            var sum = 0f;
            var start = i; // newest sample is _work[history + i]; the window covers taps.Length samples
            for (var t = 0; t < taps.Length; t++)
                sum += taps[t] * _work[start + t];
            Emit(sum, onFrame);
        }

        Array.Copy(_work, count, _carry, 0, history);
    }

    private void Emit(float sample, Action<short[]> onFrame)
    {
        sample = _highPass2.Process(_highPass1.Process(sample));

        var level = _noiseReduction;
        if (level != _activeNoiseReduction)
        {
            if (_activeNoiseReduction == 0) { _reducer.Reset(); _reducerCount = 0; }
            _activeNoiseReduction = level;
        }

        if (level == 0)
        {
            Output(sample, onFrame);
            return;
        }

        _reducer.Floor = level >= 2 ? 0.126f : 0.25f;
        _reducerIn[_reducerCount++] = sample;
        if (_reducerCount < _reducerIn.Length) return;
        _reducerCount = 0;
        _reducer.ProcessHop(_reducerIn, _reducerOut);
        foreach (var cleaned in _reducerOut)
            Output(cleaned, onFrame);
    }

    private void Output(float sample, Action<short[]> onFrame)
    {
        _frame[_frameCount++] = Limit(sample);
        if (_frameCount < FrameSamples) return;
        _frameCount = 0;
        onFrame(_frame);
    }

    /// <summary>Unchanged below 70 % of full scale; above that, peaks are rounded off instead of cut.</summary>
    private static short Limit(float sample)
    {
        var magnitude = Math.Abs(sample);
        if (magnitude > LimiterKnee)
        {
            var room = short.MaxValue - LimiterKnee;
            magnitude = LimiterKnee + room * MathF.Tanh((magnitude - LimiterKnee) / room);
        }
        var value = (int)MathF.Round(magnitude);
        if (value > short.MaxValue) value = short.MaxValue;
        return (short)(sample < 0 ? -value : value);
    }

    // Windowed-sinc low-pass (Hamming), unity gain at DC.
    private static float[] DesignLowPass(int sampleRate, double cutoffHz, int length)
    {
        var taps = new double[length];
        var centre = (length - 1) / 2.0;
        var normalized = cutoffHz / sampleRate;
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            var x = i - centre;
            var sinc = Math.Abs(x) < 1e-9 ? 2 * normalized : Math.Sin(2 * Math.PI * normalized * x) / (Math.PI * x);
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (length - 1));
            taps[i] = sinc * window;
            sum += taps[i];
        }

        var result = new float[length];
        for (var i = 0; i < length; i++)
            result[i] = (float)(taps[i] / sum);
        return result;
    }

    private sealed class Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = (float)(b0 / a0);
            _b1 = (float)(b1 / a0);
            _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0);
            _a2 = (float)(a2 / a0);
        }

        public static Biquad HighPass(double sampleRate, double cutoffHz, double q)
        {
            var w = 2 * Math.PI * cutoffHz / sampleRate;
            var cos = Math.Cos(w);
            var alpha = Math.Sin(w) / (2 * q);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        // Transposed direct form II.
        public float Process(float x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}

/// <summary>
/// Reduces steady background noise in 8 kHz speech: the signal is split into narrow
/// frequency bands, the noise level of every band is followed during the gaps between
/// words, and each band is turned down by the amount the noise dominates it (Wiener
/// gain with decision-directed smoothing). Bands are never turned down further than
/// <see cref="Floor"/>, which keeps the remaining noise natural instead of "watery".
/// </summary>
internal sealed class SpectralNoiseReducer
{
    public const int Hop = 80;              // 10 ms
    private const int Window = 240;         // 30 ms, three overlapping windows
    private const int FftSize = 256;
    private const int Bins = FftSize / 2 + 1;
    public const int LatencyMs = (Window - Hop) * 1000 / TxAudioProcessor.OutputRate;

    private const float PowerSmoothing = 0.7f;
    private const float PresenceRatio = 5f;       // a band this far above its recent minimum carries speech
    private const float PresenceSmoothing = 0.2f;
    private const float NoiseSmoothing = 0.95f;
    private const float PrioriSmoothing = 0.98f;
    private const int MinimumWindowFrames = 100;  // 1 s; the minimum is taken over 1 to 2 s

    private readonly float[] _window = new float[Window];
    private readonly float[] _input = new float[Window];
    private readonly float[] _overlap = new float[Window];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _noise = new float[Bins];
    private readonly float[] _smooth = new float[Bins];
    private readonly float[] _minimum = new float[Bins];
    private readonly float[] _minimumNext = new float[Bins];
    private readonly float[] _presence = new float[Bins];
    private readonly float[] _previous = new float[Bins];
    private int _frames;

    public SpectralNoiseReducer()
    {
        for (var i = 0; i < Window; i++)
            _window[i] = MathF.Sqrt(0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / Window));
    }

    /// <summary>Lowest gain of a band, 0-1.</summary>
    public float Floor { get; set; } = 0.25f;

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_overlap);
        Array.Clear(_presence);
        Array.Clear(_previous);
        _frames = 0;
    }

    /// <summary>Takes 80 samples and returns 80 samples, delayed by 160 samples.</summary>
    public void ProcessHop(ReadOnlySpan<float> input, Span<float> output)
    {
        Array.Copy(_input, Hop, _input, 0, Window - Hop);
        input.CopyTo(_input.AsSpan(Window - Hop));

        for (var i = 0; i < Window; i++) _re[i] = _input[i] * _window[i];
        Array.Clear(_re, Window, FftSize - Window);
        Array.Clear(_im);
        Transform(_re, _im, inverse: false);

        var first = _frames == 0;
        for (var k = 0; k < Bins; k++)
        {
            var power = _re[k] * _re[k] + _im[k] * _im[k] + 1e-3f;

            if (first)
            {
                _smooth[k] = _minimum[k] = _minimumNext[k] = _noise[k] = power;
            }
            else
            {
                _smooth[k] = PowerSmoothing * _smooth[k] + (1 - PowerSmoothing) * power;
                if (_smooth[k] < _minimum[k]) _minimum[k] = _smooth[k];
                if (_smooth[k] < _minimumNext[k]) _minimumNext[k] = _smooth[k];
            }

            // Follow the noise only while the band holds no speech.
            var speech = _smooth[k] > PresenceRatio * _minimum[k] ? 1f : 0f;
            _presence[k] = PresenceSmoothing * _presence[k] + (1 - PresenceSmoothing) * speech;
            var keep = NoiseSmoothing + (1 - NoiseSmoothing) * _presence[k];
            _noise[k] = keep * _noise[k] + (1 - keep) * power;

            var posteriori = power / _noise[k];
            var priori = PrioriSmoothing * _previous[k] + (1 - PrioriSmoothing) * MathF.Max(posteriori - 1f, 0f);
            var gain = MathF.Max(priori / (1f + priori), Floor);
            _previous[k] = gain * gain * posteriori;

            _re[k] *= gain;
            _im[k] *= gain;
            if (k > 0 && k < FftSize / 2)
            {
                _re[FftSize - k] = _re[k];
                _im[FftSize - k] = -_im[k];
            }
        }

        if (++_frames % MinimumWindowFrames == 0)
        {
            for (var k = 0; k < Bins; k++)
            {
                _minimum[k] = MathF.Min(_minimumNext[k], _smooth[k]);
                _minimumNext[k] = _smooth[k];
            }
        }

        Transform(_re, _im, inverse: true);

        // Three windows overlap; their squares add up to 1.5.
        for (var i = 0; i < Window; i++)
            _overlap[i] += _re[i] * _window[i] * (1f / 1.5f);
        _overlap.AsSpan(0, Hop).CopyTo(output);
        Array.Copy(_overlap, Hop, _overlap, 0, Window - Hop);
        Array.Clear(_overlap, Window - Hop, Hop);
    }

    // In-place radix-2 FFT; the inverse is scaled by 1/N.
    private static void Transform(float[] re, float[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var stepRe = (float)Math.Cos(angle);
            var stepIm = (float)Math.Sin(angle);
            for (var start = 0; start < n; start += length)
            {
                float wRe = 1f, wIm = 0f;
                var half = length >> 1;
                for (var k = 0; k < half; k++)
                {
                    var a = start + k;
                    var b = a + half;
                    var tRe = re[b] * wRe - im[b] * wIm;
                    var tIm = re[b] * wIm + im[b] * wRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    var nextRe = wRe * stepRe - wIm * stepIm;
                    wIm = wRe * stepIm + wIm * stepRe;
                    wRe = nextRe;
                }
            }
        }

        if (!inverse) return;
        var scale = 1f / n;
        for (var i = 0; i < n; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }
}
