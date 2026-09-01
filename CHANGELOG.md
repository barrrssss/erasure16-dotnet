# Changelog

## Unreleased

- Adopted specification **0.1.1**. `Erasure16.VectorGen` now emits a transport-scale
  encode and reconstruct case at `K = 6410`, `R = 1744`, `shardLen = 8`. The shared
  vectors topped out at `K = 32` before, which is 256 generator-matrix entries over a
  handful of GF(2^16) elements; the new case has 11.2 million entries and drops 512 of
  its 8,154 shards, 401 of them data.
- The generator writes compact cases as two concatenated blobs and records the absent
  indices rather than a full present mask (`SPEC.md` section 8.1). The per-shard layout
  would have emitted 8,154 `*.bin` files for this one case. `ManifestVersion` is 2.
- `ConformanceTests` reads both layouts and expects `manifestVersion` 2. A case must
  carry exactly one of each pair; a reader that silently skipped the layout it did not
  understand would skip the only case above `K = 32` and still report a pass.
- Verified first that a no-change regeneration of the vectors produces a zero diff, then
  that the new manifest is additive in every section.

## 0.1.0 — 2026-08-23

- Initial public package metadata for the `Erasure16` NuGet package.
- Systematic Cauchy Reed–Solomon implementation conforming to specification
  version 0.1.0.
- Targets `netstandard2.0` with no runtime dependencies.
