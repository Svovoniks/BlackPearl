using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Plays a folder of WAV recordings through the voice processing and reports what the gate would send, grouped by
// the part of each file name before "__": --voice-probe <folder> [Standard|Light|Off]. Set VOICE_PROBE_VERBOSE=1
// for every file with its peak speech probability. Used to tune speech detection against recorded sounds.
static class VoiceProbe
{
    const int Rate = VoicePacket.SampleRate, Frame = VoicePacket.FrameSamples;
    public static void Run(string folder, string? only)
    {
        var modes = only is null ? [VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light] : new[] { Enum.Parse<VoiceAudio.Processing>(only) };
        var verbose = Environment.GetEnvironmentVariable("VOICE_PROBE_VERBOSE") == "1";
        foreach (var mode in modes)
        {
            Console.WriteLine($"== {mode}");
            var totals = new Dictionary<string, (int Files, int Triggered, int Frames)>();
            foreach (var file in Directory.GetFiles(folder, "*.wav").Order())
            {
                if (Read(file) is not { } samples) continue;
                var capture = new VoiceCapture { Transmitting = true };
                var sent = 0; capture.Frame += _ => sent++;
                VoiceProcessor.InputLevel.Forget();
                using var processor = new VoiceProcessor(capture, mode);
                float peak = 0, peakHeard = 0, peakCleaned = 0;
                for (var at = 0; at + Frame <= samples.Length; at += Frame)
                {
                    processor.Write(samples.AsSpan(at, Frame));
                    var (heard, cleaned) = processor.SpeechProbability;
                    peak = Math.Max(peak, Math.Min(heard, cleaned)); peakHeard = Math.Max(peakHeard, heard); peakCleaned = Math.Max(peakCleaned, cleaned);
                }
                var name = Path.GetFileNameWithoutExtension(file);
                var group = name.Split("__")[0];
                var total = totals.GetValueOrDefault(group);
                totals[group] = (total.Files + 1, total.Triggered + (sent > 0 ? 1 : 0), total.Frames + sent);
                if (verbose) Console.WriteLine($"{name,-40} sent {sent,5} peak {peak:F2} (heard {peakHeard:F2}, cleaned {peakCleaned:F2})");
            }
            foreach (var (group, total) in totals.OrderBy(t => t.Key)) Console.WriteLine($"{group,-24} sent from {total.Triggered}/{total.Files} files, {total.Frames} frames");
        }
    }
    // Writes what the processing does to each recording: --voice-render <folder> <output folder> [Standard|Light|Off].
    // "<name>.proc.wav" is the processed microphone before the voice gate, "<name>.sent.wav" is what a listener
    // decodes (silence where nothing was sent), and "<name>.csv" has one row per 20 ms frame. Both start a second
    // before the recording does, as the input is read with a second of silence in front. A file named
    // "<name>.ref.wav" beside a recording is what the speakers play, for the echo canceller. Every recording starts
    // as a first session does: no level learned, echo not yet looked for. VOICE_PROBE_ALWAYS=1 keeps the gate open,
    // so the sent audio shows the codec alone.
    public static void Render(string folder, string output, string? only)
    {
        var mode = only is null ? VoiceAudio.Processing.Standard : Enum.Parse<VoiceAudio.Processing>(only);
        Directory.CreateDirectory(output);
        foreach (var file in Directory.GetFiles(folder, "*.wav").Where(f => !f.EndsWith(".ref.wav", StringComparison.OrdinalIgnoreCase)).Order())
        {
            if (Read(file) is not { } samples) continue;
            var reference = File.Exists(Path.ChangeExtension(file, ".ref.wav")) ? Read(Path.ChangeExtension(file, ".ref.wav")) : null;
            var capture = new VoiceCapture { Transmitting = true, AlwaysOn = Environment.GetEnvironmentVariable("VOICE_PROBE_ALWAYS") == "1" };
            var decoder = Concentus.OpusCodecFactory.CreateDecoder(Rate, 1);
            var frames = samples.Length / Frame;
            var processed = new List<short>(samples.Length);
            var sent = new short[frames * Frame];
            var packets = new List<byte[]>();
            capture.Frame += packet => packets.Add(packet.ToArray());
            VoiceProcessor.InputLevel.Forget(); EchoPresence.Forget();
            using var processor = new VoiceProcessor(capture, mode);
            processor.Processed = block => processed.AddRange(block);
            var rows = new System.Text.StringBuilder("frame,heard,cleaned,sent,gain,limiter,restored,echo,similarity,delay\n");
            var decoded = new short[Frame];
            for (var frame = 0; frame < frames; frame++)
            {
                if (reference is not null && (frame + 1) * Frame <= reference.Length) processor.Reference(reference.AsSpan(frame * Frame, Frame));
                processor.Write(samples.AsSpan(frame * Frame, Frame));
                // Packets sent now are this frame and, when the gate has just opened, the pre-roll before it.
                for (var i = 0; i < packets.Count; i++)
                {
                    if (!VoicePacket.TryRead(packets[i], out _, out _, out _, out var payload)) continue;
                    decoder.Decode(payload, decoded, Frame, false);
                    var at = frame - (packets.Count - 1 - i);
                    if (at >= 0) decoded.CopyTo(sent, at * Frame);
                }
                var (heard, cleaned) = processor.SpeechProbability;
                rows.Append(System.Globalization.CultureInfo.InvariantCulture, $"{frame},{heard:F3},{cleaned:F3},{packets.Count},{processor.InputGainDb:F1},{processor.LimiterDb:F1},{processor.Restored:F2},{(processor.EchoPresent ? 1 : 0)},{processor.EchoEvidence.Similarity:F2},{processor.EchoEvidence.DelayMs}\n");
                packets.Clear();
            }
            var name = Path.Combine(output, Path.GetFileNameWithoutExtension(file));
            Write(name + ".proc.wav", processed.ToArray());
            Write(name + ".sent.wav", sent);
            File.WriteAllText(name + ".csv", rows.ToString());
        }
    }
    static void Write(string path, short[] samples)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(Rate); writer.Write(Rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples.Length * 2);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
    }
    // 16-bit PCM WAV, first channel, resampled to 48 kHz after a second of silence.
    static short[]? Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int rate = 0, channels = 0, bits = 0, offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4); var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(bytes, offset + 10); rate = BitConverter.ToInt32(bytes, offset + 12); bits = BitConverter.ToInt16(bytes, offset + 22); }
            if (id == "data" && bits == 16 && rate > 0 && channels > 0)
            {
                var frames = Math.Min(size, bytes.Length - offset - 8) / 2 / channels;
                var length = (int)((long)frames * Rate / rate);
                var result = new short[length + Rate];
                for (var i = 0; i < length; i++)
                {
                    var position = (double)i * rate / Rate; var at = (int)position; var next = Math.Min(at + 1, frames - 1);
                    var a = BitConverter.ToInt16(bytes, offset + 8 + at * 2 * channels); var b = BitConverter.ToInt16(bytes, offset + 8 + next * 2 * channels);
                    result[Rate + i] = (short)(a + (b - a) * (position - at));
                }
                return result;
            }
            offset += 8 + size + (size & 1);
        }
        return null;
    }
}
