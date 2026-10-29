# Concurrency & consistency guarantees

This document specifies what the concurrent ordered maps promise to callers, and — for maintainers —
the memory-ordering discipline the implementations rely on. It covers the two **supported** structures:

- `ConcurrentSkipListDictionary<TKey,TValue>` — a lock-free skip list (a port of Doug Lea's
  `ConcurrentSkipListMap`).
- `ConcurrentBTreeDictionary<TKey,TValue>` — an Optimistic-Lock-Coupling (OLC) B+ tree.

> `BLinkTree` is **experimental and unsupported** — it has known unfixed concurrency races and is
> excluded from the shipped surface. Do not use it in production.

## Consistency model (what callers can rely on)

### Point operations are linearizable
`TryGetValue`, `ContainsKey`, the indexer get/set, `TryAdd`, `TryRemove`, `this[k]=v`, the navigable
queries `TryGetCeiling` / `TryGetFloor` / `TryGetHigher` / `TryGetLower` (and their `...Key` variants),
`TryGetFirst` / `TryGetLast`, plus `TryRemoveFirst` / `TryRemoveLast`, are **linearizable**: each appears
to take effect atomically at a single instant between its invocation and its return, consistent with a
single global order. This is verified by a model-based linearizability test (Wing–Gong checker against a
sequential `SortedDictionary` oracle) across tens of thousands of random concurrent histories, plus
targeted "impossible-answer" tests (`AdversarialTests`), on both x64 and arm64.

**Exception (skip list poll).** `ConcurrentSkipListDictionary.TryRemoveFirst` / `TryRemoveLast` keep the
behaviour of Java's `pollFirstEntry` / `pollLastEntry`: each entry is removed by exactly one caller, but the
"is it still the first (last)?" check and the removing CAS are separate steps, so a key inserted *before*
(after) it in that sub-microsecond window can make the poll return an entry that was no longer the
minimum (maximum). The B+ tree's polls lock the edge leaves and are strictly linearizable.

**Compound helpers.** `GetOrAdd`, `AddOrUpdate` and `TryReplace` are retry loops over the primitives above
with `ConcurrentDictionary` semantics: the value factory may run more than once and the update is a
compare-by-`Equals` CAS (so it is subject to ABA on equal values), exactly like `ConcurrentDictionary`.

### Enumeration and range views are *weakly consistent*
`GetEnumerator()`, `SubMap` / `HeadMap` / `TailMap` / `DescendingMap` and their enumeration, and the
`Keys` / `Values` collections traverse the **live** structure. They behave like `ConcurrentDictionary`'s
enumerator:

- They **never throw** due to concurrent modification.
- They yield keys in strict sorted order (ascending, or descending for `DescendingMap`), and a range view
  yields only keys within its bounds.
- They reflect *some* linearization of the operations that completed, but are **not a point-in-time
  snapshot**: a concurrent insert/remove may or may not be observed. Do not assume an enumeration reflects
  a single consistent instant.

### `Count` / `IsEmpty` are weakly consistent
`Count` is maintained by a striped (LongAdder-style) counter. It is **exact when the structure is
quiescent**, but under concurrent mutation its `Sum()` reads stripes at different instants and may
transiently over- or under-shoot the true live-key count. It is clamped to be **non-negative**. Treat a
live `Count` as an estimate; only trust it when no other thread is mutating.

### `Clear`
- **B+ tree:** O(1) — swaps in a fresh empty tree and linearizes at the swap. A writer already in flight on
  the old tree completes against it (it linearizes before the clear) and its effect — including its
  contribution to `Count`, which is tracked per tree generation — is discarded with that tree. Root
  replacement elsewhere is compare-and-swap, so an in-flight split/merge can never re-install the old tree.
- **Skip list:** O(n), like `ConcurrentSkipListMap.clear()` — removes every mapping present when it is
  called, one at a time through the normal deletion protocol (so `Count` stays exact), in one bounded
  ascending pass. Not atomic as a whole: a mapping inserted concurrently may or may not survive.

### Keys, comparers and values
Keys must be non-null. Ordering is by the supplied `IComparer<TKey>` (or `Comparer<TKey>.Default`); the
comparer must be consistent and stable for the lifetime of the map. Values may be any type, including
reference types and `null` (both structures).

The comparer is only ever handed keys that were actually inserted: if an optimistic read feeds it a torn or
just-cleared slot and it throws, the read is re-validated and silently retried. An exception the comparer
(or `TValue.Equals`, for the value-matching overloads) throws on consistent data propagates to the caller
and leaves the map unchanged and fully usable — no lock is left held.

## Memory-ordering discipline (for maintainers)

`ConcurrentBPlusTree` uses a per-node **seqlock**: a version word (`even` = free, `odd` = write-locked).

- **Writers**: `WriteLock` flips the version to odd via a full-barrier `Interlocked.CompareExchange`,
  mutate the node in place (or build a new node), then `WriteUnlock` via `Volatile.Write` (a **release**
  store) — so every data write is globally visible *before* the version bump.
- **New nodes** (split's `right`, a new root) are fully constructed *before* being made reachable
  (`leaf.Next` / `parent.Children[i]` / the `volatile _root`); readers reach them through dependent loads.
- **Readers** are optimistic: `TryReadVersion` (acquire) → read node data → `Validate(v)` → retry on
  mismatch.

**The critical invariant:** `Node.Validate` issues a **LoadLoad fence before re-reading the version**, on
weak-memory architectures (arm64/arm). Without it, the optimistic data reads (`Children[ci]` in the
descent, key/value array copies in a scan, `Keys[i]`/`Values[i]` in a point read) can be reordered *past*
the acquire-load, so a node validates as "unchanged" while the data was actually read after a concurrent
split/merge mutated it — yielding torn `(key,value)` reads and a `NullReferenceException` in the descent
(reading a `Children[]` slot a concurrent merge just nulled). The fence is **gated to arm/arm64** because
x86/x64 is TSO (loads never reorder with loads) and needs no fence there. This was a real bug, fixed, and
verified on both arches.

> ⚠️ Do not add an optimistic read that reads node data and then checks the version *without* going
> through `Node.Validate`, and do not remove the architecture-gated fence. Either reintroduces an
> arm64-only tearing/NRE that x86 testing will not catch.

## How correctness is tested

- **`ParallelRangeStressTests` / `ValueShapeStressTests`** — oversubscribed (2× cores) churn at orders
  4/8/64, asserting sorted + unique + **value-consistency** + bounds + navigable + count invariants, over
  value-type, reference-type, string, and custom-comparer shapes.
- **`AdversarialTests`** — writer patterns that make a specific answer *impossible* under linearizability
  (e.g. key x is only ever present while key y is), so a single observation proves a bug: cross-leaf
  floor/ceiling, first/last, poll, `Clear` racing writers (exact `Count`, no resurrected keys), throwing
  comparers, and comparers that would dereference torn slots.
- **`LinearizabilityTests`** — model-based linearizability (Wing–Gong) over many small random concurrent
  histories, with a self-test proving the checker rejects non-linearizable histories.
- **Cross-architecture CI** (`.github/workflows/ci.yml`) — x64 and **arm64** (Apple silicon + arm64 Linux),
  because the demonstrated risk is the hardware memory model; a nightly soak runs longer with
  `DOTNET_TieredCompilation=0`.

### Lock order and exception safety (B+ tree)
Locks are taken parent-before-child and, among leaves, strictly left→right; operations that need leaves in
another order (`TryRemoveLast`) acquire them by non-blocking version upgrade and back off on failure. The
latch-coupled split and merge paths record each held node's child index on the way down, so once mutation
starts no comparer call can occur — a split or merge is applied entirely or not at all.

## Known limitations / not yet done

A public concurrency library would still benefit from: a written linearizability proof of the OLC
protocol, Microsoft Coyote systematic testing of the *locking* paths (latch-coupling order, merge
cascade), and broader fuzzing. The current evidence is strong stress + linearizability testing on two
architectures — meaningful, but not a formal proof of correctness.
