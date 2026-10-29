# Mobratil.Collections

A **concurrent B+ tree** and a **concurrent skip list** for .NET — thread-safe, sorted, navigable
dictionaries; the ordered, range-queryable counterparts to `ConcurrentDictionary`.

[![ci](https://github.com/mobratil/collections/actions/workflows/ci.yml/badge.svg)](https://github.com/mobratil/collections/actions/workflows/ci.yml)

> ⚠️ **Vibe-coded.** This library was designed and implemented largely through AI-assisted ("vibe")
> coding. It is tested hard — highly-parallel stress and model-based linearizability on both x64 and
> arm64, and several real concurrency bugs were found and fixed that way — but it has **not** had a
> line-by-line human audit. It's early (0.1.x); treat it accordingly.

- `ConcurrentSkipListDictionary<TKey,TValue>` — lock-free skip list (a port of Doug Lea's `ConcurrentSkipListMap`).
- `ConcurrentBTreeDictionary<TKey,TValue>` — optimistic-lock-coupling B+ tree (compact, high read throughput).

Both keep keys sorted, so on top of the usual dictionary surface they add navigable lookups and live range views.

## Install

```
dotnet add package Mobratil.Collections
```

Targets `net8.0` and `net10.0`.

## Usage

```csharp
using Mobratil.Collections;

var map = new ConcurrentBTreeDictionary<int, string>();   // or ConcurrentSkipListDictionary
map[1] = "one";
map.TryAdd(2, "two");

map.TryGetCeiling(1, out var e);                  // least entry with key >= 1 (also Floor/Higher/Lower)
foreach (var kv in map.GetViewBetween(1, 9)) { } // keys in [1,9), sorted (also GetViewFrom/GetViewTo)
foreach (var kv in map.Reverse()) { }            // whole map, descending
```

## Guarantees

Point operations (get/add/remove, the indexer, `GetOrAdd`/`AddOrUpdate`, and the navigable lookups) are
**linearizable**. Enumeration and range views are **weakly consistent** — never throw on concurrent
mutation, always sorted, but not a point-in-time snapshot (like `ConcurrentDictionary`'s enumerator).
`Count` is weakly consistent and non-negative (exact once quiescent). Values may be `null`. A throwing
comparer surfaces its exception and leaves the map intact. Details:
[docs/CONCURRENCY.md](https://github.com/mobratil/collections/blob/main/docs/CONCURRENCY.md).

## Correctness

Tested on **x64 and arm64** on every push — highly-parallel stress plus model-based linearizability
(checked against a `SortedDictionary` oracle). Strong evidence, not a formal proof.

## Benchmarks

**What's measured.** Throughput in **Mops/s — millions of operations per second, higher is better**.
*N* threads hammer one shared dictionary pre-filled with the stated number of `<long,long>` keys; we count
how many operations complete per second (best-of-N runs, server GC). An *operation* is:

- **read** — a single `TryGetValue` (keys drawn so ~50% hit, ~50% miss);
- **write** — a 50/50 mix of insert and remove;
- **mixed** — half the threads read, half write.

Measured on `Standard_D64s_v5` — Intel Xeon 8370C (Ice Lake), **32 physical cores / 64 vCPU, 2 NUMA
nodes** — via the [`bench-sweep`](bench-sweep) harness (order 64). One machine, one session.

**Scaling to 64 threads (1M keys):**

| op | threads | `ConcurrentSkipListDictionary` | `ConcurrentBTreeDictionary` |
|----|--------:|-------------------------------:|----------------------------:|
| read  | 8  | 7.3  | 31.0  |
|       | 32 | 31.8 | 102.3 |
|       | 64 | 44.6 | **209.7** |
| write | 32 | 14.1 | 59.2  |
|       | 64 | 21.9 | **81.3** |
| mixed | 64 | 28.7 | **118.4** |

At 64 threads the B+ tree is **~4–5×** the skip list; its reads scale near-linearly through one socket.

**Read throughput vs dataset size (@64 threads, Mops/s)** and **memory footprint:**

| | 1M | 10M | 100M | bytes/entry @100M |
|---|---:|---:|---:|---:|
| `ConcurrentBTreeDictionary` | **209.7** | **104.7** | **59.5** | **~24** |
| `ConcurrentSkipListDictionary` | 44.6 | 18.6 | 11.0 | ~80 |

Throughput drops with size for both (deeper structures, more cache/TLB misses), but the B+ tree keeps its
lead and packs near-optimally (16 B/entry is the raw `<long,long>` payload). Writes/mixed pay a ~20–26%
penalty when threads span both NUMA nodes (both structures are NUMA-oblivious).

## Status

Pre-1.0 (0.1.x) — the API may still move. (An experimental B-link tree lives in the repo for
benchmarking and is not part of the package.)

## License

MIT — see [LICENSE](LICENSE). `ConcurrentSkipListDictionary` ports Doug Lea's public-domain
`ConcurrentSkipListMap`; see [NOTICE](NOTICE).
