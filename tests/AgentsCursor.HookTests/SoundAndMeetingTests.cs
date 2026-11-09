using System.Buffers.Binary;
using AgentsCursor.Core;

/// <summary>The regression groups for the chimes (when one plays, its settings, volume and the built-in
/// files) and for hiding the token during meetings (the decision, the watcher, the live signals).</summary>
static class SoundAndMeetingTests
{
    static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}{(what is null ? "" : ": ")}expected '{expected}', got '{actual}'.");
    }

    static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    static WaitingAgent Blocked(string session) => new("claude-code", WaitingState.Blocked, session);
    static WaitingAgent Finished(string session) => new("codex", WaitingState.Finished, session);
    static readonly WaitingAgent[] None = Array.Empty<WaitingAgent>();

    // ---------- sounds ----------

    public static void SoundDecisions()
    {
        // Arrivals: a session that wasn't waiting, or went from finished to blocked; never one that
        // just stays, or goes from blocked to finished, or leaves.
        Equal("Blocked", string.Join(',', SoundCues.Arrivals(None, new[] { Blocked("a") })), "new blocked");
        Equal("Finished", string.Join(',', SoundCues.Arrivals(None, new[] { Finished("a") })), "new finished");
        Equal("", string.Join(',', SoundCues.Arrivals(new[] { Blocked("a") }, new[] { Blocked("a") })), "still blocked");
        Equal("Blocked", string.Join(',', SoundCues.Arrivals(new[] { Finished("a") }, new[] { Blocked("a") })), "finished -> blocked");
        Equal("", string.Join(',', SoundCues.Arrivals(new[] { Blocked("a") }, new[] { Finished("a") })), "blocked -> finished");
        Equal("", string.Join(',', SoundCues.Arrivals(new[] { Blocked("a"), Finished("b") }, new[] { Finished("b") })), "one leaves");
        Equal("Finished,Blocked", string.Join(',', SoundCues.Arrivals(new[] { Blocked("a") }, new[] { Blocked("a"), Finished("b"), Blocked("c") })), "two arrive");

        var on = new CursorSettings { SoundsEnabled = true };
        var t0 = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        // Off by default: nothing plays.
        Equal(false, new CursorSettings().SoundsEnabled, "sounds default off");
        Equal(Chime.None, new SoundCues().Next(None, new[] { Blocked("a") }, true, new CursorSettings(), t0), "off");

        // Blocked plays "Needs you", finished plays "Done"; a mix plays "Needs you".
        Equal(Chime.Rise, new SoundCues().Next(None, new[] { Blocked("a") }, true, on, t0), "needs you");
        Equal(Chime.Settle, new SoundCues().Next(None, new[] { Finished("a") }, true, on, t0), "done");
        Equal(Chime.Rise, new SoundCues().Next(None, new[] { Finished("a"), Blocked("b") }, true, on, t0), "blocked first");
        var bell = new CursorSettings { SoundsEnabled = true, NeedsYouSound = Chime.Bell, DoneSound = Chime.Glow };
        Equal(Chime.Bell, new SoundCues().Next(None, new[] { Blocked("a") }, true, bell, t0), "chosen needs-you chime");
        Equal(Chime.Glow, new SoundCues().Next(None, new[] { Finished("a") }, true, bell, t0), "chosen done chime");
        var quietBlocked = new CursorSettings { SoundsEnabled = true, NeedsYouSound = Chime.None };
        Equal(Chime.Settle, new SoundCues().Next(None, new[] { Blocked("a"), Finished("b") }, true, quietBlocked, t0), "a silent state gives way");
        Equal(Chime.None, new SoundCues().Next(None, new[] { Blocked("a") }, true, quietBlocked, t0), "None plays nothing");
        Equal(Chime.None, new SoundCues().Next(None, new[] { Blocked("a") }, true, new CursorSettings { SoundsEnabled = true, SoundVolume = 0 }, t0), "volume 0");

        // Never while the token is hidden, and a hidden arrival doesn't start a burst.
        var cues = new SoundCues();
        Equal(Chime.None, cues.Next(None, new[] { Blocked("a") }, false, on, t0), "hidden");
        Equal(Chime.Settle, cues.Next(new[] { Blocked("a") }, new[] { Blocked("a"), Finished("b") }, true, on, t0.AddMilliseconds(200)), "shown again");

        // A burst within a second is one sound; the next one after it plays again.
        cues = new SoundCues();
        Equal(Chime.Rise, cues.Next(None, new[] { Blocked("a") }, true, on, t0), "first of a burst");
        Equal(Chime.None, cues.Next(new[] { Blocked("a") }, new[] { Blocked("a"), Blocked("b") }, true, on, t0.AddMilliseconds(400)), "second in the burst");
        Equal(Chime.None, cues.Next(new[] { Blocked("a"), Blocked("b") }, new[] { Blocked("a"), Blocked("b"), Finished("c") }, true, on, t0.AddMilliseconds(900)), "third in the burst");
        Equal(Chime.Settle, cues.Next(new[] { Blocked("b") }, new[] { Blocked("b"), Finished("d") }, true, on, t0.AddMilliseconds(1100)), "after the burst");
        // A change that brings no arrival plays nothing (a session leaving, a sweep).
        Equal(Chime.None, cues.Next(new[] { Blocked("b"), Finished("d") }, new[] { Blocked("b") }, true, on, t0.AddSeconds(5)), "a session leaves");

        // The activity model names each session, so the same tool's two sessions are two arrivals.
        var activity = new AgentActivity(TimeSpan.FromMinutes(1));
        activity.Report("codex", "one", null, AgentEventKind.TurnComplete, t0);
        var first = activity.Waiting;
        activity.Report("codex", "two", null, AgentEventKind.TurnComplete, t0);
        Equal(1, SoundCues.Arrivals(first, activity.Waiting).Count, "a second session of the same tool arrives");
    }

    public static void SoundSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AgentsCursor.HookTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var defaults = new CursorSettings();
            Equal((false, Chime.Rise, Chime.Settle, 60), (defaults.SoundsEnabled, defaults.NeedsYouSound, defaults.DoneSound, defaults.SoundVolume), "defaults");
            File.WriteAllText(path, "{\"Size\": 120}"); // a file from before the sounds
            var old = SettingsStore.Load(path);
            Equal((false, Chime.Rise, Chime.Settle, 60), (old.SoundsEnabled, old.NeedsYouSound, old.DoneSound, old.SoundVolume), "an older file");

            SettingsStore.Save(new CursorSettings { SoundsEnabled = true, NeedsYouSound = Chime.Bell, DoneSound = Chime.None, SoundVolume = 35 }, path);
            string json = File.ReadAllText(path);
            True(json.Contains("\"NeedsYouSound\": \"Bell\"") && json.Contains("\"DoneSound\": \"None\""), "chimes saved by name: " + json);
            var loaded = SettingsStore.Load(path);
            Equal((true, Chime.Bell, Chime.None, 35), (loaded.SoundsEnabled, loaded.NeedsYouSound, loaded.DoneSound, loaded.SoundVolume), "round trip");

            File.WriteAllText(path, "{\"SoundVolume\": 250, \"NeedsYouSound\": 42}");
            loaded = SettingsStore.Load(path);
            Equal((100, Chime.Rise), (loaded.SoundVolume, loaded.NeedsYouSound), "clamped and defaulted");
            File.WriteAllText(path, "{\"SoundVolume\": -4}");
            Equal(0, SettingsStore.Load(path).SoundVolume, "negative volume");
            File.WriteAllText(path, "{\"DoneSound\": \"Kazoo\"}"); // an unknown name: the file is unreadable, so defaults
            Equal(Chime.Settle, SettingsStore.Load(path).DoneSound, "unknown chime name");

            var live = new LiveSettings(new CursorSettings(), _ => { });
            live.Change(s => s.SoundVolume = 500);
            Equal(100, live.Live.SoundVolume, "a live edit is clamped");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    public static void ChimeVolume()
    {
        Equal(0f, ChimeAudio.Gain(0), "silent");
        Equal(1f, ChimeAudio.Gain(100), "full");
        Equal(0.25f, ChimeAudio.Gain(50), "half the slider is a quarter of the amplitude");
        Equal(1f, ChimeAudio.Gain(300), "clamped");

        byte[] wav = Wav(new short[] { 0, 1000, -1000, 32767, -32768 });
        byte[] half = ChimeAudio.Scale(wav, 0.5f);
        Equal(wav.Length, half.Length, "same length");
        True(wav.AsSpan(0, 44).SequenceEqual(half.AsSpan(0, 44)), "headers untouched");
        Equal("0,500,-500,16384,-16384", string.Join(',', Samples(half)), "samples halved");
        Equal("0,1000,-1000,32767,-32768", string.Join(',', Samples(wav)), "the original is left alone");
        Equal("0,0,0,0,0", string.Join(',', Samples(ChimeAudio.Scale(wav, 0f))), "silenced");
        Threw<InvalidDataException>(() => ChimeAudio.Scale(new byte[] { 1, 2, 3 }, 1f), "not a WAVE");
        byte[] eightBit = (byte[])wav.Clone();
        BinaryPrimitives.WriteInt16LittleEndian(eightBit.AsSpan(34), 8);
        Threw<InvalidDataException>(() => ChimeAudio.Scale(eightBit, 1f), "8-bit");
    }

    // The files the app embeds: 16-bit mono PCM, under a second, peaking at -3 dBFS with no clipping,
    // starting and ending in silence, and different from one another.
    public static void BuiltInChimes()
    {
        string folder = Path.Combine(RepoRoot(), "src", "AgentsCursor.App", "Assets", "Sounds");
        var seen = new HashSet<string>();
        foreach (var chime in Enum.GetValues<Chime>().Where(c => c != Chime.None))
        {
            string path = Path.Combine(folder, chime.ToString().ToLowerInvariant() + ".wav");
            byte[] wav = File.ReadAllBytes(path);
            Equal(1, (int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)), $"{chime}: mono");
            int rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24));
            short[] samples = Samples(wav);
            double seconds = samples.Length / (double)rate;
            True(seconds is > 0.3 and <= 1.0, $"{chime}: {seconds:0.00} s");
            int peak = samples.Max(s => Math.Abs((int)s));
            True(peak is > 20000 and < 32000, $"{chime}: peak {peak}");
            True(Math.Abs((int)samples[0]) < 100 && samples.TakeLast(20).All(s => Math.Abs((int)s) < 100), $"{chime}: starts and ends silent");
            True(seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav))), $"{chime}: distinct");
            ChimeAudio.Scale(wav, 0.5f); // parses
        }
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AgentsCursor.sln"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    // A minimal 16-bit mono 44.1 kHz WAVE file holding <samples>.
    static byte[] Wav(short[] samples)
    {
        var bytes = new byte[44 + samples.Length * 2];
        void Tag(int at, string tag) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)tag[i]; }
        Tag(0, "RIFF"); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8); Tag(8, "WAVE");
        Tag(12, "fmt "); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 88200);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        Tag(36, "data"); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples.Length * 2);
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + 2 * i), samples[i]);
        return bytes;
    }

    // The samples of a WAVE file whose data chunk starts at byte 44 (as the generator writes them).
    static short[] Samples(byte[] wav)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        var samples = new short[length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + 2 * i));
        return samples;
    }

    static void Threw<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name);
    }

    // ---------- meetings ----------

    public static void MeetingDecisions()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var longAgo = now.AddHours(-2);
        DeviceUse Use(MeetingSignal signal, string app, double secondsAgo, DateTime? runningSince)
            => new(signal, app, now.AddSeconds(-secondsAgo), runningSince);
        const string zoom = @"C:\Users\me\AppData\Roaming\Zoom\bin\Zoom.exe";

        Equal<Meeting?>(null, MeetingDetection.Detect(false, false, Array.Empty<DeviceUse>(), now), "nothing");
        Equal(new Meeting(MeetingSignal.Presenting, null), MeetingDetection.Detect(true, true, new[] { Use(MeetingSignal.Camera, zoom, 60, longAgo) }, now), "presentation mode first");
        Equal(new Meeting(MeetingSignal.DisplayDuplicated, null), MeetingDetection.Detect(false, true, Array.Empty<DeviceUse>(), now), "duplicated displays");

        // The camera and screen capture count after a moment (a screenshot holds the capture for ms).
        Equal<Meeting?>(null, MeetingDetection.Detect(false, false, new[] { Use(MeetingSignal.ScreenShared, "Microsoft.ScreenSketch_8wekyb3d8bbwe", 0.03, longAgo) }, now), "a screenshot");
        Equal(new Meeting(MeetingSignal.ScreenShared, zoom), MeetingDetection.Detect(false, false, new[] { Use(MeetingSignal.ScreenShared, zoom, 2, longAgo) }, now), "sharing");
        Equal(new Meeting(MeetingSignal.Camera, "MSTeams_8wekyb3d8bbwe"), MeetingDetection.Detect(false, false, new[] { Use(MeetingSignal.Camera, "MSTeams_8wekyb3d8bbwe", 2, longAgo) }, now), "camera");

        // An entry left "in use" by a copy that's gone never counts: the app isn't running, or what
        // runs now started after the use began (the old copy was killed mid-call, or the PC shut down).
        Equal<Meeting?>(null, MeetingDetection.Detect(false, false, new[] { Use(MeetingSignal.Camera, zoom, 3600, null) }, now), "app not running");
        Equal<Meeting?>(null, MeetingDetection.Detect(false, false, new[] { Use(MeetingSignal.Camera, zoom, 3600, now.AddMinutes(-5)) }, now), "a newer copy runs");
        Equal(true, MeetingDetection.IsLeftBehind(Use(MeetingSignal.Camera, zoom, 3600, now.AddMinutes(-5))), "left behind");
        Equal(false, MeetingDetection.IsLeftBehind(Use(MeetingSignal.Camera, zoom, 3600, now.AddHours(-2))), "held by a running copy");
        // The microphone alone is no signal at all (dictation, voice chat and audio mixers hold it for
        // hours): there is nothing to report it as.
        Equal(false, Enum.GetNames<MeetingSignal>().Any(n => n.Contains("Micro")), "no microphone signal");

        // The strongest signal wins: sharing, then the camera.
        var all = new[]
        {
            Use(MeetingSignal.Camera, "chrome", 60, longAgo),
            Use(MeetingSignal.ScreenShared, @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", 60, longAgo),
        };
        Equal(MeetingSignal.ScreenShared, MeetingDetection.Detect(false, false, all, now)!.Signal, "sharing outranks the camera");

        Equal("Zoom", MeetingDetection.AppName(zoom), "exe name");
        Equal("MSTeams", MeetingDetection.AppName("MSTeams_8wekyb3d8bbwe"), "package");
        Equal("WhatsAppDesktop", MeetingDetection.AppName("5319275A.WhatsAppDesktop_cv1g1gvanyjgm"), "package with a publisher");
        Equal(true, MeetingDetection.IsExePath(zoom), "path");
        Equal(false, MeetingDetection.IsExePath("MSTeams_8wekyb3d8bbwe"), "package family");

        // Why the token is hidden: one decision, in order.
        var call = new Meeting(MeetingSignal.Camera, zoom);
        Equal(TokenVisibility.Idle, TokenVisibility.Decide(false, false, true, false, call), "nothing waiting comes first");
        Equal(HideReason.TurnedOff, TokenVisibility.Decide(true, false, true, false, call).Reason, "turned off");
        Equal(HideReason.AppInFront, TokenVisibility.Decide(true, true, true, false, call).Reason, "an app in front");
        Equal(new TokenVisibility(HideReason.Meeting, call), TokenVisibility.Decide(true, true, false, false, call), "a meeting");
        Equal(HideReason.None, TokenVisibility.Decide(true, true, false, true, call).Reason, "shown during meetings when asked");
        Equal(true, TokenVisibility.Decide(true, true, false, false, null).Shown, "shown");
        // The signals are only checked when they could hide a token that would otherwise show.
        Equal(true, TokenVisibility.NeedsMeetingCheck(true, true, false, false), "check");
        Equal(false, TokenVisibility.NeedsMeetingCheck(false, true, false, false), "nothing waiting: no check");
        Equal(false, TokenVisibility.NeedsMeetingCheck(true, false, false, false), "turned off: no check");
        Equal(false, TokenVisibility.NeedsMeetingCheck(true, true, true, false), "an app in front: no check");
        Equal(false, TokenVisibility.NeedsMeetingCheck(true, true, false, true), "shown during meetings: no check");
        Equal(false, new CursorSettings().ShowDuringMeetings, "hidden during meetings by default");
    }

    // The watcher with a fake probe: it checks only while watching, reports changes, and treats a
    // failing probe as no meeting.
    public static void MeetingWatching()
    {
        Meeting? reported = null;
        int probes = 0;
        var logs = new List<string>();
        using var changed = new SemaphoreSlim(0);
        using (var watcher = new MeetingWatcher(() => { Interlocked.Increment(ref probes); return reported; }, TimeSpan.FromMilliseconds(20), logs.Add))
        {
            watcher.Changed += () => changed.Release();
            Equal(0, probes, "nothing is checked until asked");
            reported = new Meeting(MeetingSignal.Camera, "app");
            watcher.Watch(true);
            Equal(1, probes, "watching checks at once");
            Equal(reported, watcher.Current, "found at once");
            Thread.Sleep(120);
            True(probes > 2, $"keeps checking while watching ({probes})");

            reported = null;
            True(changed.Wait(2000), "a change is reported");
            Equal<Meeting?>(null, watcher.Current, "the meeting ended");

            watcher.Watch(false);
            int stopped = Volatile.Read(ref probes);
            Thread.Sleep(120);
            Equal(stopped, Volatile.Read(ref probes), "no checks while not watching");
            reported = new Meeting(MeetingSignal.ScreenShared, "app");
            Equal<Meeting?>(null, watcher.Current, "not watching: no meeting");
            watcher.Watch(true);
            Equal(reported, watcher.Current, "watching again checks again");
        }
        int afterDispose = Volatile.Read(ref probes);
        Thread.Sleep(80);
        Equal(afterDispose, Volatile.Read(ref probes), "disposed: no more checks");

        using (var failing = new MeetingWatcher(() => throw new UnauthorizedAccessException("denied"), TimeSpan.FromMilliseconds(10), logs.Add))
        {
            failing.Watch(true);
            Thread.Sleep(80);
            Equal<Meeting?>(null, failing.Current, "a failing check is no meeting");
        }
        Equal(1, logs.Count, "the failure is logged once");
    }

    // The reader looks an app up only while one of its uses could still count: a use found left
    // behind is remembered (it can never count again) until the store stops listing it.
    public static void MeetingSignalsCache()
    {
        var now = DateTime.UtcNow;
        var stale = new HeldDevice(MeetingSignal.ScreenShared, @"C:\Apps\Discord\Discord.exe", now.AddDays(-16));
        var live = new HeldDevice(MeetingSignal.Camera, "MSTeams_8wekyb3d8bbwe", now.AddMinutes(-3));
        var held = new List<HeldDevice> { stale };
        var running = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [stale.App] = now.AddHours(-1),   // a copy started after the use: that use was left behind
            [live.App] = now.AddHours(-1),    // started before the use: it holds the camera
        };
        var lookedUp = new List<string>();
        var reader = new MeetingSignals(() => held.ToArray(), apps => { lookedUp.AddRange(apps); return running; });

        Equal(0, reader.DevicesInUse().Count, "only a stale entry: nothing in use");
        Equal("Discord.exe", string.Join(",", lookedUp.Select(Path.GetFileName)), "looked up once");
        lookedUp.Clear();
        for (int i = 0; i < 5; i++) Equal(0, reader.DevicesInUse().Count, "still nothing");
        Equal(0, lookedUp.Count, "a known stale entry is never looked up again");

        held.Add(live);
        var uses = reader.DevicesInUse();
        Equal(1, uses.Count, "the camera in use");
        Equal(live.App, uses[0].App, "it is the live one");
        Equal("MSTeams_8wekyb3d8bbwe", string.Join(",", lookedUp), "only the live entry's app is looked up");
        lookedUp.Clear();
        Equal(1, reader.DevicesInUse().Count, "still in use");
        Equal(1, lookedUp.Count, "a use that counts is checked each time (its app may quit)");

        // The stale entry goes, then comes back with the same start: forgotten, so looked up afresh.
        held.Remove(stale); held.Remove(live);
        reader.DevicesInUse();
        lookedUp.Clear();
        held.Add(stale);
        reader.DevicesInUse();
        Equal(1, lookedUp.Count, "forgotten once the store stopped listing it");
    }

    // The live signals read without throwing on this machine (read-only; whatever they say).
    public static void MeetingSignalsSmoke()
    {
        MeetingSignals.PresentationMode();
        MeetingSignals.DisplaysDuplicated();
        var reader = new MeetingSignals();
        foreach (var use in reader.DevicesInUse())
        {
            True(use.App.Length > 0 && use.StartUtc.Kind == DateTimeKind.Utc, "a device use names its app: " + use);
            // An app that isn't running has no start time at all (never a default date, which would
            // read as "running since forever" and make a stale entry count).
            True(use.RunningSinceUtc is null || use.RunningSinceUtc.Value.Year > 2000, "a real start time or none: " + use);
            True(!MeetingDetection.IsLeftBehind(use), "a reader returns only uses that can count: " + use);
        }
        reader.Read();
    }
}
