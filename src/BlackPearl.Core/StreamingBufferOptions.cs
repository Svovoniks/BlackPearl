namespace BlackPearl.Core;

// A streamed video is buffered to its end in a temporary file (StreamCache). These bounds
// apply only when that file cannot be used, such as a full disk: the buffer is then held in
// memory and covers this much video past the playback position.
public static class StreamingBufferOptions
{
    public const int MemorySeconds = 30;
    public const long MaximumReadAheadBytes = 256L * 1024 * 1024;

    // Read-ahead is not a start threshold: VLC starts from its short RTT-based network
    // cache. Average bitrate plus a quarter covers ordinary variable-bitrate peaks.
    public static long ReadAheadBytes(long mediaBytes, long durationMs, int seconds = MemorySeconds) =>
        mediaBytes <= 0 || durationMs <= 0 ? 0 :
        (long)Math.Min(MaximumReadAheadBytes, mediaBytes * 1.25 * seconds * 1000 / durationMs);
}
