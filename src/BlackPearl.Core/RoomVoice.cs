using System.Collections.Concurrent;

namespace BlackPearl.Core;

// Voice chat over the room's direct connections. Each speaker sends its microphone frames straight to everyone else
// in voice, so voice carries on whoever is streaming and when the streamer changes. Who is in voice is part of the
// room state the streamer keeps. Frames bypass the control loop for latency, so sending and receiving read an immutable
// routing table that is replaced whenever the room state changes.
public sealed partial class RoomClient
{
    private IVoiceSink? voiceOutput;
    // Receives the room's voice while the local user is in voice.
    public IVoiceSink? VoiceOutput { get => Volatile.Read(ref voiceOutput); set => Volatile.Write(ref voiceOutput, value); }
    // Bit 1: in voice; bit 2: microphone muted.
    private int voiceDesired;
    // Talkers: the slot of everyone else whose microphone is on. Listeners: everyone else in voice.
    private sealed record VoiceRoutes(IReadOnlyDictionary<string, byte> Talkers, string[] Listeners, byte Slot, bool Talking);
    private VoiceRoutes voiceRoutes = new(new Dictionary<string, byte>(), [], 0, false);
    private sealed class VoiceBudget { public double Tokens = 50; public long Refilled = Environment.TickCount64; }
    private readonly ConcurrentDictionary<string, VoiceBudget> voiceBudgets = new();
    private (string Key, long At)? voiceSent;
    public bool InVoice => (Volatile.Read(ref voiceDesired) & 1) != 0;
    // The room's state is known, and whoever keeps it supports voice.
    public bool VoiceAvailable => IsConnected && Snapshot?.Voice is not null && (Identity?.Host == true || directConnected.Task.IsCompletedSuccessfully);
    public VoiceMember? VoiceState(string peer) => Snapshot?.Voice?.FirstOrDefault(v => v.Peer == peer);
    public void SetVoice(bool joined, bool muted)
    {
        var state = (joined ? 1 : 0) | (muted ? 2 : 0);
        if (Interlocked.Exchange(ref voiceDesired, state) == state) return;
        PlaybackDiagnostics.Record("voice-state", new { room = diagnosticId, joined, muted });
        Post(new("voice-local"));
    }
    private void ApplyLocalVoice()
    {
        if (Identity?.Host == true) { if (coordinator is not null) ApplyHostCommand(Identity.Peer, new("voice", Number: Volatile.Read(ref voiceDesired))); }
        else ReconcileVoice();
    }
    // Members repeat their state until the streamer's snapshot reflects it, including after a reconnect replaced the
    // connection or the streaming role moved: quickly at first so joining voice is not held up by one lost message,
    // then every few seconds.
    private int voiceRetries;
    private void ReconcileVoice()
    {
        if (Identity is not { Host: false } identity || hostPeer is not { } host || !negotiated.ContainsKey(host) || Snapshot?.Voice is not { } members) return;
        var desired = Volatile.Read(ref voiceDesired);
        var mine = members.FirstOrDefault(v => v.Peer == identity.Peer);
        if (mine is not null && (mine.Joined ? 1 : 0) + (mine.Muted ? 2 : 0) == desired) { voiceSent = null; voiceRetries = 0; return; }
        var key = peers.GetValueOrDefault(host)?.DiagnosticId + ":" + desired;
        var interval = voiceRetries < 6 ? 400 : 3000;
        if (voiceSent is { } sent && sent.Key == key && Environment.TickCount64 - sent.At < interval) return;
        if (voiceSent is { } previous && previous.Key == key) voiceRetries++; else voiceRetries = 0;
        voiceSent = (key, Environment.TickCount64);
        SendPeer(host, new("voice", Number: desired));
        // Check again shortly rather than waiting for the next health tick.
        _ = Task.Delay(interval + 50, lifetime.Token).ContinueWith(_ => Post(new("voice-local")), TaskContinuationOptions.OnlyOnRanToCompletion);
    }
    private void UpdateVoiceRoutes(RoomSnapshot snapshot)
    {
        if (Identity is not { } identity) return;
        var members = snapshot.Voice ?? [];
        var mine = members.FirstOrDefault(v => v.Peer == identity.Peer);
        Volatile.Write(ref voiceRoutes, new(
            members.Where(v => v.Joined && !v.Muted && v.Peer != identity.Peer).GroupBy(v => v.Peer).ToDictionary(v => v.Key, v => (byte)v.First().Slot),
            members.Where(v => v.Joined && v.Peer != identity.Peer).Select(v => v.Peer).Distinct().ToArray(),
            (byte)(mine?.Slot ?? 0), mine is { Joined: true, Muted: false }));
    }
    // Called on the capture thread with a frame whose slot is not yet set.
    public void TransmitVoice(ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref voiceDesired) != 1 || !IsConnected || frame.Length > VoicePacket.HeaderBytes + VoicePacket.MaxPayload) return;
        var routes = Volatile.Read(ref voiceRoutes);
        // The room has to know this member is in voice with its microphone on before the others accept its frames.
        if (!routes.Talking || routes.Slot == 0) return;
        Span<byte> stamped = stackalloc byte[frame.Length];
        frame.CopyTo(stamped); stamped[1] = routes.Slot;
        foreach (var listener in routes.Listeners)
            if (negotiated.ContainsKey(listener) && peers.TryGetValue(listener, out var peer)) peer.SendVoice(stamped);
    }
    // Called on a native transport thread.
    private void ReceiveVoice(string sender, PeerTransport transport, ReadOnlySpan<byte> frame)
    {
        if (!InVoice || !VoicePacket.TryRead(frame, out _, out var sequence, out var flags, out var payload)) return;
        if (!peers.TryGetValue(sender, out var current) || current != transport || !negotiated.ContainsKey(sender)) return;
        // The slot comes from the room state for the member this connection belongs to, never from the frame, so nobody
        // can speak as someone else. Only people in voice with their microphone on are heard, and none faster than
        // real time allows.
        if (!Volatile.Read(ref voiceRoutes).Talkers.TryGetValue(sender, out var assigned) || !AdmitVoice(sender)) return;
        VoiceOutput?.Receive(assigned, sequence, flags, payload);
    }
    private bool AdmitVoice(string sender)
    {
        var budget = voiceBudgets.GetOrAdd(sender, _ => new());
        lock (budget)
        {
            var now = Environment.TickCount64;
            // 50 frames per second of speech, plus room for pre-roll and network bunching.
            budget.Tokens = Math.Min(60, budget.Tokens + (now - budget.Refilled) * 0.06); budget.Refilled = now;
            if (budget.Tokens < 1) return false;
            budget.Tokens--; return true;
        }
    }
}
