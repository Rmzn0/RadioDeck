using NAudio.Codecs;

namespace RadioDeck.Services.Audio;

/// <summary>G.711 A-law (PCMA) helpers shared by capture, playback and the level meters.</summary>
public static class G711
{
    /// <summary>A-law code for digital silence.</summary>
    public const byte Silence = 0xD5;

    private static readonly short[] DecodeTable = BuildDecodeTable();

    private static short[] BuildDecodeTable()
    {
        var table = new short[256];
        for (var i = 0; i < table.Length; i++)
            table[i] = ALawDecoder.ALawToLinearSample((byte)i);
        return table;
    }

    public static short Decode(byte alaw) => DecodeTable[alaw];

    public static byte Encode(short pcm) => ALawEncoder.LinearToALawSample(pcm);

    /// <summary>Encodes 16-bit little-endian mono PCM into A-law, one byte per sample.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> pcm16)
    {
        var encoded = new byte[pcm16.Length / 2];
        for (var i = 0; i < encoded.Length; i++)
            encoded[i] = Encode((short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8)));
        return encoded;
    }

    /// <summary>Encodes 16-bit mono samples into A-law, one byte per sample.</summary>
    public static byte[] Encode(ReadOnlySpan<short> pcm)
    {
        var encoded = new byte[pcm.Length];
        for (var i = 0; i < encoded.Length; i++)
            encoded[i] = Encode(pcm[i]);
        return encoded;
    }

    /// <summary>
    /// Copy of a frame with a linear fade from silence to full level (or the reverse),
    /// used where the stream switches between speech and silence so it does not click.
    /// </summary>
    public static byte[] Fade(ReadOnlySpan<byte> alaw, bool fadeIn)
    {
        var faded = new byte[alaw.Length];
        for (var i = 0; i < faded.Length; i++)
        {
            var ramp = (i + 1) / (double)faded.Length;
            if (!fadeIn) ramp = 1.0 - ramp;
            faded[i] = Encode((short)Math.Round(DecodeTable[alaw[i]] * ramp));
        }
        return faded;
    }

    public static byte[] SilenceFrame(int length)
    {
        var frame = new byte[Math.Max(0, length)];
        Array.Fill(frame, Silence);
        return frame;
    }

    /// <summary>Meter value 0-100 from the mean absolute amplitude, boosted so quiet speech is visible.</summary>
    public static int Level(ReadOnlySpan<byte> alaw)
    {
        if (alaw.Length == 0) return 0;
        long sum = 0;
        foreach (var sample in alaw)
            sum += Math.Abs((int)DecodeTable[sample]);
        var level = (int)Math.Round(sum / (double)alaw.Length / 32124.0 * 180);
        return Math.Clamp(level, 0, 100);
    }
}
