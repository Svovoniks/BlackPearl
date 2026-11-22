using System.Diagnostics;

namespace BlackPearl.Core;

// Where the PC's player is in its video, readable from any thread at any moment, for a phone that plays the PC's sound
// in step with its picture (PHONE-REMOTE.md, "Sound on the phone"). libVLC says where it is only every quarter second,
// but each report is exact to about a millisecond (measured on a 1x clock), so between reports the position is the
// last report advanced by the time since.
public sealed class PlaybackClock
{
    // How far libVLC's reported position runs ahead of the sound coming out, and so of the picture, which it keeps in
    // step with the sound: about 60 to 70 ms once settled, measured on this PC through a virtual cable. Phones are sent
    // the position less this, the moment on screen.
    public const int PictureLagMs = 60;
    private readonly object gate = new();
    private long positionMs, at = Stopwatch.GetTimestamp();
    private bool running;
    private string? source;

    // The video playing, as the phone's sound stream names it; null when nothing plays.
    public string? Source { get { lock (gate) return source; } set { lock (gate) source = value; } }

    // A position libVLC reported (its TimeChanged event, from its own thread).
    public void Report(long timeMs)
    {
        lock (gate) { positionMs = timeMs; at = Stopwatch.GetTimestamp(); }
    }

    // Playback started or stopped advancing, where it is now.
    public void SetRunning(bool value)
    {
        lock (gate)
        {
            if (running == value) return;
            positionMs = Position(); at = Stopwatch.GetTimestamp(); running = value;
        }
    }

    public (string? Source, long PositionMs, bool Playing) Read()
    {
        lock (gate) return (source, Position(), running);
    }

    // When each phone last asked (Environment.TickCount64). A phone playing the sound asks every second, so one that
    // stopped asking is gone well before its link times out, which takes about half a minute.
    private readonly Dictionary<string, long> heard = [];
    public void Heard(string phoneId) { lock (gate) heard[phoneId] = Environment.TickCount64; }
    public long? LastHeard(string phoneId) { lock (gate) return heard.TryGetValue(phoneId, out var at) ? at : null; }

    // Called with the lock held.
    private long Position() => running ? positionMs + (long)Stopwatch.GetElapsedTime(at).TotalMilliseconds : positionMs;
}

// What a phone listening to the PC's sound is sent (PHONE-REMOTE.md): the sound of the video playing as pieces of
// MPEG-TS with AAC, read on the media channel as "stream/<StreamId>/<piece>". Source names the video, as the clock does.
public sealed record PhoneSoundStream(string StreamId, string Source, int Segments, int SegmentMs, long DurationMs);
