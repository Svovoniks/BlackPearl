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
            var rows = new System.Text.StringBuilder("frame,heard,cleaned,sent,gain,limiter,restored,echo,similarity,delay,cancelled,echoDb,speechDb,subtracted,working\n");
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
                rows.Append(System.Globalization.CultureInfo.InvariantCulture, $"{frame},{heard:F3},{cleaned:F3},{packets.Count},{processor.InputGainDb:F1},{processor.LimiterDb:F1},{processor.Restored:F2},{(processor.EchoPresent ? 1 : 0)},{processor.EchoEvidence.Similarity:F2},{processor.EchoEvidence.DelayMs},{(processor.EchoCancelled ? 1 : 0)},{processor.EchoDb:F1},{processor.SpeechDb:F1},{(processor.EchoSubtracted ? 1 : 0)},{processor.SubtractionDb:F1}\n");
                packets.Clear();
            }
            var name = Path.Combine(output, Path.GetFileNameWithoutExtension(file));
            Write(name + ".proc.wav", processed.ToArray());
            Write(name + ".sent.wav", sent);
            File.WriteAllText(name + ".csv", rows.ToString());
        }
    }
    // Watches the processing on a real microphone without keeping any audio:
    // --voice-live <part of the microphone's name> <seconds> [Standard|Light|Off] [<recording> <part of an output's name>].
    // It opens that microphone and listens to what the default output plays, as voice does, and prints once a second:
    // the processed level, the speech detector, whether the speakers were found in the microphone and how alike the
    // two were, the input gain, and the limiter. Name a microphone that is safe to open: a Bluetooth headset's
    // switches it to call mode.
    //
    // With a recording and an output, it plays the recording into that output and reports how the voice came through:
    // meant for a virtual cable, whose output nobody hears and whose other end is the microphone. It refuses when
    // another program has that microphone open, as that program would pass the recording on.
    public static async Task Live(string microphone, int seconds, string? only, string? recording = null, string? into = null)
    {
        var mode = only is null ? VoiceAudio.Processing.Standard : Enum.Parse<VoiceAudio.Processing>(only);
        var (inputs, _) = await VoiceAudio.ListDevicesAsync();
        var device = inputs.FirstOrDefault(d => d.Name.Contains(microphone, StringComparison.OrdinalIgnoreCase));
        if (device is null) { Console.WriteLine($"No microphone named like \"{microphone}\". Found: {string.Join("; ", inputs.Select(d => d.Name))}"); Environment.ExitCode = 1; return; }
        var capture = new VoiceCapture { Transmitting = true };
        var sent = 0; capture.Frame += _ => Interlocked.Increment(ref sent);
        VoiceProcessor.InputLevel.Forget(); EchoPresence.Forget();
        await using var audio = new VoiceAudio(capture, null, new(device.Id, null, mode));
        await audio.StartAsync();
        Console.WriteLine($"Microphone: {audio.InputName}; ready: {audio.MicrophoneReady}; problem: {audio.Problem ?? "none"}");
        if (!audio.MicrophoneReady || audio.Processor is not { } processor) { Environment.ExitCode = 1; return; }
        Console.WriteLine($"Echo cancelling available: {processor.EchoCancelling}; denoiser: {processor.DenoiserName}; speech detection: {processor.DetectsSpeech}");
        short[]? played = null; var heardBack = new List<short>(); Task? playing = null;
        if (recording is not null && into is not null)
        {
            var (_, outputs) = await VoiceAudio.ListDevicesAsync();
            var output = outputs.FirstOrDefault(d => d.Name.Contains(into, StringComparison.OrdinalIgnoreCase));
            if (output is null) { Console.WriteLine($"No output named like \"{into}\". Found: {string.Join("; ", outputs.Select(d => d.Name))}"); Environment.ExitCode = 1; return; }
            using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            using (var endpoint = enumerator.GetDevice(device.Id))
            {
                var sessions = endpoint.AudioSessionManager.Sessions; var others = new List<string>();
                for (var i = 0; i < sessions.Count; i++)
                    if (sessions[i].State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive && sessions[i].GetProcessID != Environment.ProcessId)
                        others.Add(System.Diagnostics.Process.GetProcessById((int)sessions[i].GetProcessID).ProcessName);
                if (others.Count > 0) { Console.WriteLine($"Not playing: {string.Join(", ", others)} has this microphone open and would pass the recording on."); Environment.ExitCode = 1; return; }
            }
            played = Read(recording);
            if (played is null) { Console.WriteLine("The recording could not be read."); Environment.ExitCode = 1; return; }
            lock (heardBack) processor.Processed = block => { lock (heardBack) heardBack.AddRange(block); };
            playing = VoiceAudio.PlayAsync(output.Id, played.Select(s => s / 32768f).ToArray(), CancellationToken.None);
            Console.WriteLine($"Playing {Path.GetFileName(recording)} ({played.Length / Rate} s) into {output.Name}");
            seconds = Math.Max(seconds, played.Length / Rate + 2);
        }
        Console.WriteLine("second  level dBFS  speech  frames sent  echo found  cancelling  similarity  delay ms  echo dB  voice dB  gain dB  limiter dB  given back");
        for (var second = 1; second <= seconds; second++)
        {
            double peak = -100; float similarity = 0, heard = 0, restored = 0; var delay = 0; double limiter = 0;
            var before = Volatile.Read(ref sent);
            for (var tick = 0; tick < 20; tick++)
            {
                await Task.Delay(50);
                peak = Math.Max(peak, capture.LevelDb); heard = Math.Max(heard, processor.SpeechProbability.Heard); restored = Math.Max(restored, processor.Restored);
                limiter = Math.Min(limiter, processor.LimiterDb);
                var evidence = processor.EchoEvidence; if (evidence.Similarity > similarity) { similarity = evidence.Similarity; delay = evidence.DelayMs; }
            }
            if (played is not null && second % 5 != 0 && second != seconds) continue;
            Console.WriteLine(FormattableString.Invariant($"{second,6}  {peak,10:F1}  {heard,6:F2}  {Volatile.Read(ref sent) - before,11}  {(processor.EchoPresent ? "yes" : "no"),10}  {(processor.EchoCancelled ? "yes" : "no"),10}  {similarity,10:F2}  {delay,8}  {processor.EchoDb,7:F1}  {processor.SpeechDb,8:F1}  {processor.InputGainDb,7:F1}  {limiter,10:F1}  {restored,10:F2}"));
        }
        if (played is null || playing is null) return;
        await playing;
        short[] back; lock (heardBack) back = heardBack.ToArray();
        Report("Through the real microphone path", played, back);
    }
    // The same report for a recording put through the processing directly, without audio devices, to tell what the
    // devices add: --voice-compare <recording> [Standard|Light|Off].
    public static void Compare(string recording, string? only)
    {
        var mode = only is null ? VoiceAudio.Processing.Standard : Enum.Parse<VoiceAudio.Processing>(only);
        if (Read(recording) is not { } samples) { Console.WriteLine("The recording could not be read."); Environment.ExitCode = 1; return; }
        VoiceProcessor.InputLevel.Forget(); EchoPresence.Forget();
        using var processor = new VoiceProcessor(new VoiceCapture { Transmitting = true }, mode);
        var processed = new List<short>(samples.Length);
        processor.Processed = block => processed.AddRange(block);
        for (var at = 0; at + Frame <= samples.Length; at += Frame) processor.Write(samples.AsSpan(at, Frame));
        Report("Without devices", samples, processed.ToArray());
    }
    static void Report(string path, short[] played, short[] heardBack)
    {
        if (heardBack.Length < played.Length / 2) { Console.WriteLine("Too little came back to compare."); return; }
        var kept = VoiceProcessingChecks.Compare(played, heardBack, 900);
        // Where each three seconds of the recording turned up in what came back. A real path that drops or repeats
        // audio shows as a delay that changes on the way, and makes the comparison above meaningless.
        var delays = new List<int>();
        for (var at = Rate * 2; at + Rate * 3 <= played.Length; at += Rate * 3)
        {
            double energy = 0;
            for (var i = at; i < at + Rate * 3; i += 3) energy += played[i] * (double)played[i];
            if (energy / Rate < 20000) continue;
            delays.Add(Enumerable.Range(0, 900).MaxBy(ms =>
            {
                double sum = 0; var shift = ms * Rate / 1000;
                for (var i = at; i < at + Rate * 3 && i + shift < heardBack.Length; i += 3) sum += played[i] * (double)heardBack[i + shift];
                return Math.Abs(sum);
            }));
        }
        Console.WriteLine($"Delay of each three seconds, ms: {string.Join(", ", delays)}");
        Console.WriteLine(FormattableString.Invariant($"{path}: gain {kept.Gain:+0.0;-0.0} dB, moving {kept.Moves:F1} dB between strong syllables, {kept.Dips:P0} of them more than 6 dB down; below 1 kHz {kept.Low:+0.0;-0.0} dB and above 4 kHz {kept.High:+0.0;-0.0} dB against the middle."));
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
