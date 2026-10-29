using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Mobratil.Collections;

/// <summary>
/// A lock-free, concurrent, sorted dictionary — the .NET equivalent of Java's
/// <c>java.util.concurrent.ConcurrentSkipListMap</c>. Implements
/// <see cref="IDictionary{TKey,TValue}"/> and <see cref="IReadOnlyDictionary{TKey,TValue}"/>.
///
/// This is a faithful port of Doug Lea's CAS-based skip-list algorithm (the same
/// algorithm that backs Java's ConcurrentSkipListMap), with one deliberate change
/// for the CLR: it is fully generic over &lt;TKey,TValue&gt;, so neither keys nor
/// values are boxed. Keys live in a typed <c>Node.Key</c> field; values live in a
/// typed field of a small <c>ValueHolder</c> object (the holder is
/// the CAS unit that lets us atomically replace/delete a value — it plays the exact
/// role of Java's <c>volatile Object value</c> field, but without boxing the value
/// itself).
///
/// Progress guarantee: lock-free. No operation ever holds a lock. Threads that
/// observe a half-completed deletion help finish it (the classic "helping" pattern),
/// so the structure as a whole always makes progress.
///
/// Linearizability: point operations have a single linearization point — the
/// successful CAS that publishes their effect (or the volatile read of the value field
/// for lookups). Concurrent readers never block writers and vice-versa. As in Java's
/// CSLM, TryRemoveFirst/TryRemoveLast and Clear are the exceptions (see docs/CONCURRENCY.md).
/// </summary>
public sealed class ConcurrentSkipListDictionary<TKey, TValue>
    : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    // Sentinel stored in the base header node's value field. Distinguishes the
    // header (which has no real key/value) from live nodes and from markers.
    private static readonly object BaseHeader = new();

    private readonly IComparer<TKey> _comparer;
    // Comparer<TKey>.Default on a value-type key: compare through the static so the JIT devirtualizes/inlines it.
    private readonly bool _defaultCmp;

    // The current top-left index node. Volatile: grows as the structure gets taller.
    private volatile HeadIndex _head;

    // Running size. Lea's ConcurrentSkipListMap deliberately has no count field (size() is O(n)) to avoid
    // a single-atomic write hotspot; a LongAdder-style striped counter gives O(stripes) Count WITHOUT that
    // contention. Bumped at the exactly-once CAS winners: insert commit (DoPut) and value->null (DoRemove,
    // TryRemoveFirst). Weakly consistent under concurrency, exact when quiescent.
    private readonly StripedCounter _count = new();

    public ConcurrentSkipListDictionary() : this(Comparer<TKey>.Default) { }

    public ConcurrentSkipListDictionary(IComparer<TKey> comparer)
    {
        _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        _defaultCmp = typeof(TKey).IsValueType && ReferenceEquals(comparer, Comparer<TKey>.Default);
        // Base header node: key is irrelevant, value == BaseHeader sentinel.
        var baseNode = new Node(default!, BaseHeader, null);
        _head = new HeadIndex(baseNode, null, null, 1);
    }

    // ---------------------------------------------------------------------
    //  Value cell.  A node's value field is `object?`, carrying four states by
    //  reference identity (BaseHeader / live / null=deleted / self=marker).
    //
    //  To make a value CAS-able while keeping it un-boxed, *value-type* values are
    //  wrapped in a tiny ValueHolder (the holder reference is the CAS unit; TValue
    //  lives un-boxed in a typed field).  *Reference-type* values need no wrapper at
    //  all — the reference IS already a CAS-able object, so we store it directly and
    //  save one allocation per put.  A null reference value is stored as the private
    //  NullValue sentinel (null itself is the "deleted" state), so — unlike Java's
    //  CSLM — null values are supported, as for any IDictionary.  The choice is a
    //  per-instantiation constant, so the JIT keeps only the relevant branch.
    // ---------------------------------------------------------------------
    private static readonly bool StoreInline = !typeof(TValue).IsValueType;
    private static readonly object NullValue = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object Box(TValue value) => StoreInline ? (object?)value ?? NullValue : new ValueHolder(value);

    // Cells are created only by Box, so their runtime type is known: skip the checked casts.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TValue Unbox(object cell)
        => StoreInline
            ? (ReferenceEquals(cell, NullValue) ? default! : Unsafe.As<object, TValue>(ref cell))
            : Unsafe.As<ValueHolder>(cell).Value;

    private sealed class ValueHolder
    {
        internal readonly TValue Value;
        internal ValueHolder(TValue value) => Value = value;
    }

    // ---------------------------------------------------------------------
    //  Node: a base-level list node.
    //    value == BaseHeader   -> the header node
    //    value is ValueHolder  -> a live mapping
    //    value == null         -> logically deleted
    //    value == this (node)  -> a "marker" node (a deletion tombstone)
    // ---------------------------------------------------------------------
    private sealed class Node
    {
        internal readonly TKey Key;
        internal object? Value;          // see state table above; accessed via Volatile/Interlocked
        internal Node? Next;             // accessed via Volatile/Interlocked

        internal Node(TKey key, object? value, Node? next)
        {
            Key = key;
            Value = value;
            Next = next;
        }

        // Marker-node constructor: value points at itself, key is irrelevant.
        internal Node(Node next)
        {
            Key = default!;
            Value = this;
            Next = next;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool CasValue(object? expect, object? update)
            => ReferenceEquals(Interlocked.CompareExchange(ref Value, update, expect), expect);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool CasNext(Node? expect, Node? update)
            => ReferenceEquals(Interlocked.CompareExchange(ref Next, update, expect), expect);

        internal bool IsMarker => ReferenceEquals(Volatile.Read(ref Value), this);
        internal bool IsBaseHeader => ReferenceEquals(Volatile.Read(ref Value), BaseHeader);

        // Append a marker node after this node (CAS next from f to a new marker->f).
        internal bool AppendMarker(Node? f) => CasNext(f, new Node(f!));

        // Help unlink a node that is being deleted: either append the marker or, if
        // the marker is already there, splice both out by advancing the predecessor.
        internal void HelpDelete(Node b, Node? f)
        {
            // Rationale (Lea): only help if our view of the chain is still consistent.
            if (ReferenceEquals(f, Volatile.Read(ref Next)) && ReferenceEquals(this, Volatile.Read(ref b.Next)))
            {
                if (f == null || !ReferenceEquals(Volatile.Read(ref f.Value), f))  // f not a marker yet
                    CasNext(f, new Node(f!));                                       // append marker
                else
                    b.CasNext(this, Volatile.Read(ref f.Next));                     // unlink this + marker
            }
        }

        // Returns the live value, or (false, default) if this node is a header/marker/deleted.
        // Liveness is by reference identity (works for both inline and holder storage).
        internal bool TryGetValidValue(out TValue value)
        {
            object? v = Volatile.Read(ref Value);
            if (v == null || ReferenceEquals(v, this) || ReferenceEquals(v, BaseHeader))
            {
                value = default!;
                return false;
            }
            value = Unbox(v);
            return true;
        }
    }

    // ---------------------------------------------------------------------
    //  Index: an index node sitting above the base list. Read-mostly.
    // ---------------------------------------------------------------------
    private class Index
    {
        internal readonly Node BaseNode;
        internal readonly Index? Down;
        internal Index? Right;     // accessed via Volatile/Interlocked

        internal Index(Node baseNode, Index? down, Index? right)
        {
            BaseNode = baseNode;
            Down = down;
            Right = right;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool CasRight(Index? expect, Index? update)
            => ReferenceEquals(Interlocked.CompareExchange(ref Right, update, expect), expect);

        // Link newSucc between this and succ. Fails if our base node got deleted.
        internal bool Link(Index? succ, Index newSucc)
        {
            Node n = BaseNode;
            Volatile.Write(ref newSucc.Right, succ);
            return Volatile.Read(ref n.Value) != null && CasRight(succ, newSucc);
        }

        // Unlink succ (splice it out). Fails if our base node got deleted.
        internal bool Unlink(Index succ)
            => Volatile.Read(ref BaseNode.Value) != null && CasRight(succ, Volatile.Read(ref succ.Right));
    }

    private sealed class HeadIndex : Index
    {
        internal readonly int Level;
        internal HeadIndex(Node baseNode, Index? down, Index? right, int level)
            : base(baseNode, down, right) => Level = level;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CasHead(HeadIndex expect, HeadIndex update)
        => ReferenceEquals(Interlocked.CompareExchange(ref _head, update, expect), expect);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Compare(TKey a, TKey b)
        => typeof(TKey).IsValueType && _defaultCmp ? Comparer<TKey>.Default.Compare(a, b) : _comparer.Compare(a, b);

    // =====================================================================
    //  Core traversal
    // =====================================================================

    /// <summary>
    /// Returns a base-level node that is &lt; key (a predecessor), pruning index
    /// entries that point at deleted nodes along the way.
    /// </summary>
    private Node FindPredecessor(TKey key)
    {
        for (; ; )
        {
            Index q = _head;
            Index? r = Volatile.Read(ref q.Right);
            for (; ; )
            {
                if (r != null)
                {
                    Node n = r.BaseNode;
                    TKey k = n.Key;
                    if (Volatile.Read(ref n.Value) == null)   // index points at deleted node
                    {
                        if (!q.Unlink(r)) break;              // inconsistent -> restart
                        r = Volatile.Read(ref q.Right);
                        continue;
                    }
                    if (Compare(key, k) > 0)
                    {
                        q = r;
                        r = Volatile.Read(ref r.Right);
                        continue;
                    }
                }
                Index? d = q.Down;
                if (d != null)
                {
                    q = d;
                    r = Volatile.Read(ref d.Right);
                }
                else
                {
                    return q.BaseNode;
                }
            }
        }
    }

    /// <summary>Find the node holding key, or null. Helps clean up deleted nodes it trips over.</summary>
    private Node? FindNode(TKey key)
    {
        for (; ; )
        {
            Node b = FindPredecessor(key);
            Node? n = Volatile.Read(ref b.Next);
            for (; ; )
            {
                if (n == null) return null;
                Node? f = Volatile.Read(ref n.Next);
                if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) break; // inconsistent -> retry
                object? v = Volatile.Read(ref n.Value);
                if (v == null) { n.HelpDelete(b, f); break; }              // deleted -> help & retry
                if (Volatile.Read(ref b.Value) == null || ReferenceEquals(v, n)) break; // b deleted or n marker
                int c = Compare(key, n.Key);
                if (c == 0) return n;
                if (c < 0) return null;
                b = n;
                n = f;
            }
        }
    }

    // =====================================================================
    //  get
    // =====================================================================
    private bool DoGet(TKey key, out TValue value)
    {
        for (; ; )
        {
            Node b = FindPredecessor(key);
            Node? n = Volatile.Read(ref b.Next);
            for (; ; )
            {
                if (n == null) goto notFound;
                Node? f = Volatile.Read(ref n.Next);
                if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) break; // retry outer
                object? v = Volatile.Read(ref n.Value);
                if (v == null) { n.HelpDelete(b, f); break; }
                if (Volatile.Read(ref b.Value) == null || ReferenceEquals(v, n)) break;
                int c = Compare(key, n.Key);
                if (c == 0)
                {
                    // v here is non-null and not the marker (checked above) -> a live value.
                    value = Unbox(v);
                    return true;
                }
                if (c < 0) goto notFound;
                b = n;
                n = f;
            }
        }
    notFound:
        value = default!;
        return false;
    }

    // =====================================================================
    //  put
    // =====================================================================
    private bool DoPut(TKey key, TValue value, bool onlyIfAbsent, out TValue previous)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        object? newCell = null;  // the value cell, allocated lazily at the commit point only
        Node z;
        for (; ; ) // outer
        {
            Node b = FindPredecessor(key);
            Node? n = Volatile.Read(ref b.Next);
            for (; ; )
            {
                if (n != null)
                {
                    Node? f = Volatile.Read(ref n.Next);
                    if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) break; // retry outer
                    object? v = Volatile.Read(ref n.Value);
                    if (v == null) { n.HelpDelete(b, f); break; }
                    if (Volatile.Read(ref b.Value) == null || ReferenceEquals(v, n)) break;
                    int c = Compare(key, n.Key);
                    if (c > 0) { b = n; n = f; continue; }
                    if (c == 0)
                    {
                        // Existing mapping.
                        if (onlyIfAbsent)
                        {
                            previous = Unbox(v);
                            return false; // did not insert; key already present (no allocation)
                        }
                        if (n.CasValue(v, newCell ??= Box(value)))
                        {
                            previous = Unbox(v);
                            return false; // replaced existing value
                        }
                        break; // lost race -> retry outer
                    }
                    // c < 0 -> insert between b and n
                }
                z = new Node(key, newCell ??= Box(value), n);
                if (!b.CasNext(n, z)) break; // lost race -> retry outer
                goto inserted;
            }
        }
    inserted:
        _count.Increment();          // exactly-once: only one thread's CasNext links z
        AddIndexLevels(z, key);
        previous = default!;
        return true;
    }

    // Randomly choose a level for the new node and splice its index tower in.
    private void AddIndexLevels(Node z, TKey key)
    {
        int rnd = NextSecondarySeed();
        // Same gate as Lea: only ~1/4 of nodes get any index at all; geometric beyond.
        if ((rnd & 0x80000001) != 0) return; // test highest and lowest bits

        // level = 1 + (number of consecutive 1-bits above bit 0). Counting trailing ones
        // of x is counting trailing zeros of ~x, which is a single TZCNT/RBIT+CLZ instruction.
        int level = 1 + BitOperations.TrailingZeroCount(~((uint)rnd >> 1));

        Index? idx = null;
        HeadIndex h = _head;
        int max = h.Level;
        if (level <= max)
        {
            for (int i = 1; i <= level; ++i)
                idx = new Index(z, idx, null);
        }
        else
        {
            // Grow by exactly one level.
            level = max + 1;
            var idxs = new Index[level + 1];
            for (int i = 1; i <= level; ++i)
                idxs[i] = idx = new Index(z, idx, null);
            for (; ; )
            {
                h = _head;
                int oldLevel = h.Level;
                if (level <= oldLevel) break;
                HeadIndex newh = h;
                Node oldBase = h.BaseNode;
                for (int j = oldLevel + 1; j <= level; ++j)
                    newh = new HeadIndex(oldBase, newh, idxs[j], j);
                if (CasHead(h, newh))
                {
                    h = newh;
                    idx = idxs[level = oldLevel];
                    break;
                }
            }
        }

        // Splice idx tower into the index levels.
        int insertionLevel = level;
        for (; ; )  // splice
        {
            int j = h.Level;
            Index q = h;
            Index? r = Volatile.Read(ref q.Right);
            Index? t = idx;
            for (; ; )
            {
                if (q == null || t == null) return;
                if (r != null)
                {
                    Node n = r.BaseNode;
                    int c = Compare(key, n.Key);
                    if (Volatile.Read(ref n.Value) == null)
                    {
                        if (!q.Unlink(r)) break;
                        r = Volatile.Read(ref q.Right);
                        continue;
                    }
                    if (c > 0)
                    {
                        q = r;
                        r = Volatile.Read(ref r.Right);
                        continue;
                    }
                }
                if (j == insertionLevel)
                {
                    if (!q.Link(r, t)) break; // restart splice
                    if (Volatile.Read(ref t.BaseNode.Value) == null) { FindNode(key); return; }
                    if (--insertionLevel == 0) return;
                }
                if (--j >= insertionLevel && j < level) t = t.Down;
                q = q.Down!;
                r = q == null ? null : Volatile.Read(ref q.Right);
            }
        }
    }

    // =====================================================================
    //  remove
    // =====================================================================
    private bool DoRemove(TKey key, bool hasExpected, TValue expected, out TValue removed)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        for (; ; ) // outer
        {
            Node b = FindPredecessor(key);
            Node? n = Volatile.Read(ref b.Next);
            for (; ; )
            {
                if (n == null) goto notFound;
                Node? f = Volatile.Read(ref n.Next);
                if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) break;
                object? v = Volatile.Read(ref n.Value);
                if (v == null) { n.HelpDelete(b, f); break; }
                if (Volatile.Read(ref b.Value) == null || ReferenceEquals(v, n)) break;
                int c = Compare(key, n.Key);
                if (c < 0) goto notFound;
                if (c > 0) { b = n; n = f; continue; }

                var current = Unbox(v);
                if (hasExpected && !EqualityComparer<TValue>.Default.Equals(current, expected))
                    goto notFound;

                if (!n.CasValue(v, null)) break;      // logically delete; lost race -> retry
                _count.Decrement();                   // exactly-once: only one thread wins the value->null CAS
                if (!n.AppendMarker(f) || !b.CasNext(n, f))
                {
                    FindNode(key);                    // couldn't splice cleanly -> let FindNode help
                }
                else
                {
                    FindPredecessor(key);             // clean out index entries
                    if (Volatile.Read(ref _head.Right) == null) TryReduceLevel();
                }
                removed = current;
                return true;
            }
        }
    notFound:
        removed = default!;
        return false;
    }

    // Drop an empty top level (conservatively, to avoid thrashing). Lea's heuristic.
    private void TryReduceLevel()
    {
        HeadIndex h = _head;
        if (h.Level <= 3) return;
        HeadIndex? d = h.Down as HeadIndex;
        HeadIndex? e = d?.Down as HeadIndex;
        if (d != null && e != null &&
            Volatile.Read(ref e.Right) == null &&
            Volatile.Read(ref d.Right) == null &&
            Volatile.Read(ref h.Right) == null &&
            CasHead(h, d) &&                          // try to drop a level
            Volatile.Read(ref h.Right) != null)       // recheck: someone added -> undo
        {
            CasHead(d, h);
        }
    }

    // =====================================================================
    //  First / Last
    // =====================================================================
    private Node? FindFirst()
    {
        for (; ; )
        {
            Node b = _head.BaseNode;
            Node? n = Volatile.Read(ref b.Next);
            if (n == null) return null;
            if (Volatile.Read(ref n.Value) != null) return n;
            n.HelpDelete(b, Volatile.Read(ref n.Next));
        }
    }

    private Node? FindLast()
    {
        Index q = _head;
        for (; ; )
        {
            Index? r = Volatile.Read(ref q.Right);
            if (r != null)
            {
                if (Volatile.Read(ref r.BaseNode.Value) == null)
                {
                    q.Unlink(r);
                    q = _head; // restart from top
                }
                else
                {
                    q = r;
                }
                continue;
            }
            Index? d = q.Down;
            if (d != null)
            {
                q = d;
                continue;
            }
            // base-level scan to the end
            Node b = q.BaseNode;
            for (; ; )
            {
                Node? n = Volatile.Read(ref b.Next);
                if (n == null) return b.IsBaseHeader ? null : b;
                Node? f = Volatile.Read(ref n.Next);
                if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) break;
                object? v = Volatile.Read(ref n.Value);
                if (v == null) { n.HelpDelete(b, f); break; }
                if (ReferenceEquals(v, n)) break;
                b = n;
            }
            q = _head;
        }
    }

    // =====================================================================
    //  Conditional replace (the CAS behind TryUpdate / AddOrUpdate)
    // =====================================================================
    private bool DoReplace(TKey key, TValue newValue, TValue comparison)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        object? newCell = null;  // allocated lazily, only once we've matched
        for (; ; )
        {
            Node b = FindPredecessor(key);
            Node? n = Volatile.Read(ref b.Next);
            for (; ; )
            {
                if (n == null) return false;
                Node? f = Volatile.Read(ref n.Next);
                if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) break;
                object? v = Volatile.Read(ref n.Value);
                if (v == null) { n.HelpDelete(b, f); break; }
                if (Volatile.Read(ref b.Value) == null || ReferenceEquals(v, n)) break;
                int c = Compare(key, n.Key);
                if (c < 0) return false;
                if (c > 0) { b = n; n = f; continue; }

                if (!EqualityComparer<TValue>.Default.Equals(Unbox(v), comparison))
                    return false;                    // current value doesn't match -> no update (no allocation)
                if (n.CasValue(v, newCell ??= Box(newValue))) return true;
                break;                               // lost race -> retry outer
            }
        }
    }

    // =====================================================================
    //  Relational find (the engine behind lower/floor/ceiling/higher).
    //  Port of Doug Lea's findNear. rel bits: EQ allows an exact match, LT means
    //  "search downward" (less-than). GT is the absence of LT.
    // =====================================================================
    private const int RelEq = 1, RelLt = 2;   // ceiling=EQ, higher=0, floor=LT|EQ, lower=LT

    private Node? FindNear(TKey key, int rel)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        for (; ; )
        {
            Node b = FindPredecessor(key);
            Node? n = Volatile.Read(ref b.Next);
            for (; ; )
            {
                if (n == null)
                    return ((rel & RelLt) == 0 || b.IsBaseHeader) ? null : b;
                Node? f = Volatile.Read(ref n.Next);
                if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) break;
                object? v = Volatile.Read(ref n.Value);
                if (v == null) { n.HelpDelete(b, f); break; }
                if (Volatile.Read(ref b.Value) == null || ReferenceEquals(v, n)) break;
                int c = Compare(key, n.Key);
                if ((c == 0 && (rel & RelEq) != 0) ||
                    (c < 0 && (rel & RelLt) == 0))
                    return n;
                if (c <= 0 && (rel & RelLt) != 0)
                    return b.IsBaseHeader ? null : b;
                b = n;
                n = f;
            }
        }
    }

    // Resolve a relational find to a stable live entry, retrying if the node we land
    // on gets deleted between locating it and reading its value.
    private bool TryFindNear(TKey key, int rel, out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; )
        {
            Node? n = FindNear(key, rel);
            if (n == null) { entry = default; return false; }
            if (n.TryGetValidValue(out var v)) { entry = new KeyValuePair<TKey, TValue>(n.Key, v); return true; }
        }
    }

    // =====================================================================
    //  Public API  (mirrors ConcurrentDictionary / ConcurrentSkipListMap)
    // =====================================================================

    /// <summary>Adds the key/value if the key is not already present. Returns false if it already existed.
    /// (<see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryAdd"/> semantics.)</summary>
    public bool TryAdd(TKey key, TValue value) => DoPut(key, value, onlyIfAbsent: true, out _);

    /// <summary>Lookup. Wait-free in the common case.</summary>
    public bool TryGetValue(TKey key, out TValue value) => DoGet(key, out value);

    public bool ContainsKey(TKey key) => DoGet(key, out _);

    /// <summary>Removes the key. Returns false if it was not present; the removed value is returned via
    /// <paramref name="value"/>. (ConcurrentDictionary.TryRemove semantics.)</summary>
    public bool TryRemove(TKey key, out TValue value) => DoRemove(key, hasExpected: false, default!, out value);

    /// <summary>Removes the key/value pair only if both the key is present and its current value equals
    /// <paramref name="item"/>.Value. (ConcurrentDictionary.TryRemove(KeyValuePair) semantics.)</summary>
    public bool TryRemove(KeyValuePair<TKey, TValue> item)
        => DoRemove(item.Key, hasExpected: true, item.Value, out _);

    /// <summary>Updates the value for <paramref name="key"/> to <paramref name="newValue"/> only if its current
    /// value equals <paramref name="comparisonValue"/>. (ConcurrentDictionary.TryUpdate semantics.)</summary>
    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        => DoReplace(key, newValue, comparisonValue);

    /// <summary>Gets or sets the value for a key. The getter throws <see cref="KeyNotFoundException"/> when the
    /// key is absent; the setter inserts or overwrites (the .NET way to "put").</summary>
    public TValue this[TKey key]
    {
        get => DoGet(key, out var v) ? v : throw new KeyNotFoundException();
        set => DoPut(key, value, onlyIfAbsent: false, out _);
    }

    /// <summary>Returns the existing value for the key, or atomically adds and returns <paramref name="value"/>.
    /// (ConcurrentDictionary.GetOrAdd semantics.)</summary>
    public TValue GetOrAdd(TKey key, TValue value)
    {
        for (; ; )
        {
            if (DoGet(key, out var existing)) return existing;
            if (DoPut(key, value, onlyIfAbsent: true, out var prev)) return value;
            // someone else inserted concurrently; prev holds their value
            return prev;
        }
    }

    /// <summary>Returns the existing value for the key, or atomically adds the value produced by
    /// <paramref name="valueFactory"/>. (ConcurrentDictionary.GetOrAdd(key, factory) semantics.)</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        for (; ; )
        {
            if (DoGet(key, out var existing)) return existing;
            var value = valueFactory(key);
            if (DoPut(key, value, onlyIfAbsent: true, out var prev)) return value;
            return prev;
        }
    }

    /// <summary>Adds a key/value if absent, otherwise replaces it with <paramref name="updateValueFactory"/>
    /// applied to the current value. Returns the value now stored. (ConcurrentDictionary.AddOrUpdate semantics.)</summary>
    public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
    {
        for (; ; )
        {
            if (DoGet(key, out var current))
            {
                var updated = updateValueFactory(key, current);
                if (DoReplace(key, updated, current)) return updated;
            }
            else if (DoPut(key, addValue, onlyIfAbsent: true, out _))
            {
                return addValue;
            }
            // lost a race — retry
        }
    }

    /// <summary>AddOrUpdate with a factory for the add case too.</summary>
    public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
    {
        for (; ; )
        {
            if (DoGet(key, out var current))
            {
                var updated = updateValueFactory(key, current);
                if (DoReplace(key, updated, current)) return updated;
            }
            else
            {
                var added = addValueFactory(key);
                if (DoPut(key, added, onlyIfAbsent: true, out _)) return added;
            }
        }
    }

    public bool IsEmpty => FindFirst() == null;

    /// <summary>
    /// Removes every mapping that is present when <c>Clear</c> is called. Like
    /// <c>java.util.concurrent.ConcurrentSkipListMap.clear()</c> it deletes entries one at a time through the
    /// ordinary (linearizable, exactly-once) deletion protocol, so it is O(n) and not atomic as a whole: a
    /// mapping inserted concurrently may or may not survive. It makes a single ascending pass bounded by the
    /// largest key present at the start, so it terminates even under a steady stream of concurrent inserts.
    /// (It deliberately does NOT swap in a fresh head: an in-flight insert could then splice its index tower —
    /// and with it the detached old list — into the new structure, resurrecting cleared keys, and in-flight
    /// writers would skew <see cref="Count"/> permanently.)
    /// </summary>
    public void Clear()
    {
        Node? last = FindLast();
        if (last == null) return;
        TKey hi = last.Key;                           // nothing beyond this existed when we started
        bool haveCursor = false;
        TKey cursor = default!;                       // key of the last node we deleted (pass is ascending)
        Node header = _head.BaseNode;                 // the base header is permanent (never replaced)
        Node b = header;
        for (; ; )
        {
            Node? n = Volatile.Read(ref b.Next);
            if (n == null) break;
            Node? f = Volatile.Read(ref n.Next);
            if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) continue;
            object? v = Volatile.Read(ref n.Value);
            if (v == null) { n.HelpDelete(b, f); continue; }
            if (ReferenceEquals(v, n) || Volatile.Read(ref b.Value) == null) { b = header; continue; }  // b deleted under us
            if (haveCursor && Compare(n.Key, cursor) <= 0) { b = n; continue; }   // inserted behind the cursor: keep
            if (Compare(n.Key, hi) > 0) break;
            if (!n.CasValue(v, null)) continue;
            _count.Decrement();                       // exactly-once: only one thread wins the value->null CAS
            cursor = n.Key; haveCursor = true;
            if (!n.AppendMarker(f) || !b.CasNext(n, f)) FindNode(n.Key);
        }
        // The indexed nodes are (almost all) deleted now: drop the index levels wholesale rather than leaving
        // thousands of dead entries for later traversals to unlink one by one. Losing a live node's index
        // entry (a concurrent insert, or a survivor) only costs search speed, never correctness.
        for (Index? q = _head; q != null; q = q.Down) Volatile.Write(ref q.Right, null);
    }

    /// <summary>Count of live mappings via the striped counter — O(stripes), not O(n). Weakly consistent
    /// under concurrency (like ConcurrentSkipListMap.size(), it is not an atomic snapshot); exact when quiescent.</summary>
    public int Count
    {
        get { long c = _count.Sum(); return c <= 0 ? 0 : c >= int.MaxValue ? int.MaxValue : (int)c; }
    }

    public bool TryGetFirst(out KeyValuePair<TKey, TValue> entry)
    {
        // If the first node is deleted under us, start over from the head: walking forward to its
        // successor could return a key that was never the minimum (a smaller one may have been inserted).
        for (; ; )
        {
            Node? n = FindFirst();
            if (n == null) { entry = default; return false; }
            if (n.TryGetValidValue(out var v)) { entry = new(n.Key, v); return true; }
        }
    }

    public bool TryGetLast(out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; )
        {
            Node? n = FindLast();
            if (n == null) { entry = default; return false; }
            if (n.TryGetValidValue(out var v)) { entry = new(n.Key, v); return true; }
        }
    }

    // Advance to the next node whose value is live, helping clean as we go.
    private Node? NextLiveNode(Node n)
    {
        for (; ; )
        {
            Node? f = Volatile.Read(ref n.Next);
            if (f == null) return null;
            object? v = Volatile.Read(ref f.Value);
            if (v != null && !ReferenceEquals(v, f)) return f; // live, non-marker
            n = f; // skip deleted/marker
        }
    }

    /// <summary>Ascending-order enumeration. Weakly consistent (like ConcurrentSkipListMap): never throws on
    /// concurrent modification, reflects some valid linearization of concurrent updates.
    /// Returns an allocation-free struct enumerator; <c>foreach</c> over the concrete type binds to it directly
    /// (enumerating via <see cref="IEnumerable{T}"/> boxes it, like any struct enumerator).</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Allocation-free ascending struct enumerator over the live mappings.</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly ConcurrentSkipListDictionary<TKey, TValue> _owner;
        private Node? _node;
        private bool _started;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(ConcurrentSkipListDictionary<TKey, TValue> owner)
        {
            _owner = owner;
            _node = null;
            _started = false;
            _current = default;
        }

        public bool MoveNext()
        {
            Node? n = _started ? _owner.NextLiveNode(_node!) : _owner.FindFirst();
            _started = true;
            for (; n != null; n = _owner.NextLiveNode(n))
            {
                if (n.TryGetValidValue(out var v))
                {
                    _node = n;
                    _current = new KeyValuePair<TKey, TValue>(n.Key, v);
                    return true;
                }
            }
            _node = null;
            _current = default;
            return false;
        }

        public readonly KeyValuePair<TKey, TValue> Current => _current;
        readonly object IEnumerator.Current => _current;
        public void Reset() { _started = false; _node = null; _current = default; }
        public readonly void Dispose() { }
    }

    // ---- Keys / Values: point-in-time snapshots, like ConcurrentDictionary ----
    /// <summary>A snapshot of the keys in ascending order.</summary>
    public ICollection<TKey> Keys
    {
        get { var list = new List<TKey>(); foreach (var kv in this) list.Add(kv.Key); return list; }
    }

    /// <summary>A snapshot of the values in ascending key order.</summary>
    public ICollection<TValue> Values
    {
        get { var list = new List<TValue>(); foreach (var kv in this) list.Add(kv.Value); return list; }
    }

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    // =====================================================================
    //  IDictionary<TKey,TValue> / ICollection<KeyValuePair<,>> members
    // =====================================================================

    /// <summary>Adds the key/value; throws if the key already exists (IDictionary contract).
    /// Use <see cref="TryAdd"/> for the non-throwing variant.</summary>
    public void Add(TKey key, TValue value)
    {
        if (!TryAdd(key, value))
            throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
    }

    /// <summary>Removes the key. Returns false if it was not present. (IDictionary.Remove.)</summary>
    public bool Remove(TKey key) => DoRemove(key, hasExpected: false, default!, out _);

    public bool IsReadOnly => false;

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public bool Contains(KeyValuePair<TKey, TValue> item)
        => TryGetValue(item.Key, out var v) && EqualityComparer<TValue>.Default.Equals(v, item.Value);

    /// <summary>Removes the pair only if both key and value match. (ICollection&lt;KeyValuePair&gt;.Remove.)</summary>
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
    //  NavigableMap: relational queries (lower/floor/ceiling/higher)
    // =====================================================================

    /// <summary>Greatest entry with a key strictly &lt; <paramref name="key"/>.</summary>
    public bool TryGetLower(TKey key, out KeyValuePair<TKey, TValue> entry) => TryFindNear(key, RelLt, out entry);

    /// <summary>Greatest entry with a key ≤ <paramref name="key"/>.</summary>
    public bool TryGetFloor(TKey key, out KeyValuePair<TKey, TValue> entry) => TryFindNear(key, RelLt | RelEq, out entry);

    /// <summary>Least entry with a key ≥ <paramref name="key"/>.</summary>
    public bool TryGetCeiling(TKey key, out KeyValuePair<TKey, TValue> entry) => TryFindNear(key, RelEq, out entry);

    /// <summary>Least entry with a key strictly &gt; <paramref name="key"/>.</summary>
    public bool TryGetHigher(TKey key, out KeyValuePair<TKey, TValue> entry) => TryFindNear(key, 0, out entry);

    /// <summary>Key of <see cref="TryGetLower(TKey, out KeyValuePair{TKey, TValue})"/>.</summary>
    public bool TryGetLowerKey(TKey key, out TKey result) => Project(TryGetLower(key, out var e), e, out result);
    public bool TryGetFloorKey(TKey key, out TKey result) => Project(TryGetFloor(key, out var e), e, out result);
    public bool TryGetCeilingKey(TKey key, out TKey result) => Project(TryGetCeiling(key, out var e), e, out result);
    public bool TryGetHigherKey(TKey key, out TKey result) => Project(TryGetHigher(key, out var e), e, out result);

    private static bool Project(bool found, KeyValuePair<TKey, TValue> e, out TKey key)
    {
        key = found ? e.Key : default!;
        return found;
    }

    // =====================================================================
    //  NavigableMap: poll (atomic remove-min / remove-max)
    // =====================================================================

    /// <summary>Atomically removes and returns the smallest entry.</summary>
    public bool TryRemoveFirst(out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; )
        {
            Node b = _head.BaseNode;
            Node? n = Volatile.Read(ref b.Next);
            if (n == null) { entry = default; return false; }
            Node? f = Volatile.Read(ref n.Next);
            if (!ReferenceEquals(n, Volatile.Read(ref b.Next))) continue;
            object? v = Volatile.Read(ref n.Value);
            if (v == null) { n.HelpDelete(b, f); continue; }
            if (ReferenceEquals(v, n)) continue;           // marker — skip
            if (!n.CasValue(v, null)) continue;            // lost race; retry
            _count.Decrement();                            // exactly-once: only one thread wins the value->null CAS
            if (!n.AppendMarker(f) || !b.CasNext(n, f))
                FindNode(n.Key);                           // help finish + prune index
            else
                FindPredecessor(n.Key);                    // prune index entries
            entry = new KeyValuePair<TKey, TValue>(n.Key, Unbox(v));
            return true;
        }
    }

    /// <summary>Atomically removes and returns the largest entry.</summary>
    public bool TryRemoveLast(out KeyValuePair<TKey, TValue> entry)
    {
        for (; ; )
        {
            Node? n = FindLast();
            if (n == null) { entry = default; return false; }
            object? v = Volatile.Read(ref n.Value);
            if (v == null || ReferenceEquals(v, n) || ReferenceEquals(v, BaseHeader)) continue;
            // Still the last node? (Re-checked right before the claiming CAS, like Lea's doRemoveLastEntry, rather
            // than via a separate keyed remove, which left a wide window for a larger key to be inserted.)
            if (Volatile.Read(ref n.Next) != null) continue;
            if (!n.CasValue(v, null)) continue;
            _count.Decrement();                       // exactly-once: only one thread wins the value->null CAS
            FindNode(n.Key);                          // help unlink n (marker + splice) and prune its index
            if (Volatile.Read(ref _head.Right) == null) TryReduceLevel();
            entry = new KeyValuePair<TKey, TValue>(n.Key, Unbox(v));
            return true;
        }
    }

    // =====================================================================
    //  Conveniences (SortedMap / Map)
    // =====================================================================

    /// <summary>The comparer that defines key ordering. (SortedMap.comparator.)</summary>
    public IComparer<TKey> Comparer => _comparer;

    /// <summary>O(n) scan for a value.</summary>
    public bool ContainsValue(TValue value)
    {
        var cmp = EqualityComparer<TValue>.Default;
        foreach (var kv in this)
            if (cmp.Equals(kv.Value, value)) return true;
        return false;
    }

    /// <summary>Value for the key, or <paramref name="defaultValue"/> if absent.</summary>
    public TValue GetValueOrDefault(TKey key, TValue defaultValue)
        => DoGet(key, out var v) ? v : defaultValue;

    public TValue? GetValueOrDefault(TKey key) => DoGet(key, out var v) ? v : default;

    /// <summary>Replaces the value only if the key is already present; returns false (and leaves the map
    /// unchanged) if absent. Reports the prior value via <paramref name="previous"/>. (ConcurrentMap.replace(k,v).)</summary>
    public bool TryReplace(TKey key, TValue newValue, out TValue previous)
    {
        for (; ; )
        {
            if (!DoGet(key, out var current)) { previous = default!; return false; }
            if (DoReplace(key, newValue, current)) { previous = current; return true; }
            // value changed under us — retry
        }
    }

    // =====================================================================
    //  Functional bulk / compute helpers
    // =====================================================================

    /// <summary>Inserts or overwrites every entry from <paramref name="items"/>.</summary>
    public void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var kv in items) DoPut(kv.Key, kv.Value, onlyIfAbsent: false, out _);
    }


    // =====================================================================
    //  NavigableMap views (live, range-restricted / reversed sub-dictionaries)
    // =====================================================================

    /// <summary>A live view over keys in [<paramref name="fromKey"/>, <paramref name="toKey"/>) (from inclusive,
    /// to exclusive — like SortedMap.subMap). Reflects and can mutate the parent within range.</summary>
    public RangeView GetViewBetween(TKey fromKey, TKey toKey) => GetViewBetween(fromKey, true, toKey, false);

    /// <summary>A live view over keys between the two bounds, with explicit inclusivity.</summary>
    public RangeView GetViewBetween(TKey fromKey, bool fromInclusive, TKey toKey, bool toInclusive)
        => new(this, true, fromKey, fromInclusive, true, toKey, toInclusive, descending: false);

    /// <summary>A live view over keys &lt; <paramref name="toKey"/> (or ≤ when inclusive).</summary>
    public RangeView GetViewTo(TKey toKey, bool inclusive = false)
        => new(this, false, default!, false, true, toKey, inclusive, descending: false);

    /// <summary>A live view over keys ≥ <paramref name="fromKey"/> (or &gt; when not inclusive).</summary>
    public RangeView GetViewFrom(TKey fromKey, bool inclusive = true)
        => new(this, true, fromKey, inclusive, false, default!, false, descending: false);

    /// <summary>A live, reverse-ordered view of the whole dictionary.</summary>
    public RangeView Reverse()
        => new(this, false, default!, false, false, default!, false, descending: true);

    /// <summary>The keys in descending order.</summary>
    public IEnumerable<TKey> DescendingKeys
    {
        get { foreach (var kv in Reverse()) yield return kv.Key; }
    }

    /// <summary>
    /// A live, navigable view of the parent restricted to a key range and/or reversed.
    /// Reads and writes pass through to the parent (writes are bounds-checked); the view
    /// is weakly consistent under concurrent modification, exactly like the parent.
    /// Returned by <see cref="GetViewBetween(TKey,TKey)"/> / <see cref="GetViewTo"/> /
    /// <see cref="GetViewFrom"/> / <see cref="Reverse"/>.
    /// </summary>
    public sealed class RangeView : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        private readonly ConcurrentSkipListDictionary<TKey, TValue> _p;
        private readonly bool _hasLo, _loInc, _hasHi, _hiInc, _desc;
        private readonly TKey _lo, _hi;

        internal RangeView(ConcurrentSkipListDictionary<TKey, TValue> parent,
            bool hasLo, TKey lo, bool loInc, bool hasHi, TKey hi, bool hiInc, bool descending)
        {
            _p = parent;
            _hasLo = hasLo; _lo = lo; _loInc = loInc;
            _hasHi = hasHi; _hi = hi; _hiInc = hiInc;
            _desc = descending;
        }

        // ---- bounds (always expressed in ascending key order) ----
        private bool TooLow(TKey k) { if (!_hasLo) return false; int c = _p.Compare(k, _lo); return c < 0 || (c == 0 && !_loInc); }
        private bool TooHigh(TKey k) { if (!_hasHi) return false; int c = _p.Compare(k, _hi); return c > 0 || (c == 0 && !_hiInc); }
        private bool InRange(TKey k) => !TooLow(k) && !TooHigh(k);

        // least / greatest in-range entry (ascending key order)
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

        /// <summary>Allocation-free struct enumerator over the view, in view order
        /// (ascending, or descending for a reversed view).</summary>
        public Enumerator GetEnumerator() => new(this);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly RangeView _view;
            private KeyValuePair<TKey, TValue> _current;
            private Node? _node;
            private bool _started, _done;

            internal Enumerator(RangeView view) { _view = view; _current = default; _node = null; _started = false; _done = false; }

            public bool MoveNext()
            {
                if (_done) return false;
                var p = _view._p;
                if (!_view._desc)
                {
                    // Ascending: locate the lower bound once, then walk the base level like the parent's
                    // enumerator — O(1) per element instead of a fresh O(log n) search for each one.
                    Node? n = _started ? p.NextLiveNode(_node!)
                            : _view._hasLo ? p.FindNear(_view._lo, _view._loInc ? RelEq : 0) : p.FindFirst();
                    _started = true;
                    for (; n != null; n = p.NextLiveNode(n))
                    {
                        if (!n.TryGetValidValue(out var v)) continue;
                        if (_view.TooHigh(n.Key)) break;
                        _node = n;
                        _current = new KeyValuePair<TKey, TValue>(n.Key, v);
                        return true;
                    }
                    _done = true; _node = null; _current = default;
                    return false;
                }
                // Descending: the base list is singly linked, so step with a relational search (in view order).
                bool ok = _started ? _view.TryGetHigher(_current.Key, out _current) : _view.TryGetFirst(out _current);
                _started = true;
                if (!ok) { _done = true; _current = default; return false; }
                return true;
            }

            public readonly KeyValuePair<TKey, TValue> Current => _current;
            readonly object IEnumerator.Current => _current;
            public void Reset() { _started = false; _done = false; _node = null; _current = default; }
            public readonly void Dispose() { }
        }

        // ---- range-clamped relational primitives (ascending key order) ----
        private bool RangeCeiling(TKey key, out KeyValuePair<TKey, TValue> e)
        {
            if (!_p.TryGetCeiling(key, out var c)) { e = default; return false; }
            if (TooLow(c.Key)) return AscFirst(out e);
            if (TooHigh(c.Key)) { e = default; return false; }
            e = c; return true;
        }
        private bool RangeHigher(TKey key, out KeyValuePair<TKey, TValue> e)
        {
            if (!_p.TryGetHigher(key, out var c)) { e = default; return false; }
            if (TooLow(c.Key)) return AscFirst(out e);
            if (TooHigh(c.Key)) { e = default; return false; }
            e = c; return true;
        }
        private bool RangeFloor(TKey key, out KeyValuePair<TKey, TValue> e)
        {
            if (!_p.TryGetFloor(key, out var c)) { e = default; return false; }
            if (TooHigh(c.Key)) return AscLast(out e);
            if (TooLow(c.Key)) { e = default; return false; }
            e = c; return true;
        }
        private bool RangeLower(TKey key, out KeyValuePair<TKey, TValue> e)
        {
            if (!_p.TryGetLower(key, out var c)) { e = default; return false; }
            if (TooHigh(c.Key)) return AscLast(out e);
            if (TooLow(c.Key)) { e = default; return false; }
            e = c; return true;
        }

        // ---- navigable queries in *view* order (inverted when descending) ----
        public bool TryGetFirst(out KeyValuePair<TKey, TValue> e) => _desc ? AscLast(out e) : AscFirst(out e);
        public bool TryGetLast(out KeyValuePair<TKey, TValue> e) => _desc ? AscFirst(out e) : AscLast(out e);
        public bool TryGetCeiling(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeFloor(key, out e) : RangeCeiling(key, out e);
        public bool TryGetHigher(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeLower(key, out e) : RangeHigher(key, out e);
        public bool TryGetFloor(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeCeiling(key, out e) : RangeFloor(key, out e);
        public bool TryGetLower(TKey key, out KeyValuePair<TKey, TValue> e) => _desc ? RangeHigher(key, out e) : RangeLower(key, out e);

        // ---- read API ----
        public bool ContainsKey(TKey key) => InRange(key) && _p.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value)
        {
            if (InRange(key)) return _p.TryGetValue(key, out value);
            value = default!;
            return false;
        }
        public bool IsEmpty { get { using var e = GetEnumerator(); return !e.MoveNext(); } }
        public int Count { get { int n = 0; foreach (var _ in this) n++; return n; } }
        public bool IsReadOnly => false;

        public ICollection<TKey> Keys { get { var l = new List<TKey>(); foreach (var kv in this) l.Add(kv.Key); return l; } }
        public ICollection<TValue> Values { get { var l = new List<TValue>(); foreach (var kv in this) l.Add(kv.Value); return l; } }
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        // ---- mutation (bounds-checked, delegated to the parent) ----
        private void CheckRange(TKey key)
        {
            if (!InRange(key))
                throw new ArgumentOutOfRangeException(nameof(key), $"Key {key} is outside the sub-dictionary's range.");
        }

        public TValue this[TKey key]
        {
            get => InRange(key) && _p.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
            set { CheckRange(key); _p[key] = value; }
        }

        public void Add(TKey key, TValue value) { CheckRange(key); _p.Add(key, value); }
        public bool TryAdd(TKey key, TValue value) { CheckRange(key); return _p.TryAdd(key, value); }
        public bool Remove(TKey key) => InRange(key) && _p.Remove(key);
        public void Clear() { foreach (var k in Keys) _p.Remove(k); }   // remove everything in range (snapshot)

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public bool Contains(KeyValuePair<TKey, TValue> item)
            => InRange(item.Key) && _p.Contains(item);
        public bool Remove(KeyValuePair<TKey, TValue> item) => InRange(item.Key) && _p.Remove(item);
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

        /// <summary>Reverses the iteration order of this view.</summary>
        public RangeView Reverse()
            => new(_p, _hasLo, _lo, _loInc, _hasHi, _hi, _hiInc, !_desc);
    }

    // =====================================================================
    //  Per-thread RNG for level selection (xorshift, like ThreadLocalRandom's
    //  secondary seed — cheap, no shared state, decorrelated across threads).
    // =====================================================================
    [ThreadStatic] private static int _seed;

    private static int NextSecondarySeed()
    {
        int s = _seed;
        if (s == 0)
        {
            // Seed from thread id; avoid zero.
            s = Environment.CurrentManagedThreadId * unchecked((int)0x9E3779B1);
            if (s == 0) s = 1;
        }
        s ^= s << 13;
        s ^= (int)((uint)s >> 17);
        s ^= s << 5;
        _seed = s;
        return s;
    }
}
