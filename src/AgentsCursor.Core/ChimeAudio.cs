using System.Buffers.Binary;

namespace AgentsCursor.Core;

/// <summary>
/// Volume for the chimes, applied to the sound itself: the samples are scaled before playing, so the
/// system volume (and the app's mixer slider) is never touched. Pure, so it is unit-tested directly.
/// </summary>
public static class ChimeAudio
{
    /// <summary>The sample gain for <paramref name="volume"/> (0-100): squared, so the slider's steps
    /// sound roughly even (loudness follows the log of amplitude, and a square tracks it well over
    /// the range that matters). 0 is silent, 100 plays the file as recorded.</summary>
    public static float Gain(int volume)
    {
        float v = Math.Clamp(volume, CursorSettings.MinVolume, CursorSettings.MaxVolume) / (float)CursorSettings.MaxVolume;
        return v * v;
    }

    /// <summary>A copy of <paramref name="wav"/> (a 16-bit PCM RIFF WAVE file) with every sample in its
    /// <c>data</c> chunk multiplied by <paramref name="gain"/> (0-1). The headers are kept as they are.
    /// Throws <see cref="InvalidDataException"/> for anything but 16-bit PCM WAVE.</summary>
    public static byte[] Scale(byte[] wav, float gain)
    {
        var data = DataChunk(wav);
        var scaled = (byte[])wav.Clone();
        gain = Math.Clamp(gain, 0f, 1f);
        for (int i = data.Offset; i + 1 < data.Offset + data.Length; i += 2)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i));
            BinaryPrimitives.WriteInt16LittleEndian(scaled.AsSpan(i), (short)MathF.Round(sample * gain));
        }
        return scaled;
    }

    // Where the samples are: walks the RIFF chunks, checking the format is 16-bit PCM on the way.
    static (int Offset, int Length) DataChunk(byte[] wav)
    {
        if (wav.Length < 12 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE")) throw new InvalidDataException("not a WAVE file");
        bool pcm16 = false;
        for (int at = 12; at + 8 <= wav.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(at + 4));
            int body = at + 8;
            if (size < 0 || body + size > wav.Length) throw new InvalidDataException("truncated WAVE chunk");
            if (Tag(wav, at, "fmt ") && size >= 16)
                pcm16 = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body)) == 1 && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 14)) == 16;
            if (Tag(wav, at, "data"))
                return pcm16 ? (body, size) : throw new InvalidDataException("not 16-bit PCM");
            at = body + size + (size & 1); // chunks are word-aligned
        }
        throw new InvalidDataException("no data chunk");
    }

    static bool Tag(byte[] bytes, int at, string tag)
        => at + 4 <= bytes.Length && bytes[at] == tag[0] && bytes[at + 1] == tag[1] && bytes[at + 2] == tag[2] && bytes[at + 3] == tag[3];
}
