using System.Diagnostics;
using System.Globalization;

namespace BlackPearl.Core;

// Cuts ffmpeg's transport stream into the pieces of a RoomStream. Conversion forces a keyframe at the
// first frame of every piece and nowhere else, so a piece begins at each keyframe, and which piece it
// is follows from the keyframe's timestamp rather than from counting.
internal sealed class TransportSegmenter(int segmentMs, double originSeconds)
{
    private const int Packet = 188, VideoPid = 0x100, MapPid = 0x1000;
    private readonly byte[] carry = new byte[Packet];
    private int carried;
    // The latest program tables, repeated at the start of every piece so each plays on its own.
    private readonly byte[] tables = new byte[Packet * 2];
    private bool association, map;
    private MemoryStream? current;
    public int Index { get; private set; } = -1;

    public void Feed(ReadOnlySpan<byte> data, Action<int, byte[]> completed)
    {
        if (carried > 0)
        {
            var take = Math.Min(Packet - carried, data.Length);
            data[..take].CopyTo(carry.AsSpan(carried)); carried += take; data = data[take..];
            if (carried < Packet) return;
            carried = 0; Accept(carry, completed);
        }
        for (; data.Length >= Packet; data = data[Packet..]) Accept(data[..Packet], completed);
        data.CopyTo(carry); carried = data.Length;
    }

    // The video ended: what was collected is its last piece.
    public void Finish(Action<int, byte[]> completed)
    {
        if (current is { Length: > 0 } last) completed(Index, last.ToArray());
        current = null;
    }

    private void Accept(ReadOnlySpan<byte> packet, Action<int, byte[]> completed)
    {
        if (packet[0] != 0x47) throw new InvalidDataException("The converted stream is not a transport stream.");
        var pid = (packet[1] & 0x1F) << 8 | packet[2];
        if (pid == 0) { packet.CopyTo(tables); association = true; }
        else if (pid == MapPid) { packet.CopyTo(tables.AsSpan(Packet)); map = true; }
        else if (pid == VideoPid && Keyframe(packet) is { } timestamp)
        {
            var index = (int)Math.Floor((timestamp / 90000.0 - Ffmpeg.TimestampOffsetSeconds - originSeconds) * 1000 / segmentMs + 0.0001);
            if (index > Index || current is null)
            {
                if (current is { Length: > 0 } done) completed(Index, done.ToArray());
                Index = Math.Max(index, 0); current = new MemoryStream();
                if (association && map) current.Write(tables);
            }
        }
        current?.Write(packet);
    }

    // The timestamp of the frame that starts in this packet, when it is a keyframe.
    private static long? Keyframe(ReadOnlySpan<byte> packet)
    {
        if ((packet[1] & 0x40) == 0 || (packet[3] & 0x20) == 0 || packet[4] == 0 || (packet[5] & 0x40) == 0) return null;
        var start = 5 + packet[4];
        if (start + 14 > Packet || packet[start] != 0 || packet[start + 1] != 0 || packet[start + 2] != 1 || (packet[start + 7] & 0x80) == 0) return null;
        var b = packet.Slice(start + 9, 5);
        return (long)(b[0] >> 1 & 7) << 30 | (long)b[1] << 22 | (long)(b[2] >> 1) << 15 | (long)b[3] << 7 | (long)(b[4] >> 1);
    }

    // What a piece past the end of the video holds: tables and nothing to play.
    public static byte[] Empty()
    {
        var packets = new byte[Packet * 2];
        for (var i = 0; i < 2; i++)
        {
            var packet = packets.AsSpan(i * Packet, Packet);
            packet.Fill(0xFF); packet[0] = 0x47; packet[1] = 0x1F; packet[2] = 0xFF; packet[3] = 0x10;
        }
        return packets;
    }
}

// Converts a room's video to a RoomStream while it plays, and hands its pieces to viewers. One ffmpeg runs
// at a time, from the piece somebody is waiting for to the end of the video or to video already converted.
public sealed class StreamConverter : IDisposable
{
    public const int SegmentMs = 4000;
    // A piece this close ahead of the running conversion is waited for; one further away restarts it there.
    private const int NearSegments = 10;
    // Conversion stops this far past the furthest piece anybody asked for, and continues when they catch up.
    private const int AheadSegments = 75;
    // While others wait elsewhere, a run that still has waiters of its own yields after this many pieces.
    private const int SliceSegments = 8;
    private const int AnswerMs = 5000;
    private static int preferredEncoder;
    private readonly object gate = new();
    private readonly string ffmpeg;
    private readonly StreamInput input;
    private readonly SegmentStore store;
    private readonly Dictionary<int, TaskCompletionSource> produced = [];
    private readonly Dictionary<int, int> waiting = [];
    private readonly Dictionary<int, int> retries = [];
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly byte[] empty = TransportSegmenter.Empty();
    private Run? run;
    private int encoder = Volatile.Read(ref preferredEncoder), demand, last = int.MaxValue;
    private bool encoderWorked, disposed;
    public RoomStream Stream { get; }
    public string? Failure { get; private set; }
    public string Encoder => Ffmpeg.Encoders[Math.Min(encoder, Ffmpeg.Encoders.Length - 1)];
    public int ConvertedSegments => store.Count;
    // Raised once, from a background thread, when no encoder on this PC can convert the video.
    public event Action<string>? Failed;

    public StreamConverter(string ffmpeg, StreamInput input, RoomStream stream, SegmentStore store)
    { this.ffmpeg = ffmpeg; this.input = input; Stream = stream; this.store = store; }

    private sealed class Run(int start)
    {
        public readonly int Start = start;
        public int Next = start, Produced;
        public readonly CancellationTokenSource Cancellation = new();
        public Process? Process;
    }

    public static string Resource(string stream, int index, bool size = false) =>
        "stream/" + stream + "/" + index.ToString(CultureInfo.InvariantCulture) + (size ? "/size" : "");

    // What a viewer's read of "stream/<id>/<piece>" or ".../size" is answered from.
    public IMediaSource? Resolve(string resource)
    {
        var parts = resource.Split('/');
        if (parts.Length is not (3 or 4) || parts[0] != "stream" || parts[1] != Stream.Id || parts.Length == 4 && parts[3] != "size" ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= Stream.Segments) return null;
        return new Piece(this, index, parts.Length == 4);
    }

    private sealed class Piece(StreamConverter converter, int index, bool size) : IMediaSource
    {
        public SharedMedia Media { get; } = new(converter.Stream.Id, "", 1, ".ts");
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            // A reader is answered within a few seconds either way, so one piece that takes long to convert never looks
            // like a lost connection; told to ask again, it does.
            int length;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(AnswerMs);
                try { length = await converter.LengthAsync(index, limit.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PeerBusyException("That part of the stream is still being converted."); }
            }
            if (size) return offset == 0 && count == sizeof(long) ? BitConverter.GetBytes((long)length) : throw new InvalidDataException("Invalid size request");
            return converter.Read(index, offset, count) ?? throw new IOException("That part of the stream is unavailable.");
        }
    }

    public byte[]? Read(int index, long offset, int count)
    {
        if (store.Has(index)) return store.Read(index, offset, count);
        return index > Volatile.Read(ref last) && offset >= 0 && count >= 0 && offset + count <= empty.Length ? empty.AsSpan((int)offset, count).ToArray() : null;
    }

    // Waits until the piece is converted and returns its size.
    public async Task<int> LengthAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= Stream.Segments) throw new IOException("The stream has no such part.");
        while (true)
        {
            if (store.Length(index) is > 0 and var length) return length;
            TaskCompletionSource arrival;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (Failure is not null) throw new IOException(Failure);
                if (index > last) return empty.Length;
                if (store.Has(index)) continue;
                if (!store.Accepting) throw new IOException("The disk has no room to convert the video.");
                if (!produced.TryGetValue(index, out arrival!)) produced[index] = arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting[index] = waiting.GetValueOrDefault(index) + 1;
                demand = Math.Max(demand, index);
                if (run is null) Start(index);
                else if (index < run.Next || index > run.Next + NearSegments) { if (!Busy(run)) Start(index); }
            }
            Wake();
            try { await arrival.Task.WaitAsync(ct); }
            finally
            {
                lock (gate)
                {
                    if (waiting.GetValueOrDefault(index) <= 1) waiting.Remove(index); else waiting[index]--;
                    // Nobody is left waiting for what the run is converting: one waiting elsewhere gets it now.
                    if (!disposed && run is { } current && !Busy(current) && Elsewhere(current) is { } other) Start(other);
                }
            }
        }
    }

    private bool Busy(Run current) => waiting.Keys.Any(index => index >= current.Next && index <= current.Next + NearSegments);
    private int? Elsewhere(Run current)
    {
        int? lowest = null;
        foreach (var index in waiting.Keys)
            if ((index < current.Next || index > current.Next + NearSegments) && !store.Has(index) && (lowest is null || index < lowest)) lowest = index;
        return lowest;
    }
    private void Wake()
    {
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }

    // Called with the lock held.
    private void Start(int index)
    {
        Stop();
        var next = new Run(index); run = next; demand = Math.Max(demand, index);
        _ = Task.Run(() => Convert(next));
    }
    private void Stop()
    {
        if (run is not { } old) return;
        run = null;
        old.Cancellation.Cancel();
        try { if (old.Process is { HasExited: false } process) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private async Task Convert(Run current)
    {
        var token = current.Cancellation.Token;
        var name = Encoder; var errors = ""; var exit = -1; var ended = false;
        var segmenter = new TransportSegmenter(Stream.SegmentMs, input.Probe.StartSeconds);
        void Completed(int index, byte[] piece)
        {
            if (index < current.Next || index >= Stream.Segments || token.IsCancellationRequested) return;
            // A stretch of the video without a single frame leaves pieces with nothing in them.
            var kept = true;
            for (var skipped = current.Next; skipped < index && kept; skipped++) kept = store.TryAdd(skipped, empty);
            kept = kept && store.TryAdd(index, piece);
            lock (gate)
            {
                if (run != current) return;
                if (!kept) { Fail("The disk has no room to convert the video."); return; }
                encoderWorked = true; current.Next = index + 1; current.Produced++;
                foreach (var arrived in produced.Keys.Where(key => key <= index && store.Has(key)).ToArray()) { produced[arrived].TrySetResult(); produced.Remove(arrived); }
                var elsewhere = Elsewhere(current);
                // Reached video that is already converted, or the end.
                if (current.Next >= Stream.Segments || store.Has(current.Next)) { Stop(); if (waiting.Keys.Where(key => !store.Has(key)).DefaultIfEmpty(-1).Min() is >= 0 and var wanted) Start(wanted); }
                else if (elsewhere is { } other && (!Busy(current) || current.Produced >= SliceSegments)) Start(other);
            }
        }
        try
        {
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in Ffmpeg.ConversionArguments(input, Stream, name, current.Start)) info.ArgumentList.Add(argument);
            PlaybackDiagnostics.Record("stream-convert", new { state = "run", stream = Stream.Id, from = current.Start, encoder = name });
            using var process = Process.Start(info) ?? throw new IOException("ffmpeg did not start.");
            lock (gate) { if (run != current) { try { process.Kill(true); } catch (InvalidOperationException) { } return; } current.Process = process; }
            // The video being watched on this PC comes first.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = process.StandardOutput.BaseStream; var buffer = new byte[188 * 512];
            while (true)
            {
                // Not reading holds ffmpeg back once it is far enough ahead of what anybody asked for.
                while (!token.IsCancellationRequested && current.Next > Volatile.Read(ref demand) + AheadSegments) await wake.WaitAsync(500, token);
                var read = await output.ReadAsync(buffer, token);
                if (read == 0) break;
                segmenter.Feed(buffer.AsSpan(0, read), Completed);
            }
            await process.WaitForExitAsync(token);
            exit = process.ExitCode; errors = await stderr;
            if (exit == 0) { segmenter.Finish(Completed); ended = true; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException) { errors = ex.Message; }
        lock (gate)
        {
            if (run != current || disposed) return;
            run = null;
            if (ended)
            {
                // The video is shorter than its stated length: the remaining pieces hold nothing.
                if (current.Next < Stream.Segments) Volatile.Write(ref last, Math.Min(last, current.Next - 1));
                foreach (var arrived in produced.Keys.Where(key => key > last || store.Has(key)).ToArray()) { produced[arrived].TrySetResult(); produced.Remove(arrived); }
                if (waiting.Keys.Where(key => key <= last && !store.Has(key)).DefaultIfEmpty(-1).Min() is >= 0 and var wanted) Start(wanted);
                return;
            }
            PlaybackDiagnostics.Record("stream-convert", new { state = "stopped", stream = Stream.Id, encoder = name, exit, produced = current.Produced, error = Ffmpeg.Tail(errors) });
            // An encoder that never produced anything is not available on this PC; the next one is tried.
            if (!encoderWorked && current.Produced == 0 && encoder + 1 < Ffmpeg.Encoders.Length) { encoder++; Start(current.Start); return; }
            var attempts = retries[current.Next] = retries.GetValueOrDefault(current.Next) + 1;
            if (encoderWorked && attempts <= 2) { Start(current.Next); return; }
            Fail(encoderWorked ? "The video could not be converted past this point." : "This PC has no video encoder that can convert the video.");
        }
    }

    // Called with the lock held.
    private void Fail(string message)
    {
        Failure = message; Stop();
        foreach (var arrival in produced.Values) arrival.TrySetException(new IOException(message));
        produced.Clear();
        PlaybackDiagnostics.Record("stream-convert", new { state = "failed", stream = Stream.Id, message });
        if (Failed is { } handler) _ = Task.Run(() => handler(message));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; Stop();
            if (encoderWorked) Volatile.Write(ref preferredEncoder, encoder);
            foreach (var arrival in produced.Values) arrival.TrySetException(new ObjectDisposedException(nameof(StreamConverter)));
            produced.Clear();
        }
        Wake();
    }
}
