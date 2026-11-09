using System.Runtime.InteropServices;
using System.Threading;
using AgentsCursor.Core;

namespace AgentsCursor.App.Services;

/// <summary>
/// Plays the built-in chimes (<c>Assets\Sounds\*.wav</c>, embedded; made by
/// <c>tools/generate-chimes.py</c>). Each play scales a copy of the sound to the volume
/// (<see cref="ChimeAudio"/>), so the system volume is never changed, and hands it to
/// <c>PlaySound</c> asynchronously from a thread-pool thread: the caller (the UI thread) never waits on
/// the audio device, and the overlay's animation thread is never involved. A new chime replaces one
/// still playing.
/// </summary>
public sealed class ChimePlayer : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<Chime, byte[]> _sounds = new();
    IntPtr _playing;   // the buffer PlaySound is reading (it must outlive the sound)
    bool _disposed;

    /// <summary>Plays <paramref name="chime"/> at <paramref name="volume"/> (0-100) without waiting.
    /// None, or volume 0, plays nothing.</summary>
    public void Play(Chime chime, int volume)
    {
        if (chime == Chime.None || volume <= 0) return;
        ThreadPool.QueueUserWorkItem(_ => PlayNow(chime, volume));
    }

    void PlayNow(Chime chime, int volume)
    {
        // A thread-pool thread: nothing may escape it, or the whole tray would go down with it.
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                byte[] wav = ChimeAudio.Scale(Sound(chime), ChimeAudio.Gain(volume));
                IntPtr buffer = Marshal.AllocHGlobal(wav.Length);
                Marshal.Copy(wav, 0, buffer, wav.Length);
                if (!PlaySound(buffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                {
                    Marshal.FreeHGlobal(buffer);
                    AppLog.Write($"couldn't play the {chime} chime (no audio device?)");
                    return;
                }
                // The new sound stopped the one before, so that one's buffer is free now.
                if (_playing != IntPtr.Zero) Marshal.FreeHGlobal(_playing);
                _playing = buffer;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"couldn't play the {chime} chime: {ex}");
        }
    }

    // The chime's WAV, read from the embedded resources once.
    byte[] Sound(Chime chime)
    {
        if (_sounds.TryGetValue(chime, out var wav)) return wav;
        string name = $"Sounds/{chime.ToString().ToLowerInvariant()}.wav";
        using var stream = typeof(ChimePlayer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("missing embedded sound " + name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return _sounds[chime] = copy.ToArray();
    }

    /// <summary>Stops any chime and frees its buffer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0); // stop: nothing reads the buffer after this
            if (_playing != IntPtr.Zero) Marshal.FreeHGlobal(_playing);
            _playing = IntPtr.Zero;
        }
    }

    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;
    [DllImport("winmm.dll")] static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
