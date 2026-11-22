using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;
using Windows.Media.SpeechSynthesis;

// BlackPearl's own microphone processing, without audio devices: the bundled models load, noise is removed, Silero
// decides what is speech, the echo canceller removes what the speakers played, and through all of it the voice
// leaves as it arrived. Speech comes from Windows' speech synthesizer; machines without a voice check everything else.
static class VoiceProcessingChecks
{
    const int Rate = VoicePacket.SampleRate, Frame = VoicePacket.FrameSamples;

    public static async Task Run(Action<bool, string> check)
    {
        using (var standard = new VoiceProcessor(new VoiceCapture(), VoiceAudio.Processing.Standard))
            check(standard.EchoCancelling && standard.DenoiserName == "deepfilter" && standard.DetectsSpeech, "echo cancellation, DeepFilterNet 3 and Silero VAD load from the app folder");
        using (var light = new VoiceProcessor(new VoiceCapture(), VoiceAudio.Processing.Light))
            check(light.EchoCancelling && light.DenoiserName == "rnnoise" && light.DetectsSpeech, "lighter processing loads RNNoise");
        using (var off = new VoiceProcessor(new VoiceCapture(), VoiceAudio.Processing.Off))
            check(!off.EchoCancelling && off.DenoiserName is null && off.DetectsSpeech, "with processing off only speech detection runs");

        // One chunk that sounds like a syllable is not speech; two in a row are, and speech lasts until it fades.
        var decision = new VoiceProcessor.SpeechDecision();
        check(!decision.Next(0.9f) && !decision.Next(0.1f) && !decision.Next(0.9f) && decision.Next(0.6f) && decision.Next(0.4f) && !decision.Next(0.3f),
            "speech needs two chunks in a row above 0.5, then holds until the probability falls below 0.35");

        Pieces(check);

        var fan = Noise(Rate * 6, 0.03, 1);
        foreach (var (mode, name) in new[] { (VoiceAudio.Processing.Standard, "DeepFilterNet"), (VoiceAudio.Processing.Light, "RNNoise") })
        {
            var result = Process(mode, fan);
            var removed = Level(fan[(Rate * 3)..]) - result.Level(3, 6);
            check(removed > 15, $"{name} removes steady fan noise ({removed:F0} dB quieter)");
            check(result.Sent == 0, $"fan noise is never sent as speech with {name} ({result.Sent} frames)");
        }
        var raw = Process(VoiceAudio.Processing.Off, fan);
        check(Math.Abs(Level(fan[(Rate * 3)..]) - raw.Level(3, 6)) < 1, "with processing off the microphone is sent as it is");

        // Typing: loud, short clicks. The old level gate opened for anything louder than -30 dBFS.
        var typing = new short[Rate * 4];
        var random = new Random(7);
        for (var at = Rate / 10; at < typing.Length - 400; at += Rate / 8 + random.Next(Rate / 10))
            for (var i = 0; i < 300; i++) typing[at + i] = (short)(Math.Exp(-i / 40.0) * (random.NextDouble() * 2 - 1) * 20000);
        foreach (var mode in new[] { VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light, VoiceAudio.Processing.Off })
        {
            var clicks = Process(mode, typing);
            check(clicks.Sent == 0, $"typing is not taken for speech ({mode}: {clicks.Sent} frames sent)");
        }
        // Things dropped or knocked: thuds that bounce, slaps, knocks and keys, loud and close to the microphone.
        var impacts = Enumerable.Range(0, 24).Select(Impact).ToArray();
        foreach (var mode in new[] { VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light })
        {
            var sent = impacts.Select(sound => Process(mode, sound).Sent).ToArray();
            check(sent.All(s => s == 0), $"dropped and knocked objects are never sent as speech ({mode}: {sent.Count(s => s > 0)} of {sent.Length} sounds sent)");
        }

        var speech = await Speak();
        if (speech is null) { Console.WriteLine("SKIP: no Windows voice is installed, so the speech checks were skipped."); return; }
        var spoken = Process(VoiceAudio.Processing.Standard, speech);
        var talking = speech.Length / Frame;
        check(spoken.Sent > talking * 0.6, $"synthesized speech is detected and sent ({spoken.Sent} of {talking} frames)");
        var noisy = Mix(speech, Noise(speech.Length, 0.03, 2));
        var overFan = Process(VoiceAudio.Processing.Standard, noisy);
        check(overFan.Sent > talking * 0.6, $"speech over fan noise is still detected ({overFan.Sent} of {talking} frames)");
        var lightOverFan = Process(VoiceAudio.Processing.Light, noisy);
        check(lightOverFan.Sent > talking * 0.6, $"speech over fan noise is detected with lighter processing ({lightOverFan.Sent} of {talking} frames)");
        // A quiet microphone, such as a virtual cable: DeepFilterNet alone would remove speech this quiet as noise.
        var quietGain = Math.Pow(10, (-42 - ActiveDb(speech)) / 20);
        var quiet = speech.Select(s => (short)(s * quietGain)).ToArray();
        foreach (var mode in new[] { VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light })
        {
            var result = Process(mode, quiet);
            check(result.Sent > talking * 0.6, $"quiet speech at -42 dBFS is sent ({mode}: {result.Sent} of {talking} frames)");
            var raised = result.Level(4, quiet.Length / Rate) - Level(quiet[(Rate * 4)..]);
            check(raised > 10, $"quiet speech is raised towards a normal level ({mode}: {raised:F0} dB louder)");
        }
        // The voice leaves as it arrived. With nothing to remove, what is sent is the microphone itself: the same
        // level from one syllable to the next, and the same tone.
        var kept = Compare(speech, spoken.Audio);
        check(kept.Moves < 1.2 && kept.Dips < 0.02, $"clean speech keeps its loud and soft syllables as they were (gain moves {kept.Moves:F1} dB, {kept.Dips:P0} of frames dip)");
        check(Math.Abs(kept.Low) < 1.5 && Math.Abs(kept.High) < 1.5, $"clean speech keeps its tone (below 1 kHz {kept.Low:+0.0;-0.0} dB, above 4 kHz {kept.High:+0.0;-0.0} dB against the middle)");
        check(spoken.Audio.Max(s => Math.Abs((int)s)) < 32000, "normal speech stays below full scale without the limiter working");
        // A voice from across a room arrives with the room's reverberation, which a denoiser takes for noise and
        // removes together with part of the voice. In a quiet room there is nothing to remove, so it stays whole.
        var distant = Room(speech);
        var far = Process(VoiceAudio.Processing.Standard, distant);
        var farKept = Compare(distant, far.Audio);
        check(farKept.Dips < 0.05 && farKept.Moves < 2.5, $"a voice from across a quiet room is kept whole ({farKept.Dips:P0} of frames dip, gain moves {farKept.Moves:F1} dB)");
        var lightFar = Compare(distant, Process(VoiceAudio.Processing.Light, distant).Audio);
        check(lightFar.Dips < 0.05 && lightFar.Moves < 2.5, $"lighter processing keeps a distant voice whole too ({lightFar.Dips:P0} of frames dip, gain moves {lightFar.Moves:F1} dB)");
        // With noise in the room the same voice is still kept, and the pauses are still cleaned.
        var noisyRoom = Mix(distant, Noise(distant.Length, 0.01, 5));
        var farNoisy = Process(VoiceAudio.Processing.Standard, noisyRoom);
        var farNoisyKept = Compare(distant, farNoisy.Audio);
        check(farNoisyKept.Dips < 0.08, $"a distant voice over fan noise is kept ({farNoisyKept.Dips:P0} of frames dip)");
        var end = noisyRoom.Length / Rate;
        var hush = Level(noisyRoom[^(Rate / 2)..]) + farNoisyKept.Gain - farNoisy.Level(end - 0.5, end);
        check(hush > 15, $"and the fan is still removed once the voice stops ({hush:F0} dB quieter)");
        // The first word is not cut off. The gate opens a little after speech starts and sends its pre-roll at once,
        // so the first burst begins earlier.
        var onset = speech.Select((s, i) => (s, i)).First(p => Math.Abs(p.s) > 1000).i / Frame;
        var opened = spoken.SentFrames.FirstOrDefault(-1);
        var from = opened - spoken.SentFrames.Count(f => f == opened) + 1;
        check(opened >= 0 && from <= onset, $"the start of the first word is sent (audio from frame {from}, speech from frame {onset})");

        // The movie's dialogue from the speakers reaches the microphone 40 ms later, quieter, with a room reflection.
        var movie = Repeat(speech, Rate * 12);
        var heard = new short[movie.Length];
        for (var i = 0; i < heard.Length; i++)
        {
            var direct = i >= 1920 ? movie[i - 1920] * 0.5 : 0;
            var reflection = i >= 3360 ? movie[i - 3360] * 0.2 : 0;
            heard[i] = (short)(direct + reflection);
        }
        var echo = Process(VoiceAudio.Processing.Standard, heard, movie);
        var cancelled = Level(heard[(Rate * 6)..]) - echo.Level(6, 12);
        check(echo.EchoPresent, "the movie from the speakers is found in the microphone");
        check(cancelled > 20, $"the movie's sound from the speakers is removed from the microphone ({cancelled:F0} dB quieter)");
        var lateSent = echo.SentAfter(6);
        check(lateSent < Rate * 6 / Frame / 10, $"once the canceller has adapted the movie's dialogue is not sent as speech ({lateSent} frames in 6 s)");
        // Talking over the movie: from 6 s the person says something else while the dialogue keeps playing.
        var voice = speech[Rate..(Rate * 7)];
        var both = heard.Select((s, i) => i >= Rate * 6 ? (short)Math.Clamp(s + voice[i - Rate * 6], short.MinValue, short.MaxValue) : s).ToArray();
        var doubleTalk = Process(VoiceAudio.Processing.Standard, both, movie);
        var voiceFrames = voice.Length / Frame;
        check(doubleTalk.SentAfter(6) > voiceFrames * 0.6, $"speaking over the movie is still sent ({doubleTalk.SentAfter(6)} of {voiceFrames} frames)");
        var level = doubleTalk.Level(6, 12) - Level(voice);
        check(level > -10, $"the voice keeps its level while the canceller removes the movie ({level:F1} dB)");
        // A longer talk over the movie: once the echo is being subtracted instead of suppressed, the voice comes
        // through whole. WebRTC's canceller alone leaves a third of it more than 6 dB down.
        var longMovie = Repeat(speech, Rate * 32);
        var longHeard = new short[longMovie.Length];
        for (var i = 0; i < longHeard.Length; i++) longHeard[i] = (short)((i >= 1920 ? longMovie[i - 1920] * 0.5 : 0) + (i >= 3360 ? longMovie[i - 3360] * 0.2 : 0));
        var longVoice = new short[longMovie.Length];
        var saying = Film(speech, Rate * 20, music: false);
        for (var i = Rate * 12; i < longVoice.Length; i++) longVoice[i] = saying[i - Rate * 12];
        var longTalk = Process(VoiceAudio.Processing.Standard, Mix(longHeard, longVoice), longMovie);
        var whole = Compare(longVoice[(Rate * 16)..], longTalk.Audio[(Rate * 16)..]);
        check(longTalk.Subtracted, $"an echo that can be subtracted is subtracted ({longTalk.Echo})");
        check(whole.Dips < 0.12, $"talking over the movie from speakers keeps the voice whole ({whole.Dips:P0} of frames dip, gain moves {whole.Moves:F1} dB; {longTalk.Echo})");
        var leftover = Level(longHeard[(Rate * 8)..(Rate * 12)]) - longTalk.Level(8, 12);
        check(leftover > 20, $"and before anyone talks the movie is still removed ({leftover:F0} dB quieter)");

        // Headphones: the movie plays, but none of it reaches the microphone. An echo canceller handed a movie it
        // cannot find suppresses the voice instead, so it has to stand aside.
        var worn = Repeat(speech, Rate * 12);
        var headphones = Process(VoiceAudio.Processing.Standard, worn, Film(speech, Rate * 12));
        check(!headphones.EchoPresent, $"with headphones no echo is found (similarity {headphones.Similarity:F2})");
        var wornKept = Compare(worn[(Rate * 4)..], headphones.Audio[(Rate * 4)..]);
        check(wornKept.Dips < 0.02 && wornKept.Moves < 1.5, $"with headphones the movie playing does not touch the voice ({wornKept.Dips:P0} of frames dip, gain moves {wornKept.Moves:F1} dB)");
        check(headphones.Sent > Rate * 12 / Frame * 0.6, $"and the voice is sent ({headphones.Sent} frames)");
    }

    // The pieces on their own, without speech.
    static void Pieces(Action<bool, string> check)
    {
        const int Block = VoiceProcessor.BlockSamples;
        var random = new Random(3);
        float[] Hiss(double amplitude) => Enumerable.Range(0, Block).Select(_ => (float)((random.NextDouble() * 2 - 1) * amplitude)).ToArray();

        // The guard only ever mixes the microphone back, so two identical signals come out as they went in, a block later.
        var guard = new VoiceGuard();
        var blocks = Enumerable.Range(0, 60).Select(_ => Hiss(0.2)).ToArray();
        double error = 0, power = 0;
        for (var i = 0; i < blocks.Length; i++)
        {
            var output = (float[])blocks[i].Clone();
            guard.Process(blocks[i], output, 1);
            if (i < 2) continue;
            for (var n = 0; n < Block; n++) { error += Math.Pow(output[n] - blocks[i - 1][n], 2); power += blocks[i - 1][n] * blocks[i - 1][n]; }
        }
        check(error / power < 1e-8, $"the guard returns unchanged audio as it was, one block later (error {10 * Math.Log10(error / power + 1e-20):F0} dB)");
        // In a pause the denoiser's silence stands; during speech in a quiet room, where the denoiser kept half of a
        // voice, the microphone comes back.
        guard = new VoiceGuard();
        float[] silence = new float[Block], pause = [], talk = [];
        for (var i = 0; i < 100; i++) { pause = (float[])silence.Clone(); guard.Process(Hiss(0.01), pause, 0); }
        for (var i = 0; i < 30; i++) { var heard = Hiss(0.2); talk = heard.Select(v => v / 2).ToArray(); guard.Process(heard, talk, 1); }
        check(pause.Max(Math.Abs) < 1e-4 && Rms(talk) > 0.1, $"the guard leaves pauses silent and gives back what a denoiser took from speech (pause peak {pause.Max(Math.Abs):F5}, speech {Rms(talk):F2} of 0.12)");
        // A sound the denoiser removed whole, with nothing of the voice near it, stays removed: a door, not a voice.
        guard = new VoiceGuard();
        for (var i = 0; i < 100; i++) guard.Process(Hiss(0.01), (float[])silence.Clone(), 0);
        for (var i = 0; i < 30; i++) { talk = (float[])silence.Clone(); guard.Process(Hiss(0.2), talk, 1); }
        check(Rms(talk) < 0.01, $"the guard does not give back a sound the denoiser removed whole ({Rms(talk):F3})");
        // With that much noise measured in the pauses, the denoiser may remove that much from speech: what it halved
        // stays halved.
        guard = new VoiceGuard();
        for (var i = 0; i < 100; i++) guard.Process(Hiss(0.2), (float[])silence.Clone(), 0);
        for (var i = 0; i < 30; i++) { var heard = Hiss(0.2); talk = heard.Select(v => v / 2).ToArray(); guard.Process(heard, talk, 1); }
        check(Rms(talk) < 0.07, $"the guard lets a denoiser remove as much as the noise it measured ({Rms(talk):F3} of 0.058)");
        // A quiet microphone that is then raised by 20 dB has the same room, 20 dB louder.
        guard = new VoiceGuard();
        for (var i = 0; i < 100; i++) guard.Process(Hiss(0.02), (float[])silence.Clone(), 0);
        for (var i = 0; i < 30; i++) { var heard = Hiss(0.2); talk = heard.Select(v => v / 2).ToArray(); guard.Process(heard, talk, 1, 20); }
        check(Rms(talk) < 0.07, $"the noise the guard measured follows the microphone's gain ({Rms(talk):F3} of 0.058)");

        // The filter in front of everything takes out rumble below the voice and leaves the lowest voices alone.
        double Passes(double hz)
        {
            var filter = Biquad.HighPass(60); double sum = 0;
            for (var i = 0; i < Rate; i++) { var y = filter.Run((float)Math.Sin(2 * Math.PI * hz * i / Rate)); if (i >= Rate / 2) sum += y * y; }
            return 10 * Math.Log10(sum / (Rate / 4.0));
        }
        check(Passes(25) < -14 && Passes(85) > -1.5 && Passes(150) > -0.2, $"rumble is filtered out below the voice (25 Hz {Passes(25):F0} dB, 85 Hz {Passes(85):F1} dB, 150 Hz {Passes(150):F1} dB)");

        // The limiter leaves anything below its ceiling alone and holds what is above it, without steps.
        var limiter = new PeakLimiter();
        float[] Tone(double amplitude, int block) => Enumerable.Range(0, Block).Select(i => (float)(amplitude * Math.Sin((block * Block + i) * 0.13))).ToArray();
        var soft = Tone(0.5, 0); var same = (float[])soft.Clone(); limiter.Apply(same);
        check(same.SequenceEqual(soft), "the limiter does not touch audio below its ceiling");
        var loud = Enumerable.Range(1, 20).Select(i => { var tone = Tone(2, i); limiter.Apply(tone); return tone; }).ToArray();
        var step = loud.Skip(2).Max(tone => Enumerable.Range(1, Block - 1).Max(i => Math.Abs(tone[i] - tone[i - 1])));
        check(loud.Max(tone => tone.Max(Math.Abs)) <= PeakLimiter.Ceiling + 1e-6f && step < PeakLimiter.Ceiling * 0.14f, $"a sound twice full scale is held below it smoothly (peak {loud.Max(tone => tone.Max(Math.Abs)):F2}, largest step {step:F3})");
        var after = soft;
        for (var i = 21; i < 121; i++) { after = Tone(0.5, i); limiter.Apply(after); }
        check(Math.Abs(after.Max(Math.Abs) - 0.5f) < 0.01f, $"and the level returns once it is over ({after.Max(Math.Abs):F2} of 0.50)");

        // A learned high gain never pushes a sudden loud sound past full scale, and the gain itself moves slowly.
        VoiceProcessor.InputLevel.Forget();
        var level = new VoiceProcessor.InputLevel(); limiter = new PeakLimiter();
        float[] Faint() => Enumerable.Range(0, Block).Select(i => (float)(0.003 * Math.Sin(i * 0.1))).ToArray();
        for (var i = 0; i < 200; i++) { var faint = Faint(); level.Learn(faint, 1); level.Apply(faint); }
        var gain = level.GainDb;
        var shouts = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, Block).Select(i => (float)(0.7 * Math.Sin(i * 0.1))).ToArray()).ToArray();
        foreach (var shout in shouts) { level.Learn(shout, 1); level.Apply(shout); limiter.Apply(shout); }
        var loudest = shouts.Max(s => s.Max(Math.Abs));
        check(gain > 20 && loudest <= PeakLimiter.Ceiling + 1e-6f, $"a loud sound after quiet speech is not clipped (gain {gain:F0} dB, peak {loudest:F2})");
        check(gain - level.GainDb < 0.5, $"and one loud moment does not change the gain ({gain - level.GainDb:F2} dB)");
        // A voice that turns out louder than when its level was learned drives the limiter; the gain comes down until
        // it no longer does, instead of the limiter holding the voice in for good.
        VoiceProcessor.InputLevel.Forget();
        var eased = new VoiceProcessor.InputLevel(); var holding = new PeakLimiter();
        for (var i = 0; i < 200; i++) { var faint = Faint(); eased.Learn(faint, 1); eased.Apply(faint); }
        float[] Louder(int block) => Enumerable.Range(0, Block).Select(i => (float)(0.06 * Math.Sin((block * Block + i) * 0.1))).ToArray();
        var limited = new List<double>();
        for (var i = 0; i < 600; i++) { var louder = Louder(i); eased.Apply(louder); holding.Apply(louder); eased.Ease(holding.GainDb); limited.Add(holding.GainDb); }
        check(limited.Take(50).Min() < -4 && limited.Skip(500).Min() > -1.5, $"a voice that outgrows its gain is not left to the limiter (limited {limited.Take(50).Min():F1} dB at first, {limited.Skip(500).Min():F1} dB after five seconds, gain {eased.GainDb:F1} dB)");
        // A level learned while WebRTC's canceller was taking some of the voice is too low. When the subtractor takes
        // over the voice comes through whole, 6 dB louder here, and the level is learned again within a second
        // instead of leaving the gain too high for minutes. A level that is long established stays.
        VoiceProcessor.InputLevel.Forget();
        var relearned = new VoiceProcessor.InputLevel(); var left = new VoiceProcessor.InputLevel();
        float[] Whole(int block) => Enumerable.Range(0, Block).Select(i => (float)(0.006 * Math.Sin((block * Block + i) * 0.1))).ToArray();
        for (var i = 0; i < 300; i++) { var faint = Faint(); relearned.Learn(faint, 1); relearned.Apply(faint); faint = Faint(); left.Learn(faint, 1); left.Apply(faint); }
        var thinned = relearned.GainDb;
        relearned.Again();
        for (var i = 0; i < 100; i++) { var whole = Whole(i); relearned.Learn(whole, 1); relearned.Apply(whole); whole = Whole(i); left.Learn(whole, 1); left.Apply(whole); }
        VoiceProcessor.InputLevel.Forget();
        var established = new VoiceProcessor.InputLevel();
        for (var i = 0; i < 1200; i++) { var faint = Faint(); established.Learn(faint, 1); established.Apply(faint); }
        var settled = established.GainDb;
        established.Again();
        for (var i = 0; i < 100; i++) { var whole = Whole(i); established.Learn(whole, 1); established.Apply(whole); }
        check(Math.Abs(thinned - relearned.GainDb - 6) < 1 && Math.Abs(left.GainDb - thinned) < 0.5 && Math.Abs(established.GainDb - settled) < 0.5,
            $"a level learned from a voice the canceller had thinned is learned again once the voice comes through whole ({thinned:F1} to {relearned.GainDb:F1} dB; {left.GainDb:F1} dB without; {established.GainDb:F1} dB when long established)");
        VoiceProcessor.InputLevel.Forget();
        for (var i = 0; i < 200; i++) { var faint = Faint(); level.Learn(faint, 1); level.Apply(faint); }
        // The next time voice opens the level is already known. Another microphone, 20 dB louder, is then learned
        // afresh within a few seconds instead of over minutes.
        level = new VoiceProcessor.InputLevel();
        check(Math.Abs(level.GainDb - gain) < 1, $"the level learned carries over to the next time ({level.GainDb:F0} dB)");
        // And to the next run of the app, as the text it is kept as.
        var kept = VoiceProcessor.InputLevel.Saved;
        VoiceProcessor.InputLevel.Forget();
        var forgotten = new VoiceProcessor.InputLevel().GainDb;
        VoiceProcessor.InputLevel.Saved = kept;
        check(kept is not null && forgotten == 0 && Math.Abs(new VoiceProcessor.InputLevel().GainDb - gain) < 1, $"and to the next run of the app (\"{kept}\")");
        VoiceProcessor.InputLevel.Saved = "not a level";
        check(Math.Abs(new VoiceProcessor.InputLevel().GainDb - gain) < 1, "a damaged saved level is ignored");
        float[] Nearer() => Enumerable.Range(0, Block).Select(i => (float)(0.03 * Math.Sin(i * 0.1))).ToArray();
        for (var i = 0; i < 400; i++) { var nearer = Nearer(); level.Learn(nearer, 1); level.Apply(nearer); }
        var asked = VoiceProcessor.InputLevel.TargetDb - 20 * Math.Log10(0.03 / Math.Sqrt(2));
        check(Math.Abs(level.GainDb - asked) < 1.5, $"a microphone that is suddenly 20 dB louder gets the gain its own level asks for within four seconds ({level.GainDb:F1} of {asked:F1} dB)");
        VoiceProcessor.InputLevel.Forget();

        // Subtracting the echo: the speakers' sound arrives 30 ms later with a reflection behind it, and someone
        // talks over it half of the time. The echo goes; the talking stays as it was.
        var subtractor = new EchoSubtractor();
        var movie = Noise(Rate * 20, 0.05, 21); var talk2 = Noise(Rate * 20, 0.05, 22);
        double echoIn = 0, echoOut = 0, talkIn = 0, talkOut = 0;
        var cleaned = new float[Block]; var previous = new float[Block];
        for (var at = 0; at + Block <= Rate * 20; at += Block)
        {
            var playedBlock = new float[Block]; var heardBlock = new float[Block]; var spoken = new float[Block];
            var talking = at / (Rate / 2) % 2 == 1;
            for (var i = 0; i < Block; i++)
            {
                var n = at + i;
                playedBlock[i] = movie[n] / 32768f;
                spoken[i] = talking ? talk2[n] / 32768f : 0;
                heardBlock[i] = spoken[i] + (n >= 1440 ? 0.5f * movie[n - 1440] / 32768f : 0) + (n >= 4000 ? 0.2f * movie[n - 4000] / 32768f : 0);
            }
            subtractor.Process(heardBlock, playedBlock, cleaned);
            // The last five seconds are measured, away from where talking starts or stops.
            var before = at; var steady = before % (Rate / 2) is >= 4800 and < Rate / 2 - 9600;
            if (at >= Rate * 15 && steady)
            {
                var wasTalking = before / (Rate / 2) % 2 == 1;
                for (var i = 0; i < Block; i++)
                {
                    var n = before + i;
                    double echo = 0.5 * movie[n - 1440] / 32768.0 + 0.2 * movie[n - 4000] / 32768.0, voice = wasTalking ? talk2[n] / 32768.0 : 0;
                    if (wasTalking) { talkIn += voice * voice; talkOut += cleaned[i] * (double)cleaned[i]; }
                    else { echoIn += echo * echo; echoOut += cleaned[i] * (double)cleaned[i]; }
                }
            }
        }
        var removed = 10 * Math.Log10(echoIn / (echoOut + 1e-12)); var keptTalk = 10 * Math.Log10(talkOut / talkIn);
        check(removed > 15 && Math.Abs(keptTalk) < 1.5 && subtractor.Restarts == 0, $"subtracting takes the echo away and leaves the talking over it ({removed:F0} dB less echo, talking {keptTalk:+0.0;-0.0} dB)");
        check(subtractor.WorkingDb > 15, $"and it knows that it is working ({subtractor.WorkingDb:F0} dB)");
        // Speakers and a microphone that are separate devices count time by clocks of their own. Here the speakers'
        // runs 40 parts per million fast: the echo slides two samples every second against what was played. The
        // subtractor finds how far apart the clocks are and has the speakers' sound read that much faster, so it
        // goes on subtracting; left alone, the slide leaves it behind.
        // (A movie's sound is mostly low: noise below about 3 kHz stands for it.)
        var hiss = Noise(Rate * 31 + 8, 0.05, 23); var lowFilm = new short[Rate * 31];
        for (var i = 0; i < lowFilm.Length; i++) { var sum = 0; for (var j = 0; j < 8; j++) sum += hiss[i + j]; lowFilm[i] = (short)(sum / 3); }
        double followed = 0;
        foreach (var following in new[] { true, false })
        {
            var drifting = new EchoSubtractor(); var line = new SpeakerQueue(); var read = new float[Block];
            const double Apart = 40e-6; double driftIn = 0, driftOut = 0; var trace = new List<string>();
            for (var at = Rate; at + Block <= Rate * 30; at += Block)
            {
                var playedBlock = new float[Block]; var heardBlock = new float[Block];
                for (var i = 0; i < Block; i++)
                {
                    playedBlock[i] = lowFilm[at + i] / 32768f;
                    var place = (at + i - 1440 - Rate) * (1 + Apart) + Rate; var whole = (int)Math.Floor(place); var part = (float)(place - whole);
                    heardBlock[i] = 0.5f * (lowFilm[whole] + part * (lowFilm[whole + 1] - lowFilm[whole])) / 32768f;
                }
                line.Write(playedBlock);
                line.Read(read, 0, following ? 1 + drifting.Skew : 1);
                drifting.Process(heardBlock, read, cleaned);
                if (at >= Rate * 20) for (var i = 0; i < Block; i++) { driftIn += heardBlock[i] * (double)heardBlock[i]; driftOut += cleaned[i] * (double)cleaned[i]; }
                if (at % (Rate * 2) < Block) trace.Add((drifting.Skew * 1e6).ToString("F0"));
            }
            var driftRemoved = 10 * Math.Log10(driftIn / (driftOut + 1e-12));
            if (following) followed = driftRemoved;
            if (following) check(Math.Abs(drifting.Skew - Apart) < 15e-6 && driftRemoved > 30, $"speakers on a clock of their own are followed ({drifting.Skew * 1e6:F0} of 40 parts per million found, {driftRemoved:F0} dB less echo; every 2 s: {string.Join(" ", trace)})");
            else check(driftRemoved < followed - 15, $"which is needed: left alone, such a clock leaves the subtractor behind ({driftRemoved:F0} dB less echo)");
        }
        // With the speakers' sound arriving after its echo, there is nothing to subtract, and it says so.
        var hopeless = new EchoSubtractor();
        for (var at = Rate; at + Block <= Rate * 8; at += Block)
        {
            var playedBlock = new float[Block]; var heardBlock = new float[Block];
            for (var i = 0; i < Block; i++) { playedBlock[i] = movie[at + i - 4800] / 32768f; heardBlock[i] = 0.5f * movie[at + i] / 32768f; }
            hopeless.Process(heardBlock, playedBlock, cleaned);
        }
        check(hopeless.WorkingDb < 6, $"where it cannot work it says so, and is not used ({hopeless.WorkingDb:F0} dB)");
        // The speakers' sound is handed over in order, a little kept in hand, with silence when it runs out.
        var queue = new SpeakerQueue(); var handed = new float[Block];
        float[] Numbered(int from) => Enumerable.Range(from, Block).Select(v => (float)v).ToArray();
        queue.Write(Numbered(0));
        var early = queue.Read(handed, 0);
        queue.Write(Numbered(Block)); queue.Write(Numbered(2 * Block));
        // (Two and a half blocks are kept in hand, and eight samples for reading between samples.)
        const int Back = 5 * Block / 2 + 8;
        var first = queue.Read(handed, 0) && handed[0] == 3 * Block - Back;
        var second = queue.Read(handed, 0) && handed[0] == 4 * Block - Back;
        var empty = !queue.Read(handed, 0) && handed.All(v => v == 0);
        check(!early && first && second && empty, "the speakers' sound reaches the subtractor in order, and silence when there is none");
        // A delivery that comes a moment late does not move what follows against the microphone: the block that was
        // missed is skipped, not handed over one block late from then on.
        queue.Write(Numbered(3 * Block)); queue.Write(Numbered(4 * Block));
        var inPlace = queue.Read(handed, 0) && handed[0] == 6 * Block - Back;
        // Running short again and again means the place is wrong: it is found afresh, with a little in hand.
        var again = !queue.Read(handed, 0) && !queue.Read(handed, 0);
        queue.Write(Numbered(5 * Block)); var waits = !queue.Read(handed, 0);
        queue.Write(Numbered(6 * Block)); queue.Write(Numbered(7 * Block)); var afresh = queue.Read(handed, 0) && handed[0] == 8 * Block - Back;
        // Speakers whose clock runs fast are read that much faster: between the samples, and exactly.
        // A low note and a high one together: the high one is where reading between samples goes wrong first.
        var fast = new SpeakerQueue(); var taken = new float[Block]; const double Faster = 1.0005; double worst = 0;
        static double Notes(double at) => 0.5 * Math.Sin(at * 2 * Math.PI * 1000 / 48000) + 0.5 * Math.Sin(at * 2 * Math.PI * 9000 / 48000);
        float[] Whistle(int from) => Enumerable.Range(from, Block).Select(v => (float)Notes(v)).ToArray();
        for (var b = 0; b < 4; b++) fast.Write(Whistle(b * Block));
        for (var b = 0; b < 200; b++)
        {
            fast.Write(Whistle((b + 4) * Block));
            if (!fast.Read(taken, 0, Faster)) { worst = 1; break; }
            // The first block starts two and a half blocks and eight samples behind the five written.
            var from = 5 * Block - Back + b * Block * Faster;
            for (var i = 0; i < Block; i++) worst = Math.Max(worst, Math.Abs(taken[i] - Notes(from + i * Faster)));
        }
        check(worst < 1e-3, $"speakers on a faster clock are read that much faster, between the samples (off by at most {worst:E1})");
        check(inPlace && again && waits && afresh, $"one late delivery of the speakers' sound does not move it against the microphone ({inPlace}, {again}, {waits}, {afresh})");

        // The microphone's buffers are handed to a thread of their own: however long one takes to process, handing the
        // next over takes no time, so Windows never has to drop audio, and they are still processed in order.
        var order = new List<short>();
        using (var pump = new VoiceAudio.Pump((samples, count) => { if (order.Count == 0) Thread.Sleep(150); lock (order) order.Add(samples[0]); }))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (short i = 0; i < 20; i++) pump.Post(new[] { i, i });
            var handedIn = clock.ElapsedMilliseconds;
            for (var wait = 0; wait < 100 && order.Count < 20; wait++) Thread.Sleep(20);
            check(handedIn < 100 && order.SequenceEqual(Enumerable.Range(0, 20).Select(i => (short)i)) && pump.SlowestMs >= 100,
                $"a slow moment in the processing never holds up the microphone ({handedIn} ms to hand over 20 buffers while the first took {pump.SlowestMs:F0} ms)");
        }

        // Whether the speakers reach the microphone: none is assumed until an echo is found, which takes a second or
        // two; it is taken to be gone after ten seconds of the speakers playing without it, and silence from the
        // speakers says nothing either way.
        EchoPresence.Forget();
        var presence = new EchoPresence();
        var film = Noise(Rate * 22, 0.05, 11); var room = Noise(Rate * 22, 0.01, 12);
        void Feed(EchoPresence target, int from, int to, Func<int, float> microphone, bool playing = true)
        {
            var played = new float[Block]; var heard = new float[Block];
            for (var at = from; at + Block <= to; at += Block)
            {
                for (var i = 0; i < Block; i++) { played[i] = playing ? film[at + i] / 32768f : 0; heard[i] = microphone(at + i); }
                target.Speakers(played, 1); target.Microphone(heard);
            }
        }
        Feed(presence, 0, Rate * 4, at => room[at] / 32768f);
        check(!presence.Present, $"speakers that do not reach the microphone are not taken for an echo (similarity {presence.Latest.Similarity:F2})");
        Feed(presence, Rate * 4, Rate * 6, at => room[at] / 32768f + 0.3f * film[at - 2400] / 32768f);
        check(presence.Present && Math.Abs(presence.Latest.DelayMs - 50) <= 2, $"an echo is found within two seconds, at its delay (similarity {presence.Latest.Similarity:F2}, {presence.Latest.DelayMs} ms)");
        Feed(presence, Rate * 6, Rate * 8, at => room[at] / 32768f, playing: false);
        check(presence.Present, "silent speakers are no sign that the echo has gone");
        check(new EchoPresence().Present, "what was found carries over to the next time voice opens");
        Feed(presence, Rate * 8, Rate * 16, at => room[at] / 32768f);
        var held = presence.Present;
        Feed(presence, Rate * 16, Rate * 22, at => room[at] / 32768f);
        check(held && !presence.Present, "an echo that has stopped is taken to be gone after ten seconds of playing, not before");
        // A single short sound that happens to match one in the movie, with silence around it, is not an echo: a
        // second with only a moment of sound in it resembles anything.
        EchoPresence.Forget();
        var chance = new EchoPresence();
        float Note(int at, int start) => at >= start && at < start + Rate * 15 / 100 ? 0.2f * MathF.Sin(2 * MathF.PI * 300 * (at - start) / Rate) : 0;
        for (var at = 0; at + Block <= Rate * 4; at += Block)
        {
            var played = new float[Block]; var heard = new float[Block];
            for (var i = 0; i < Block; i++) { played[i] = film[at + i] / 32768f * 0.2f + Note(at + i, Rate * 2); heard[i] = Note(at + i, Rate * 2 + 2400) + (float)(random.NextDouble() - 0.5) * 2e-4f; }
            chance.Speakers(played, 1); chance.Microphone(heard);
        }
        check(!chance.Present, "one short sound like one in the movie, with silence around it, is not taken for an echo");
        EchoPresence.Forget();
    }
    static double Rms(float[] samples) => Math.Sqrt(samples.Average(s => (double)s * s));

    // How the processed audio differs from the voice that went in, over the frames that carry it (within 12 dB of
    // its level) once the level has settled: the gain, how far the gain moves between such frames (the spread from
    // the 10th to the 90th percentile, in dB), the share of them more than 6 dB below the usual gain, and the
    // change of the low and the high band against the middle one.
    internal sealed record Kept(double Gain, double Moves, double Dips, double Low, double High);
    internal static Kept Compare(short[] voice, short[] processed, int withinMs = 80)
    {
        // The processing delays the audio by a known amount at most; find it within 80 ms (real devices add more).
        var lag = Enumerable.Range(0, withinMs).Select(ms => ms * Rate / 1000).MaxBy(shift =>
        {
            double sum = 0;
            for (var i = Rate * 2; i < Math.Min(voice.Length, processed.Length - shift) && i < Rate * 5; i += 3) sum += voice[i] * (double)processed[i + shift];
            return Math.Abs(sum);
        });
        var frames = Math.Min(voice.Length, processed.Length - lag) / Frame;
        double[] Band(short[] samples, int offset, double from, double to)
        {
            var high = new Filter(from, true); var low = new Filter(to, false);
            var energy = new double[frames];
            for (var i = 0; i < frames * Frame; i++) { var v = low.Run(high.Run(samples[offset + i] / 32768.0)); energy[i / Frame] += v * v; }
            return energy;
        }
        double[] before = Band(voice, 0, 300, 4000), after = Band(processed, lag, 300, 4000);
        var top = before.OrderDescending().Take(Math.Max(1, frames / 10)).Average();
        var carried = Enumerable.Range(0, frames).Where(f => f * Frame > Rate * 3 && before[f] > top / 16).ToArray();
        var gains = carried.Select(f => 10 * Math.Log10((after[f] + 1e-12) / before[f])).Order().ToArray();
        double At(double share) => gains[(int)(share * (gains.Length - 1))];
        double Change(double from, double to)
        {
            double[] a = Band(voice, 0, from, to), b = Band(processed, lag, from, to);
            return 10 * Math.Log10(carried.Sum(f => b[f]) / carried.Sum(f => a[f])) - 10 * Math.Log10(carried.Sum(f => after[f]) / carried.Sum(f => before[f]));
        }
        return new(At(0.5), At(0.9) - At(0.1), gains.Count(g => g < At(0.5) - 6) / (double)gains.Length, Change(100, 1000), Change(4000, 10000));
    }
    sealed class Filter
    {
        readonly double b0, b1, b2, a1, a2; double z1, z2;
        public Filter(double hz, bool high)
        {
            var w = 2 * Math.PI * hz / Rate; var cos = Math.Cos(w); var alpha = Math.Sin(w) / Math.Sqrt(2); var a0 = 1 + alpha; var mid = high ? 1 + cos : 1 - cos;
            b0 = b2 = mid / 2 / a0; b1 = (high ? -mid : mid) / a0; a1 = -2 * cos / a0; a2 = (1 - alpha) / a0;
        }
        public double Run(double x) { var y = b0 * x + z1; z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y; return y; }
    }
    // The voice as a microphone across a room hears it: the direct sound and half a second of decaying reflections
    // that carry more energy than it does.
    static short[] Room(short[] voice)
    {
        var random = new Random(21);
        var response = new double[Rate / 2];
        for (var i = 240; i < response.Length; i++) response[i] = (random.NextDouble() * 2 - 1) * Math.Exp(-6.9 * i / (Rate * 0.45)) * 0.06;
        response[0] = 1;
        // The reflections are dense, so only every eighth is used, scaled to the same energy.
        var result = new double[voice.Length];
        for (var tap = 0; tap < response.Length; tap += tap == 0 ? 240 : 8)
        {
            var weight = tap == 0 ? 1 : response[tap] * Math.Sqrt(8);
            for (var i = tap; i < voice.Length; i++) result[i] += voice[i - tap] * weight;
        }
        var scale = Math.Sqrt(voice.Sum(s => (double)s * s) / result.Sum(v => v * v));
        return result.Select(v => (short)Math.Clamp(v * scale, short.MinValue, short.MaxValue)).ToArray();
    }
    // A movie's sound that has nothing to do with the voice: another, higher and faster voice made from the same
    // speech, over notes of music.
    static short[] Film(short[] speech, int length, bool music = true)
    {
        var random = new Random(31);
        var result = new double[length];
        for (var i = 0; i < length; i++)
        {
            var position = (i * 1.19 + Rate * 3) % (speech.Length - 1); var at = (int)position;
            result[i] = speech[at] + (speech[at + 1] - speech[at]) * (position - at);
        }
        for (var start = 0; music && start < length;)
        {
            var note = (int)(Rate * (0.2 + random.NextDouble() * 0.4)); var pitch = 110 * Math.Pow(2, random.Next(24) / 12.0);
            for (var i = 0; i < note && start + i < length; i++)
            {
                var t = (double)i / Rate;
                result[start + i] += 2500 * Math.Exp(-t * 3) * (Math.Sin(2 * Math.PI * pitch * t) + 0.5 * Math.Sin(4 * Math.PI * pitch * t) + 0.25 * Math.Sin(6 * Math.PI * pitch * t));
            }
            start += note;
        }
        return result.Select(v => (short)Math.Clamp(v, short.MinValue, short.MaxValue)).ToArray();
    }


    sealed record Result(double[] Levels, List<int> SentFrames, short[] Audio, bool EchoPresent, float Similarity, bool Subtracted, string Echo)
    {
        public int Sent => SentFrames.Count;
        public int SentAfter(int seconds) => SentFrames.Count(f => f >= seconds * Rate / Frame);
        // Mean power of the processed frames between two times, in dBFS.
        public double Level(double from, double to)
        {
            var frames = Levels[(int)(from * Rate / Frame)..Math.Min(Levels.Length, (int)(to * Rate / Frame))];
            return 10 * Math.Log10(frames.Average(db => Math.Pow(10, db / 10)) + 1e-10);
        }
    }
    // Runs samples through the processor in 20 ms writes, as the microphone delivers them, with an optional echo reference.
    static Result Process(VoiceAudio.Processing mode, short[] microphone, short[]? speakers = null)
    {
        var capture = new VoiceCapture { Transmitting = true };
        var sent = new List<int>(); var frame = 0;
        capture.Frame += _ => sent.Add(frame);
        VoiceProcessor.InputLevel.Forget(); EchoPresence.Forget();
        using var processor = new VoiceProcessor(capture, mode);
        var audio = new List<short>(microphone.Length);
        processor.Processed = block => audio.AddRange(block);
        var levels = new double[microphone.Length / Frame];
        var similarity = 0f; int late = 0, subtracted = 0, suppressed = 0;
        for (frame = 0; frame < levels.Length; frame++)
        {
            if (speakers is not null) processor.Reference(speakers.AsSpan(frame * Frame, Frame));
            processor.Write(microphone.AsSpan(frame * Frame, Frame));
            levels[frame] = capture.LevelDb;
            similarity = Math.Max(similarity, processor.EchoEvidence.Similarity);
            if (frame >= levels.Length / 2) { late++; if (processor.EchoSubtracted) subtracted++; else if (processor.EchoCancelled) suppressed++; }
        }
        return new(levels, sent, audio.ToArray(), processor.EchoPresent, similarity, processor.EchoSubtracted, FormattableString.Invariant($"echo {processor.EchoDb:F0} dBFS, voice {processor.SpeechDb:F0} dBFS, subtraction working {processor.SubtractionDb:F0} dB; in the second half subtracted {100 * subtracted / Math.Max(late, 1)}% and suppressed {100 * suppressed / Math.Max(late, 1)}% of the time"));
    }
    // The level of the loudest tenth of the samples: what a voice's level means, ignoring its pauses.
    static double ActiveDb(short[] samples)
    {
        var loud = samples.Select(s => Math.Abs((double)s)).OrderDescending().Take(Math.Max(1, samples.Length / 10)).ToArray();
        return 20 * Math.Log10(Math.Sqrt(loud.Average(v => v * v)) / 32768 + 1e-9);
    }
    static double Level(short[] samples) => 10 * Math.Log10(samples.Average(s => s / 32768.0 * (s / 32768.0)) + 1e-10);
    // Fan-like noise: white noise through a gentle low-pass, so most of its energy sits where speech does.
    static short[] Noise(int length, double amplitude, int seed)
    {
        var random = new Random(seed); var result = new short[length]; double low = 0;
        for (var i = 0; i < length; i++) { low = low * 0.7 + (random.NextDouble() * 2 - 1) * 0.3; result[i] = (short)(low * amplitude * 2.5 * 32767); }
        return result;
    }
    // One of a family of impact sounds, chosen by seed: a body ringing low, a sharp contact click, and bounces.
    static short[] Impact(int seed)
    {
        var random = new Random(seed);
        var result = new double[Rate * 3];
        var low = random.Next(4) switch { 0 => random.NextDouble() * 60 + 50, 1 => random.NextDouble() * 150 + 100, 2 => random.NextDouble() * 300 + 250, _ => random.NextDouble() * 3000 + 2000 };
        var amplitude = random.NextDouble() * 0.6 + 0.3; var click = random.NextDouble() * 2; var decay = random.NextDouble() * 40 + 15;
        var at = Rate / 2 + random.Next(Rate / 4);
        for (var bounce = 0; bounce < 1 + random.Next(4) && at < result.Length; bounce++)
        {
            var scale = Math.Pow(0.45, bounce);
            for (var i = 0; i < Rate / 2 && at + i < result.Length; i++)
            {
                var t = (double)i / Rate;
                result[at + i] += amplitude * scale * (Math.Sin(2 * Math.PI * low * t) * Math.Exp(-t * decay) + 0.5 * Math.Sin(2 * Math.PI * low * 2.7 * t) * Math.Exp(-t * decay * 1.6)
                    + (random.NextDouble() * 2 - 1) * click * Math.Exp(-t * 300));
            }
            at += (int)(Rate * (0.12 + random.NextDouble() * 0.25));
        }
        return result.Select(v => (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue)).ToArray()[..(result.Length / Frame * Frame)];
    }
    static short[] Mix(short[] a, short[] b) => a.Select((s, i) => (short)Math.Clamp(s + b[i], short.MinValue, short.MaxValue)).ToArray();
    static short[] Repeat(short[] samples, int length) => Enumerable.Range(0, length).Select(i => samples[i % samples.Length]).ToArray();

    // Synthesized speech at 48 kHz with a second of silence on each side: English where Windows has an English voice,
    // otherwise the first voice it has, speaking its own language. Null without any voice.
    static async Task<short[]?> Speak()
    {
        var voices = SpeechSynthesizer.AllVoices;
        var voice = voices.FirstOrDefault(v => v.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) ?? voices.FirstOrDefault();
        if (voice is null) return null;
        Console.WriteLine($"Voice: {voice.DisplayName} ({voice.Language})");
        var text = voice.Language.StartsWith("ru-", StringComparison.OrdinalIgnoreCase)
            ? "Всем меня слышно? Давайте включим следующую серию, когда попкорн будет готов. По-моему, концовка прошлой была лучшей частью всего сезона."
            : "Can everyone hear me? Let's start the next episode once the popcorn is ready. I think the ending of the last one was the best part of the whole season.";
        using var synthesizer = new SpeechSynthesizer { Voice = voice };
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        using var input = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
        using var buffer = new MemoryStream(); await input.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        int rate = 0, channels = 0, offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4); var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(bytes, offset + 10); rate = BitConverter.ToInt32(bytes, offset + 12); }
            if (id == "data" && rate > 0 && channels > 0)
            {
                var frames = Math.Min(size, bytes.Length - offset - 8) / 2 / channels;
                var length = (int)((long)frames * Rate / rate);
                var result = new short[length + 2 * Rate];
                for (var i = 0; i < length; i++)
                {
                    var position = (double)i * rate / Rate; var at = (int)position; var next = Math.Min(at + 1, frames - 1);
                    var a = BitConverter.ToInt16(bytes, offset + 8 + at * 2 * channels); var b = BitConverter.ToInt16(bytes, offset + 8 + next * 2 * channels);
                    result[Rate + i] = (short)(a + (b - a) * (position - at));
                }
                return result[..(result.Length / Frame * Frame)];
            }
            offset += 8 + size + (size & 1);
        }
        return null;
    }
}
