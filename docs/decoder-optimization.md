# Decoder optimization: E×E submatrix solve + progress signals

Status: implemented in `erasure16-dotnet`, ported from the `erasure16-java`
`perf/submatrix-decode` branch. Output-identical to the previous decoder; **no wire-format,
spec, or conformance-vector change**. The two implementations stay a parity pair — same
algorithm, same recovered bytes, verified against the same shared conformance vectors.

## Summary

The reconstruction path used to invert the full **K×K** generator submatrix whenever any
shard was missing — `O(K³)` time and `O(K²)` memory, regardless of how few shards were
actually lost. That is fine at the GF(256) scale it was inherited from (`K ≤ 255`), but at
GF(2¹⁶) `K` can reach the thousands, where the K×K inversion dominates time and its matrix
allocation is what makes a memory-constrained restorer (a phone) run out of heap.

This change replaces that with the standard erasure-decode optimization: **solve only the
`E×E` system for the `E` missing data shards** (`E` = number of erased data shards). The
recovered bytes are unchanged — the erasure solution is unique — so it is a pure
implementation swap.

Headline effects:

- **Peak matrix memory: ~17× smaller** — e.g. 24 MB → 1.4 MB at K=2048, 384 MB → 22 MB at
  K=8192. This is the win that removes the phone out-of-memory wall. (These .NET figures are
  half the `erasure16-java` reference for the same shapes: .NET carries each field element as
  a 16-bit `ushort`, Java as a 32-bit `int`, so every matrix is half the bytes. The
  structural `K²` → `E·K` win is identical in both.)
- **Wall-clock time: scales with the loss, not the whole encoded set** — the eliminated `K³`
  inversion is replaced by `E³`, so the speedup grows with K, while staying *feasible* at
  sizes where the old path is impractical. (The `erasure16-java` reference measured
  ~1.4–2.1× at K=512–2048 and ~36 s at K=8192 where the K×K path is minutes; the .NET
  algorithm is identical, and `DecoderBenchmark` measures local .NET wall-clock — see
  [Measured results](#measured-results).)
- **Recovered output: byte-identical** to the legacy decoder and to the committed
  cross-language conformance vectors.

## Why the previous decoder was expensive

For a missing set, the old `RecoverDataFullMatrix` selects any `K` present shards, builds
their `K×K` generator submatrix `M`, inverts it by Gauss–Jordan (`≈ 2K³` field multiplies,
`≈ 3·K²` field elements held live), then applies `M⁻¹` to the missing rows. The inversion
cost is independent of how many shards were lost: losing 1 data shard out of 3000 costs the
same full `K×K` inversion as losing all of them.

## The optimization

Let the `K` data shards split into `K − E` **present** and `E` **missing**. Pick `E` present
parity shards as equations (there are always at least `E`: `presentCount ≥ K` and only
`K − E` data shards are present, so `≥ E` parity shards remain). Each parity value is the
generator-weighted sum over **all** `K` data shards:

```
parity_p = Σ_j  G[p][j] · data_j            (j over all K data columns)
```

Split the sum into the known (present) and unknown (missing) data and move the known part to
the right-hand side:

```
Σ_{j∈missing} G[p][j]·data_j  =  parity_p  −  Σ_{j∈present} G[p][j]·data_j   =:  rhs_p
```

Stacking the `E` chosen equations gives an `E×E` system `A · x = rhs`, where
`A[i][c] = G[p_i][missing_c]` and `x` is the `E` missing data words. `A` is a submatrix of the
Cauchy generator (distinct `X = {p_i}`, distinct `Y = {R + missing_c}`, `X ∩ Y = ∅`), hence a
Cauchy matrix and invertible. Solve `x = A⁻¹ · rhs` per word.

Implementation notes (`RecoverDataSubmatrix`):

- `A` (E×E) is the system to invert; `B` (E×(K−E)) holds the known-data coefficients so the
  RHS is a matrix–vector product per word rather than repeated inverse applications.
- The per-word hot loops pre-gather the present-data words once (they feed every equation)
  and read flat locals, mirroring the legacy layout; the GF dot product uses four
  accumulators to avoid a single serial XOR-reduction chain.
- Cauchy coefficients are read directly via `CauchyMatrix.ParityCoeff(p, j, r)`; the full
  generator is never materialized.

### Cost: before vs after

With `E` erased data shards out of `K`, symbol length `S` bytes (`words = S/2`):

| | Time (field muls) | Peak matrix memory |
| --- | --- | --- |
| Legacy `FullMatrix` | `2·K³ + (S/2)·E·K` | `~3·K²` ushorts (`6·K²` bytes) |
| New `Submatrix` | `2·E³ + (S/2)·E·K` | `~(2·E² + E·(K−E))` ushorts (`≈ 2·E·K` bytes) |

The two share the `(S/2)·E·K` term (the "touch the surviving data" syndrome/apply work,
irreducible for a dense code); the optimization removes the `K³` inversion, replacing it with
`E³`. They coincide exactly when `E = K` (all data lost — nothing to trim).

## Correctness

The recovered data is the unique solution of the erasure system, so any correct decoder
returns the same bytes. This is enforced three ways:

- **Conformance vectors** (`ConformanceTests`) — the new default `Submatrix` path reproduces
  every committed `reconstruct` vector byte-for-byte against the shared
  `external/erasure16-spec/vectors` blobs (including K=32/R=8), the same vectors the Java and
  reference generators produce.
- **Differential test** (`DecoderEquivalenceTests`) — across many `(K, R)` shapes and loss
  patterns (E=1, E≈R/2, E=R worst case, mixed data/parity, E=K, and K=1024), `Submatrix`
  output equals both `FullMatrix` and the original encoding, byte-for-byte.
- **Round-trip matrix** (`ReedSolomonTests`) — unchanged, now exercising the new default.

The legacy `FullMatrix` decoder is retained (via the internal `RecoveryAlgorithm` seam,
exposed to the test assembly through `InternalsVisibleTo`) purely as the differential-test
oracle and benchmark baseline.

## Measured results

`DecoderBenchmark` is gated off by default (it runs for tens of seconds to minutes at the
larger sizes). Enable it with the environment variable:

```sh
ERASURE16_BENCH=true dotnet test -c Release --filter FullyQualifiedName~DecoderBenchmark
```

It reports .NET wall-clock time for the legacy `FullMatrix` decoder versus the new
`Submatrix` decoder at a realistic worst case (loss `E = R`, redundancy `R = 15% of K`,
symbol 1024 B, best-of-3 after warmup), and asserts the two decoders produced byte-identical
output at every size (so it doubles as a large-shape correctness check).

Measured on the dev machine (.NET 10.0.6, amd64; times are wall-clock best-of-3 and will vary
by hardware — the **memory** columns are the exact computed peak matrix footprint for this
`ushort`-based implementation, independent of hardware):

| K | R = E | full (ms) | sub (ms) | speedup | full mem | sub mem |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 512  | 77   | 163.5  | 99.2    | 1.6× | 1.5 MB   | 0.1 MB  |
| 1024 | 154  | 841.2  | 477.5   | 1.8× | 6.0 MB   | 0.3 MB  |
| 2048 | 308  | 4530.5 | 1993.9  | 2.3× | 24.0 MB  | 1.4 MB  |
| 4096 | 615  | —      | 8627.7  | —    | 96.0 MB  | 5.5 MB  |
| 8192 | 1229 | —      | 39256.6 | —    | 384.0 MB | 22.1 MB |

(The K=4096/8192 legacy `FullMatrix` runs are omitted — the K×K inversion is minutes-long at
these sizes.) These track the `erasure16-java` reference closely (Java measured 1.4–2.1× at
K=512–2048 and ~36 s at K=8192), confirming the two implementations share one algorithm and
performance character. The memory win is the durable, size-independent structural result:
~17× smaller peak matrix footprint at 15% loss.

### Honest reading of the numbers

- The **time** speedup is real but modest at moderate sizes (~2×), not the ~40× a naive
  field-multiply count suggests. The reason: the shared `(S/2)·E·K` syndrome term is
  **bandwidth-bound on the GF(2¹⁶) exp/log multiply tables** (two 128 KB tables), and both
  decoders pay it in full. The optimization removes the `K³` inversion *on top of* that shared
  floor, so the speedup is `1 + (inversion)/(shared floor)` — which **grows with K**.
- Multi-accumulator unrolling of the reduction was tried (in the Java reference) and made no
  measurable difference, confirming the bottleneck is table-load traffic, not reduction
  latency. The .NET `Dot` keeps the four-accumulator shape for parity; it likewise does no
  harm.
- The **memory** reduction is the durable, size-independent structural win (`K²` → `E·K`):
  ~17× at 15% loss. This is what actually moves the restorable ceiling on a phone, because the
  old failure at large `K` was an out-of-memory crash, not merely a slow decode.

## Progress signals (`ProgressListener`)

Long reconstructs previously looked frozen to a UI. Reconstruction now accepts an optional
`ProgressListener` (public overloads of `Reconstruct` / `ReconstructData`; `null` = the old
no-op behavior with zero overhead). `ProgressListener` is a delegate — the idiomatic .NET
equivalent of the Java functional interface — with the same inline, throw-to-cancel contract:

```csharp
rs.ReconstructData(shards, present, (done, total) => UpdateBar(done, total));
```

> Note: use the named `ProgressListener` delegate, not `IProgress<T>`. `IProgress<T>` /
> `Progress<T>` deliberately marshal callbacks *asynchronously* to a captured
> `SynchronizationContext`, which would break both the inline "runs on the decode thread"
> contract and throw-to-cancel. A plain synchronous delegate preserves both.

Design:

- **Work-unit model.** `total` is an estimate of field-op cost summed across the three
  phases — build (`≈ E·K`), invert (`≈ 2·E³`), and per-word solve (`≈ words·E·K`) — so the
  reported fraction `done/total` is roughly proportional to real work regardless of which
  phase dominates (inversion-heavy at high loss / short symbols; per-word-heavy at long
  symbols). Units are abstract; treat only the ratio as meaningful.
- **Phase coverage.** The `E×E` inversion reports per-column via a `CauchyMatrix.Invert`
  callback; the per-word loop reports as words complete. So both the (rare) inversion-bound
  and the (common) per-word-bound cases get smooth updates rather than one long silent gap.
- **Throttled + monotonic.** A small relay caps output at ~200 callbacks per decode, is
  monotonic non-decreasing, clamps to `[0, total]`, and guarantees a final exact
  `done == total` tick. Trivial cases (nothing missing / only parity missing) emit a single
  completion tick.
- **Cancellation.** Throwing from the callback propagates out and aborts the decode (discard
  the partially-written shard array). This gives a UI a cancel button at zero API cost.
- **Threading.** The callback runs inline on the decoding thread — post to the UI thread from
  inside it; do not do work there. Run the decode off the UI thread.

Verified by `ProgressListenerTests` (monotonicity, bounds, constant positive `total`, final
exact tick, trivial-case completion, throwing-cancels, and null-listener equivalence).

Limitation: because the per-word estimate assumes uniform cost, progress is approximately —
not exactly — linear in wall-clock time. That is the right trade for a progress bar.

## Public API changes (all additive, backward-compatible)

- `void Reconstruct(shards, present, ProgressListener)` — new overload.
- `void ReconstructData(shards, present, ProgressListener)` — new overload.
- `delegate ProgressListener(long completedUnits, long totalUnits)` — new public delegate.
- Existing two-argument methods are unchanged and now use the `Submatrix` path by default.
