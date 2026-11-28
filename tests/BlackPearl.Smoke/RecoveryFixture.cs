using BlackPearl.Core;

static class RecoveryFixture
{
    public static async Task Run(Action<bool, string> check)
    {
        var pause = new PlaybackPause();
        var stop = new PlaybackState(0, "movie", false, 0, 0, new string('a', 32));
        pause.Request(stop, 10);
        for (var revision = 10; revision < 18; revision++)
            pause.Observe(new(revision, "movie", true, 5000, revision * 1000));
        check(pause.Pending, "local stop stays paused through seven seconds of stale playing updates");
        pause.Observe(new(18, "movie", false, 0, 18000, new string('b', 32)));
        check(pause.Pending, "another command cannot acknowledge our pending stop");
        pause.Observe(stop with { Revision = 19 });
        check(!pause.Pending, "matching authoritative command acknowledges immediate local stop");
        pause.Request(stop, 20); pause.Observe(new(20, "movie", false, 0, 20000));
        check(pause.Pending, "old snapshot cannot acknowledge a new stop");
        pause.Observe(new(21, "movie", false, 0, 21000));
        check(!pause.Pending, "legacy room service acknowledges stop without command IDs");
        pause.Request(stop, 21); pause.Request(stop with { Playing = true }, 21);
        check(!pause.Pending, "explicit play supersedes pending local pause");
        pause.Request(stop, 21); pause.Observe(new(22, "next-movie", false, 0, 22000));
        check(!pause.Pending, "media change clears pending pause");
        pause.Request(stop, 22); pause.Clear();
        check(!pause.Pending, "leaving room clears pending local pause");
        var time = new ClockTime(); var clock = new ServerClock(time);
        clock.Sent(1); time.Advance(100); clock.Receive(1, 100050);
        time.Utc -= 10000; time.Advance(1000);
        check(clock.Now == 101100 && clock.OffsetMs == 10000, "server clock survives backward wall-clock jump");
        clock.Sent(2); time.Advance(100); check(clock.Receive(2, 101150) && clock.Now == 101200, "monotonic RTT accepts valid response after clock jump");
        time.Utc += 20000; time.Advance(1000);
        check(clock.Now == 102200 && clock.OffsetMs == -10000, "server clock survives forward wall-clock jump");
        check(!clock.Receive(2, 900000) && !clock.Receive(123, 900000), "duplicate and uncorrelated clock responses ignored");
        clock.Sent(3); time.Advance(10001); check(!clock.Receive(3, 900000), "expired clock probes ignored");
        clock.Sent(4); time.Advance(200); check(clock.Receive(4, 113301) && clock.Now == 113401, "a slower measurement that contradicts earlier ones replaces them, as when the streamer's clock moved");
        // A congested link: after one good measurement, every reply waits three seconds on its way back for a minute.
        var congested = new ClockTime(); var held = new ServerClock(congested);
        held.Sent(1); congested.Advance(20); held.Receive(1, congested.Utc - 10);
        long heldWorst = 0;
        for (var ping = 2; ping < 32; ping++)
        {
            congested.Advance(1980); held.Sent(ping);
            congested.Advance(20); var stamp = congested.Utc; congested.Advance(3000);
            held.Receive(ping, stamp);
            heldWorst = Math.Max(heldWorst, Math.Abs(held.Now - congested.Utc));
        }
        check(heldWorst <= 50, $"replies held up on a congested link keep the room clock on its last good measurement (worst {heldWorst} ms off)");
        held.Sent(40); congested.Advance(60); held.Receive(40, congested.Utc - 30 + 400);
        check(held.Now == congested.Utc + 400, "a quick measurement that contradicts an old one replaces it");
        var settling = new PlaybackSettling(); settling.Seek(10, 90000, 1000);
        check(settling.Waiting(10, 0, 1250) && settling.Waiting(10, 90000, 1500) && !settling.Waiting(10, 90000, 3000), "seek waits for decoding even when VLC time updates immediately");
        settling.Seek(10, 90000, 1000);
        check(!settling.Waiting(11, 0, 1250), "new playback command interrupts seek settling");
        settling.Seek(10, 90000, 1000);
        check(!settling.Waiting(10, 0, 3000), "stalled seek expires instead of blocking playback forever");
        settling.Seek(10, 90000, 1000); settling.Observe(90000, 1050);
        check(settling.Waiting(10, 90000, 1400), "seek setter echo alone cannot complete settling");
        settling.Observe(90300, 1350);
        check(!settling.Waiting(10, 90300, 1400), "advancing native timeline completes seek settling before timeout");
        settling.Seek(10, 90000, 1000); settling.Observe(1000, 1050); settling.Observe(1300, 1350);
        check(settling.Waiting(10, 1300, 1400), "old timeline events do not complete a new seek");
        var position = new PlaybackPosition(); position.Observe(90000, 1000);
        check(position.Estimate(90000, true, 1.03f, 1250) == 90257, "position estimate advances between native observations at playback rate");
        check(position.Estimate(90000, true, 1f, 3000) == 90500 && position.Estimate(90000, false, 1f, 1250) == 90000, "position estimate is bounded during stalls and never advances paused playback");
        check(position.Estimate(150000, true, 1f, 1250) == 150000, "new native position supersedes old interpolation anchor");
        // A converted stream as libVLC plays it: a report about every quarter second, repeating (with a few ms of
        // wobble) a time it takes once a second, so each new time is seen up to a report later.
        var coarse = new PlaybackPosition(); long coarseWorst = 0, taken = 0;
        for (long now = 0; now <= 30000; now += 10)
        {
            if (now % 1000 == 0) taken = now;
            if (now % 260 == 0) coarse.Observe(taken + now / 260 % 3 - 1, now);
            if (now % 250 == 120 && now >= 6000) coarseWorst = Math.Max(coarseWorst, Math.Abs(now - coarse.Estimate(taken, true, 1, now)));
        }
        check(coarseWorst <= 150, $"a time repeated between once-a-second changes is not read as playback falling behind (worst {coarseWorst} ms)");
        check(coarse.Estimate(taken, true, 1, 33000) <= taken + 1500, "a stalled once-a-second clock is not extrapolated indefinitely");
        // The room reads the position on every tick, paused or not.
        for (long now = 31000; now <= 36000; now += 250) coarse.Estimate(taken, false, 1, now);
        check(coarse.Estimate(taken, true, 1, 36100) == taken + 100, "playback paused for a while moves on from where it was, not from before the pause");
        var slipped = new PlaybackPosition();
        for (long now = 0; now <= 3000; now += 250) slipped.Observe(now, now);
        slipped.Observe(2950, 3250);
        check(slipped.Estimate(2950, true, 1, 3300) == 3000, "playback that slips back is followed at once, not held up by earlier reports");
        // A seek's own echo reads a little ahead of where the file then plays on.
        slipped.Observe(60070, 3500); slipped.Observe(60264, 3750);
        check(slipped.Estimate(60264, true, 1, 3850) == 60364, "a seek's echo that reads a little ahead does not hold the estimate ahead");
        for (long now = 4000; now <= 6000; now += 250) slipped.Observe(60264 + now - 3750, now);
        slipped.Observe(60264 + 6000 - 3750, 8000);
        for (long now = 8250; now <= 9000; now += 250) slipped.Observe(60264 + 6000 - 3750 + now - 8000, now);
        check(slipped.Estimate(60264 + 6000 - 3750 + 1000, true, 1, 12000) == 60264 + 6000 - 3750 + 1000 + 500, "a stall does not lengthen how far a file is extrapolated through the next one");
        check(SyncMath.TunedCorrection(0) == 1 && SyncMath.TunedCorrection(10000) == 1.05f && SyncMath.TunedCorrection(-10000) == .95f, "rate correction is neutral when aligned and bounded in both directions");
        check(SyncMath.EstablishedCorrection(99) == 1f && SyncMath.EstablishedCorrection(100) == 1.03f && SyncMath.EstablishedCorrection(-100) == .97f, "established rate policy preserves its deadband and correction limits");
        var rateControl = new PlaybackRateControl();
        var stable = true;
        // Aligned video with VLC's 500 ms native time updates: the old raw-time
        // correction would alternate 1 and 1.03 at each UI tick.
        for (long now = 0; now <= 10000; now += 250)
        {
            var native = now / 500 * 500;
            if (now % 500 == 0) position.Observe(native, now);
            stable &= rateControl.Update(now - position.Estimate(native, true, 1, now), now) == 1;
        }
        check(stable, "sparse native clock updates do not change aligned playback speed");
        rateControl.Reset();
        check(rateControl.Update(400, 0) == 1 && rateControl.Update(400, 500) == 1 && rateControl.Update(50, 750) == 1,
            "brief clock jitter cannot trigger rate correction");
        check(rateControl.Update(400, 1000) == 1 && rateControl.Update(400, 1750) == 1.03f && rateControl.Update(150, 2000) == 1.03f,
            "persistent lag accelerates playback and hysteresis avoids rate chatter");
        check(rateControl.Update(-250, 2250) == 1 && rateControl.Update(-250, 3000) == .97f && rateControl.Update(-50, 3250) == 1,
            "overshoot returns to normal speed before sustained reverse correction");
        rateControl.Update(400, 4000); rateControl.Reset();
        check(rateControl.Update(400, 4750) == 1, "new playback commands clear previous drift history");
        var intent = new PlaybackIntent();
        var waiting = new PlaybackState(10, "movie", false, 0, 0, RequestedPlaying: true);
        check(intent.Playing(waiting, false), "buffering preserves the requested Play intent");
        intent.Request(stop, 10);
        check(!intent.Playing(waiting, false), "Pause toggles immediately while waiting for readiness");
        intent.Observe(waiting with { Revision = 11, CommandId = "other" });
        check(!intent.Playing(waiting, false), "unrelated acknowledgements cannot overwrite pending Pause");
        intent.Observe(stop with { Revision = 12, RequestedPlaying = false });
        check(!intent.Playing(stop, true), "acknowledged Pause overrides native playback state");
        intent.Request(stop with { Playing = true }, 12); intent.Clear();
        check(!intent.Playing(null, false), "disconnect clears pending Play intent");
        intent.Request(stop with { Seek = true, PositionMs = 90000 }, 12);
        check(intent.Position(waiting, 1000, 1000) == 90000, "Play or another skip preserves a seek still awaiting acknowledgement");
        intent.Clear();
        settling.Seek(20, 90000, 1000, playing: false); settling.Observe(90000, 1100);
        check(!settling.Waiting(20, 90000, 1400), "paused seek settles without requiring the timeline to advance");
        var buffer = new PlaybackBuffering(); buffer.Cache(0, 1000); buffer.Cache(100, 1004);
        check(!buffer.Poll(true, 1600), "short seek buffering bursts do not pause the room");
        buffer.Recover(1800); buffer.Cache(0, 2000);
        check(!buffer.Poll(true, 2749) && buffer.Poll(true, 2750) && !buffer.Poll(true, 3000), "sustained starvation pauses the room once");
        check(!buffer.Cache(100, 3100) && buffer.IsActive, "one full-cache event cannot prematurely resume playback");
        buffer.Cache(60, 3300); buffer.Cache(100, 3500);
        check(!buffer.Recover(4249) && buffer.Recover(4250) && !buffer.Recover(4500), "recovery waits for sustained full cache and reports ready once");
        buffer.Cache(60, 9000);
        check(buffer.IsActive && buffer.Poll(true, 9750), "partial cache starvation is detected even without a zero-percent event");
        buffer.Cache(100, 9800);
        check(buffer.Recover(10550) && !buffer.IsActive, "stable full cache releases the synchronization hold");
        check(MediaStreaming.NetworkCacheForRtt(50) == 1500 && MediaStreaming.NetworkCacheForRtt(900) == 3600 &&
            MediaStreaming.NetworkCacheForRtt(long.MaxValue) == 5000, "guest buffer adapts to RTT within a fixed upper bound");

        var recovery = new PlaybackRecovery();
        check(!recovery.Begin(true) && !recovery.ShouldPause(true, false) && !recovery.Begin(true) && !recovery.ShouldPause(true, false),
            "an already playing guest continues through a connection interruption while buffered video lasts");
        check(recovery.ShouldPause(true, true) && recovery.ShouldPause(true, false),
            "exhausted native buffer pauses recovery and stays paused until reconnection");
        recovery.Clear(); recovery.Begin(true);
        check(recovery.ShouldPause(false, false), "a pending Pause immediately overrides buffered recovery");
        recovery.Clear();
        check(recovery.Begin(false) && recovery.ShouldPause(true, false),
            "connection recovery never starts a video that was already paused");
        recovery.Clear();
        check(!recovery.ShouldPause(true, true), "successful reconnection clears the recovery hold");
    }
    sealed class ClockTime : TimeProvider
    {
        public long Utc = 100000, Tick;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Tick;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Utc);
        public void Advance(long ms) { Utc += ms; Tick += ms; }
    }
}
