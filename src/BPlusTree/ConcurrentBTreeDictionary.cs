using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mobratil.Collections;

/// <summary>
/// A concurrent, sorted <see cref="IDictionary{TKey,TValue}"/> backed by an in-memory B+ tree and kept
/// thread-safe with Optimistic Lock Coupling (OLC). Think of it as the ordered, range-queryable sibling of
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>: because keys are held in
/// sort order it adds navigable lookups (<c>TryGetCeiling</c>/<c>TryGetFloor</c>/<c>TryGetHigher</c>/
/// <c>TryGetLower</c>) and live range views (<c>GetViewBetween</c>, <c>GetViewFrom</c>, <c>GetViewTo</c>,
/// <c>Reverse</c>) in the spirit of <see cref="SortedSet{T}"/>.
///
/// <para><b>Consistency.</b> Point operations — <see cref="TryGetValue"/>, <see cref="TryAdd"/>,
/// <see cref="TryRemove(TKey, out TValue)"/>, the indexer, <see cref="GetOrAdd(TKey, TValue)"/>,
/// <c>AddOrUpdate</c>, and the navigable lookups — are <i>linearizable</i>. Enumeration and range views are
/// <i>weakly consistent</i>: they never throw on concurrent mutation and always yield keys in order, but
/// are not a point-in-time snapshot (like <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>'s
/// enumerator). <see cref="Count"/> is weakly consistent and never negative. See <c>docs/CONCURRENCY.md</c>.</para>
///
/// <para><b>How it stays correct under contention.</b> Reads are optimistic and never block: a reader
/// snapshots a per-node version, descends, and re-validates; any concurrent write bumps the version and
/// triggers a cheap retry rather than a stall. (On weak-memory hardware the re-validation carries a load
/// fence — see <c>Node.Validate</c>.) Writes latch-couple top-down, releasing an ancestor the moment it is
/// known it cannot split; splits propagate up the locked chain and recursive sibling merges reclaim space
/// automatically. Locks are always taken parent-before-child, so the structure cannot deadlock.</para>
///
/// <para>The <c>order</c> constructor parameter sets node fan-out (max keys per node; default 64).</para>
/// </summary>
public sealed class ConcurrentBTreeDictionary<TKey, TValue>
    : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    // ---------------------------------------------------------------------
    //  Nodes + per-node optimistic lock (version word: even = free, odd = write-locked)
    // ---------------------------------------------------------------------
    private abstract class Node
    {
        internal long Version;          // even = unlocked; odd = write-locked
        internal int Count;             // keys in use
        internal TKey[] Keys = default!;

        internal void WriteLock()
        {
            var sw = new SpinWait();
            for (; ; )
            {
                long v = Volatile.Read(ref Version);
                if ((v & 1L) == 0 && Interlocked.CompareExchange(ref Version, v + 1, v) == v) return;
                sw.SpinOnce();
            }
        }

        internal void WriteUnlock() => Volatile.Write(ref Version, Volatile.Read(ref Version) + 1); // odd -> even

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadVersion(out long v)        // true if currently unlocked
        {
            v = Volatile.Read(ref Version);
            return (v & 1L) == 0;
        }

        // On weak-memory hardware (ARM/Apple silicon) an acquire-load alone does NOT stop the
        // optimistic DATA reads that precede this check (Children[ci], Keys/Values, leaf links)
        // from being reordered AFTER the version read — so we could validate a node as unchanged
        // yet have read a child slot a concurrent merge already nulled (NRE) or a torn key/value.
        // A LoadLoad fence before the version read closes the seqlock. x86/x64 is TSO (loads never
        // reorder with loads), so the fence is gated off there and adds zero cost on that path.
        private static readonly bool FenceBeforeValidate =
            RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Validate(long v)
        {
            if (FenceBeforeValidate) Interlocked.MemoryBarrier();
            return Volatile.Read(ref Version) == v;                          // unchanged since we read v
        }

        // Optimistically upgrade to a write lock: succeeds only if the version is still `observed`
        // (i.e. the node hasn't been touched since we read it during the optimistic descent).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryUpgrade(long observed)
            => Interlocked.CompareExchange(ref Version, observed + 1, observed) == observed;
    }

    private sealed class Leaf : Node
    {
        internal TValue[] Values = default!;
        internal Leaf? Next;            // right sibling (ascending)
        internal Leaf? Prev;            // left sibling (descending) — doubly-linked for O(k) reverse scans
        // The size counter of the tree GENERATION this leaf belongs to. Clear() swaps in a fresh root with a
        // fresh counter; an in-flight writer that still lands in a detached (pre-Clear) leaf then bumps the
        // detached generation's counter, so the live Count stays exact once quiescent.
        internal StripedCounter Counter = default!;
    }

    private sealed class Internal : Node
    {
        internal Node[] Children = default!;   // Count + 1 in use
    }

    // The latch-coupled (pessimistic) paths record the nodes they hold plus each node's child index in its
    // parent, so split/merge propagation never has to call the user's comparer after mutation has begun (a
    // throwing comparer could otherwise leave a half-applied split). Rented per thread and grown on demand.
    private sealed class LockPath
    {
        internal Node[] Nodes = new Node[16];
        internal int[] Idx = new int[16];
        internal long[] Ver = new long[16];
        internal int Count;

        internal void Push(Node n, int idx, long ver = 0)
        {
            if (Count == Nodes.Length)
            {
                Array.Resize(ref Nodes, Count * 2);
                Array.Resize(ref Idx, Count * 2);
                Array.Resize(ref Ver, Count * 2);
            }
            Nodes[Count] = n; Idx[Count] = idx; Ver[Count] = ver; Count++;
        }

        internal void UnlockAll()
        {
            for (int i = 0; i < Count; i++) { Nodes[i].WriteUnlock(); Nodes[i] = null!; }
            Count = 0;
        }

        internal void Reset()
        {
            for (int i = 0; i < Count; i++) Nodes[i] = null!;
            Count = 0;
        }
    }

    [ThreadStatic] private static LockPath? t_path;

    // Rent/return (rather than share) the thread's path, so a comparer that re-enters another dictionary of
    // the same closed type on this thread can't clobber a path that is still in use.
    private static LockPath RentPath()
    {
        var p = t_path;
        if (p == null) return new LockPath();
        t_path = null;
        return p;
    }

    private static void ReturnPath(LockPath p) { p.Reset(); t_path = p; }

    private readonly IComparer<TKey> _cmp;
    // True when ordering is Comparer<TKey>.Default for a value-type key: Compare() then calls it through the
    // static, which the JIT devirtualizes and inlines (e.g. long/int compare) instead of an interface call.
    private readonly bool _defaultCmp;
    // ...and that compare can never throw, whatever (torn) bits it is handed: primitive keys under the default
    // comparer. Then the optimistic paths skip the torn-read exception bookkeeping entirely.
    private readonly bool _cmpNeverThrows;
    private static readonly bool s_primitiveKey =
        typeof(TKey) == typeof(int) || typeof(TKey) == typeof(long) || typeof(TKey) == typeof(uint) || typeof(TKey) == typeof(ulong) ||
        typeof(TKey) == typeof(short) || typeof(TKey) == typeof(ushort) || typeof(TKey) == typeof(byte) || typeof(TKey) == typeof(sbyte) ||
        typeof(TKey) == typeof(char) || typeof(TKey) == typeof(double) || typeof(TKey) == typeof(float) || typeof(TKey) == typeof(nint) ||
        typeof(TKey) == typeof(nuint) || typeof(TKey) == typeof(DateTime) || typeof(TKey) == typeof(TimeSpan) || typeof(TKey) == typeof(Guid);
    private readonly int _max;
    private readonly int _min;          // merge target: a merge cascade packs survivors back up to >= _min (order/2)
    private readonly int _mergeBelow;   // merge TRIGGER, hardcoded to order/3 (see ctor)

    private volatile Node _root;
    private volatile StripedCounter _count = new();   // counter of the CURRENT generation (see Leaf.Counter)
    private readonly object _clearGate = new();
    internal long _splits, _merges;   // diagnostic counters (leaf split / leaf merge) — read by the soak harness
    internal (long splits, long merges) RebalanceCounts => (Volatile.Read(ref _splits), Volatile.Read(ref _merges));

    /// <param name="order">Node fan-out (max keys per node). Default 64.</param>
    /// <param name="comparer">Key ordering; defaults to <see cref="Comparer{T}.Default"/>.</param>
    public ConcurrentBTreeDictionary(int order = 64, IComparer<TKey>? comparer = null)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "order must be >= 3");
        _max = order;
        _min = order / 2;
        // Merge TRIGGER, hardcoded at order/3. Deletes free a key immediately; a *structural* merge is only
        // attempted once a leaf falls below order/3 — well under the order/2 fill a split leaves behind — so
        // ordinary churn never oscillates across the threshold. That avoids split/merge "thrash", which would
        // otherwise lock upper nodes constantly and storm concurrent optimistic reads with restarts (measured
        // ~14 restarts/read at the half-full trigger -> ~0 here, roughly doubling contended read throughput).
        // When it does fire, the merge cascade still packs survivors back up to >= order/2, so reclamation
        // stays effective and automatic — no manual compaction. order/3 balances that reclamation against the
        // throughput win (lower would scale reads further but leave leaves emptier; this is the chosen point).
        _mergeBelow = Math.Max(1, order / 3);
        _cmp = comparer ?? Comparer<TKey>.Default;
        _defaultCmp = typeof(TKey).IsValueType && ReferenceEquals(_cmp, Comparer<TKey>.Default);
        _cmpNeverThrows = _defaultCmp && s_primitiveKey;
        _root = NewLeaf(_count);
    }

    // Sum() is a weakly-consistent striped read (LongAdder semantics) -> can momentarily be < 0 mid-churn; clamp.
    public int Count { get { long c = _count.Sum(); return c <= 0 ? 0 : c >= int.MaxValue ? int.MaxValue : (int)c; } }
    public bool IsEmpty => _count.Sum() <= 0;

    private Leaf NewLeaf(StripedCounter counter) => new() { Keys = new TKey[_max + 1], Values = new TValue[_max + 1], Counter = counter };
    private Internal NewInternal() => new() { Keys = new TKey[_max + 1], Children = new Node[_max + 2] };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Compare(TKey a, TKey b)
        => typeof(TKey).IsValueType && _defaultCmp ? Comparer<TKey>.Default.Compare(a, b) : _cmp.Compare(a, b);

    // ---- Branchless search for integral keys under the default comparer ----
    // Random-key binary search mispredicts about half its branches (~6 per 64-key node). For integral keys we
    // instead narrow the window with a conditional ADD (compiled to cset/csel), which never mispredicts and
    // lets the CPU overlap the loads. Semantics are identical to Comparer<T>.Default for these types.
    private static readonly bool s_integralKey =
        typeof(TKey) == typeof(long) || typeof(TKey) == typeof(int) || typeof(TKey) == typeof(ulong) || typeof(TKey) == typeof(uint) ||
        typeof(TKey) == typeof(short) || typeof(TKey) == typeof(ushort) || typeof(TKey) == typeof(byte) || typeof(TKey) == typeof(sbyte) ||
        typeof(TKey) == typeof(char) || typeof(TKey) == typeof(nint) || typeof(TKey) == typeof(nuint);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Less(TKey a, TKey b)
    {
        if (typeof(TKey) == typeof(long)) return Unsafe.As<TKey, long>(ref a) < Unsafe.As<TKey, long>(ref b);
        if (typeof(TKey) == typeof(int)) return Unsafe.As<TKey, int>(ref a) < Unsafe.As<TKey, int>(ref b);
        if (typeof(TKey) == typeof(ulong)) return Unsafe.As<TKey, ulong>(ref a) < Unsafe.As<TKey, ulong>(ref b);
        if (typeof(TKey) == typeof(uint)) return Unsafe.As<TKey, uint>(ref a) < Unsafe.As<TKey, uint>(ref b);
        if (typeof(TKey) == typeof(short)) return Unsafe.As<TKey, short>(ref a) < Unsafe.As<TKey, short>(ref b);
        if (typeof(TKey) == typeof(ushort)) return Unsafe.As<TKey, ushort>(ref a) < Unsafe.As<TKey, ushort>(ref b);
        if (typeof(TKey) == typeof(byte)) return Unsafe.As<TKey, byte>(ref a) < Unsafe.As<TKey, byte>(ref b);
        if (typeof(TKey) == typeof(sbyte)) return Unsafe.As<TKey, sbyte>(ref a) < Unsafe.As<TKey, sbyte>(ref b);
        if (typeof(TKey) == typeof(char)) return Unsafe.As<TKey, char>(ref a) < Unsafe.As<TKey, char>(ref b);
        if (typeof(TKey) == typeof(nint)) return Unsafe.As<TKey, nint>(ref a) < Unsafe.As<TKey, nint>(ref b);
        if (typeof(TKey) == typeof(nuint)) return Unsafe.As<TKey, nuint>(ref a) < Unsafe.As<TKey, nuint>(ref b);
        return Comparer<TKey>.Default.Compare(a, b) < 0;
    }

    // First index i in [0,n) with keys[i] >= x (strict=false) or keys[i] > x (strict=true); n if none.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BranchlessBound(TKey[] keys, int n, TKey x, bool strict)
    {
        if (n <= 0) return 0;
        ref TKey k0 = ref MemoryMarshal.GetArrayDataReference(keys);
        if ((uint)n > (uint)keys.Length) n = keys.Length;      // torn count: stay in bounds (read is re-validated)
        int b = 0, len = n;
        while (len > 1)
        {
            int half = len >> 1;
            TKey probe = Unsafe.Add(ref k0, b + half - 1);
            bool right = strict ? !Less(x, probe) : Less(probe, x);    // probe <= x  /  probe < x
            b += Unsafe.BitCast<bool, byte>(right) * half;
            len -= half;
        }
        TKey last = Unsafe.Add(ref k0, b);
        bool past = strict ? !Less(x, last) : Less(last, x);
        return past ? n : b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Find(TKey[] keys, int count, TKey key)
    {
        if (s_integralKey && _defaultCmp)
        {
            int i = BranchlessBound(keys, count, key, strict: false);
            return i < count && !Less(key, keys[i]) ? i : ~i;
        }
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int c = Compare(key, keys[mid]);
            if (c == 0) return mid;
            if (c < 0) hi = mid - 1; else lo = mid + 1;
        }
        return ~lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ChildIndex(TKey[] keys, int count, TKey key)
    {
        if (s_integralKey && _defaultCmp) return BranchlessBound(keys, count, key, strict: true);
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (Compare(key, keys[mid]) >= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static void ThrowKeyNull() => throw new ArgumentNullException("key");

    // =====================================================================
    //  Lookup — optimistic, lock-free
    //
    //  Optimistic reads may hand the comparer a torn or just-cleared slot (e.g. a null reference key) before
    //  validation would reject the read. The hot cores are EH-free (a try region inside the descent loop
    //  costs ~10% by pinning its locals to the stack); they record the node + version being read in a
    //  caller-owned ReadPos, and a thin wrapper's `catch when (pos.Torn)` decides: version changed -> the
    //  comparer saw garbage, retry silently; version unchanged -> the comparer genuinely threw, propagate.
    // =====================================================================
    private struct ReadPos
    {
        internal Node? Node;
        internal long Version;
        internal readonly bool Torn => Node != null && !Node.Validate(Version);
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (key == null) ThrowKeyNull();
        var pos = new ReadPos();
        return _cmpNeverThrows ? TryGetValueCore(key, ref pos, out value) : TryGetValueGuarded(key, ref pos, out value);
    }

    private bool TryGetValueGuarded(TKey key, ref ReadPos pos, out TValue value)
    {
        for (; ; )
        {
            try { return TryGetValueCore(key, ref pos, out value); }
            catch when (pos.Torn) { }                         // comparer fed a torn read -> retry
        }
    }

    private bool TryGetValueCore(TKey key, ref ReadPos pos, out TValue value)
    {
        for (; ; ) // restart
        {
            Node cur = _root;
            if (!cur.TryReadVersion(out long v)) continue;    // node locked -> restart
            if (cur != _root) continue;                       // root advanced -> restart

            while (cur is Internal ind)
            {
                if (!_cmpNeverThrows) { pos.Node = ind; pos.Version = v; }
                int ci = ChildIndex(ind.Keys, ind.Count, key);
                Node child = ind.Children[ci];
                if (!ind.Validate(v)) goto restart;       // routing read consistent
                // Fix B: if the child is briefly write-locked, SPIN on it (cheap) rather than re-descending
                // from the root (expensive). Bail to restart only if the PARENT changes while we wait — then
                // our routing may be stale. Mirrors the B-link reads; kills the descent-into-locked-leaf storm.
                long cv;
                var sw = new SpinWait();
                while (!child.TryReadVersion(out cv))
                {
                    sw.SpinOnce();
                    if (!ind.Validate(v)) goto restart;
                }
                if (!ind.Validate(v)) goto restart;       // parent unchanged through the child read
                cur = child; v = cv;
            }

            var leaf = (Leaf)cur;
            if (!_cmpNeverThrows) { pos.Node = leaf; pos.Version = v; }
            int cnt = Volatile.Read(ref leaf.Count);
            int i = Find(leaf.Keys, cnt, key);
            bool found = i >= 0;
            TValue val = found ? leaf.Values[i] : default!;
            if (!leaf.Validate(v)) goto restart;              // leaf unchanged during our read
            value = val;
            return found;

        restart:;
        }
    }

    // Find under a write lock held by the caller; if the comparer throws, release the lock before propagating
    // (otherwise the leaf — and with it the whole map — would stay locked forever). Kept out of line so the
    // callers' hot paths carry no EH region.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindLocked(Node n, TKey key) => _cmpNeverThrows ? Find(n.Keys, n.Count, key) : FindLockedGuarded(n, key);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int FindLockedGuarded(Node n, TKey key)
    {
        try { return Find(n.Keys, n.Count, key); }
        catch { n.WriteUnlock(); throw; }
    }

    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    public TValue this[TKey key]
    {
        get => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
        set => Insert(key, value, onlyIfAbsent: false);
    }

    public bool TryAdd(TKey key, TValue value) => Insert(key, value, onlyIfAbsent: true);

    // =====================================================================
    //  Insert — optimistic leaf lock; optimistic parent+leaf split; latch-coupling only as last resort
    // =====================================================================
    private Node LockRoot()
    {
        for (; ; )
        {
            Node r = _root;
            r.WriteLock();
            if (r == _root) return r;
            r.WriteUnlock();
        }
    }

    // A node is "insert-safe" if a separator pushed into it cannot overflow it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool InsertSafe(Node n) => n.Count < _max;

    // Optimistically descend (no locks) to the leaf that should hold `key`; return it and the
    // version observed for it. Returns false (caller retries) if a node was locked mid-descent.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDescendToLeaf(TKey key, out Leaf leaf, out long version)
    {
        var pos = new ReadPos();
        return _cmpNeverThrows ? DescendToLeafCore(key, ref pos, out leaf, out version) : TryDescendToLeafGuarded(key, ref pos, out leaf, out version);
    }

    private bool TryDescendToLeafGuarded(TKey key, ref ReadPos pos, out Leaf leaf, out long version)
    {
        try { return DescendToLeafCore(key, ref pos, out leaf, out version); }
        catch when (pos.Torn) { leaf = null!; version = 0; return false; }   // comparer fed a torn read -> retry
    }

    private bool DescendToLeafCore(TKey key, ref ReadPos pos, out Leaf leaf, out long version)
    {
        leaf = null!; version = 0;
        Node cur = _root;
        if (!cur.TryReadVersion(out long v)) return false;
        if (cur != _root) return false;                          // root advanced -> retry
        while (cur is Internal ind)
        {
            if (!_cmpNeverThrows) { pos.Node = ind; pos.Version = v; }
            int ci = ChildIndex(ind.Keys, ind.Count, key);
            Node child = ind.Children[ci];
            if (!ind.Validate(v)) return false;                  // child pointer + routing read was consistent
            if (!child.TryReadVersion(out long cv)) return false;
            if (!ind.Validate(v)) return false;                  // parent unchanged THROUGH the child read -> routing still valid
            cur = child; v = cv;
        }
        leaf = (Leaf)cur; version = v;
        return true;
    }

    // Fast path: optimistic descent + lock ONLY the leaf. If the upgrade succeeds the leaf is
    // unchanged since we read it, which guarantees `key` still belongs in this leaf (a key can only
    // migrate to another leaf via a split, which would have bumped the version). If the leaf has room
    // we finish here — no root/ancestor locks. A full leaf is split under just (parent, leaf) when the
    // parent has room; only a split that would cascade falls back to root-down latch coupling.
    private bool Insert(TKey key, TValue value, bool onlyIfAbsent)
    {
        if (key == null) ThrowKeyNull();
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v) || !leaf.TryUpgrade(v)) continue;

            int idx = FindLocked(leaf, key);
            if (idx >= 0)
            {
                if (!onlyIfAbsent) leaf.Values[idx] = value;     // replace in place, no structural change
                leaf.WriteUnlock();
                return false;
            }
            if (leaf.Count < _max)                               // has room -> no split, finish on the leaf
            {
                InsertIntoLeaf(leaf, ~idx, key, value);
                leaf.WriteUnlock();
                leaf.Counter.Increment();
                return true;
            }

            leaf.WriteUnlock();                                  // full -> needs a split
            if (TrySplitInsert(key, value)) return true;
            return InsertPessimistic(key, value, onlyIfAbsent);
        }
    }

    // Split a full leaf holding only its parent and itself (both acquired by version upgrade, so both are
    // exactly as seen during the validated descent). Applies when the parent can absorb the separator,
    // i.e. the overwhelmingly common case — so splits no longer serialize on the root lock. Returns false
    // (caller falls back to InsertPessimistic) if anything moved or the parent is full.
    private bool TrySplitInsert(TKey key, TValue value)
    {
        if (!TryDescendToParent(key, out var parent, out int ci, out long pv, out var leaf, out long lv)) return false;
        if (!parent.TryUpgrade(pv)) return false;
        if (parent.Count >= _max || !leaf.TryUpgrade(lv)) { parent.WriteUnlock(); return false; }

        int idx;
        try { idx = Find(leaf.Keys, leaf.Count, key); }
        catch { leaf.WriteUnlock(); parent.WriteUnlock(); throw; }   // (cold path: splits only)
        if (idx >= 0 || leaf.Count < _max)
        {   // the leaf changed shape between our attempts (key appeared / room freed): let the caller redo it
            leaf.WriteUnlock(); parent.WriteUnlock();
            return false;
        }
        InsertIntoLeaf(leaf, ~idx, key, value);
        SplitLeaf(leaf, out TKey sep, out Node right);
        InsertIntoInternal(parent, ci, sep, right);
        leaf.WriteUnlock();
        parent.WriteUnlock();
        leaf.Counter.Increment();
        return true;
    }

    private bool InsertPessimistic(TKey key, TValue value, bool onlyIfAbsent)
    {
        var path = RentPath();
        try
        {
            Node cur = LockRoot();
            path.Push(cur, -1);
            int idx;
            try
            {
                while (cur is Internal ind)
                {
                    int ci = ChildIndex(ind.Keys, ind.Count, key);
                    Node child = ind.Children[ci];
                    child.WriteLock();
                    if (InsertSafe(child)) path.UnlockAll();     // child can absorb any split from below
                    path.Push(child, ci);
                    cur = child;
                }
                idx = Find(cur.Keys, cur.Count, key);
            }
            catch { path.UnlockAll(); throw; }                   // comparer threw: nothing mutated yet

            // ---- no comparer calls past this point: mutation is all-or-nothing ----
            // cur is the leaf (last in path); path holds the locked chain from the deepest safe node down.
            var leaf = (Leaf)cur;
            bool added;
            if (idx >= 0)
            {
                if (!onlyIfAbsent) leaf.Values[idx] = value;     // replace (we hold the leaf's write lock)
                added = false;
            }
            else
            {
                InsertIntoLeaf(leaf, ~idx, key, value);
                added = true;
                if (leaf.Count > _max)
                {
                    SplitLeaf(leaf, out TKey sep, out Node right);
                    Propagate(path, sep, right);
                }
            }

            path.UnlockAll();
            if (added) leaf.Counter.Increment();
            return added;
        }
        finally { ReturnPath(path); }
    }

    // Propagate a (separator, rightChild) split up through the locked chain. The last node in `path` just
    // split; insert into its parent at the child index recorded on the way down (no comparer calls), splitting
    // further as needed; grow the root if the topmost held node (the root) splits.
    private void Propagate(LockPath path, TKey sep, Node right)
    {
        for (int i = path.Count - 2; i >= 0; i--)
        {
            var parent = (Internal)path.Nodes[i];
            InsertIntoInternal(parent, path.Idx[i + 1], sep, right);
            if (parent.Count <= _max) return;            // absorbed
            SplitInternal(parent, out sep, out right);   // continue up (parent stays at its own index)
        }
        // topmost held node split -> grow a new root (Nodes[0] is the old root, still locked)
        var old = path.Nodes[0];
        var newRoot = NewInternal();
        newRoot.Keys[0] = sep;
        newRoot.Children[0] = old;
        newRoot.Children[1] = right;
        newRoot.Count = 1;
        // Publish while still holding the old root's lock. CAS (not a plain store) so a concurrent Clear()
        // that already swapped in a fresh root is never overwritten by this now-detached tree.
#pragma warning disable CS0420
        Interlocked.CompareExchange(ref _root, newRoot, old);
#pragma warning restore CS0420
    }

    private static void InsertIntoLeaf(Leaf leaf, int at, TKey key, TValue value)
    {
        Array.Copy(leaf.Keys, at, leaf.Keys, at + 1, leaf.Count - at);
        Array.Copy(leaf.Values, at, leaf.Values, at + 1, leaf.Count - at);
        leaf.Keys[at] = key;
        leaf.Values[at] = value;
        leaf.Count++;     // count is the last field touched -> readers see a consistent prefix (and re-validate)
    }

    private static void InsertIntoInternal(Internal n, int ci, TKey sep, Node right)
    {
        Array.Copy(n.Keys, ci, n.Keys, ci + 1, n.Count - ci);
        Array.Copy(n.Children, ci + 1, n.Children, ci + 2, n.Count - ci);
        n.Keys[ci] = sep;
        n.Children[ci + 1] = right;
        n.Count++;
    }

    private void SplitLeaf(Leaf leaf, out TKey sep, out Node rightNode)
    {
        Interlocked.Increment(ref _splits);
        int mid = (_max + 1) / 2;
        int rc = leaf.Count - mid;
        var right = NewLeaf(leaf.Counter);
        Array.Copy(leaf.Keys, mid, right.Keys, 0, rc);
        Array.Copy(leaf.Values, mid, right.Values, 0, rc);
        right.Count = rc;
        // Splice `right` between `leaf` and its old right sibling, fixing the doubly-linked chain.
        // `leaf` is already write-locked (held). We briefly lock the old right sibling to fix its Prev;
        // sibling locks are always acquired left→right, so this can't deadlock with the top-down latches.
        var oldNext = leaf.Next;
        right.Prev = leaf;
        right.Next = oldNext;
        if (oldNext != null) { oldNext.WriteLock(); oldNext.Prev = right; oldNext.WriteUnlock(); }
        leaf.Next = right;                               // publish in the forward chain last
        leaf.Count = mid;
        Array.Clear(leaf.Keys, mid, rc);
        Array.Clear(leaf.Values, mid, rc);
        sep = right.Keys[0];
        rightNode = right;
    }

    private void SplitInternal(Internal node, out TKey sep, out Node rightNode)
    {
        int mid = node.Count / 2;                 // node.Keys[mid] moves UP
        int rKeys = node.Count - mid - 1;
        var right = NewInternal();
        Array.Copy(node.Keys, mid + 1, right.Keys, 0, rKeys);
        Array.Copy(node.Children, mid + 1, right.Children, 0, rKeys + 1);
        right.Count = rKeys;
        sep = node.Keys[mid];
        Array.Clear(node.Keys, mid, rKeys + 1);          // keys[mid .. _max]
        Array.Clear(node.Children, mid + 1, rKeys + 1);  // children[mid+1 .. _max+1]
        node.Count = mid;
        rightNode = right;
    }

    // =====================================================================
    //  Delete — lazy (lock-couple to the leaf, remove; deletes never split, so always safe)
    // =====================================================================
    public bool TryRemove(TKey key, out TValue value) => RemoveCore(key, false, default!, out value);

    /// <summary>Removes the pair only if both key and current value match. (ConcurrentMap.remove(k,v).)</summary>
    public bool TryRemove(KeyValuePair<TKey, TValue> item) => RemoveCore(item.Key, true, item.Value, out _);

    // Lazy delete never splits/merges, so a leaf is always "safe": optimistic descent + lock only
    // the leaf. An unchanged leaf (upgrade succeeded) is guaranteed to own `key` if present.
    private bool RemoveCore(TKey key, bool hasExpected, TValue expected, out TValue removed)
    {
        if (key == null) ThrowKeyNull();
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v) || !leaf.TryUpgrade(v)) continue;

            int i = FindLocked(leaf, key);
            if (i >= 0 && hasExpected && !ValueEqualsLocked(leaf, i, expected)) i = -1;
            if (i < 0) { leaf.WriteUnlock(); removed = default!; return false; }

            removed = leaf.Values[i];
            RemoveAt(leaf, i);
            bool underfull = leaf.Count < _mergeBelow;   // lazier trigger -> no thrash (fix A)
            leaf.WriteUnlock();
            leaf.Counter.Decrement();
            if (underfull) TryMergeUnderfullLeaf(key);    // best-effort space reclamation (sibling merge)
            return true;
        }
    }

    // Value equality under the leaf's write lock; a throwing Equals must not leave the leaf locked.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ValueEqualsLocked(Leaf leaf, int i, TValue expected)
    {
        try { return EqualityComparer<TValue>.Default.Equals(leaf.Values[i], expected); }
        catch { leaf.WriteUnlock(); throw; }
    }

    // Remove entry i from a write-locked leaf.
    private static void RemoveAt(Leaf leaf, int i)
    {
        int n = leaf.Count;
        Array.Copy(leaf.Keys, i + 1, leaf.Keys, i, n - i - 1);
        Array.Copy(leaf.Values, i + 1, leaf.Values, i, n - i - 1);
        leaf.Count = n - 1;
        leaf.Keys[n - 1] = default!;
        leaf.Values[n - 1] = default!;
    }

    // =====================================================================
    //  Space reclamation — LEAF SIBLING MERGE so delete-heavy churn doesn't leak nodes.
    //
    //  Lazy delete leaves underfull leaves; under scattered deletion a leaf rarely empties outright,
    //  so unlinking only EMPTY leaves reclaims almost nothing. Here, a delete that leaves a leaf below
    //  half-full triggers a merge with an adjacent sibling: the right node's entries are appended to
    //  the left node and the right node + its separator are removed from the parent. Two ≤half-full
    //  leaves always fit (sum < _max), so scattered churn coalesces back toward ~half occupancy and
    //  the leaf count tracks the live set — not the build-time peak.
    //
    //  Concurrency: we DON'T lock the root (that would serialize every merge and fight every root
    //  split — wrecking write scalability). We optimistically descend to the leaf's PARENT, upgrade
    //  just that parent, then lock the adjacent leaf pair in the tree's global order (parent ↓, then
    //  leaves strictly left→right: leftNode, rightNode, rightNode.Next). Same discipline SplitLeaf
    //  uses, so it can't deadlock with concurrent splits/inserts. All three leaf locks are held across
    //  the whole mutation, so a scanner sees the trio either fully pre- or fully post-merge.
    //
    //  Scan-safety: right's entries move INTO left, so a scan that snapshotted left's OLD entries must
    //  not then skip right. Two things guarantee no missed key: (1) we never destroy right's entries —
    //  it stays readable for a scanner that followed the old left.Next==right; (2) scans read each
    //  leaf's entries and its Next/Prev atomically under one version (see SnapshotLeaf), so the link a
    //  scan follows always matches the entries it just saw. When a leaf merge would underflow a non-root
    //  parent, we hand off to MergePessimistic, which cascades the merge up the tree and collapses a
    //  single-child root — so a full drain returns all the way to a single-leaf root with no skeleton.
    // =====================================================================

    // Optimistic descent that stops at the leaf's PARENT. Returns false (caller gives up — this is
    // best-effort) if the root is a bare leaf, or a node was locked / shifted mid-descent.
    private bool TryDescendToParent(TKey key, out Internal parent, out int ci, out long pver, out Leaf leaf, out long lver)
    {
        var pos = new ReadPos();
        try { return DescendToParentCore(key, ref pos, out parent, out ci, out pver, out leaf, out lver); }
        catch when (pos.Torn) { parent = null!; ci = 0; pver = 0; leaf = null!; lver = 0; return false; }
    }

    private bool DescendToParentCore(TKey key, ref ReadPos pos, out Internal parent, out int ci, out long pver, out Leaf leaf, out long lver)
    {
        parent = null!; ci = 0; pver = 0; leaf = null!; lver = 0;
        Node cur = _root;
        if (cur is not Internal) return false;                   // no parent to edit (root is a leaf)
        if (!cur.TryReadVersion(out long v)) return false;
        if (cur != _root) return false;
        var par = (Internal)cur; long pv = v;
        for (; ; )
        {
            if (!_cmpNeverThrows) { pos.Node = par; pos.Version = pv; }
            int index = ChildIndex(par.Keys, par.Count, key);
            Node child = par.Children[index];
            if (!par.Validate(pv)) return false;                 // routing read consistent
            if (!child.TryReadVersion(out long cv)) return false;
            if (!par.Validate(pv)) return false;                 // parent unchanged THROUGH the child read
            if (child is Leaf lf) { parent = par; ci = index; pver = pv; leaf = lf; lver = cv; return true; }
            par = (Internal)child; pv = cv;
        }
    }

    private void TryMergeUnderfullLeaf(TKey key)
    {
        // A single merge can leave the survivor still underfull (e.g. two empty leaves -> one empty leaf),
        // which nothing else would re-trigger (an empty leaf never receives another delete). So keep
        // merging toward `key` until the surviving leaf is at least half full or no further merge applies.
        for (int guard = 0; guard < 64 && MergeUnderfullLeafOnce(key); guard++) { }
    }

    // One merge step for the leaf on the path to `key`. Returns true iff it merged AND the survivor is
    // still underfull (so the caller should loop). Returns false when the leaf is already healthy, the
    // sibling is too full to merge, or the work was handed to the pessimistic cascade.
    private bool MergeUnderfullLeafOnce(TKey key)
    {
        for (int attempt = 0; attempt < 2; attempt++)           // bounded: contended parent -> brief retry
        {
            if (!TryDescendToParent(key, out var parent, out int ci, out long pv, out _, out _))
                return false;

            if (!parent.TryUpgrade(pv)) continue;               // parent changed under us -> retry
            int pc = parent.Count;                              // separators; children = pc+1 (stable: parent locked)
            bool parentIsRoot = ReferenceEquals(parent, _root);
            // Cascade cases — merging the leaf here would underflow a NON-root parent, or the parent is
            // already a single-child skeleton: hand off to the latch-coupled path that merges up to the
            // root and collapses a single-child root. The fast path below handles the common case where
            // the parent stays healthy (or is the root) with no root lock.
            if (pc == 0 || (pc <= _min && !parentIsRoot))
            {
                parent.WriteUnlock();
                return MergePessimistic(key);                   // returns whether the survivor stays underfull
            }

            // Adjacent pair (li, li+1): merge the underfull leaf with its right sibling, or — if it's the
            // rightmost child — merge it into its left sibling. Either way li < li+1 (left→right).
            int li = (ci < pc) ? ci : ci - 1;
            var left = (Leaf)parent.Children[li];
            var right = (Leaf)parent.Children[li + 1];

            left.WriteLock();                                   // left sibling first (global left→right order)
            right.WriteLock();
            var target = (ci == li) ? left : right;             // the leaf on the path to `key`
            if (target.Count >= _min || left.Count + right.Count > _max)
            {                                                   // already healthy, or sibling too full to merge
                right.WriteUnlock(); left.WriteUnlock(); parent.WriteUnlock();
                return false;
            }
            Leaf? rnext = Volatile.Read(ref right.Next);
            if (rnext != null) rnext.WriteLock();

            // append right's entries to left (left's keys are all < right's, so order is preserved)
            int lc = left.Count, rc = right.Count;
            Array.Copy(right.Keys, 0, left.Keys, lc, rc);
            Array.Copy(right.Values, 0, left.Values, lc, rc);
            Volatile.Write(ref left.Next, rnext);               // splice right out of the forward chain
            if (rnext != null) Volatile.Write(ref rnext.Prev, left);
            left.Count = lc + rc;                               // publish count LAST (readers re-validate version)
            Interlocked.Increment(ref _merges);
            // (right keeps its entries + Next/Prev intact: a scan that already followed left.Next==right
            //  still reads right's keys; dedup tolerates the transient overlap with left.)

            RemoveChild(parent, li + 1);                        // drop right child + separator Keys[li]

            bool collapseRoot = ReferenceEquals(parent, _root) && parent.Count == 0;
            Node? newRoot = collapseRoot ? parent.Children[0] : null;
            bool survivorUnderfull = left.Count < _min && !collapseRoot;

            if (rnext != null) rnext.WriteUnlock();
            right.WriteUnlock();
            left.WriteUnlock();
#pragma warning disable CS0420
            // publish new root while still holding old root's lock; CAS so a concurrent Clear() is never undone
            if (collapseRoot) Interlocked.CompareExchange(ref _root, newRoot!, parent);
#pragma warning restore CS0420
            parent.WriteUnlock();
            return survivorUnderfull;
        }
        return false;
    }

    // Remove child pointer `ci` (0..Count) and its adjacent separator from an internal node.
    private static void RemoveChild(Internal n, int ci)
    {
        int c = n.Count;                                         // c separators, c+1 children
        int sep = ci > 0 ? ci - 1 : 0;                           // separator that bounded the removed child
        Array.Copy(n.Keys, sep + 1, n.Keys, sep, c - 1 - sep);
        Array.Copy(n.Children, ci + 1, n.Children, ci, c - ci);
        n.Count = c - 1;
        Array.Clear(n.Keys, c - 1, 1);
        Array.Clear(n.Children, c, 1);
    }

    // =====================================================================
    //  Recursive (cascading) merge — eliminates the single-child internal skeleton that leaf merge
    //  alone leaves behind. Used as the FALLBACK when a leaf merge would underflow a non-root parent.
    //
    //  Pessimistic latch-coupling, mirroring InsertPessimistic but for deletion: descend from the root
    //  locking each node, and RELEASE all ancestors once we reach a "delete-safe" one (Count > _min —
    //  it can absorb one child removal without underflowing), so the root lock is dropped in the common
    //  case. We merge the leaf pair, then walk the held chain UP, merging each underfull internal with a
    //  sibling (which removes a child from its parent) until a node stays >= _min, then collapse a
    //  single-child root. The deepest safe ancestor caps the cascade, so we never need a lock above what
    //  we hold. Lock order is the tree's global order (parent ↓, siblings left→right), so no deadlock
    //  with splits/inserts/other merges.
    // =====================================================================
    // Returns true iff the merged leaf survivor is still underfull (caller should loop another merge).
    private bool MergePessimistic(TKey key)
    {
        var path = RentPath();
        try
        {
            Node root = LockRoot();
            if (root is Leaf) { root.WriteUnlock(); return false; } // shrank to a single leaf — nothing to merge
            path.Push(root, -1);
            Node cur = root;
            int leafIdx;

            // Descend to the leaf's parent, latch-coupling with delete-safe ancestor release. Child indices are
            // recorded so nothing below calls the comparer once we start mutating.
            try
            {
                while (true)
                {
                    var ind = (Internal)cur;
                    int ci = ChildIndex(ind.Keys, ind.Count, key);
                    Node child = ind.Children[ci];
                    if (child is Leaf) { leafIdx = ci; break; }      // `ind` is the leaf's parent
                    child.WriteLock();
                    if (((Internal)child).Count > _min) path.UnlockAll();   // delete-safe: absorbs one child removal
                    path.Push(child, ci);
                    cur = child;
                }
            }
            catch { path.UnlockAll(); throw; }

            int hc = path.Count;
            Node[] held = path.Nodes;
            var P = (Internal)held[hc - 1];                         // leaf's parent (locked)
            // Every held node stays LOCKED through the whole operation (MergeInternalLevel re-locks the node
            // it touches), so we just unlock the entire held chain at the end.

            // --- leaf level: merge the underfull leaf with a sibling (if it has one and they fit) ---
            bool survivorUnderfull = false;
            int pc = P.Count;
            if (pc > 0)
            {
                int lo = (leafIdx < pc) ? leafIdx : leafIdx - 1, hi = lo + 1;
                var left = (Leaf)P.Children[lo];
                var right = (Leaf)P.Children[hi];
                left.WriteLock();
                right.WriteLock();
                if (left.Count + right.Count <= _max)
                {
                    Leaf? rnext = Volatile.Read(ref right.Next);
                    if (rnext != null) rnext.WriteLock();
                    int lc = left.Count, rc = right.Count;
                    Array.Copy(right.Keys, 0, left.Keys, lc, rc);
                    Array.Copy(right.Values, 0, left.Values, lc, rc);
                    Volatile.Write(ref left.Next, rnext);
                    if (rnext != null) Volatile.Write(ref rnext.Prev, left);
                    left.Count = lc + rc;
                    Interlocked.Increment(ref _merges);
                    RemoveChild(P, hi);                             // P.Count--
                    survivorUnderfull = left.Count < _min;         // still underfull? caller loops another merge
                    if (rnext != null) rnext.WriteUnlock();
                    right.WriteUnlock();
                    left.WriteUnlock();
                }
                else
                {
                    right.WriteUnlock(); left.WriteUnlock();        // sibling too full to merge -> give up
                    path.UnlockAll();
                    return false;
                }
            }
            // (pc == 0: P is a single-child skeleton internal; no leaf merge, but P is underfull -> propagate.)

            // --- propagate up: merge each underfull internal with a sibling until one stays healthy ---
            int level = hc - 1;
            while (level >= 1)
            {
                if (((Internal)held[level]).Count >= _min) break;   // healthy -> stop
                var gp = (Internal)held[level - 1];
                if (gp.Count == 0) { level--; continue; }           // gp single-child: no sibling for held[level] here;
                                                                    // gp is itself underfull -> handle it one level up
                int idx = path.Idx[level];                          // held[level]'s index within gp (recorded on descent)
                if (!MergeInternalLevel(gp, idx, (Internal)held[level])) break;   // sibling too full -> stop
                level--;                                            // gp.Count-- ; keep climbing
            }

            // --- root collapse: a single-child internal root becomes its only child ---
#pragma warning disable CS0420
            if (ReferenceEquals(held[0], _root) && held[0] is Internal ri && ri.Count == 0)
                Interlocked.CompareExchange(ref _root, ri.Children[0], ri);   // CAS: never undo a concurrent Clear()
#pragma warning restore CS0420

            path.UnlockAll();                                       // every held node was kept locked
            return survivorUnderfull;
        }
        finally { ReturnPath(path); }
    }

    // Merge the underfull internal `child` (= parent.Children[idx], locked) with an adjacent sibling,
    // chosen as the pair (lo, hi) around idx and locked left→right (in the rightmost case we briefly
    // release+relock `child` to keep that order — safe because holding `parent` pins its child set).
    // The separator parent.Keys[lo] is pulled down between the two halves and the right node is dropped
    // from `parent`. Caller contract: `child` and `parent` REMAIN LOCKED on return (the sibling is
    // unlocked internally); the caller unlocks the whole held chain at the end. Returns false (parent
    // unchanged) if the two wouldn't fit one node. Precondition: parent.Count >= 1 (it has a sibling).
    private bool MergeInternalLevel(Internal parent, int idx, Internal child)
    {
        int gc = parent.Count;
        int lo = (idx < gc) ? idx : idx - 1, hi = lo + 1;
        if (lo == idx)                                          // child is the LEFT member of the pair
        {
            Internal L = child;                                // already locked
            var R = (Internal)parent.Children[hi];
            R.WriteLock();                                      // right sibling — locked after child (order ok)
            if (L.Count + 1 + R.Count > _max) { R.WriteUnlock(); return false; }
            MergeInternalInto(L, parent.Keys[lo], R);
            RemoveChild(parent, hi);                            // drop the sibling R; child (L) survives, stays locked
            R.WriteUnlock();
            return true;
        }
        else                                                   // child is the rightmost: relock the pair in order
        {
            child.WriteUnlock();                               // release to acquire the left sibling first
            var L = (Internal)parent.Children[lo];
            L.WriteLock();
            var R = (Internal)parent.Children[hi];             // == child, re-locked as the right member
            R.WriteLock();
            if (L.Count + 1 + R.Count > _max) { L.WriteUnlock(); return false; }   // child (R) stays locked
            MergeInternalInto(L, parent.Keys[lo], R);
            RemoveChild(parent, hi);                            // drop R == child (detached but kept locked for caller)
            L.WriteUnlock();                                   // sibling L (the survivor) unlocked
            return true;
        }
    }

    // L absorbs [L's entries, pulled-down separator, R's entries]; their child arrays concatenate.
    private static void MergeInternalInto(Internal L, TKey separator, Internal R)
    {
        int lc = L.Count, rc = R.Count;
        L.Keys[lc] = separator;
        Array.Copy(R.Keys, 0, L.Keys, lc + 1, rc);
        Array.Copy(R.Children, 0, L.Children, lc + 1, rc + 1);
        L.Count = lc + 1 + rc;
    }

    // =====================================================================
    //  Conditional replace (TryUpdate) — lock the leaf and CAS the value in place.
    // =====================================================================
    private bool DoReplace(TKey key, TValue newValue, bool hasComparison, TValue comparison)
    {
        if (key == null) ThrowKeyNull();
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v) || !leaf.TryUpgrade(v)) continue;
            int i = FindLocked(leaf, key);
            if (i >= 0 && hasComparison && !ValueEqualsLocked(leaf, i, comparison)) i = -1;
            if (i < 0) { leaf.WriteUnlock(); return false; }
            leaf.Values[i] = newValue;
            leaf.WriteUnlock();
            return true;
        }
    }

    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue) => DoReplace(key, newValue, true, comparisonValue);

    // =====================================================================
    //  NavigableMap: relational queries + first/last + poll, via the leaf chain.
    // =====================================================================

    // first index with keys[i] >= key (lower bound); ChildIndex already gives the upper bound (> key).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LowerBound(TKey[] keys, int count, TKey key)
    {
        if (s_integralKey && _defaultCmp) return BranchlessBound(keys, count, key, strict: false);
        int lo = 0, hi = count;
        while (lo < hi) { int mid = (int)(((uint)lo + (uint)hi) >> 1); if (Compare(keys[mid], key) < 0) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // Optimistic descent to the leftmost (or rightmost) leaf. Re-checks the root after reading its version:
    // without that, a reader that grabbed the old root just before a root split would descend into what is
    // now only the LEFT half and report its rightmost leaf as the tree's last leaf.
    private Leaf EdgeLeaf(bool rightmost, out long version)
    {
        for (; ; )
        {
            Node cur = _root;
            if (!cur.TryReadVersion(out long v) || cur != _root) continue;
            while (cur is Internal ind)
            {
                Node child = ind.Children[rightmost ? ind.Count : 0];
                if (!ind.Validate(v)) goto restart;
                if (!child.TryReadVersion(out long cv)) goto restart;
                if (!ind.Validate(v)) goto restart;
                cur = child; v = cv;
            }
            version = v;
            return (Leaf)cur;
        restart:;
        }
    }

    private Leaf LeftmostLeaf() => EdgeLeaf(false, out _);
    private Leaf RightmostLeaf() => EdgeLeaf(true, out _);

    // The first (forward) or last (backward) entry of the leaf chain starting at `cur`, read as ONE atomic
    // snapshot together with `origin` (the leaf the query started in, observed at version `ov`). Every leaf
    // passed over (empty) is re-validated at the end, so the answer reflects a single instant: no key can
    // have appeared in `origin` or a skipped leaf while we were looking further along the chain.
    // Returns 1 = found, 0 = chain exhausted (no entry), -1 = something moved, caller must retry.
    private int EdgeOfChain(Leaf? cur, bool forward, Leaf origin, long ov, out KeyValuePair<TKey, TValue> e)
    {
        LockPath? passed = null;
        int result;
        e = default;
        var spin = new SpinWait();
        for (; ; )
        {
            if (cur == null) { result = 0; break; }
            if (!cur.TryReadVersion(out long cv)) { spin.SpinOnce(-1); continue; }
            int cnt = Volatile.Read(ref cur.Count);
            if (cnt > 0)
            {
                int j = forward ? 0 : cnt - 1;
                var kv = new KeyValuePair<TKey, TValue>(cur.Keys[j], cur.Values[j]);
                if (!cur.Validate(cv)) continue;
                e = kv; result = 1; break;
            }
            Leaf? nx = forward ? Volatile.Read(ref cur.Next) : Volatile.Read(ref cur.Prev);
            if (!cur.Validate(cv)) continue;
            (passed ??= RentPath()).Push(cur, 0, cv);
            cur = nx;
        }
        bool ok = origin.Validate(ov);
        if (passed != null)
        {
            for (int i = 0; ok && i < passed.Count; i++) ok = passed.Nodes[i].Validate(passed.Ver[i]);
            ReturnPath(passed);
        }
        if (!ok) { e = default; return -1; }
        return result;
    }

    private bool Edge(bool first, out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; )
        {
            var leaf = EdgeLeaf(!first, out long v);
            int r = EdgeOfChain(leaf, first, leaf, v, out entry);
            if (r >= 0) return r == 1;
        }
    }

    public bool TryGetFirst(out KeyValuePair<TKey, TValue> entry) => Edge(first: true, out entry);
    public bool TryGetLast(out KeyValuePair<TKey, TValue> entry) => Edge(first: false, out entry);

    /// <summary>Least entry with key ≥ <paramref name="key"/>. (ceilingEntry.)</summary>
    public bool TryGetCeiling(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: false, inclusive: true, out entry);
    /// <summary>Least entry with key &gt; <paramref name="key"/>. (higherEntry.)</summary>
    public bool TryGetHigher(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: false, inclusive: false, out entry);
    /// <summary>Greatest entry with key ≤ <paramref name="key"/>. (floorEntry.)</summary>
    public bool TryGetFloor(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: true, inclusive: true, out entry);
    /// <summary>Greatest entry with key &lt; <paramref name="key"/>. (lowerEntry.)</summary>
    public bool TryGetLower(TKey key, out KeyValuePair<TKey, TValue> entry) => Relational(key, lower: true, inclusive: false, out entry);

    public bool TryGetCeilingKey(TKey key, out TKey k) => Proj(TryGetCeiling(key, out var e), e, out k);
    public bool TryGetHigherKey(TKey key, out TKey k) => Proj(TryGetHigher(key, out var e), e, out k);
    public bool TryGetFloorKey(TKey key, out TKey k) => Proj(TryGetFloor(key, out var e), e, out k);
    public bool TryGetLowerKey(TKey key, out TKey k) => Proj(TryGetLower(key, out var e), e, out k);
    private static bool Proj(bool ok, KeyValuePair<TKey, TValue> e, out TKey k) { k = ok ? e.Key : default!; return ok; }

    // The leaf that owns `key` answers the query if it holds a key on the requested side; otherwise the
    // answer is the nearest entry in the neighbouring leaves, which EdgeOfChain reads atomically WITH this
    // leaf (re-validating it), so a key inserted here meanwhile can't be skipped over.
    private bool Relational(TKey key, bool lower, bool inclusive, out KeyValuePair<TKey, TValue> entry)
    {
        if (key == null) ThrowKeyNull();
        for (; ; )
        {
            if (!TryDescendToLeaf(key, out var leaf, out long v)) continue;
            int cnt = Volatile.Read(ref leaf.Count);
            int i = NearIndex(leaf, v, cnt, key, lower, inclusive);
            if (i == int.MinValue) continue;                     // comparer fed a torn read -> retry

            if (lower ? i >= 0 : i < cnt)
            {
                var kv = new KeyValuePair<TKey, TValue>(leaf.Keys[i], leaf.Values[i]);
                if (!leaf.Validate(v)) continue;
                entry = kv;
                return true;
            }
            Leaf? nb = lower ? Volatile.Read(ref leaf.Prev) : Volatile.Read(ref leaf.Next);
            if (!leaf.Validate(v)) continue;
            int r = EdgeOfChain(nb, forward: !lower, leaf, v, out entry);
            if (r >= 0) return r == 1;
        }
    }

    // Index of the in-leaf candidate (see Relational). int.MinValue = the comparer threw on a torn read.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NearIndex(Leaf leaf, long v, int cnt, TKey key, bool lower, bool inclusive)
        => _cmpNeverThrows ? NearIndexCore(leaf.Keys, cnt, key, lower, inclusive) : NearIndexGuarded(leaf, v, cnt, key, lower, inclusive);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NearIndexCore(TKey[] keys, int cnt, TKey key, bool lower, bool inclusive)
    {
        if (!lower) return inclusive ? LowerBound(keys, cnt, key) : ChildIndex(keys, cnt, key);
        return (inclusive ? ChildIndex(keys, cnt, key) : LowerBound(keys, cnt, key)) - 1;
    }

    private int NearIndexGuarded(Leaf leaf, long v, int cnt, TKey key, bool lower, bool inclusive)
    {
        try { return NearIndexCore(leaf.Keys, cnt, key, lower, inclusive); }
        catch when (!leaf.Validate(v)) { return int.MinValue; }
    }

    /// <summary>Atomically removes and returns the smallest entry.</summary>
    public bool TryRemoveFirst(out KeyValuePair<TKey, TValue> entry) => RemoveEdge(first: true, out entry);

    /// <summary>Atomically removes and returns the largest entry.</summary>
    public bool TryRemoveLast(out KeyValuePair<TKey, TValue> entry) => RemoveEdge(first: false, out entry);

    // Linearizable poll: optimistically find the edge leaf and any (empty) leaves between it and the first
    // non-empty one, then write-lock that whole run by version upgrade in the global left→right order. If
    // every upgrade succeeds, none of those leaves changed since we read them, so — under the locks — the
    // target entry is provably the minimum (maximum) and nothing smaller (larger) can be inserted until we
    // finish removing it.
    private bool RemoveEdge(bool first, out KeyValuePair<TKey, TValue> entry)
    {
        var spin = new SpinWait();
        for (; ; )
        {
            var path = RentPath();
            try
            {
                Leaf? cur = EdgeLeaf(!first, out long cv);
                bool consistent = true;
                while (cur != null)
                {
                    path.Push(cur, 0, cv);
                    int cnt = Volatile.Read(ref cur.Count);
                    Leaf? nx = first ? Volatile.Read(ref cur.Next) : Volatile.Read(ref cur.Prev);
                    if (!cur.Validate(cv)) { consistent = false; break; }
                    if (cnt > 0) break;
                    cur = nx;
                    if (cur != null && !cur.TryReadVersion(out cv)) { consistent = false; break; }
                }
                if (!consistent) { spin.SpinOnce(-1); continue; }

                int n = path.Count;
                if (cur == null)
                {   // every leaf we saw was empty: the map was empty iff none of them changed meanwhile
                    bool same = true;
                    for (int i = 0; same && i < n; i++) same = path.Nodes[i].Validate(path.Ver[i]);
                    if (same) { entry = default; return false; }
                    spin.SpinOnce(-1);
                    continue;
                }

                // path[0] is the edge leaf, path[n-1] the non-empty target. Lock left→right.
                int locked = 0;
                for (; locked < n; locked++)
                {
                    int idx = first ? locked : n - 1 - locked;
                    if (!path.Nodes[idx].TryUpgrade(path.Ver[idx])) break;
                }
                if (locked < n)
                {
                    for (int k = 0; k < locked; k++) path.Nodes[first ? k : n - 1 - k].WriteUnlock();
                    spin.SpinOnce(-1);
                    continue;
                }

                var target = (Leaf)path.Nodes[n - 1];
                int j = first ? 0 : target.Count - 1;
                var kv = new KeyValuePair<TKey, TValue>(target.Keys[j], target.Values[j]);
                RemoveAt(target, j);
                bool underfull = target.Count < _mergeBelow;
                path.UnlockAll();
                target.Counter.Decrement();
                if (underfull) TryMergeUnderfullLeaf(kv.Key);
                entry = kv;
                return true;
            }
            finally { ReturnPath(path); }
        }
    }

    // =====================================================================
    //  Enumeration — weakly consistent, strictly ordered. Snapshots each leaf (keys, values AND its
    //  chain link) under one optimistic read and only yields keys strictly beyond the last yielded (so a
    //  key that a concurrent split/merge made visible in two leaves is yielded at most once).
    // =====================================================================

    /// <summary>Ascending enumeration of the live map. Weakly consistent: never throws on concurrent
    /// modification, strictly ascending, not a point-in-time snapshot. Returns a struct enumerator;
    /// <c>foreach</c> over the concrete type binds to it directly without boxing.</summary>
    public Enumerator GetEnumerator() => new(this, false, default!, false, false, default!, false, descending: false);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();

    /// <summary>Struct enumerator over the map or a <see cref="RangeView"/> (ascending or descending).
    /// Per element it does no comparer calls in the common case: the range bounds and the cross-leaf
    /// de-duplication are checked once per leaf, plus only at the leaf's edge where they can matter.</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly ConcurrentBTreeDictionary<TKey, TValue> _p;
        private readonly TKey _lo, _hi;
        private readonly bool _hasLo, _loInc, _hasHi, _hiInc, _desc;
        private Leaf? _leaf;             // next leaf to snapshot
        private TKey[]? _k;              // snapshot buffers (allocated on first MoveNext)
        private TValue[]? _v;
        private int _i, _n;              // cursor into the current snapshot (ascending: _i.._n-1; descending: _i..0)
        private bool _started, _done, _have, _dedup, _checkFar, _checkNear;
        private TKey _last;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(ConcurrentBTreeDictionary<TKey, TValue> p, bool hasLo, TKey lo, bool loInc, bool hasHi, TKey hi, bool hiInc, bool descending)
        {
            _p = p; _hasLo = hasLo; _lo = lo; _loInc = loInc; _hasHi = hasHi; _hi = hi; _hiInc = hiInc; _desc = descending;
            _leaf = null; _k = null; _v = null; _i = 0; _n = 0;
            _started = _done = _have = _dedup = _checkFar = _checkNear = false;
            _last = default!; _current = default;
        }

        private readonly bool TooLow(TKey k) { int c = _p.Compare(k, _lo); return c < 0 || (c == 0 && !_loInc); }
        private readonly bool TooHigh(TKey k) { int c = _p.Compare(k, _hi); return c > 0 || (c == 0 && !_hiInc); }

        public bool MoveNext()
        {
            if (_done) return false;
            if (!_started)
            {
                _started = true;
                var p = _p;
                _leaf = !_desc ? (_hasLo ? p.LeafForKey(_lo) : p.LeftmostLeaf())
                               : (_hasHi ? p.LeafForKey(_hi) : p.RightmostLeaf());
                _k = GC.AllocateUninitializedArray<TKey>(p._max + 1);
                _v = GC.AllocateUninitializedArray<TValue>(p._max + 1);
            }
            for (; ; )
            {
                if (!_desc)
                {
                    while (_i < _n)
                    {
                        int i = _i++;
                        TKey k = _k![i];
                        // near side: before the first yield, skip keys below the start bound; afterwards, skip
                        // keys a concurrent split/merge let us see twice. Keys within a snapshot ascend, so
                        // once one key passes, the rest of this leaf passes too.
                        if (_checkNear)
                        {
                            if (_have ? _p.Compare(k, _last) <= 0 : (_hasLo && TooLow(k))) continue;
                            _checkNear = false;
                        }
                        if (_checkFar && TooHigh(k)) { Finish(); return false; }
                        _last = k; _have = true;
                        _current = new KeyValuePair<TKey, TValue>(k, _v![i]);
                        return true;
                    }
                }
                else
                {
                    while (_i >= 0 && _i < _n)
                    {
                        int i = _i--;
                        TKey k = _k![i];
                        if (_checkNear)
                        {
                            if (_have ? _p.Compare(k, _last) >= 0 : (_hasHi && TooHigh(k))) continue;
                            _checkNear = false;
                        }
                        if (_checkFar && TooLow(k)) { Finish(); return false; }
                        _last = k; _have = true;
                        _current = new KeyValuePair<TKey, TValue>(k, _v![i]);
                        return true;
                    }
                }

                var leaf = _leaf;
                if (leaf == null) { Finish(); return false; }
                _n = _p.SnapshotLeaf(leaf, _k!, _v!, out var next, out var prev);
                _leaf = _desc ? prev : next;                             // read atomically with the snapshot
                if (_n == 0) continue;
                if (!_desc)
                {
                    _i = 0;
                    _checkNear = _have || _hasLo;
                    _checkFar = _hasHi && TooHigh(_k![_n - 1]);         // whole leaf inside the bound? no per-key check
                }
                else
                {
                    _i = _n - 1;
                    _checkNear = _have || _hasHi;
                    _checkFar = _hasLo && TooLow(_k![0]);
                }
            }
        }

        private void Finish() { _done = true; _leaf = null; _k = null; _v = null; _current = default; }

        public readonly KeyValuePair<TKey, TValue> Current => _current;
        readonly object IEnumerator.Current => _current;
        public void Reset() { _started = false; _done = false; _have = false; _leaf = null; _i = _n = 0; _current = default; }
        public void Dispose() { _leaf = null; _k = null; _v = null; }
    }

    // Snapshot a leaf's keys/values AND its chain links under a SINGLE validated version. Reading the
    // neighbour pointer in the same validated window is what makes scans merge-safe: a merge moves
    // right's keys into left and repoints left.Next past right, all under left's lock (one version bump).
    // If a scan snapshotted left's OLD keys it must NOT then follow left's NEW Next (it would skip
    // right's keys, which weren't in the old snapshot) — so we re-read Next/Prev inside the validated
    // block and retry on any version change, yielding a consistent (keys, links) pair.
    private int SnapshotLeaf(Leaf leaf, TKey[] bufK, TValue[] bufV, out Leaf? next, out Leaf? prev)
    {
        for (; ; )
        {
            if (!leaf.TryReadVersion(out long v)) continue;
            int n = Volatile.Read(ref leaf.Count);
            if (n > bufK.Length) n = bufK.Length;
            Array.Copy(leaf.Keys, bufK, n);
            Array.Copy(leaf.Values, bufV, n);
            next = Volatile.Read(ref leaf.Next);
            prev = Volatile.Read(ref leaf.Prev);
            if (leaf.Validate(v)) return n;   // Validate carries the LoadLoad fence (seqlock read)
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- snapshots (ascending), like ConcurrentDictionary ----
    public ICollection<TKey> Keys { get { var l = new List<TKey>(); foreach (var kv in this) l.Add(kv.Key); return l; } }
    public ICollection<TValue> Values { get { var l = new List<TValue>(); foreach (var kv in this) l.Add(kv.Value); return l; } }
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;
    public IEnumerable<TKey> DescendingKeys { get { foreach (var kv in Reverse()) yield return kv.Key; } }

    // =====================================================================
    //  Clear, conveniences, functional helpers (ConcurrentDictionary parity)
    // =====================================================================

    /// <summary>Removes all entries by swapping in a fresh, empty tree (O(1)). Linearizes at the swap. A writer
    /// already in flight on the old tree completes against it (it linearizes before the clear); its effect,
    /// and its contribution to <see cref="Count"/>, are discarded with that tree. Root publication elsewhere
    /// is compare-and-swap, so an in-flight split/merge can never re-install the old tree.</summary>
    public void Clear()
    {
        var counter = new StripedCounter();
        var fresh = NewLeaf(counter);
        // Serialize clears: Clear is the only writer of (_root generation, _count), and two overlapping
        // clears must not pair one call's root with the other call's counter.
        lock (_clearGate)
        {
            _count = counter;
            _root = fresh;
        }
    }

    public IComparer<TKey> Comparer => _cmp;

    public bool ContainsValue(TValue value)
    {
        var cmp = EqualityComparer<TValue>.Default;
        foreach (var kv in this) if (cmp.Equals(kv.Value, value)) return true;
        return false;
    }

    public TValue GetValueOrDefault(TKey key, TValue defaultValue) => TryGetValue(key, out var v) ? v : defaultValue;
    public TValue? GetValueOrDefault(TKey key) => TryGetValue(key, out var v) ? v : default;

    public bool TryReplace(TKey key, TValue newValue, out TValue previous)
    {
        for (; ; )
        {
            if (!TryGetValue(key, out var cur)) { previous = default!; return false; }
            if (DoReplace(key, newValue, true, cur)) { previous = cur; return true; }
        }
    }

    public TValue GetOrAdd(TKey key, TValue value)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var v)) return v;
            if (TryAdd(key, value)) return value;
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var v)) return v;
            var nv = valueFactory(key);
            if (TryAdd(key, nv)) return nv;
        }
    }


    public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var cur))
            {
                var nv = updateValueFactory(key, cur);
                if (DoReplace(key, nv, true, cur)) return nv;
            }
            else if (TryAdd(key, addValue)) return addValue;
        }
    }

    public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
    {
        for (; ; )
        {
            if (TryGetValue(key, out var cur))
            {
                var nv = updateValueFactory(key, cur);
                if (DoReplace(key, nv, true, cur)) return nv;
            }
            else { var add = addValueFactory(key); if (TryAdd(key, add)) return add; }
        }
    }

    public void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> items)
    {
        foreach (var kv in items) this[kv.Key] = kv.Value;
    }

    // =====================================================================
    //  IDictionary<TKey,TValue> / ICollection<KeyValuePair<,>>
    // =====================================================================
    public void Add(TKey key, TValue value)
    {
        if (!TryAdd(key, value)) throw new ArgumentException($"An item with the same key already exists. Key: {key}", nameof(key));
    }

    public bool Remove(TKey key) => TryRemove(key, out _);
    public bool IsReadOnly => false;
    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<TKey, TValue> item)
        => TryGetValue(item.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, item.Value);
    public bool Remove(KeyValuePair<TKey, TValue> item) => TryRemove(item);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        foreach (var kv in this)
        {
            if (arrayIndex >= array.Length) throw new ArgumentException("Destination array is not long enough.");
            array[arrayIndex++] = kv;
        }
    }

    // =====================================================================
    //  Native bounded range scans (the B+-tree's strength): walk the leaf chain
    //  forward (or backward via Prev), snapshotting each leaf, filtering to the
    //  bounds, deduping to stay strictly monotone under concurrent splits.
    // =====================================================================
    private Leaf LeafForKey(TKey key) { for (; ; ) if (TryDescendToLeaf(key, out var leaf, out _)) return leaf; }

    // =====================================================================
    //  NavigableMap views
    // =====================================================================
    public RangeView GetViewBetween(TKey fromKey, TKey toKey) => GetViewBetween(fromKey, true, toKey, false);
    public RangeView GetViewBetween(TKey fromKey, bool fromInclusive, TKey toKey, bool toInclusive)
        => new(this, true, fromKey, fromInclusive, true, toKey, toInclusive, descending: false);
    public RangeView GetViewTo(TKey toKey, bool inclusive = false)
        => new(this, false, default!, false, true, toKey, inclusive, descending: false);
    public RangeView GetViewFrom(TKey fromKey, bool inclusive = true)
        => new(this, true, fromKey, inclusive, false, default!, false, descending: false);
    public RangeView Reverse()
        => new(this, false, default!, false, false, default!, false, descending: true);

    /// <summary>A live, navigable view over a key range (and/or reversed). Reads/scans use the native
    /// leaf chain; writes are bounds-checked and delegate to the parent. Weakly consistent, like the parent.</summary>
    public sealed class RangeView : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        private readonly ConcurrentBTreeDictionary<TKey, TValue> _p;
        private readonly bool _hasLo, _loInc, _hasHi, _hiInc, _desc;
        private readonly TKey _lo, _hi;

        internal RangeView(ConcurrentBTreeDictionary<TKey, TValue> p, bool hasLo, TKey lo, bool loInc, bool hasHi, TKey hi, bool hiInc, bool descending)
        { _p = p; _hasLo = hasLo; _lo = lo; _loInc = loInc; _hasHi = hasHi; _hi = hi; _hiInc = hiInc; _desc = descending; }

        private bool TooLow(TKey k) { if (!_hasLo) return false; int c = _p.Compare(k, _lo); return c < 0 || (c == 0 && !_loInc); }
        private bool TooHigh(TKey k) { if (!_hasHi) return false; int c = _p.Compare(k, _hi); return c > 0 || (c == 0 && !_hiInc); }
        private bool InRange(TKey k) => !TooLow(k) && !TooHigh(k);
        private void CheckRange(TKey k) { if (!InRange(k)) throw new ArgumentOutOfRangeException(nameof(k), $"Key {k} is outside the sub-dictionary range."); }

        /// <summary>Allocation-light struct enumerator over the view, in view order (ascending, or descending
        /// for a reversed view). Walks the native leaf chain.</summary>
        public Enumerator GetEnumerator() => new(_p, _hasLo, _lo, _loInc, _hasHi, _hi, _hiInc, _desc);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ascending-order first/last in range
        private bool AscFirst(out KeyValuePair<TKey, TValue> e)
        {
            bool ok = _hasLo ? (_loInc ? _p.TryGetCeiling(_lo, out e) : _p.TryGetHigher(_lo, out e)) : _p.TryGetFirst(out e);
            if (ok && TooHigh(e.Key)) { e = default; return false; }
            return ok;
        }
        private bool AscLast(out KeyValuePair<TKey, TValue> e)
        {
            bool ok = _hasHi ? (_hiInc ? _p.TryGetFloor(_hi, out e) : _p.TryGetLower(_hi, out e)) : _p.TryGetLast(out e);
            if (ok && TooLow(e.Key)) { e = default; return false; }
            return ok;
        }
        private bool RangeCeiling(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetCeiling(key, out var c)) { e = default; return false; } if (TooLow(c.Key)) return AscFirst(out e); if (TooHigh(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeHigher(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetHigher(key, out var c)) { e = default; return false; } if (TooLow(c.Key)) return AscFirst(out e); if (TooHigh(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeFloor(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetFloor(key, out var c)) { e = default; return false; } if (TooHigh(c.Key)) return AscLast(out e); if (TooLow(c.Key)) { e = default; return false; } e = c; return true; }
        private bool RangeLower(TKey key, out KeyValuePair<TKey, TValue> e)
        { if (!_p.TryGetLower(key, out var c)) { e = default; return false; } if (TooHigh(c.Key)) return AscLast(out e); if (TooLow(c.Key)) { e = default; return false; } e = c; return true; }

        // view-order navigable (inverted when descending)
        public bool TryGetFirst(out KeyValuePair<TKey, TValue> e) => _desc ? AscLast(out e) : AscFirst(out e);
        public bool TryGetLast(out KeyValuePair<TKey, TValue> e) => _desc ? AscFirst(out e) : AscLast(out e);
        public bool TryGetCeiling(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeFloor(key, out e) : RangeCeiling(key, out e);
        public bool TryGetHigher(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeLower(key, out e) : RangeHigher(key, out e);
        public bool TryGetFloor(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeCeiling(key, out e) : RangeFloor(key, out e);
        public bool TryGetLower(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeHigher(key, out e) : RangeLower(key, out e);

        public bool ContainsKey(TKey key) => InRange(key) && _p.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value) { if (InRange(key)) return _p.TryGetValue(key, out value); value = default!; return false; }
        public bool IsEmpty { get { using var e = GetEnumerator(); return !e.MoveNext(); } }
        public int Count { get { int n = 0; foreach (var _ in this) n++; return n; } }
        public bool IsReadOnly => false;

        public ICollection<TKey> Keys { get { var l = new List<TKey>(); foreach (var kv in this) l.Add(kv.Key); return l; } }
        public ICollection<TValue> Values { get { var l = new List<TValue>(); foreach (var kv in this) l.Add(kv.Value); return l; } }
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        public TValue this[TKey key]
        {
            get => InRange(key) && _p.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
            set { CheckRange(key); _p[key] = value; }
        }
        public void Add(TKey key, TValue value) { CheckRange(key); _p.Add(key, value); }
        public bool TryAdd(TKey key, TValue value) { CheckRange(key); return _p.TryAdd(key, value); }
        public bool Remove(TKey key) => InRange(key) && _p.Remove(key);
        public void Clear() { foreach (var k in Keys) _p.Remove(k); }
        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public bool Contains(KeyValuePair<TKey, TValue> item) => InRange(item.Key) && _p.Contains(item);
        public bool Remove(KeyValuePair<TKey, TValue> item) => InRange(item.Key) && _p.Remove(item);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            foreach (var kv in this) { if (arrayIndex >= array.Length) throw new ArgumentException("Destination array is not long enough."); array[arrayIndex++] = kv; }
        }
        public RangeView Reverse() => new(_p, _hasLo, _lo, _loInc, _hasHi, _hi, _hiInc, !_desc);
    }

    /// <summary>Test hook: total keys actually present in the leaf chain (quiescent only).</summary>
    public long CountLeafKeys()
    {
        long sum = 0;
        Node cur = _root;
        while (cur is Internal ind) cur = ind.Children[0];
        for (var leaf = (Leaf)cur; leaf != null; leaf = leaf.Next) sum += leaf.Count;
        return sum;
    }

    /// <summary>
    /// Quiescent structural check (call with no concurrent ops). Verifies the B+-tree invariants:
    /// (1) BALANCE — every leaf is at the same depth; (2) keys strictly ascending within each node
    /// and inside their separator window; (3) the leaf sibling chain is globally strictly ascending
    /// and complete; (4) leaf-key total == Count. Note: it intentionally does NOT check minimum
    /// occupancy — lazy delete (no merge) is allowed to leave underfull/empty nodes.
    /// </summary>
    public void Validate()
    {
        int leafDepth = -1, leafTotal = 0;
        ValidateNode(_root, 0, false, default!, false, default!, ref leafDepth, ref leafTotal);
        long cnt = _count.Sum();
        if (leafTotal != cnt) throw new InvalidOperationException($"Count mismatch: tree holds {leafTotal}, _count={cnt}");

        Node n = _root;
        while (n is Internal ind) n = ind.Children[0];
        bool first = true; TKey prev = default!; long chain = 0;
        for (var leaf = (Leaf)n; leaf != null; leaf = leaf.Next)
            for (int i = 0; i < leaf.Count; i++)
            {
                if (!first && Compare(leaf.Keys[i], prev) <= 0)
                    throw new InvalidOperationException($"leaf chain not strictly ascending at {leaf.Keys[i]} after {prev}");
                prev = leaf.Keys[i]; first = false; chain++;
            }
        if (chain != cnt) throw new InvalidOperationException($"leaf chain has {chain} keys, _count={cnt}");
    }

    private void ValidateNode(Node node, int depth, bool hasLo, TKey lo, bool hasHi, TKey hi, ref int leafDepth, ref int leafTotal)
    {
        for (int i = 0; i < node.Count; i++)
        {
            if (i > 0 && Compare(node.Keys[i - 1], node.Keys[i]) >= 0)
                throw new InvalidOperationException("keys not ascending within node");
            if (hasLo && Compare(node.Keys[i], lo) < 0) throw new InvalidOperationException("key below separator lower bound");
            if (hasHi && Compare(node.Keys[i], hi) >= 0) throw new InvalidOperationException("key at/above separator upper bound");
        }
        if (node is Leaf leaf)
        {
            if (leafDepth == -1) leafDepth = depth;
            else if (depth != leafDepth) throw new InvalidOperationException($"UNBALANCED: leaf at depth {depth}, expected {leafDepth}");
            leafTotal += leaf.Count;
            return;
        }
        var ind = (Internal)node;
        for (int i = 0; i <= ind.Count; i++)
        {
            bool cLo = i > 0 || hasLo; TKey childLo = i > 0 ? ind.Keys[i - 1] : lo;
            bool cHi = i < ind.Count || hasHi; TKey childHi = i < ind.Count ? ind.Keys[i] : hi;
            ValidateNode(ind.Children[i], depth + 1, cLo, childLo, cHi, childHi, ref leafDepth, ref leafTotal);
        }
    }

    /// <summary>Test/diagnostic hook (quiescent): (depth, internalNodes, leaves, avgLeafFillPercent).</summary>
    public (int Depth, int Internals, int Leaves, double AvgLeafFill) DebugStats()
    {
        int depth = 0; Node n = _root;
        while (n is Internal ind) { depth++; n = ind.Children[0]; }
        int internals = 0, leaves = 0; long keys = 0;
        CountNodes(_root, ref internals, ref leaves, ref keys);
        double fill = leaves == 0 ? 0 : 100.0 * keys / ((double)leaves * _max);
        return (depth, internals, leaves, fill);
    }

    private void CountNodes(Node node, ref int internals, ref int leaves, ref long keys)
    {
        if (node is Leaf leaf) { leaves++; keys += leaf.Count; return; }
        var ind = (Internal)node;
        internals++;
        for (int i = 0; i <= ind.Count; i++) CountNodes(ind.Children[i], ref internals, ref leaves, ref keys);
    }
}
