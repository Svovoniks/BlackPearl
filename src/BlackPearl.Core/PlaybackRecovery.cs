namespace BlackPearl.Core;

// Continue only playback that was already running, for as long as buffered video
// lasts. Recovery never starts a paused video, and a pause stays latched until the
// peer reconnects.
public sealed class PlaybackRecovery
{
    private bool started, held;

    public bool Begin(bool playing)
    {
        if (started) return held;
        started = true; held = !playing;
        return held;
    }

    public bool ShouldPause(bool playbackAllowed, bool starved)
    {
        if (!started) return false;
        held |= !playbackAllowed || starved;
        return held;
    }

    public void Clear() => started = held = false;
}
