using RadioDeck.Services.Audio;
using NAudio.Wave;
using static T;

/// <summary>Microphone clean-up (band limiting, noise reduction, limiter) and the playback buffer.</summary>
internal static class AudioRegression
{
    public static async Task Run()
    {
        await Check("Microphone audio is limited to the voice band and framed as 20 ms at 8 kHz", VoiceBand);
        await Check("Loud speech is rounded off, normal levels pass unchanged", Limiter);
        await Check("Noise reduction turns steady noise down and keeps speech", NoiseReduction);
        await Check("Playback collects a reserve, plays in order and drops instead of growing", PlaybackBuffer);
        await Check("Fades between speech and silence start and end at the right level", Fades);
    }

    /// <summary>Optional (--native-audio): the real capture path on this PC's default microphone.</summary>
    public static async Task NativeMicrophone()
    {
        using var hub = new MicrophoneHub();
        var log = new List<string>();
        hub.Log += line => { lock (log) log.Add(line); };
        hub.SetNoiseReduction(1);

        int frames = 0, wrongSize = 0, peak = 0;
        using (hub.Subscribe(frame =>
               {
                   Interlocked.Increment(ref frames);
                   if (frame.Length != 160) Interlocked.Increment(ref wrongSize);
                   var level = G711.Level(frame);
                   if (level > peak) peak = level;
               }))
        {
            await Task.Delay(2000);
        }

        lock (log) foreach (var line in log) Console.WriteLine("     " + line);
        Console.WriteLine($"     {frames} frames in 2 s, peak level {peak} %");
        Require(frames is >= 80 and <= 110, $"Expected about 100 frames in two seconds, got {frames}");
        Require(wrongSize == 0, $"{wrongSize} frames were not 20 ms long");
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] ToBytes(IReadOnlyList<double> samples)
    {
        var bytes = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
        {
            var value = (short)Math.Clamp(Math.Round(samples[i]), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)value;
            bytes[i * 2 + 1] = (byte)(value >> 8);
        }
        return bytes;
    }

    private static double[] Sine(int rate, double hz, double amplitude, double seconds)
    {
        var samples = new double[(int)(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = amplitude * Math.Sin(2 * Math.PI * hz * i / rate);
        return samples;
    }

    /// <summary>Runs the processor over the input in uneven blocks, as a sound driver would deliver it.</summary>
    private static short[] Run(TxAudioProcessor processor, byte[] pcm, int blockBytes = 1000)
    {
        var output = new List<short>();
        for (var offset = 0; offset < pcm.Length; offset += blockBytes)
        {
            processor.Process(pcm.AsSpan(offset, Math.Min(blockBytes, pcm.Length - offset)), frame =>
            {
                Require(frame.Length == TxAudioProcessor.FrameSamples, $"Frame of {frame.Length} samples");
                output.AddRange(frame);
            });
        }
        return output.ToArray();
    }

    private static double Rms(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    private static double Decibel(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-9));

    // ---------------------------------------------------------------- checks

    private static void VoiceBand()
    {
        const int rate = 48000;
        const double amplitude = 10000;
        var reference = amplitude / Math.Sqrt(2);

        double Gain(double hz)
        {
            var output = Run(new TxAudioProcessor(rate), ToBytes(Sine(rate, hz, amplitude, 1.0)));
            Require(output.Length == rate / 6 / 160 * 160, $"{output.Length} samples from one second of capture");
            return Rms(output.AsSpan(800)) / reference; // skip the first 100 ms while the filters settle
        }

        foreach (var hz in new[] { 300.0, 500, 1000, 2000, 3000 })
        {
            var gain = Gain(hz);
            Require(gain is > 0.85 and < 1.1, $"{hz} Hz is speech and must pass (gain {gain:0.000})");
        }

        // Above the voice band: at 8 kHz these would fold back to 3.5, 3, 1 and 0 kHz.
        foreach (var hz in new[] { 4500.0, 5000, 7000, 8000, 12000, 20000 })
        {
            var gain = Gain(hz);
            Require(gain < 0.01, $"{hz} Hz must be removed before the conversion to 8 kHz (gain {gain:0.0000})");
        }

        // Below the voice band: wind rumble, engine drone, mains hum.
        Require(Gain(50) < 0.005 && Gain(100) < 0.05, $"Rumble and hum must be removed (50 Hz {Gain(50):0.0000}, 100 Hz {Gain(100):0.0000})");

        // A capture that is already at 8 kHz is only filtered and framed.
        var direct = Run(new TxAudioProcessor(8000), ToBytes(Sine(8000, 1000, amplitude, 1.0)), blockBytes: 322);
        Require(direct.Length == 8000 && Rms(direct.AsSpan(800)) / reference is > 0.95 and < 1.05, "8 kHz capture must pass unchanged");

        var rejected = false;
        try { _ = new TxAudioProcessor(44100); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected, "A capture rate that is not a multiple of 8 kHz must be refused");
    }

    private static void Limiter()
    {
        var normal = Run(new TxAudioProcessor(8000), ToBytes(Sine(8000, 1000, 8000, 0.5)));
        var peak = normal.Skip(800).Max(s => Math.Abs((int)s));
        Require(peak is > 7800 and < 8200, $"Normal speech level changed (peak {peak})");

        var loud = Run(new TxAudioProcessor(8000), ToBytes(Sine(8000, 937, 32767, 0.5)));
        var loudPeak = loud.Skip(800).Max(s => Math.Abs((int)s));
        Require(loudPeak is > 26000 and <= short.MaxValue, $"Loud speech must be limited, not wrapped or crushed (peak {loudPeak})");

        // A flat top (many samples at the same extreme value) is what hard clipping looks like.
        var top = loud.Skip(800).Count(s => Math.Abs((int)s) >= loudPeak - 1);
        Require(top < loud.Length / 20, $"The peaks are cut flat ({top} samples at the maximum)");
    }

    private static void NoiseReduction()
    {
        const int rate = 8000;
        const int seconds = 8;
        const int period = 6400;  // 0.4 s of speech, 0.4 s pause
        var random = new Random(12345);
        var input = new double[rate * seconds];
        for (var i = 0; i < input.Length; i++)
        {
            // Gaussian noise, like fan or engine noise spread over the voice band.
            var noise = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()) * 300;
            double speech = 0;
            if (i % period < period / 2)
                for (var harmonic = 2; harmonic <= 20; harmonic++)
                    speech += 600 * Math.Sin(2 * Math.PI * 150 * harmonic * i / rate + harmonic);
            input[i] = noise + speech;
        }
        var pcm = ToBytes(input);

        var plain = Run(new TxAudioProcessor(rate, 0), pcm);
        var normal = Run(new TxAudioProcessor(rate, 1), pcm);
        var strong = Run(new TxAudioProcessor(rate, 2), pcm);
        Require(plain.Length == input.Length && normal.Length >= input.Length - 480, "Noise reduction must not lose audio");

        // Second half only (the noise level is known by then); 80 ms at every edge left out.
        (double Speech, double Pause) Measure(short[] audio)
        {
            double speech = 0, pause = 0;
            int speechParts = 0, pauseParts = 0;
            for (var start = rate * 4; start + period <= audio.Length; start += period)
            {
                speech += Rms(audio.AsSpan(start + 640, period / 2 - 1280)); speechParts++;
                pause += Rms(audio.AsSpan(start + period / 2 + 640, period / 2 - 1280)); pauseParts++;
            }
            return (speech / speechParts, pause / pauseParts);
        }

        var a = Measure(plain);
        var b = Measure(normal);
        var c = Measure(strong);
        var noiseNormal = Decibel(b.Pause / a.Pause);
        var noiseStrong = Decibel(c.Pause / a.Pause);
        var speechNormal = Decibel(b.Speech / a.Speech);
        var speechStrong = Decibel(c.Speech / a.Speech);
        Console.WriteLine($"     noise reduction: normal {noiseNormal:0.0} dB noise / {speechNormal:0.0} dB speech, strong {noiseStrong:0.0} dB / {speechStrong:0.0} dB");

        Require(noiseNormal < -8, $"Normal: noise in the pauses only {noiseNormal:0.0} dB lower");
        Require(noiseStrong < -12 && noiseStrong < noiseNormal - 2, $"Strong: noise in the pauses only {noiseStrong:0.0} dB lower");
        Require(speechNormal > -1.5 && speechStrong > -2.0, $"Speech was turned down too ({speechNormal:0.0} dB / {speechStrong:0.0} dB)");
        Require(normal.All(s => s > short.MinValue) && Rms(normal.AsSpan(0, 160)) < 2000, "Start-up artefact");

        // Switching while running keeps the stream going.
        var processor = new TxAudioProcessor(rate, 0);
        var first = Run(processor, pcm.AsSpan(0, rate * 2).ToArray());
        processor.NoiseReduction = 2;
        var second = Run(processor, pcm.AsSpan(rate * 2, rate * 2).ToArray());
        processor.NoiseReduction = 0;
        var third = Run(processor, pcm.AsSpan(rate * 4, rate * 2).ToArray());
        Require(first.Length + second.Length + third.Length >= rate * 3 - 480, "Audio was lost when the setting changed");
    }

    private static void PlaybackBuffer()
    {
        var format = new WaveFormat(8000, 16, 2);
        const int packetBytes = 640; // 20 ms
        var number = 0;
        byte[] Packet()
        {
            var packet = new byte[packetBytes];
            Array.Fill(packet, (byte)(++number));
            return packet;
        }

        var buffer = new PlaybackJitterBuffer(format, startMs: 80, maxMs: 280);
        var block = new byte[packetBytes * 2]; // the device asks for 40 ms

        Require(buffer.Read(block, 0, block.Length) == block.Length && block.All(b => b == 0), "An empty buffer must play silence");
        buffer.Add(Packet(), 0, packetBytes);
        buffer.Add(Packet(), 0, packetBytes);
        buffer.Read(block, 0, block.Length);
        Require(block.All(b => b == 0) && buffer.BufferedMs == 40, "Playback started before the reserve was collected");

        buffer.Add(Packet(), 0, packetBytes);
        buffer.Add(Packet(), 0, packetBytes);
        buffer.Read(block, 0, block.Length);
        Require(block.Take(packetBytes).All(b => b == 1) && block.Skip(packetBytes).All(b => b == 2), "The first packets were not played in order");
        buffer.Read(block, 0, block.Length);
        Require(block.Take(packetBytes).All(b => b == 3) && block.Skip(packetBytes).All(b => b == 4) && buffer.BufferedMs == 0, "Packets 3 and 4 not played");

        // The reception ended. The next one collects its reserve again.
        buffer.Read(block, 0, block.Length);
        buffer.Add(Packet(), 0, packetBytes);
        buffer.Read(block, 0, block.Length);
        Require(block.All(b => b == 0), "A new reception must collect a reserve again");

        // Clock drift or a burst: single packets are dropped, the delay stays bounded and what is played stays in order.
        buffer.Clear();
        number = 0;
        var accepted = new List<byte>();
        for (var i = 0; i < 40; i++)
        {
            var packet = Packet();
            if (buffer.Add(packet, 0, packetBytes)) accepted.Add(packet[0]);
        }
        Require(buffer.BufferedMs is > 200 and <= 280 && buffer.Dropped == 40 - accepted.Count && buffer.Dropped > 0,
            $"The reserve is not bounded ({buffer.BufferedMs} ms, {buffer.Dropped} dropped)");
        var played = new List<byte>();
        while (buffer.BufferedMs > 0)
        {
            var before = buffer.BufferedMs;
            buffer.Read(block, 0, block.Length);
            for (var offset = 0; offset < block.Length && offset / 32 < before; offset += packetBytes)
                played.Add(block[offset]);
        }
        Require(played.SequenceEqual(accepted), "Buffered audio was played out of order");
    }

    private static void Fades()
    {
        var loud = Enumerable.Repeat(G711.Encode((short)12000), 160).ToArray();
        var fadeIn = G711.Fade(loud, fadeIn: true);
        var fadeOut = G711.Fade(loud, fadeIn: false);
        Require(fadeIn.Length == 160 && fadeOut.Length == 160, "Length changed");
        Require(Math.Abs(G711.Decode(fadeIn[0])) < 200 && Math.Abs(G711.Decode(fadeIn[159]) - 12000) < 700, "Fade-in must rise from silence to the full level");
        Require(Math.Abs(G711.Decode(fadeOut[159])) < 200 && Math.Abs(G711.Decode(fadeOut[0]) - 12000) < 700, "Fade-out must fall from the full level to silence");
        Require(Math.Abs(G711.Decode(fadeIn[80]) - 6000) < 500, "The fade is not linear");
    }
}
