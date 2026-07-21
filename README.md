# erasure16-dotnet

Reference C#/.NET implementation of **erasure16** — a general-purpose **systematic
Reed-Solomon erasure code over GF(2^16)** using a **Cauchy** generator matrix. Given
`K` equal-length data shards it produces `R` parity shards such that the original `K`
data shards can be reconstructed from **any `K` of the `K + R` shards**.

The library is deliberately abstract and self-contained: **shard byte arrays in, shard
byte arrays out**. It performs no I/O, compression, encryption, framing, or container
handling, and carries no dependencies beyond the .NET base class library.

The normative contract is [`SPEC.md`](external/erasure16-spec/SPEC.md) in the
`erasure16-spec` repository (included here as a submodule). This implementation follows
it exactly and is verified byte-for-byte against the committed conformance vectors.

## Layout

| Path | Purpose |
| --- | --- |
| `src/Erasure16` | The library (`netstandard2.0`, namespace `Erasure16`). |
| `tests/Erasure16.Tests` | xUnit tests: field laws, encode/reconstruct round-trips, conformance. |
| `tools/Erasure16.VectorGen` | Console tool that generates the canonical vectors + `MANIFEST.json`. |
| `external/erasure16-spec` | Git submodule: the spec and conformance vectors. |

## Field

GF(2^16) with primitive reduction polynomial `0x1100B`
(x^16 + x^12 + x^3 + x + 1) and generator `alpha = 2`. Arithmetic uses exp/log tables;
the full period 65535 is asserted on table build. Add/subtract is bitwise XOR.

## Shards

A shard is a `byte[]`. Within one operation all shards share the same length, which
must be **even and greater than 0**. Shards are read as 16-bit field elements in
**big-endian**: `word[w] = (shard[2w] << 8) | shard[2w+1]`. Coding is applied
independently per word index.

## Public API

```csharp
using Erasure16;

// K data shards, R parity shards. Validates K >= 1, R >= 0, K + R <= 65536.
var rs = ReedSolomon16.Create(dataShardCount: 4, parityShardCount: 2);

// shards.Length == K + R, equal even lengths. Caller fills [0..K); Encode fills [K..K+R).
byte[][] shards = /* K data shards + R empty parity shards */;
rs.Encode(shards);

// Rebuild every missing shard (>= K must be present). present.Length == K + R.
rs.Reconstruct(shards, present);

// Rebuild only the missing data shards [0..K), leaving missing parity untouched.
rs.ReconstructData(shards, present);

// Optional progress for long decodes (drive a UI progress bar; throw to cancel):
rs.ReconstructData(shards, present, (done, total) => UpdateBar(done, total));

// True if the parity shards are consistent with the data shards.
bool ok = rs.Verify(shards);

// Helpers: pad/concatenate raw data into K shards and back.
byte[][] dataShards = ReedSolomon16.Split(data, dataShardCount: 4, shardSize: 64);
byte[] data = ReedSolomon16.Join(dataShards, outputLength);

// Field arithmetic is exposed for advanced users.
ushort p = Gf16.Mul(a, b);
ushort i = Gf16.Inv(a);
ushort q = Gf16.Div(a, b);
ushort e = Gf16.Exp(exponent);
int    l = Gf16.Log(a);
```

Invalid parameters, mismatched or odd shard lengths, and fewer than `K` present shards
all throw `Erasure16Exception`.

Reconstruction solves only the `E×E` system for the `E` missing data shards rather than
inverting the full `K×K` matrix — byte-identical output, far less memory (`~E·K` vs `K²`),
and time that scales with the loss rather than the whole encoded set. See
[`docs/decoder-optimization.md`](docs/decoder-optimization.md) for the math, correctness
argument, measured results, and the `ProgressListener` design.

## Build, test, generate vectors

```sh
# 0/0 build.
dotnet build Erasure16.sln

# Field laws + encode/reconstruct round-trips. With the submodule checked out,
# ConformanceTests also assert byte-exact agreement with external/erasure16-spec/vectors.
dotnet test

# (Re)generate the canonical vectors + MANIFEST.json into a target directory.
dotnet run --project tools/Erasure16.VectorGen -- <output-directory>
```

## Conformance vectors

`external/erasure16-spec/vectors/MANIFEST.json` indexes every vector (field, encode,
reconstruct) and references each raw `*.bin` shard file by byte length and SHA-256.
Data shard bytes are produced by a documented SplitMix64 PRNG, so any implementation
can regenerate identical files. `ConformanceTests` verify the digests and reproduce
every case byte-for-byte.

To check out or update the submodule:

```sh
git submodule update --init --recursive
```
