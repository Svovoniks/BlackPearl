using System.Diagnostics;
using System.Numerics;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// The parts of the microphone processing that keep a voice as it is (see VoiceProcessor): whether there is any echo
// to cancel, how much of what the denoiser removed was noise, and peaks that would clip.

// Whether what the speakers play reaches the microphone. An echo canceller that is given a movie to cancel while the
// listener wears headphones finds nothing to cancel and suppresses the voice instead, so it is only used once the
// speakers' sound is found in the microphone.
//
// Both signals are band-limited to the range every microphone and speaker carries, reduced to 4 kHz, and compared
// twice a second: the last second of microphone against the speakers at every delay from 100 ms early to 500 ms late.
// With an echo the two correlate strongly at one delay, most clearly while nobody speaks; without one they do not.
// An echo shows within a second or so of the speakers playing, while its absence can only be concluded slowly, so
// none is assumed until one is found: speakers leak a moment of the movie once, instead of headphones losing the
// first seconds of the voice.
internal sealed class EchoPresence
{
    public const int Rate = 4000, Step = VoicePacket.SampleRate / Rate;
    public const int Window = Rate, Hop = Rate / 2, Early = Rate / 10, Late = Rate / 2;
    // One window this similar is an echo; so are two less similar ones at the same delay, a second apart so that they
    // share no audio. Speech against an unrelated movie reaches 0.25 in about one window in thirty and stayed below
    // 0.6, at delays that differ from one window to the next; an echo in a pause reaches 0.7 to 0.95.
    public const float Certain = 0.7f, Likely = 0.25f;
    // A second of microphone with only a moment of sound in it, such as the start of a word after silence, resembles
    // anything by chance. To count, at least this many of its twenty 50 ms pieces have to carry sound, each within
    // 10 dB of the second's average.
    private const int Pieces = 20, Sounding = 8;
    // Once found, an echo is taken to be gone after this many windows in which the speakers played without one: ten
    // seconds, so a talkative speaker or a quiet scene does not hide it.
    public const int ForgetWindows = 20;
    // Quieter than this, the speakers are not playing: a recording's own hiss and hum between its sounds is too faint
    // to echo, and hum resembles any other hum.
    private const double PlayingPower = 1e-6, HearingPower = 1e-10;
    // What was found last time carries over to the next time voice opens, as the devices usually stay the same. The
    // app keeps it between runs as well (see Voice.cs), so speakers do not leak the movie at the start of every one.
    private static bool? remembered;
    internal static bool? Remembered { get => remembered; set => remembered = value; }
    private readonly Band microphoneBand = new(), speakerBand = new();
    private readonly float[] microphone = new float[Window + Early], speakers = new float[Window + Early + Late];
    private readonly float[] heard = new float[Window], played = new float[Window + Early + Late];
    private readonly object gate = new();
    private int sinceCompared, quiet, lastDelay = int.MinValue, earlierDelay = int.MinValue;
    private long lastPlayed;
    public bool Present { get; private set; } = remembered ?? false;
    // The latest comparison, for diagnostics and checks: how similar, and at which delay in milliseconds.
    internal (float Similarity, int DelayMs) Latest { get; private set; }
    internal static void Forget() => remembered = null;

    // What the speakers play, 48 kHz, from the loopback thread.
    public void Speakers(ReadOnlySpan<float> samples) => Speakers(samples, Stopwatch.GetTimestamp());
    internal void Speakers(ReadOnlySpan<float> samples, long now)
    {
        lock (gate)
        {
            // Windows sends nothing while nothing plays; what was played before a gap is not what the microphone hears now.
            if (lastPlayed != 0 && Stopwatch.GetElapsedTime(lastPlayed, now).TotalMilliseconds > 150) Array.Clear(speakers);
            lastPlayed = now;
            speakerBand.Write(samples, speakers);
        }
    }
    // The microphone before echo cancellation, 48 kHz, from the capture thread.
    public void Microphone(ReadOnlySpan<float> samples)
    {
        sinceCompared += microphoneBand.Write(samples, microphone);
        if (sinceCompared < Hop) return;
        sinceCompared = 0;
        // The microphone window ends a little before now, so a microphone that is ahead of the loopback is covered too.
        microphone.AsSpan(0, Window).CopyTo(heard);
        lock (gate) speakers.CopyTo(played, 0);
        Compare();
    }
    private void Compare()
    {
        double heardPower = Dot(heard, heard, Window), playedPower = 0;
        for (var i = 0; i < played.Length; i++) playedPower += played[i] * played[i];
        if (playedPower / played.Length < PlayingPower || heardPower / Window < HearingPower) { Skip(); return; }
        if (Thin(heard, heardPower)) { Skip(); return; }
        // Speakers[n + offset] against microphone[n]: offset 0 is the latest delay, the last offset the earliest.
        double window = Dot(played, played, Window), best = 0; var bestOffset = 0;
        for (var offset = 0; offset <= Early + Late; offset++)
        {
            if (offset > 0) window += played[offset + Window - 1] * (double)played[offset + Window - 1] - played[offset - 1] * (double)played[offset - 1];
            if (window <= heardPower * 1e-9) continue;
            var similarity = Math.Abs(Dot(heard, played.AsSpan(offset), Window)) / Math.Sqrt(heardPower * window);
            if (similarity > best) { best = similarity; bestOffset = offset; }
        }
        // The second the microphone resembles has to be one in which the speakers were playing.
        var matched = played.AsSpan(bestOffset, Window);
        if (Dot(matched, matched, Window) / Window < PlayingPower) { Skip(); return; }
        var delay = Late - bestOffset;
        Latest = ((float)best, delay * 1000 / Rate);
        // Windows overlap by half, so the one to agree with is the one before the last.
        var echo = best >= Certain || best >= Likely && Math.Abs(delay - earlierDelay) <= Rate / 1000;
        earlierDelay = lastDelay; lastDelay = best >= Likely ? delay : int.MinValue;
        if (echo) { quiet = 0; Set(true); }
        else if (Present && ++quiet >= ForgetWindows) Set(false);
    }
    private static bool Thin(ReadOnlySpan<float> window, double power)
    {
        var sounding = 0; var length = Window / Pieces;
        for (var piece = 0; piece < Pieces; piece++)
            if (Dot(window[(piece * length)..], window[(piece * length)..], length) >= power / Pieces / 10) sounding++;
        return sounding < Sounding;
    }
    // A window that says nothing either way.
    private void Skip() { Latest = (0, 0); earlierDelay = lastDelay; lastDelay = int.MinValue; }
    private void Set(bool present)
    {
        if (Present != present) PlaybackDiagnostics.Record("voice-echo", new { present, similarity = Math.Round(Latest.Similarity, 2), delayMs = Latest.DelayMs });
        Present = present; remembered = present;
    }
    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int length)
    {
        var sum = Vector<float>.Zero; var i = 0;
        for (; i <= length - Vector<float>.Count; i += Vector<float>.Count) sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        double total = Vector.Sum(sum);
        for (; i < length; i++) total += a[i] * b[i];
        return total;
    }

    // 150 Hz to 1.4 kHz, every twelfth sample, appended to a history that keeps its newest sample last.
    private sealed class Band
    {
        private readonly Biquad high = Biquad.HighPass(150), low = Biquad.LowPass(1400), lower = Biquad.LowPass(1400);
        private int phase;
        public int Write(ReadOnlySpan<float> samples, float[] history)
        {
            var count = (samples.Length + phase) / Step;
            if (count > 0) Array.Copy(history, count, history, 0, history.Length - count);
            var at = history.Length - count;
            foreach (var sample in samples)
            {
                var value = lower.Run(low.Run(high.Run(sample)));
                if (++phase < Step) continue;
                phase = 0;
                if (at < history.Length) history[at++] = value;
            }
            return count;
        }
    }
}

// A second-order Butterworth low-pass or high-pass at 48 kHz (transposed direct form II).
internal sealed class Biquad
{
    private double b0, b1, b2, a1, a2, z1, z2;
    public static Biquad LowPass(double hz) => Make(hz, false);
    public static Biquad HighPass(double hz) => Make(hz, true);
    private static Biquad Make(double hz, bool high)
    {
        var w = 2 * Math.PI * hz / VoicePacket.SampleRate;
        var cos = Math.Cos(w); var alpha = Math.Sin(w) / Math.Sqrt(2); var a0 = 1 + alpha;
        var mid = high ? 1 + cos : 1 - cos;
        return new() { b0 = mid / 2 / a0, b1 = (high ? -mid : mid) / a0, b2 = mid / 2 / a0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0 };
    }
    public float Run(float x)
    {
        var y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return (float)y;
    }
}

// Keeps a denoiser from removing the voice. A denoiser cannot tell a room's reverberation, a distant voice or a
// breathy consonant from noise, and removes part of them. What is noise is known better between words: whatever the
// microphone carries then. So while someone speaks, the denoiser may take away no more from each frequency band than
// the noise measured in the pauses accounts for, with a margin; where it took more, the microphone's own signal is
// mixed back until that holds. Pauses keep the denoiser's full suppression, so nothing changes there.
//
// In a quiet room that leaves the voice exactly as the microphone heard it; the more noise there is, the more the
// denoiser is allowed to do. It works on 20 ms windows that overlap by half, and delays the audio by 10 ms.
internal sealed class VoiceGuard
{
    public const int Block = VoiceProcessor.BlockSamples, Delay = Block;
    private const int Size = 1024, Bins = Size / 2 + 1, Bands = 24;
    // The denoiser may remove this many times the noise measured in the pauses. Noise is not steady, and the measure
    // already follows its louder moments, so this mostly covers noise that grew since the last pause.
    public const float Margin = 8;
    // Speech is present from this probability, fully at the upper one, for 150 ms after it was last heard. The audio
    // here is 40 ms behind the speech detector, so a word's first sounds are covered as well.
    public const float Maybe = 0.15f, Surely = 0.4f;
    public const int HoldBlocks = 15, AheadBlocks = 4;
    private static readonly float[] Shape = MakeWindow();
    private static readonly int[] Edges = MakeEdges();
    private static readonly (int Band, float Next)[] Spread = MakeSpread();
    private readonly Fourier fourier = new(Size);
    private readonly float[] noisyLast = new float[Block], cleanLast = new float[Block], overlap = new float[Block];
    private readonly float[] noisyReal = new float[Size], noisyImaginary = new float[Size], cleanReal = new float[Size], cleanImaginary = new float[Size];
    private readonly double[] noise = new double[Bands];
    private readonly float[] weight = new float[Bands], recent = new float[HoldBlocks + AheadBlocks + 1];
    private int recentAt;
    private double gain;
    // Noise rises within 50 ms and falls over 3 s, so it stays near the louder moments of an unsteady noise.
    private static readonly double Rise = 1 - Math.Exp(-10 / 50.0), Fall = 1 - Math.Exp(-10 / 3000.0);
    // What is given back comes in over about 70 ms and goes over about 40 ms. A click lasts a block or two, so most of
    // it stays removed, while what a denoiser takes from a voice it takes for longer.
    private const float Attack = 0.15f;
    private static readonly float Release = (float)Math.Exp(-10 / 40.0);
    // How much of the microphone was mixed back into the latest block, 0 to 1, for diagnostics and checks.
    internal float Restored { get; private set; }

    public VoiceGuard() { Array.Fill(noise, 1e-12); }

    // One block of the microphone as the denoiser received it, delayed to line up with the denoiser's output, and that
    // output, which is replaced by the guarded audio one block later. probability: the speech detector's latest.
    // gainDb: the gain the microphone had before it reached the denoiser; the noise measured so far follows it, so
    // raising a quiet microphone does not make its room look quieter than it is.
    public void Process(ReadOnlySpan<float> noisy, float[] clean, float probability, double gainDb = 0)
    {
        if (gainDb != gain)
        {
            var change = Math.Pow(10, (gainDb - gain) / 10);
            for (var band = 0; band < Bands; band++) noise[band] *= change;
            gain = gainDb;
        }
        recent[recentAt] = probability; recentAt = (recentAt + 1) % recent.Length;
        var heard = 0f;
        foreach (var value in recent) heard = Math.Max(heard, value);
        var presence = Math.Clamp((heard - Maybe) / (Surely - Maybe), 0, 1);

        Load(noisyLast, noisy, noisyReal, noisyImaginary);
        Load(cleanLast, clean, cleanReal, cleanImaginary);
        fourier.Forward(noisyReal, noisyImaginary); fourier.Forward(cleanReal, cleanImaginary);
        var restored = 0f;
        for (var band = 0; band < Bands; band++)
        {
            double before = 0, after = 0;
            for (var bin = Edges[band]; bin < Edges[band + 1]; bin++)
            {
                before += noisyReal[bin] * (double)noisyReal[bin] + noisyImaginary[bin] * (double)noisyImaginary[bin];
                after += cleanReal[bin] * (double)cleanReal[bin] + cleanImaginary[bin] * (double)cleanImaginary[bin];
            }
            if (presence <= 0) noise[band] += (before > noise[band] ? Rise : Fall) * (before - noise[band]);
            var allowed = Margin * noise[band];
            var mix = 0f;
            if (presence > 0 && before - after > allowed)
            {
                // Mixing the microphone back raises the band's amplitude about linearly from the denoiser's to its own.
                double from = Math.Sqrt(after), to = Math.Sqrt(before), wanted = Math.Sqrt(Math.Max(before - allowed, 0));
                mix = (float)Math.Clamp((wanted - from) / Math.Max(to - from, 1e-12), 0, 1) * presence;
            }
            weight[band] = Math.Max(Math.Min(mix, weight[band] + Attack), weight[band] * Release);
            restored = Math.Max(restored, weight[band]);
        }
        Restored = restored;
        for (var bin = 0; bin < Bins; bin++)
        {
            var (band, next) = Spread[bin];
            var mix = weight[band] + (weight[Math.Min(band + 1, Bands - 1)] - weight[band]) * next;
            cleanReal[bin] += mix * (noisyReal[bin] - cleanReal[bin]);
            cleanImaginary[bin] += mix * (noisyImaginary[bin] - cleanImaginary[bin]);
        }
        for (var bin = 1; bin < Bins - 1; bin++) { cleanReal[Size - bin] = cleanReal[bin]; cleanImaginary[Size - bin] = -cleanImaginary[bin]; }
        fourier.Inverse(cleanReal, cleanImaginary);
        for (var i = 0; i < Block; i++)
        {
            clean[i] = overlap[i] + cleanReal[i] * Shape[i];
            overlap[i] = cleanReal[Block + i] * Shape[Block + i];
        }
    }
    // The previous block and this one under the window, padded to the transform's size.
    private static void Load(float[] last, ReadOnlySpan<float> block, float[] real, float[] imaginary)
    {
        for (var i = 0; i < Block; i++) { real[i] = last[i] * Shape[i]; real[Block + i] = block[i] * Shape[Block + i]; }
        Array.Clear(real, 2 * Block, Size - 2 * Block); Array.Clear(imaginary);
        block.CopyTo(last);
    }
    // The Vorbis window: applied before and after the transform, overlapping halves add up to the signal itself.
    private static float[] MakeWindow()
    {
        var window = new float[2 * Block];
        for (var i = 0; i < window.Length; i++)
        {
            var sine = Math.Sin(0.5 * Math.PI * (i + 0.5) / Block);
            window[i] = (float)Math.Sin(0.5 * Math.PI * sine * sine);
        }
        return window;
    }
    // Bands about equally wide to the ear (the ERB scale) from 100 Hz to 20 kHz; the first and last reach the ends.
    private static int[] MakeEdges()
    {
        static double Scale(double hz) => 21.4 * Math.Log10(1 + 0.00437 * hz);
        var edges = new int[Bands + 1];
        for (var i = 0; i <= Bands; i++)
        {
            var hz = (Math.Pow(10, (Scale(100) + (Scale(20000) - Scale(100)) * i / Bands) / 21.4) - 1) / 0.00437;
            edges[i] = Math.Max((int)Math.Round(hz * Size / VoicePacket.SampleRate), i == 0 ? 0 : edges[i - 1] + 1);
        }
        edges[0] = 0; edges[Bands] = Bins;
        return edges;
    }
    // Each bin's place between the centres of two neighbouring bands, so the mix changes smoothly across frequency.
    private static (int, float)[] MakeSpread()
    {
        var spread = new (int, float)[Bins];
        var centres = Enumerable.Range(0, Bands).Select(band => (Edges[band] + Edges[band + 1] - 1) / 2f).ToArray();
        for (var bin = 0; bin < Bins; bin++)
        {
            var band = 0;
            while (band < Bands - 2 && bin >= centres[band + 1]) band++;
            spread[bin] = (band, Math.Clamp((bin - centres[band]) / (centres[band + 1] - centres[band]), 0, 1));
        }
        return spread;
    }

    // An in-place radix-2 transform of complex data held as two arrays.
    private sealed class Fourier
    {
        private readonly int size;
        private readonly int[] reversed;
        private readonly float[] cosine, sine;
        public Fourier(int size)
        {
            this.size = size;
            reversed = new int[size]; cosine = new float[size / 2]; sine = new float[size / 2];
            var bits = BitOperations.Log2((uint)size);
            for (var i = 0; i < size; i++)
            {
                var value = 0;
                for (var bit = 0; bit < bits; bit++) if ((i & 1 << bit) != 0) value |= 1 << bits - 1 - bit;
                reversed[i] = value;
            }
            for (var i = 0; i < size / 2; i++) { cosine[i] = (float)Math.Cos(2 * Math.PI * i / size); sine[i] = (float)Math.Sin(2 * Math.PI * i / size); }
        }
        public void Forward(float[] real, float[] imaginary) => Run(real, imaginary, -1);
        public void Inverse(float[] real, float[] imaginary)
        {
            Run(real, imaginary, 1);
            for (var i = 0; i < size; i++) { real[i] /= size; imaginary[i] /= size; }
        }
        private void Run(float[] real, float[] imaginary, int sign)
        {
            for (var i = 0; i < size; i++)
            {
                var j = reversed[i];
                if (j <= i) continue;
                (real[i], real[j]) = (real[j], real[i]); (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
            for (var half = 1; half < size; half *= 2)
            {
                var stride = size / (2 * half);
                for (var start = 0; start < size; start += 2 * half)
                    for (var k = 0; k < half; k++)
                    {
                        float c = cosine[k * stride], s = sign * sine[k * stride];
                        int a = start + k, b = a + half;
                        float re = real[b] * c - imaginary[b] * s, im = real[b] * s + imaginary[b] * c;
                        real[b] = real[a] - re; imaginary[b] = imaginary[a] - im;
                        real[a] += re; imaginary[a] += im;
                    }
            }
        }
    }
}

// A signal delayed by a fixed number of samples, a block at a time.
internal sealed class DelayLine(int length)
{
    private readonly float[] held = new float[length];
    private int at;
    public int Length => length;
    public void Run(ReadOnlySpan<float> input, float[] output)
    {
        if (length == 0) { input.CopyTo(output); return; }
        for (var i = 0; i < input.Length; i++)
        {
            var sample = input[i];
            output[i] = held[at]; held[at] = sample;
            at = (at + 1) % length;
        }
    }
}

// Keeps peaks below full scale without changing anything quieter. A block that would pass the ceiling is turned down
// over its first 2 ms and the gain returns over about 200 ms, so loud laughter is lowered smoothly instead of clipping.
// The ceiling sits just under full scale, so a microphone that is already loud passes untouched.
internal sealed class PeakLimiter
{
    public const float Ceiling = 0.97f;
    private const int Attack = VoicePacket.SampleRate / 500;
    private static readonly float Recover = 1 - (float)Math.Exp(-10 / 200.0);
    private float gain = 1;
    public float GainDb => 20 * MathF.Log10(gain);
    public void Apply(float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        var released = gain + (1 - gain) * Recover;
        var target = peak * released > Ceiling ? Ceiling / peak : released;
        if (gain == 1 && target == 1) return;
        // Down quickly, up across the whole block.
        var ramp = target < gain ? Attack : samples.Length;
        for (var i = 0; i < samples.Length; i++)
        {
            var now = i < ramp ? gain + (target - gain) * (i + 1) / ramp : target;
            samples[i] = Math.Clamp(samples[i] * now, -Ceiling, Ceiling);
        }
        gain = target;
    }
}
