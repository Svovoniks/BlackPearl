namespace BlackPearl.Core;

/// <summary>
/// Shuffle, as music players do it: every video in a list (a show's episodes or a queue) plays once, in a random
/// order, before any plays again. Previous walks back through what actually played, and Next then retraces the same
/// path. The pick for Next is kept until it plays or stops being possible, so the up-next card, the Next button and
/// prefetching all agree on it.
/// </summary>
public sealed class ShuffleOrder(Random? random = null)
{
    // Long enough for any show; older entries only matter to a very long walk back with Previous.
    private const int MaxHistory = 4000;
    private readonly Random random = random ?? Random.Shared;
    // What played, in order, and where the playing video is in it.
    private readonly List<string> history = [];
    private int position = -1;
    // Where this round began: from here on, history holds what this round has played.
    private int roundStart;
    private string? scope;
    private string? upcoming;
    // The pick starts a new round (Loop all after every video played).
    private bool upcomingStartsRound;
    // The video the player is going back to with Previous: playing it is a step back, not a new pick of a video that
    // already played. Only GoingBack sets it; asking Previous, as the player does to enable its button, doesn't.
    private string? goingBack;

    /// <summary>The list the order belongs to: a show or a queue.</summary>
    public string? Scope => scope;

    public void Reset()
    {
        history.Clear(); position = -1; roundStart = 0; scope = null; upcoming = null; upcomingStartsRound = false; goingBack = null;
    }

    /// <summary>Records that <paramref name="id"/> plays now, in the list <paramref name="scopeKey"/>. Another list starts a new order.</summary>
    public void Played(string scopeKey, string id)
    {
        if (scope != scopeKey) { Reset(); scope = scopeKey; }
        if (position >= 0 && history[position] == id) return;
        var back = goingBack == id && position > 0 && history[position - 1] == id;
        goingBack = null;
        if (back) position--;
        else if (position + 1 < history.Count && history[position + 1] == id) position++;
        else
        {
            // A video chosen any other way starts a new path from here.
            history.RemoveRange(position + 1, history.Count - position - 1);
            history.Add(id);
            position = history.Count - 1;
            if (upcomingStartsRound && upcoming == id) roundStart = position;
        }
        if (upcoming == id) { upcoming = null; upcomingStartsRound = false; }
        if (history.Count > MaxHistory)
        {
            var drop = history.Count - MaxHistory;
            history.RemoveRange(0, drop);
            position -= drop; roundStart = Math.Max(0, roundStart - drop);
        }
    }

    /// <summary>
    /// The video to play after <paramref name="currentId"/>, from <paramref name="pool"/>: one this round hasn't played,
    /// or, once every video has played, null; with <paramref name="loop"/> a new round starts instead.
    /// <paramref name="canPlay"/> may touch the disk, so it is asked only for the videos it considers.
    /// </summary>
    public string? Next(string scopeKey, string currentId, IReadOnlyList<string> pool, bool loop, Func<string, bool> canPlay)
    {
        Played(scopeKey, currentId);
        var members = pool.ToHashSet();
        // After Previous, Next goes forward along the path that played.
        while (position + 1 < history.Count)
        {
            var ahead = history[position + 1];
            if (members.Contains(ahead) && canPlay(ahead)) return ahead;
            history.RemoveAt(position + 1);
        }
        if (upcoming is { } kept && kept != currentId && members.Contains(kept) && (loop || !upcomingStartsRound) && canPlay(kept)) return kept;
        upcoming = null; upcomingStartsRound = false;
        var played = history.Skip(roundStart).ToHashSet();
        var fresh = members.Where(id => !played.Contains(id)).ToList();
        if (Pick(fresh, canPlay) is { } pick) return upcoming = pick;
        if (!loop) return null;
        // Every video has played: a new round, which doesn't start with the video that just ended.
        upcoming = Pick(members.Where(id => id != currentId).ToList(), canPlay);
        upcomingStartsRound = upcoming is not null;
        return upcoming;
    }

    /// <summary>The video that played before <paramref name="currentId"/>, skipping any that can't play now.</summary>
    public string? Previous(string scopeKey, string currentId, IReadOnlyList<string> pool, Func<string, bool> canPlay)
    {
        Played(scopeKey, currentId);
        var members = pool.ToHashSet();
        // Videos that can't play now leave the path, so the one returned is the step back that Played recognises.
        while (position > 0)
        {
            var behind = history[position - 1];
            if (members.Contains(behind) && canPlay(behind)) return behind;
            history.RemoveAt(position - 1);
            position--;
            if (roundStart > position) roundStart = position;
        }
        return null;
    }

    /// <summary>The player is about to play <paramref name="id"/>, which <see cref="Previous"/> offered: it plays as a step back.</summary>
    public void GoingBack(string id) => goingBack = id;

    private string? Pick(List<string> candidates, Func<string, bool> canPlay)
    {
        // Shuffled in full, then checked in that order, so a missing file only costs a look at the next one.
        for (var i = candidates.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        return candidates.FirstOrDefault(canPlay);
    }
}
