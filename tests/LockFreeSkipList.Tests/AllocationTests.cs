using Mobratil.Collections;
using Xunit;
using Xunit.Abstractions;

namespace LockFreeSkipList.Tests;

/// <summary>
/// Pins down the allocation behaviour: reads allocate nothing, reference-typed values
/// store inline (no per-put wrapper), value-typed values keep the un-boxing holder, and
/// the reference path is correct even for the awkward <c>object</c> value type.
/// </summary>
public class AllocationTests
{
    private readonly ITestOutputHelper _out;
    public AllocationTests(ITestOutputHelper output) => _out = output;

    private static long Measure(Action action, int iterations)
    {
        for (int i = 0; i < 2000; i++) action();                 // JIT/tiering warmup
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Reference_Value_Overwrite_And_Get_Are_Allocation_Free()
    {
        var d = new ConcurrentSkipListDictionary<int, string>();
        d[1] = "x";
        const int n = 200_000;

        long putBytes = Measure(() => d[1] = "x", n);
        long getBytes = Measure(() => d.TryGetValue(1, out _), n);

        _out.WriteLine($"ref-value overwrite: {putBytes} B over {n} ops; get: {getBytes} B");
        // 200k ops with any per-op allocation would be millions of bytes; assert ~zero.
        Assert.True(putBytes < 100_000, $"expected no per-op allocation, saw {putBytes} bytes");
        Assert.True(getBytes < 100_000, $"expected no per-op allocation, saw {getBytes} bytes");
    }

    [Fact]
    public void Value_Type_Overwrite_Allocates_The_Holder_But_Get_Does_Not()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        d[1] = 0;
        const int n = 200_000;

        long putBytes = Measure(() => d[1] = 7, n);
        long getBytes = Measure(() => d.TryGetValue(1, out _), n);

        _out.WriteLine($"value-value overwrite: {putBytes} B over {n} ops; get: {getBytes} B");
        Assert.True(putBytes > n, "value-typed overwrite should allocate one holder per put");
        Assert.True(getBytes < 100_000, $"lookups must not allocate, saw {getBytes} bytes");
    }

    [Fact]
    public void Present_Key_TryAdd_Does_Not_Allocate()
    {
        // Deferred allocation: a TryAdd that loses (key present) must allocate nothing.
        var d = new ConcurrentSkipListDictionary<int, int>();
        d[1] = 1;
        long bytes = Measure(() => d.TryAdd(1, 99), 200_000);
        _out.WriteLine($"present-key TryAdd: {bytes} B");
        Assert.True(bytes < 100_000, $"no-op TryAdd should not allocate, saw {bytes} bytes");
    }

    [Fact]
    public void Object_Valued_Dictionary_Is_Correct_With_Inline_Storage()
    {
        // `object` is the tricky case: a stored value is type-indistinguishable from the
        // internal sentinels, so the implementation must rely on reference identity.
        var d = new ConcurrentSkipListDictionary<int, object>();
        d[1] = "a";
        d[2] = 42;          // user-boxed int, stored as a reference
        var marker = new object();
        d[3] = marker;

        Assert.Equal("a", d[1]);
        Assert.Equal(42, d[2]);
        Assert.Same(marker, d[3]);
        Assert.Equal(3, d.Count);

        Assert.True(d.TryRemove(2, out var r) && (int)r == 42);
        Assert.False(d.ContainsKey(2));
        Assert.Equal(new[] { 1, 3 }, d.Keys);
    }

    [Fact]
    public void Null_Values_Are_Supported_For_Reference_Types()
    {
        // docs/CONCURRENCY.md: "Values may be any type, including reference types and null." The inline
        // (no-holder) storage maps null onto a private sentinel, since null itself means "deleted".
        var d = new ConcurrentSkipListDictionary<int, string?>();
        d[1] = null;
        Assert.True(d.TryGetValue(1, out var s) && s is null);
        Assert.True(d.ContainsKey(1));
        Assert.False(d.TryAdd(1, "x"));
        Assert.True(d.ContainsValue(null));
        Assert.True(d.TryUpdate(1, "ok", null));
        Assert.Equal("ok", d[1]);
        Assert.True(d.TryUpdate(1, null, "ok"));
        Assert.True(d.TryRemove(new KeyValuePair<int, string?>(1, null)));
        Assert.False(d.ContainsKey(1));
        Assert.True(d.TryAdd(2, null));
        Assert.Null(d.GetOrAdd(2, "y"));
        Assert.True(d.TryRemoveFirst(out var e) && e.Key == 2 && e.Value is null);
        Assert.Equal(0, d.Count);
        // Nullable value types are value types -> holder path -> null is a normal value.
        var nd = new ConcurrentSkipListDictionary<int, int?>();
        nd[1] = null;
        Assert.True(nd.TryGetValue(1, out var v) && v is null);
        Assert.True(nd.ContainsKey(1));
    }

    [Fact]
    public void Foreach_Over_Dictionary_And_View_Is_Allocation_Free()
    {
        var d = new ConcurrentSkipListDictionary<int, int>();
        for (int i = 0; i < 16; i++) d[i] = i;
        var view = d.GetViewBetween(2, 12);   // create the view once

        int sink = 0;
        long dictBytes = Measure(() => { foreach (var kv in d) sink += kv.Key; }, 50_000);
        long viewBytes = Measure(() => { foreach (var kv in view) sink += kv.Key; }, 50_000);

        _out.WriteLine($"50k foreach passes — dict: {dictBytes} B, view: {viewBytes} B (sink={sink})");
        // A heap-allocating (yield) enumerator would cost ~80 B/pass => millions of bytes.
        Assert.True(dictBytes < 100_000, $"dictionary foreach should not allocate, saw {dictBytes} bytes");
        Assert.True(viewBytes < 100_000, $"view foreach should not allocate, saw {viewBytes} bytes");
    }

    [Fact]
    public void Reference_Values_Survive_Concurrent_Churn()
    {
        var d = new ConcurrentSkipListDictionary<long, string>();
        int threads = Math.Max(4, Environment.ProcessorCount);
        const int perThread = 40_000;

        // disjoint inserts with reference values
        Parallel.For(0, threads, t =>
        {
            long baseKey = (long)t * perThread;
            for (int i = 0; i < perThread; i++)
                Assert.True(d.TryAdd(baseKey + i, $"v{baseKey + i}"));
        });

        int total = threads * perThread;
        Assert.Equal(total, d.Count);

        long prev = long.MinValue;
        foreach (var kv in d)
        {
            Assert.True(kv.Key > prev);
            Assert.Equal($"v{kv.Key}", kv.Value);   // inline-stored reference intact
            prev = kv.Key;
        }

        // concurrent overwrite + remove of disjoint ranges
        Parallel.For(0, threads, t =>
        {
            long baseKey = (long)t * perThread;
            for (int i = 0; i < perThread; i++)
            {
                if ((i & 1) == 0) d[baseKey + i] = "overwritten";
                else Assert.True(d.TryRemove(baseKey + i, out _));
            }
        });

        Assert.Equal(total / 2, d.Count);
        foreach (var kv in d) Assert.Equal("overwritten", kv.Value);
    }
}
