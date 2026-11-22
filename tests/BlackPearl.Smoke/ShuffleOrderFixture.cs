using BlackPearl.Core;

// Shuffle plays every video once before any repeats; Previous walks back through what played.
static class ShuffleOrderFixture
{
    public static void Run(Action<bool, string> check)
    {
        string[] pool = ["e1", "e2", "e3", "e4", "e5", "e6"];
        bool Any(string _) => true;

        // Without Loop all: each video once, then nothing.
        var order = new ShuffleOrder(new Random(7));
        var current = "e1"; var played = new List<string> { current };
        for (string? next; (next = order.Next("show", current, pool, false, Any)) is not null;) { played.Add(next); current = next; }
        check(played.Count == pool.Length && played.Distinct().Count() == pool.Length, "shuffle plays every episode once before running out");
        check(!played.SequenceEqual(pool), "the shuffled order is not the show's order");
        check(order.Next("show", current, pool, false, Any) is null, "without Loop all shuffle stops after the last unplayed episode");

        // Asked again, Next keeps its pick, so the up-next card, Next and prefetching agree.
        order = new ShuffleOrder(new Random(3));
        var pick = order.Next("show", "e1", pool, false, Any);
        check(pick is not null && pick != "e1" && Enumerable.Range(0, 5).All(_ => order.Next("show", "e1", pool, false, Any) == pick), "the next pick stays the same until it plays");
        check(order.Next("show", "e1", pool, false, id => id != pick) is { } other && other != pick, "a pick that can no longer play is replaced");

        // Previous walks back through what played, and Next retraces it.
        order = new ShuffleOrder(new Random(11));
        var path = new List<string> { "e3" };
        for (var i = 0; i < 3; i++) path.Add(order.Next("show", path[^1], pool, false, Any)!);
        var back = order.Previous("show", path[3], pool, Any);
        check(back == path[2], "Previous goes to the episode that played before");
        back = order.Previous("show", back!, pool, Any);
        check(back == path[1], "Previous again goes one further back");
        check(order.Next("show", back!, pool, false, Any) == path[2], "Next after Previous retraces the path that played");
        back = order.Previous("show", path[2], pool, Any);
        back = order.Previous("show", back!, pool, Any);
        check(back == "e3" && order.Previous("show", "e3", pool, Any) is null, "the first video of the shuffle has nothing before it");
        // Unplayable videos are skipped on the way back.
        order = new ShuffleOrder(new Random(5));
        path = ["e1"];
        for (var i = 0; i < 2; i++) path.Add(order.Next("show", path[^1], pool, false, Any)!);
        check(order.Previous("show", path[2], pool, id => id != path[1]) == path[0], "Previous skips a video that can no longer play");

        // A video picked by hand counts as played and starts a new path.
        order = new ShuffleOrder(new Random(1));
        order.Next("show", "e1", pool, false, Any);
        order.Played("show", "e4");
        current = "e4"; played = ["e1", "e4"];
        for (string? next; (next = order.Next("show", current, pool, false, Any)) is not null;) { played.Add(next); current = next; }
        check(played.Count == pool.Length && played.Distinct().Count() == pool.Length, "a hand-picked episode counts as played in the round");

        // With Loop all: a new round after every video has played, never starting with the one that just ended.
        order = new ShuffleOrder(new Random(9));
        current = "e1"; played = [current];
        for (var i = 0; i < pool.Length * 3 - 1; i++) { current = order.Next("show", current, pool, true, Any)!; played.Add(current); }
        var rounds = played.Chunk(pool.Length).ToArray();
        check(rounds.All(round => round.Distinct().Count() == pool.Length), "with Loop all each round plays every episode once");
        check(Enumerable.Range(1, played.Count - 1).All(i => played[i] != played[i - 1]), "a new round never repeats the episode that just ended");
        check(order.Next("show", "e1", ["e1"], true, Any) is null, "a list of one has nothing else to shuffle to");

        // Another list starts a new order.
        order = new ShuffleOrder(new Random(2));
        for (current = "e1"; order.Next("show", current, pool, false, Any) is { } next;) current = next;
        check(order.Next("queue:a", "e1", pool, false, Any) is not null && order.Scope == "queue:a", "switching to another list starts a fresh shuffle");
        check(order.PlayedCount(pool) == 1, "a fresh shuffle counts only the playing video");
    }
}
