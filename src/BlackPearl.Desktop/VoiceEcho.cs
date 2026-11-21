using BlackPearl.Core;

namespace BlackPearl.Desktop;

// An echo canceller that leaves the voice on top of the echo as it is. WebRTC's canceller removes the echo by
// suppressing the microphone wherever echo might be, and with a movie playing all the time that takes about half of
// the voice with it. This one works out how the speakers' sound arrives at the microphone and subtracts exactly
// that, which costs the voice nothing; what the subtraction leaves is then turned down only in the bands where it
// outweighs everything else.
//
// The path from speakers to microphone is a filter 240 ms long, kept as 24 pieces in the frequency domain. Two
// copies: the background one adapts all the time, and the foreground one, which makes the output, takes it over
// only once it has done clearly better for a few blocks. A voice makes the background filter worse, never better,
// so talking cannot spoil the output.
//
// It cannot do everything WebRTC's can: speakers and microphone on different clocks, or a path that keeps moving,
// leave it behind. So it reports how well it is working, and the caller uses it only while that is good enough.
internal sealed class EchoSubtractor
{
    public const int Block = VoiceProcessor.BlockSamples, Delay = Block;
    private const int Size = 1024, Bins = Size / 2 + 1, Partitions = 24, Tidy = 4, Bands = 24;
    // How far the background filter moves on each block: quickly while it is still finding the room, then slowly, so
    // that a voice disturbs it little. And how many of the leftover's worth is taken off a band, down to what floor.
    private const float QuickStep = 0.3f, Step = 0.1f, Over = 3, Floor = 0.1f;
    private const double FoundDb = 12;
    private static readonly float[] Shape = MakeWindow();
    private static readonly int[] Edges = MakeEdges();
    private static readonly (int Band, float Next)[] Spread = MakeSpread();
    private readonly VoiceGuard.Fourier fourier = new(Size);
    private readonly float[] played = new float[Size];
    private readonly float[][] playedReal = Rows(), playedImaginary = Rows(), backReal = Rows(), backImaginary = Rows(), frontReal = Rows(), frontImaginary = Rows();
    private readonly float[] real = new float[Size], imaginary = new float[Size], echoReal = new float[Size], echoImaginary = new float[Size];
    private readonly float[] power = new float[Bins], backError = new float[Block], frontError = new float[Block], frontEcho = new float[Block];
    private readonly float[] errorLast = new float[Block], echoLast = new float[Block], overlap = new float[Block];
    private readonly double[] leak = new double[Bands];
    private readonly float[] gain = new float[Bands];
    private int head, tidy, better, louder;
    private double backLevel = 1e-9, frontLevel = 1e-9, longOutput = 1e-9, longMicrophone = 1e-9;
    // How much quieter the output is than the microphone, in dB, over the blocks in which the predicted echo is at
    // least half of what the microphone hears: a high point of that ratio. A voice is not counted against it, and a
    // filter that has stopped fitting the room shows at once.
    public double WorkingDb { get; private set; }
    // Times it started again because it had made things louder.
    internal int Restarts { get; private set; }

    public EchoSubtractor() { Array.Fill(gain, 1f); }
    private static float[][] Rows() => Enumerable.Range(0, Partitions).Select(_ => new float[Bins]).ToArray();

    // One block of the microphone and of what the speakers played, the two as they arrived together. The output is
    // the microphone without the echo, one block later.
    public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> speakers, float[] output)
    {
        // The speakers' last 1024 samples, and their spectrum beside those of the 23 blocks before.
        Array.Copy(played, Block, played, 0, Size - Block); speakers.CopyTo(played.AsSpan(Size - Block));
        head = (head + Partitions - 1) % Partitions;
        played.CopyTo(real, 0); Array.Clear(imaginary);
        fourier.Forward(real, imaginary);
        Array.Copy(real, playedReal[head], Bins); Array.Copy(imaginary, playedImaginary[head], Bins);

        Predict(backReal, backImaginary, backError);
        Predict(frontReal, frontImaginary, frontEcho);
        double back = 0, front = 0, heard = 0, playing = 0, predicted = 0;
        for (var i = 0; i < Block; i++)
        {
            backError[i] = microphone[i] - backError[i]; frontError[i] = microphone[i] - frontEcho[i];
            back += backError[i] * (double)backError[i]; front += frontError[i] * (double)frontError[i]; heard += microphone[i] * (double)microphone[i];
            playing += speakers[i] * (double)speakers[i]; predicted += frontEcho[i] * (double)frontEcho[i];
        }

        // The background filter moves towards whatever would have removed more of what it left.
        Array.Clear(power);
        for (var k = 0; k < Partitions; k++)
            for (var bin = 0; bin < Bins; bin++) power[bin] += playedReal[k][bin] * playedReal[k][bin] + playedImaginary[k][bin] * playedImaginary[k][bin];
        Array.Clear(real); Array.Clear(imaginary);
        backError.CopyTo(real, Size - Block);
        fourier.Forward(real, imaginary);
        var step = WorkingDb < FoundDb ? QuickStep : Step;
        for (var bin = 0; bin < Bins; bin++) { var scale = step / (power[bin] + 1e-3f * Size); real[bin] *= scale; imaginary[bin] *= scale; }
        for (var k = 0; k < Partitions; k++)
        {
            float[] xr = playedReal[(head + k) % Partitions], xi = playedImaginary[(head + k) % Partitions], wr = backReal[k], wi = backImaginary[k];
            for (var bin = 0; bin < Bins; bin++)
            {
                wr[bin] += xr[bin] * real[bin] + xi[bin] * imaginary[bin];
                wi[bin] += xr[bin] * imaginary[bin] - xi[bin] * real[bin];
            }
        }
        // Each piece covers 480 samples of the path; a few a block are cut back to that length.
        for (var n = 0; n < Tidy; n++, tidy = (tidy + 1) % Partitions)
        {
            Unfold(backReal[tidy], backImaginary[tidy], echoReal, echoImaginary);
            fourier.Inverse(echoReal, echoImaginary);
            Array.Clear(echoReal, Block, Size - Block); Array.Clear(echoImaginary);
            fourier.Forward(echoReal, echoImaginary);
            Array.Copy(echoReal, backReal[tidy], Bins); Array.Copy(echoImaginary, backImaginary[tidy], Bins);
        }

        backLevel = 0.7 * backLevel + 0.3 * back; frontLevel = 0.7 * frontLevel + 0.3 * front;
        better = backLevel < 0.8 * frontLevel ? better + 1 : 0;
        if (better >= 3) { Copy(backReal, frontReal); Copy(backImaginary, frontImaginary); frontLevel = backLevel; better = 0; }
        else if (backLevel > 2 * frontLevel) { Copy(frontReal, backReal); Copy(frontImaginary, backImaginary); backLevel = frontLevel; }
        // A filter that has made things louder than the microphone for a second has gone wrong: start again.
        longOutput = 0.98 * longOutput + 0.02 * front; longMicrophone = 0.98 * longMicrophone + 0.02 * heard;
        louder = longOutput > 2 * longMicrophone ? louder + 1 : 0;
        if (louder >= 100)
        {
            foreach (var rows in new[] { backReal, backImaginary, frontReal, frontImaginary }) foreach (var row in rows) Array.Clear(row);
            louder = 0; Restarts++;
            microphone.CopyTo(frontError); Array.Clear(frontEcho);
        }

        TurnDown(output);
        if (playing > Block * 1e-7 && predicted > 0.5 * heard)
        {
            double left = 0;
            foreach (var sample in output) left += sample * (double)sample;
            var now = 10 * Math.Log10((heard + 1e-9) / (left + 1e-9));
            WorkingDb += 0.3 * (0.8 - (now < WorkingDb ? 1 : 0));
        }
    }
    // What the speakers' sound becomes on the way to the microphone, by one of the two filters: this block's worth.
    private void Predict(float[][] filterReal, float[][] filterImaginary, float[] echo)
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
        Array.Copy(real, Size - Block, echo, 0, Block);
    }
    private static void Unfold(float[] halfReal, float[] halfImaginary, float[] fullReal, float[] fullImaginary)
    {
        Array.Copy(halfReal, fullReal, Bins); Array.Copy(halfImaginary, fullImaginary, Bins);
        for (var bin = 1; bin < Bins - 1; bin++) { fullReal[Size - bin] = halfReal[bin]; fullImaginary[Size - bin] = -halfImaginary[bin]; }
    }
    private static void Copy(float[][] from, float[][] to) { for (var k = 0; k < Partitions; k++) Array.Copy(from[k], to[k], Bins); }

    // What is left of the echo in a band is some share of what the filter predicted there. That share is a low point
    // of the ratio between what is left and what was predicted, which a voice can only raise. Where the leftover is
    // most of the band, the band is turned down; where a voice is, it is not.
    private void TurnDown(float[] output)
    {
        for (var i = 0; i < Block; i++)
        {
            real[i] = errorLast[i] * Shape[i]; real[Block + i] = frontError[i] * Shape[Block + i];
            echoReal[i] = echoLast[i] * Shape[i]; echoReal[Block + i] = frontEcho[i] * Shape[Block + i];
        }
        Array.Clear(real, 2 * Block, Size - 2 * Block); Array.Clear(imaginary);
        Array.Clear(echoReal, 2 * Block, Size - 2 * Block); Array.Clear(echoImaginary);
        frontError.CopyTo(errorLast, 0); frontEcho.CopyTo(echoLast, 0);
        fourier.Forward(real, imaginary); fourier.Forward(echoReal, echoImaginary);
        for (var band = 0; band < Bands; band++)
        {
            double left = 0, predicted = 0;
            for (var bin = Edges[band]; bin < Edges[band + 1]; bin++)
            {
                left += real[bin] * (double)real[bin] + imaginary[bin] * (double)imaginary[bin];
                predicted += echoReal[bin] * (double)echoReal[bin] + echoImaginary[bin] * (double)echoImaginary[bin];
            }
            if (predicted > 1e-7)
            {
                var ratio = Math.Log10(Math.Max(left, 1e-15) / predicted);
                leak[band] = Math.Clamp(leak[band] + 0.05 * (0.3 - (ratio < leak[band] ? 1 : 0)), -4, 0);
            }
            var wanted = (float)Math.Clamp(1 - Over * Math.Pow(10, leak[band]) * predicted / Math.Max(left, 1e-12), Floor, 1);
            gain[band] = wanted < gain[band] ? wanted : gain[band] + 0.5f * (wanted - gain[band]);
        }
        for (var bin = 0; bin < Bins; bin++)
        {
            var (band, next) = Spread[bin];
            var factor = gain[band] + (gain[Math.Min(band + 1, Bands - 1)] - gain[band]) * next;
            real[bin] *= factor; imaginary[bin] *= factor;
        }
        for (var bin = 1; bin < Bins - 1; bin++) { real[Size - bin] = real[bin]; imaginary[Size - bin] = -imaginary[bin]; }
        fourier.Inverse(real, imaginary);
        for (var i = 0; i < Block; i++)
        {
            output[i] = overlap[i] + real[i] * Shape[i];
            overlap[i] = real[Block + i] * Shape[Block + i];
        }
    }
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
    // Bands of equal width in octaves, from 100 Hz up; the first and last reach the ends.
    private static int[] MakeEdges()
    {
        var edges = new int[Bands + 1];
        for (var i = 0; i <= Bands; i++) edges[i] = Math.Max((int)Math.Round(2 * Math.Pow((Bins - 1) / 2.0, (double)i / Bands)), i == 0 ? 0 : edges[i - 1] + 1);
        edges[0] = 0; edges[Bands] = Bins;
        return edges;
    }
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
}

// What the speakers played, handed from the loopback thread to the microphone's a block at a time. The two arrive in
// bursts of their own, so a little is kept in hand; when the speakers fall silent Windows sends nothing, and the
// microphone gets silence until there is enough in hand again.
internal sealed class SpeakerQueue
{
    private const int Block = VoiceProcessor.BlockSamples, InHand = 2 * Block, TooMuch = 12 * Block;
    private readonly float[] samples = new float[VoicePacket.SampleRate * 2];
    private readonly object gate = new();
    private long written, read;
    private bool flowing;
    public void Write(ReadOnlySpan<float> block)
    {
        lock (gate)
        {
            foreach (var sample in block) { samples[written % samples.Length] = sample; written++; }
        }
    }
    // The next block, `late` samples older than the newest that could be given. False, and silence, when there is
    // not enough to give.
    public bool Read(float[] block, int late)
    {
        lock (gate)
        {
            var waiting = written - read - late;
            if (!flowing) { if (waiting < InHand) { Array.Clear(block); return false; } flowing = true; read = written - late - InHand; }
            else if (waiting > TooMuch) read = written - late - InHand;
            else if (waiting < Block) { flowing = false; Array.Clear(block); return false; }
            if (read < 0 || written - read > samples.Length) { flowing = false; Array.Clear(block); return false; }
            for (var i = 0; i < Block; i++) block[i] = samples[(read + i) % samples.Length];
            read += Block;
            return true;
        }
    }
}
