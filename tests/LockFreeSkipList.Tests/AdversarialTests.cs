using System.Diagnostics;
using Mobratil.Collections;
using Xunit;

namespace LockFreeSkipList.Tests;

/// <summary>
/// Regression tests for bugs found by adversarial probing. Each concurrent test is built around a writer
/// pattern that makes a specific answer IMPOSSIBLE under linearizability (e.g. key x is only ever present
/// while key y is), so a single observation of that answer is a proof of a bug — no history checker needed.
/// Bounded by STRESS_SECONDS (default 1s per structure).
/// </summary>
public class AdversarialTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("STRESS_SECONDS"), out var s) && s > 0 ? s : 1.0);

    private static int Workers => Math.Max(4, Environment.ProcessorCount);

    // ---- minimal uniform surface over the two shipped structures ----
    public interface IMap<K, V>
    {
        V this[K k] { set; }
        bool TryAdd(K k, V v); bool TryRemove(K k, out V v); bool TryGetValue(K k, out V v);
        bool TryGetFloor(K k, out KeyValuePair<K, V> e); bool TryGetCeiling(K k, out KeyValuePair<K, V> e);
        bool TryGetFirst(out KeyValuePair<K, V> e); bool TryGetLast(out KeyValuePair<K, V> e);
        bool TryRemoveFirst(out KeyValuePair<K, V> e); bool TryRemoveLast(out KeyValuePair<K, V> e);
        int Count { get; }
        void Clear();
        List<K> EnumerateKeys();
        void Validate();
    }

    private sealed class SkipMap<K, V>(ConcurrentSkipListDictionary<K, V> d) : IMap<K, V> where K : notnull
    {
        public V this[K k] { set => d[k] = value; }
        public bool TryAdd(K k, V v) => d.TryAdd(k, v); public bool TryRemove(K k, out V v) => d.TryRemove(k, out v);
        public bool TryGetValue(K k, out V v) => d.TryGetValue(k, out v);
        public bool TryGetFloor(K k, out KeyValuePair<K, V> e) => d.TryGetFloor(k, out e);
        public bool TryGetCeiling(K k, out KeyValuePair<K, V> e) => d.TryGetCeiling(k, out e);
        public bool TryGetFirst(out KeyValuePair<K, V> e) => d.TryGetFirst(out e); public bool TryGetLast(out KeyValuePair<K, V> e) => d.TryGetLast(out e);
        public bool TryRemoveFirst(out KeyValuePair<K, V> e) => d.TryRemoveFirst(out e); public bool TryRemoveLast(out KeyValuePair<K, V> e) => d.TryRemoveLast(out e);
        public int Count => d.Count; public void Clear() => d.Clear();
        public List<K> EnumerateKeys() { var l = new List<K>(); foreach (var kv in d) l.Add(kv.Key); return l; }
        public void Validate() { }
    }

    private sealed class TreeMap<K, V>(ConcurrentBTreeDictionary<K, V> d) : IMap<K, V> where K : notnull
    {
        public V this[K k] { set => d[k] = value; }
        public bool TryAdd(K k, V v) => d.TryAdd(k, v); public bool TryRemove(K k, out V v) => d.TryRemove(k, out v);
        public bool TryGetValue(K k, out V v) => d.TryGetValue(k, out v);
        public bool TryGetFloor(K k, out KeyValuePair<K, V> e) => d.TryGetFloor(k, out e);
        public bool TryGetCeiling(K k, out KeyValuePair<K, V> e) => d.TryGetCeiling(k, out e);
        public bool TryGetFirst(out KeyValuePair<K, V> e) => d.TryGetFirst(out e); public bool TryGetLast(out KeyValuePair<K, V> e) => d.TryGetLast(out e);
        public bool TryRemoveFirst(out KeyValuePair<K, V> e) => d.TryRemoveFirst(out e); public bool TryRemoveLast(out KeyValuePair<K, V> e) => d.TryRemoveLast(out e);
        public int Count => d.Count; public void Clear() => d.Clear();
        public List<K> EnumerateKeys() { var l = new List<K>(); foreach (var kv in d) l.Add(kv.Key); return l; }
        public void Validate() => d.Validate();
    }

    private static IMap<K, V> Create<K, V>(string kind, IComparer<K>? cmp = null) where K : notnull => kind switch
    {
        "skiplist" => new SkipMap<K, V>(new ConcurrentSkipListDictionary<K, V>(cmp ?? Comparer<K>.Default)),
        "bptree4" => new TreeMap<K, V>(new ConcurrentBTreeDictionary<K, V>(4, cmp)),
        "bptree64" => new TreeMap<K, V>(new ConcurrentBTreeDictionary<K, V>(64, cmp)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static TheoryData<string> All => new() { "skiplist", "bptree4", "bptree64" };
    public static TheoryData<string> Trees => new() { "bptree4", "bptree64" };

    private static void RunFor(TimeSpan budget, params Action<Func<bool>>[] bodies)
    {
        var sw = Stopwatch.StartNew();
        bool Running() => sw.Elapsed < budget;
        var threads = bodies.Select(b => new Thread(() => b(Running)) { IsBackground = true }).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(budget + TimeSpan.FromSeconds(30)), "worker hung");
    }

    // ------------------------------------------------------------------------------------------------
    //  A throwing comparer must surface its exception and leave the map fully usable. (B+ tree bug: the
    //  exception escaped while a leaf — or the whole root-down latch chain — was write-locked, wedging
    //  every later operation forever.)
    // ------------------------------------------------------------------------------------------------
    private sealed class PoisonComparer : IComparer<int>
    {
        public int Compare(int a, int b) => a < 0 || b < 0 ? throw new InvalidOperationException("poison") : a.CompareTo(b);
    }

    [Theory, MemberData(nameof(All))]
    public void Throwing_Comparer_Surfaces_And_Never_Wedges_The_Map(string kind)
    {
        var m = Create<int, int>(kind, new PoisonComparer());
        var oracle = new SortedDictionary<int, int>();
        var rng = new Random(7);
        var ops = new Action[]
        {
            () => m.TryAdd(-1, 0), () => m[-2] = 0, () => m.TryRemove(-3, out _), () => m.TryGetValue(-4, out _),
            () => m.TryGetCeiling(-5, out _), () => m.TryGetFloor(-6, out _),
        };
        for (int i = 0; i < 5000; i++)
        {
            int k = rng.Next(2000);                  // drive splits, merges and cascades at every shape
            if (rng.Next(3) == 0) { m.TryRemove(k, out _); oracle.Remove(k); }
            else { m[k] = i; oracle[k] = i; }
            if (i % 7 == 0)
            {
                var op = ops[rng.Next(ops.Length)];
                Assert.Throws<InvalidOperationException>(op);
            }
        }
        var done = Task.Run(() =>
        {
            foreach (var kv in oracle) Assert.True(m.TryGetValue(kv.Key, out var v) && v == kv.Value);
            Assert.Equal(oracle.Keys.ToList(), m.EnumerateKeys());
            Assert.Equal(oracle.Count, m.Count);
            m.Validate();
        });
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "map wedged after a comparer exception");
        done.GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------------------------------------
    //  Optimistic readers must not leak exceptions caused by feeding the comparer torn/cleared slots.
    //  (B+ tree bug: a comparer that — legitimately, keys are documented non-null — dereferences its
    //  arguments got null from a slot a concurrent remove/split had just cleared, and the NRE escaped.)
    // ------------------------------------------------------------------------------------------------
    private sealed class RefKey(int v) { public readonly int V = v; }
    private sealed class RefKeyComparer : IComparer<RefKey>
    {
        public int Compare(RefKey? a, RefKey? b) => a!.V.CompareTo(b!.V);   // assumes non-null, as documented
    }

    [Theory, MemberData(nameof(All))]
    public void Optimistic_Reads_Never_Leak_Comparer_Exceptions(string kind)
    {
        var m = Create<RefKey, int>(kind, new RefKeyComparer());
        var keys = Enumerable.Range(0, 4096).Select(i => new RefKey(i)).ToArray();
        foreach (var k in keys) m[k] = k.V;
        Exception? first = null;
        RunFor(Budget, Enumerable.Range(0, Workers).Select(t => (Action<Func<bool>>)(running =>
        {
            var r = new Random(t);
            try
            {
                while (running())
                    for (int i = 0; i < 256; i++)
                    {
                        var k = keys[r.Next(keys.Length)];
                        switch (r.Next(5))
                        {
                            case 0: m.TryRemove(k, out _); break;
                            case 1: m[k] = 1; break;
                            case 2: m.TryGetValue(k, out _); break;
                            case 3: m.TryGetCeiling(k, out _); break;
                            default: m.TryGetFloor(k, out _); break;
                        }
                    }
            }
            catch (Exception e) { Interlocked.CompareExchange(ref first, e, null); }
        })).ToArray());
        Assert.Null(first);
    }

    // ------------------------------------------------------------------------------------------------
    //  Floor / Ceiling linearizability. Per slot the writer inserts y then x and removes x then y, so x
    //  is NEVER present without y (y between x and the probe key). Returning x is therefore impossible.
    //  (B+ tree bug: when the owning leaf had no key on the wanted side, the neighbouring leaf was read
    //  at a later instant without re-validating the first, so y-then-x inserts could yield x.)
    // ------------------------------------------------------------------------------------------------
    [Theory, MemberData(nameof(All))]
    public void Floor_And_Ceiling_Are_Linearizable_Across_Leaves(string kind)
    {
        var m = Create<int, int>(kind);
        const int Slots = 512;
        for (int i = 0; i <= Slots; i++) m[i * 1000] = 0;
        long bad = 0;
        int writers = Math.Max(1, Workers / 2);
        var bodies = new List<Action<Func<bool>>>();
        for (int w = 0; w < writers; w++)
        {
            int ww = w;
            bodies.Add(running =>
            {
                while (running())
                    for (int s = ww; s < Slots; s += writers)
                    {
                        int b = s * 1000;
                        m[b + 400] = 1; m[b + 300] = 1;      // floor side:   300 only while 400
                        m[b + 600] = 1; m[b + 700] = 1;      // ceiling side: 700 only while 600
                        m.TryRemove(b + 300, out _); m.TryRemove(b + 400, out _);
                        m.TryRemove(b + 700, out _); m.TryRemove(b + 600, out _);
                    }
            });
        }
        for (int rd = 0; rd < Workers - writers + 1; rd++)
        {
            int seed = rd;
            bodies.Add(running =>
            {
                var r = new Random(seed);
                while (running())
                    for (int i = 0; i < 256; i++)
                    {
                        int b = r.Next(Slots) * 1000;
                        if (m.TryGetFloor(b + 450, out var f) && f.Key == b + 300) Interlocked.Increment(ref bad);
                        if (m.TryGetCeiling(b + 550, out var c) && c.Key == b + 700) Interlocked.Increment(ref bad);
                    }
            });
        }
        RunFor(Budget, bodies.ToArray());
        Assert.Equal(0, bad);
    }

    // ------------------------------------------------------------------------------------------------
    //  TryGetFirst / TryGetLast linearizability. The writer cycles {lo-, mid} so that `far` is never the
    //  minimum (resp. maximum). (Skip-list bug: if the first node was deleted under it, TryGetFirst walked
    //  forward to the successor instead of restarting, returning a key that was never the minimum. B+ tree:
    //  the edge-leaf descent didn't re-check the root, so a root split could hide the true last leaf.)
    // ------------------------------------------------------------------------------------------------
    [Theory, MemberData(nameof(All))]
    public void First_And_Last_Are_Linearizable(string kind)
    {
        var m = Create<int, int>(kind);
        for (int i = 0; i < 300; i++) m[10_000 + i] = 0;      // bulk in the middle (depth / several leaves)
        m[20] = 0; m[10] = 0;                                 // first side: 20 is never the minimum
        m[90_000] = 0; m[90_010] = 0;                         // last side: 90_000 is never the maximum
        long bad = 0;
        var bodies = new List<Action<Func<bool>>>
        {
            running => { while (running()) { m[5] = 0; m.TryRemove(10, out _); m[10] = 0; m.TryRemove(5, out _); } },
            running => { while (running()) { m[90_020] = 0; m.TryRemove(90_010, out _); m[90_010] = 0; m.TryRemove(90_020, out _); } },
            running => { var r = new Random(3); while (running()) { int k = 10_000 + r.Next(2000); m[k] = 0; m.TryRemove(k, out _); } },
        };
        for (int t = 0; t < Math.Max(2, Workers - 3); t++)
            bodies.Add(running =>
            {
                while (running())
                    for (int i = 0; i < 256; i++)
                    {
                        if (m.TryGetFirst(out var e) && e.Key == 20) Interlocked.Increment(ref bad);
                        if (m.TryGetLast(out var l) && l.Key == 90_000) Interlocked.Increment(ref bad);
                    }
            });
        RunFor(Budget, bodies.ToArray());
        Assert.Equal(0, bad);
    }

    // ------------------------------------------------------------------------------------------------
    //  TryRemoveFirst / TryRemoveLast linearizability (B+ tree; see docs for the skip list's inherited,
    //  narrower window). Round: map = {edge}; R polls while W adds a smaller (larger) key and then observes
    //  `edge` still present. If W saw `edge` AFTER the new key existed, `edge` was removed while the new
    //  key was present, so the poll must not have returned `edge`. (Bug: poll was "get first, then remove
    //  that key", leaving a window in which a smaller key could arrive.)
    // ------------------------------------------------------------------------------------------------
    [Theory, MemberData(nameof(Trees))]
    public void Poll_First_And_Last_Are_Linearizable(string kind)
    {
        foreach (bool first in new[] { true, false })
        {
            var m = Create<int, int>(kind);
            int edge = first ? 5 : 900_000, beyond = first ? 1 : 900_005;
            for (int i = 0; i < 2000; i++) m[first ? 100_000 + i : 10 + i] = 0;   // bulk on the far side (depth)
            long bad = 0;
            using var barrier = new Barrier(2);
            int got = int.MinValue; bool saw = false, stop = false;
            var poller = new Thread(() =>
            {
                var r = new Random(1);
                while (true)
                {
                    barrier.SignalAndWait();
                    if (Volatile.Read(ref stop)) return;
                    Thread.SpinWait(r.Next(40));
                    KeyValuePair<int, int> e;
                    bool ok = first ? m.TryRemoveFirst(out e) : m.TryRemoveLast(out e);
                    got = ok ? e.Key : int.MinValue;
                    barrier.SignalAndWait();
                }
            }) { IsBackground = true };
            poller.Start();
            var wr = new Random(2);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < Budget / 2)
            {
                m[edge] = 0;
                barrier.SignalAndWait();
                Thread.SpinWait(wr.Next(40));
                m[beyond] = 0;
                saw = m.TryGetValue(edge, out _);
                barrier.SignalAndWait();
                if (got == edge && saw) bad++;
                m.TryRemove(beyond, out _); m.TryRemove(edge, out _);
                if (got != edge && got != beyond && got != int.MinValue) m[got] = 0;   // put back a bulk key
            }
            Volatile.Write(ref stop, true);
            barrier.SignalAndWait();
            poller.Join();
            Assert.Equal(0, bad);
        }
    }

    // ------------------------------------------------------------------------------------------------
    //  TryRemoveLast racing a split (deterministic, via an internal test seam). SplitLeaf publishes the new
    //  right leaf through oldNext.Prev before the parent links it. A poller walking Prev from an empty right
    //  neighbour reached that leaf and emptied it, and the split then installed default(TKey) as the
    //  separator: keys stayed enumerable but point lookups lost them, and Validate() failed.
    //  (Scenario found by an independent code review.)
    // ------------------------------------------------------------------------------------------------
    [Fact]
    public void Poll_Last_Cannot_Touch_A_Split_Leaf_Before_Its_Parent_Links_It()
    {
        var d = new ConcurrentBTreeDictionary<int, int>(3);
        for (int k = 10; k <= 160; k += 10) d[k] = k;
        d[71] = 71; d[72] = 72;
        foreach (var k in new[] { 130, 140, 150, 160, 70, 71 }) Assert.True(d.TryRemove(k, out _));
        d[111] = 111;

        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int fired = 0;
        d.SplitPublishedHook = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0) return;
            paused.Set();
            resume.Wait(TimeSpan.FromSeconds(30));
        };
        var insert = Task.Run(() => d.TryAdd(112, 112));
        Assert.True(paused.Wait(TimeSpan.FromSeconds(30)), "split hook never fired");

        // While the split is paused, poll the end of the tree. A correct implementation may have to wait
        // for the split to finish (the new leaf is locked); it must never modify that leaf.
        var polls = Task.Run(() =>
        {
            d.TryRemoveLast(out var a);
            d.TryRemoveLast(out var b);
            return (a.Key, b.Key);
        });
        polls.Wait(TimeSpan.FromMilliseconds(300));
        resume.Set();
        Assert.True(insert.Wait(TimeSpan.FromSeconds(30)) && insert.Result);
        Assert.True(polls.Wait(TimeSpan.FromSeconds(30)), "polls did not finish after the split completed");
        d.SplitPublishedHook = null;

        d.Validate();
        foreach (var kv in d)
            Assert.True(d.TryGetValue(kv.Key, out var v) && v == kv.Value, $"key {kv.Key} enumerable but not found by lookup");
        Assert.Equal(d.Count, d.Keys.Count);
    }

    // ------------------------------------------------------------------------------------------------
    //  Clear() racing writers. (1) Once quiescent, Count must equal the keys actually present — both
    //  structures let in-flight writers skew the counter permanently. (2) A key whose TryAdd completed
    //  before Clear() was invoked (and is never re-added) must never be observed after Clear() returns —
    //  the B+ tree's in-flight splits/merges re-published the old root over the new one, and the skip list
    //  spliced an in-flight node's index tower (and with it the old list) into the new head.
    // ------------------------------------------------------------------------------------------------
    [Theory, MemberData(nameof(All))]
    public void Clear_Under_Contention_Keeps_Count_Exact(string kind)
    {
        var m = Create<int, int>(kind);
        var bodies = Enumerable.Range(0, Workers - 1).Select(t => (Action<Func<bool>>)(running =>
        {
            var r = new Random(t);
            while (running()) for (int i = 0; i < 256; i++) { int k = r.Next(300); if (r.Next(2) == 0) m[k] = 1; else m.TryRemove(k, out _); }
        })).ToList();
        bodies.Add(running => { while (running()) { Thread.SpinWait(2000); m.Clear(); } });
        bodies.Add(running => { while (running()) { Thread.SpinWait(1500); m.Clear(); } });   // overlapping clears
        RunFor(Budget, bodies.ToArray());
        Assert.Equal(m.EnumerateKeys().Count, m.Count);
        m.Validate();
    }

    [Theory, MemberData(nameof(All))]
    public void Clear_Never_Resurrects_Keys(string kind)
    {
        var m = Create<int, int>(kind);
        int W = Math.Max(2, Workers - 1);
        var done = new long[W * 16];
        Array.Fill(done, -1L);
        long bad = 0;
        var bodies = Enumerable.Range(0, W).Select(w => (Action<Func<bool>>)(running =>
        {
            for (long i = 0; running(); i++)
            {
                m.TryAdd((int)(w + W * i), 0);                     // unique keys, never re-added
                Volatile.Write(ref done[w * 16], i);
                if ((i & 3) == 0) m.TryRemove((int)(w + W * (i - 2)), out _);
            }
        })).ToList();
        bodies.Add(running =>
        {
            var snap = new long[W];
            var r = new Random(5);
            while (running())
            {
                for (int w = 0; w < W; w++) snap[w] = Volatile.Read(ref done[w * 16]);
                m.Clear();
                for (int rep = 0; rep < 100; rep++)
                {
                    if (m.TryGetFirst(out var e) && e.Key / W <= snap[e.Key % W]) Interlocked.Increment(ref bad);
                    int w2 = r.Next(W);
                    if (snap[w2] < 0) continue;
                    long i2 = r.NextInt64(snap[w2] + 1);
                    if ((i2 & 3) != 2 && m.TryGetValue((int)(w2 + W * i2), out _)) Interlocked.Increment(ref bad);
                }
            }
        });
        RunFor(Budget, bodies.ToArray());
        Assert.Equal(0, bad);
    }
}
