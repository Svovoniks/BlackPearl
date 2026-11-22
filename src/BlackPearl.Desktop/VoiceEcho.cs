using BlackPearl.Core;

namespace BlackPearl.Desktop;

// An echo canceller that leaves the voice on top of the echo as it is. WebRTC's canceller removes the echo by
// suppressing the microphone wherever echo might be, and with a movie playing all the time that takes about half of
// the voice with it. This one works out how the speakers' sound arrives at the microphone and subtracts exactly
// that, which costs the voice nothing. What the subtraction leaves is faint, and is left to the noise removal and
// the speech gate that follow: turning it down here as well cost the voice more than it took off the echo, and where
// the filter could not fit the room it turned everything down.
//
// The path from speakers to microphone is a filter 240 ms long, kept as 24 pieces in the frequency domain. Beside
// each value of the filter it keeps how unsure that value still is. A value moves towards what would have removed
// more of the leftover in proportion to that doubt, set against everything else the microphone hears in that
// frequency: a voice is "everything else", so while someone talks the filter all but stands still there, and it
// needs no second copy to be safe from talking. The doubt falls as the filter learns and is topped up a little all
// the time, because rooms change. (A partitioned-block frequency-domain Kalman filter.)
//
// Speakers and microphone that are separate devices each count time by their own clock, a few parts in a hundred
// thousand apart, so the echo slides against the speakers' sound by a sample every second or so: enough to leave a
// filter behind within one sentence. Every half second the filter is compared with what it was: a slide shows as
// the whole filter having moved a little in time, and the caller is told how much faster or slower to read the
// speakers' sound (Skew) so that it stops.
//
// It still cannot do everything WebRTC's can, so it reports how well it is working, and the caller uses it only
// while that is good enough.
internal sealed class EchoSubtractor
{
    public const int Block = VoiceProcessor.BlockSamples;
    private const int Size = 1024, Bins = Size / 2 + 1, Partitions = 24, Tidy = 4;
    // Share: the part of a 1024-sample spectrum that one 480-sample block of leftover carries. Lasting: how much
    // of what is known about the room still holds 10 ms later (squared). Settled: how slowly the picture of what
    // the microphone hears follows it.
    private const float Share = (float)Block / Size, Lasting = 0.9995f * 0.9995f, Settled = 0.9f;
    // The doubt to start from: how strong the path from speakers to microphone may be, as a ratio of powers. Too
    // high, and a voice louder than the echo is taken for echo the filter has yet to learn: with a faint echo the
    // first seconds then came out worse than the microphone. The caller knows roughly how strong the echo is.
    private readonly float unknown;
    private readonly VoiceGuard.Fourier fourier = new(Size);
    private readonly float[] played = new float[Size];
    private readonly float[][] playedReal = Rows(), playedImaginary = Rows(), filterReal = Rows(), filterImaginary = Rows(), doubt = Rows();
    private readonly float[] real = new float[Size], imaginary = new float[Size], pieceReal = new float[Size], pieceImaginary = new float[Size];
    private readonly float[] leftover = new float[Bins], hearing = new float[Bins], pace = new float[Bins];
    private readonly float[] error = new float[Block], echo = new float[Block];
    private int head, tidy, louder;
    private double longOutput = 1e-9, longMicrophone = 1e-9;
    // How much quieter the output is than the microphone, in dB, over the blocks in which the predicted echo is at
    // least half of what the microphone hears: a high point of that ratio. A voice is not counted against it, and a
    // filter that has stopped fitting the room shows at once.
    public double WorkingDb { get; private set; }
    // Times it started again because it had made things louder.
    internal int Restarts { get; private set; }
    // A filter that has settled moves slowly, which is what keeps a voice from disturbing it, and what makes it slow
    // to follow when the echo itself has changed. After Failing measurements in a row in which less than PoorDb of
    // the echo was removed, it is that: the doubt goes back to where it started, and the filter learns as quickly
    // as at first. The caller asks for the same, and for the filter to be forgotten, when the speakers' sound has
    // been lined up with the microphone afresh, which moves the echo by a block or more.
    private const double PoorDb = 3;
    private const int Failing = 25;
    private int failing;
    public void Relearn(bool forget)
    {
        foreach (var row in doubt) Array.Fill(row, unknown);
        if (forget) foreach (var rows in new[] { filterReal, filterImaginary }) foreach (var row in rows) Array.Clear(row);
        WorkingDb = 0; measured = 0; failing = 0; blocks = 0; compared = false;
    }
    // Blocks so far in which the speakers' sound was most of what the microphone heard and at least 6 dB of it was
    // removed: each one shows an echo that is there and being subtracted.
    public int Proofs { get; private set; }
    private const double ProofDb = 6;
    private const int Finding = 30, Few = 5;
    private readonly double[] first = new double[Finding], sorted = new double[Finding];
    private int measured;
    // How much faster than the microphone's clock the speakers' sound has to be read for the echo to stay put: 0.00002
    // is 20 parts per million. Compared every Compare blocks, once the filter has had Settling blocks to form, over
    // the frequencies where a movie has most of its sound (bins First to Last, about 100 Hz to 4 kHz). A filter that
    // moved more than NotDrift samples in that time was moved by something else, such as the microphone. One
    // comparison is off by a good part of a sample, as a voice jostles the filter, so each is followed only in
    // part: by Quick of what it found, no more than Step at a time, until Locking comparisons have been made while
    // the echo was being removed; then by Slow and no more than Creep at a time, which holds it steady to a part per
    // million or so. Clocks do not change their rate in seconds: a filter that moves quickly after that is learning
    // a room that changed, and following it as if it were the clocks would lose the echo for a while. Within Most
    // in all.
    public double Skew { get; private set; }
    private const int Compare = 50, Settling = 200, First = 2, Last = 85, Locking = 20;
    private const double Quick = 0.3, Slow = 0.05, Step = 2e-5, Creep = 1e-6, Most = 3e-4, NotDrift = 5;
    private readonly float[][] earlierReal = Rows(), earlierImaginary = Rows();
    private int blocks, comparisons, proven;
    private bool compared;

    // path: how much of the speakers' sound reaches the microphone, as a ratio of powers, when that is known.
    public EchoSubtractor(double skew = 0, double path = 1)
    {
        Skew = Math.Clamp(skew, -Most, Most);
        unknown = (float)Math.Clamp(path > 0 ? path : 1, 1e-4, 100);
        foreach (var row in doubt) Array.Fill(row, unknown);
    }
    private static float[][] Rows(float value = 0)
    {
        var rows = Enumerable.Range(0, Partitions).Select(_ => new float[Bins]).ToArray();
        if (value != 0) foreach (var row in rows) Array.Fill(row, value);
        return rows;
    }

    // One block of the microphone and of what the speakers played, the two as they arrived together. The output is
    // the microphone without the echo.
    public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> speakers, float[] output)
    {
        // The speakers' last 1024 samples, and their spectrum beside those of the 23 blocks before.
        Array.Copy(played, Block, played, 0, Size - Block); speakers.CopyTo(played.AsSpan(Size - Block));
        head = (head + Partitions - 1) % Partitions;
        played.CopyTo(real, 0); Array.Clear(imaginary);
        fourier.Forward(real, imaginary);
        Array.Copy(real, playedReal[head], Bins); Array.Copy(imaginary, playedImaginary[head], Bins);

        Predict(echo);
        double left = 0, heard = 0, playing = 0, predicted = 0;
        for (var i = 0; i < Block; i++)
        {
            error[i] = microphone[i] - echo[i];
            left += error[i] * (double)error[i]; heard += microphone[i] * (double)microphone[i];
            playing += speakers[i] * (double)speakers[i]; predicted += echo[i] * (double)echo[i];
        }

        // How much echo the doubt says is still in each frequency of what was left.
        Array.Clear(leftover);
        for (var k = 0; k < Partitions; k++)
        {
            float[] xr = playedReal[(head + k) % Partitions], xi = playedImaginary[(head + k) % Partitions], unsure = doubt[k];
            for (var bin = 0; bin < Bins; bin++) leftover[bin] += Share * (xr[bin] * xr[bin] + xi[bin] * xi[bin]) * unsure[bin];
        }
        Array.Clear(real); Array.Clear(imaginary);
        error.CopyTo(real, Size - Block);
        fourier.Forward(real, imaginary);
        // Against it stands everything the microphone hears there, now or lately, whichever is more.
        for (var bin = 0; bin < Bins; bin++)
        {
            var now = real[bin] * real[bin] + imaginary[bin] * imaginary[bin];
            hearing[bin] = Settled * hearing[bin] + (1 - Settled) * now;
            pace[bin] = Share / (leftover[bin] + Math.Max(hearing[bin], now) + 1e-10f);
        }
        for (var k = 0; k < Partitions; k++)
        {
            float[] xr = playedReal[(head + k) % Partitions], xi = playedImaginary[(head + k) % Partitions], wr = filterReal[k], wi = filterImaginary[k], unsure = doubt[k];
            for (var bin = 0; bin < Bins; bin++)
            {
                var step = unsure[bin] * pace[bin];
                wr[bin] += step * (xr[bin] * real[bin] + xi[bin] * imaginary[bin]);
                wi[bin] += step * (xr[bin] * imaginary[bin] - xi[bin] * real[bin]);
                unsure[bin] = Lasting * (1 - Share * step * (xr[bin] * xr[bin] + xi[bin] * xi[bin])) * unsure[bin] + (1 - Lasting) * (wr[bin] * wr[bin] + wi[bin] * wi[bin]);
            }
        }
        // Each piece covers 480 samples of the path; a few a block are cut back to that length.
        for (var n = 0; n < Tidy; n++, tidy = (tidy + 1) % Partitions)
        {
            Unfold(filterReal[tidy], filterImaginary[tidy], pieceReal, pieceImaginary);
            fourier.Inverse(pieceReal, pieceImaginary);
            Array.Clear(pieceReal, Block, Size - Block); Array.Clear(pieceImaginary);
            fourier.Forward(pieceReal, pieceImaginary);
            Array.Copy(pieceReal, filterReal[tidy], Bins); Array.Copy(pieceImaginary, filterImaginary[tidy], Bins);
        }

        // A filter that has made things louder than the microphone for a second has gone wrong: start again.
        longOutput = 0.98 * longOutput + 0.02 * left; longMicrophone = 0.98 * longMicrophone + 0.02 * heard;
        louder = longOutput > 2 * longMicrophone ? louder + 1 : 0;
        if (louder >= 100)
        {
            foreach (var rows in new[] { filterReal, filterImaginary }) foreach (var row in rows) Array.Clear(row);
            foreach (var row in doubt) Array.Fill(row, unknown);
            louder = 0; Restarts++; WorkingDb = 0; measured = 0; blocks = 0; compared = false;
            microphone.CopyTo(error); left = heard;
        }

        if (++blocks % Compare == 0) FollowClock();
        error.CopyTo(output, 0);
        if (playing > Block * 1e-7 && predicted > 0.5 * heard)
        {
            var now = 10 * Math.Log10((heard + 1e-9) / (left + 1e-9));
            // From the first few measurements the high point is worked out exactly, so a filter that has found the
            // room is used within a second; after that it is followed a little at a time.
            if (measured < Finding)
            {
                first[measured++] = now;
                if (measured >= Few)
                {
                    Array.Copy(first, sorted, measured); Array.Sort(sorted, 0, measured);
                    WorkingDb = sorted[(int)(0.8 * (measured - 1))];
                }
            }
            else WorkingDb += 0.3 * (0.8 - (now < WorkingDb ? 1 : 0));
            if (now >= ProofDb) Proofs++;
            failing = now < PoorDb ? failing + 1 : 0;
            if (failing >= Failing) Relearn(forget: false);
        }
    }
    // A filter that is the earlier one moved by some samples in time differs from it by a turn of phase that grows
    // evenly with frequency. The turn from each frequency to the next, added up over all of them with the strong
    // ones counting most, gives the move without having to unwind any phase.
    private void FollowClock()
    {
        if (compared && blocks > Settling)
        {
            Span<double> crossReal = stackalloc double[Last - First + 1], crossImaginary = stackalloc double[Last - First + 1];
            crossReal.Clear(); crossImaginary.Clear();
            for (var k = 0; k < Partitions; k++)
            {
                float[] pr = earlierReal[k], pi = earlierImaginary[k], wr = filterReal[k], wi = filterImaginary[k];
                for (var bin = First; bin <= Last; bin++)
                {
                    crossReal[bin - First] += pr[bin] * (double)wr[bin] + pi[bin] * (double)wi[bin];
                    crossImaginary[bin - First] += pr[bin] * (double)wi[bin] - pi[bin] * (double)wr[bin];
                }
            }
            double turnReal = 0, turnImaginary = 0;
            for (var i = 0; i + 1 < crossReal.Length; i++)
            {
                turnReal += crossReal[i + 1] * crossReal[i] + crossImaginary[i + 1] * crossImaginary[i];
                turnImaginary += crossImaginary[i + 1] * crossReal[i] - crossReal[i + 1] * crossImaginary[i];
            }
            var later = -Size / (2 * Math.PI) * Math.Atan2(turnImaginary, turnReal);
            if (Math.Abs(later) < NotDrift)
            {
                var locked = comparisons >= Locking; var most = locked ? Creep : Step;
                Skew = Math.Clamp(Skew - Math.Clamp((locked ? Slow : Quick) * later / (Compare * Block), -most, most), -Most, Most);
            }
            if (Proofs != proven) { proven = Proofs; comparisons++; }
        }
        for (var k = 0; k < Partitions; k++) { Array.Copy(filterReal[k], earlierReal[k], Bins); Array.Copy(filterImaginary[k], earlierImaginary[k], Bins); }
        compared = true;
    }
    // What the speakers' sound becomes on the way to the microphone: this block's worth.
    private void Predict(float[] into)
    {
        Array.Clear(real, 0, Bins); Array.Clear(imaginary, 0, Bins);
        for (var k = 0; k < Partitions; k++)
        {
            float[] xr = playedReal[(head + k) % Partitions], xi = playedImaginary[(head + k) % Partitions], wr = filterReal[k], wi = filterImaginary[k];
            for (var bin = 0; bin < Bins; bin++)
            {
                real[bin] += wr[bin] * xr[bin] - wi[bin] * xi[bin];
                imaginary[bin] += wr[bin] * xi[bin] + wi[bin] * xr[bin];
            }
        }
        for (var bin = 1; bin < Bins - 1; bin++) { real[Size - bin] = real[bin]; imaginary[Size - bin] = -imaginary[bin]; }
        fourier.Inverse(real, imaginary);
        Array.Copy(real, Size - Block, into, 0, Block);
    }
    private static void Unfold(float[] halfReal, float[] halfImaginary, float[] fullReal, float[] fullImaginary)
    {
        Array.Copy(halfReal, fullReal, Bins); Array.Copy(halfImaginary, fullImaginary, Bins);
        for (var bin = 1; bin < Bins - 1; bin++) { fullReal[Size - bin] = halfReal[bin]; fullImaginary[Size - bin] = -halfImaginary[bin]; }
    }
}

// What the speakers played, handed from the loopback thread to the microphone's a block at a time. The two arrive in
// bursts of their own, so a little is kept in hand; when the speakers fall silent Windows sends nothing, and the
// microphone gets silence until there is enough in hand again. Both arrive in whole blocks, so two blocks in hand
// would leave exactly one block at the worst moment and not a sample to spare: the half block more is the room in
// which the place read from can move as the clocks are followed.
internal sealed class SpeakerQueue
{
    private const int Block = VoiceProcessor.BlockSamples, InHand = 5 * Block / 2, TooMuch = 12 * Block, Often = 100;
    // A value between samples is worked out from the Taps samples around it, with weights kept for Phases places
    // between two samples (and the place after the last, to go between those too). Four samples and a simple curve
    // through them were not enough: at 60 parts per million the place between samples sweeps round three times a
    // second, and a curve's small error in the high notes, sweeping with it, was echo the filter could not remove.
    private const int Taps = 16, Phases = 128, Ahead = Taps / 2;
    private static readonly float[][] Between = MakeBetween();
    private static float[][] MakeBetween()
    {
        var rows = new float[Phases + 1][];
        for (var phase = 0; phase <= Phases; phase++)
        {
            var row = rows[phase] = new float[Taps]; double sum = 0;
            for (var tap = 0; tap < Taps; tap++)
            {
                // How far this sample is from the place wanted, in samples; the weights fade out over eight either way.
                var away = tap - (Taps / 2 - 1) - (double)phase / Phases;
                var across = 2 * Math.PI * (away + Taps / 2) / Taps;
                var fade = 0.35875 - 0.48829 * Math.Cos(across) + 0.14128 * Math.Cos(2 * across) - 0.01168 * Math.Cos(3 * across);
                var weight = (Math.Abs(away) < 1e-9 ? 1 : Math.Sin(Math.PI * away) / (Math.PI * away)) * fade;
                row[tap] = (float)weight; sum += weight;
            }
            for (var tap = 0; tap < Taps; tap++) row[tap] = (float)(row[tap] / sum);
        }
        return rows;
    }
    private readonly float[] samples = new float[VoicePacket.SampleRate * 2];
    private readonly object gate = new();
    private long written;
    private double read;
    private bool flowing;
    private int shortfall;
    // For the probe: times the speakers' sound was lined up with the microphone afresh, times there was none to give,
    // and the least and most that was waiting.
    internal int Anchored, Starved;
    internal double Least = double.MaxValue, Most = double.MinValue;
    public void Write(ReadOnlySpan<float> block)
    {
        lock (gate)
        {
            foreach (var sample in block) { samples[written % samples.Length] = sample; written++; }
        }
    }
    // The next block. When all is well it is taken from `late` samples and two and a half blocks behind the newest
    // that has arrived; how far behind then varies as the two arrive, and nothing is done about that, because the
    // place read from is what lines the speakers' sound up with the microphone. False, and silence, when what is
    // wanted has not arrived yet. Windows now and then delivers the speakers' sound a moment late: the place is
    // kept then, so what follows still lines up with the microphone as it did, and the subtractor has nothing to
    // learn again. Running short three times within a second or two means the place itself is wrong, and it is
    // found afresh; so it is when far too much has piled up. The more `late` the echo allows, the more room there
    // is for late deliveries.
    //
    // `rate` is how fast to read: 1.00002 takes 20 parts per million more of the speakers' sound than the
    // microphone gave in the same time, for speakers whose clock runs that much faster. The place read from then
    // falls between samples.
    public bool Read(float[] block, int late, double rate = 1)
    {
        lock (gate)
        {
            var have = written - read; var wanted = late + InHand + Ahead;
            if (flowing) { Least = Math.Min(Least, have); Most = Math.Max(Most, have); }
            if (!flowing) { if (have < InHand) { Array.Clear(block); return false; } flowing = true; read = written - wanted; Anchored++; }
            else if (have > wanted + TooMuch) { read = written - wanted; Anchored++; }
            else if (have < Block * rate + Ahead)
            {
                Starved++; shortfall += Often; Array.Clear(block);
                if (shortfall > 2 * Often) { flowing = false; shortfall = 0; read = written; } else read += Block * rate;
                return false;
            }
            shortfall = Math.Max(0, shortfall - 1);
            if (read < Ahead || written - read > samples.Length - Ahead) { flowing = false; Array.Clear(block); return false; }
            for (var i = 0; i < Block; i++)
            {
                var at = read + i * rate; var whole = (long)Math.Floor(at); var part = (at - whole) * Phases;
                if (part == 0) { block[i] = samples[whole % samples.Length]; continue; }
                var phase = (int)part; var further = (float)(part - phase);
                float[] near = Between[phase], next = Between[phase + 1];
                var first = whole - (Taps / 2 - 1); float sum = 0;
                for (var tap = 0; tap < Taps; tap++) sum += (near[tap] + further * (next[tap] - near[tap])) * samples[(first + tap) % samples.Length];
                block[i] = sum;
            }
            read += Block * rate;
            return true;
        }
    }
}
