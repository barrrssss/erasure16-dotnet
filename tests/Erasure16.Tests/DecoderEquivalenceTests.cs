using System;
using System.Collections.Generic;
using Erasure16;
using Xunit;

namespace Erasure16.Tests
{
    /// <summary>
    /// Differential correctness gate for the optimized <c>Submatrix</c> decoder: across a broad
    /// matrix of <c>(K, R)</c> shapes and loss patterns, it must produce <em>byte-identical</em>
    /// output to both the legacy <c>FullMatrix</c> decoder and the original encoded shards. This
    /// is what backs "the optimization cannot change recovered bytes" — the recovered data is the
    /// unique solution of the erasure system, so any two correct decoders agree exactly.
    ///
    /// Emphasis on the loss shapes that actually exercise the data-recovery solve: the
    /// <c>E = R</c> worst case (all redundancy spent on lost data), partial data loss
    /// (<c>E &lt; R</c>), the <c>E = K</c> degenerate case (all data lost, where the two
    /// algorithms provably coincide), and mixed data/parity loss.
    /// </summary>
    public sealed class DecoderEquivalenceTests
    {
        private const int Seed = 0xC0FFEE;

        private static byte[][] RandomEncoded(int k, int r, int shardLen, Random rnd)
        {
            var shards = new byte[k + r][];
            for (int i = 0; i < k; i++)
            {
                var s = new byte[shardLen];
                rnd.NextBytes(s);
                shards[i] = s;
            }

            for (int i = k; i < k + r; i++)
            {
                shards[i] = new byte[shardLen];
            }

            ReedSolomon16.Create(k, r).Encode(shards);
            return shards;
        }

        private static byte[][] CopyDroppingIndices(byte[][] original, int[] drop, bool[] presentOut)
        {
            int n = original.Length;
            var shards = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                presentOut[i] = true;
                shards[i] = (byte[])original[i].Clone();
            }

            foreach (int idx in drop)
            {
                shards[idx] = null;
                presentOut[idx] = false;
            }

            return shards;
        }

        /// <summary>
        /// Runs both decoders on the same loss and asserts both equal the original encoding (and
        /// therefore each other), byte-for-byte, for both <c>ReconstructData</c> and the full
        /// <c>Reconstruct</c>.
        /// </summary>
        private static void AssertEquivalent(int k, int r, int shardLen, int[] drop, Random rnd)
        {
            byte[][] original = RandomEncoded(k, r, shardLen, rnd);
            string tag = "K=" + k + " R=" + r + " len=" + shardLen + " drop=[" + string.Join(",", drop) + "]";

            // ReconstructData: Submatrix (public default) vs FullMatrix (legacy oracle).
            var presentSub = new bool[k + r];
            byte[][] sub = CopyDroppingIndices(original, drop, presentSub);
            ReedSolomon16.Create(k, r).ReconstructData(sub, presentSub); // default = Submatrix

            var presentFull = new bool[k + r];
            byte[][] full = CopyDroppingIndices(original, drop, presentFull);
            ReedSolomon16.Create(k, r).ReconstructData(full, presentFull, ReedSolomon16.RecoveryAlgorithm.FullMatrix);

            for (int j = 0; j < k; j++)
            {
                Assert.True(ByteArraysEqual(original[j], sub[j]), "Submatrix data shard " + j + " (" + tag + ")");
                Assert.True(ByteArraysEqual(original[j], full[j]), "FullMatrix data shard " + j + " (" + tag + ")");
                Assert.True(ByteArraysEqual(full[j], sub[j]), "Submatrix vs FullMatrix data shard " + j + " (" + tag + ")");
            }

            // Full reconstruct (also refills parity): Submatrix default must reproduce all N.
            var presentAll = new bool[k + r];
            byte[][] all = CopyDroppingIndices(original, drop, presentAll);
            var rs = ReedSolomon16.Create(k, r);
            rs.Reconstruct(all, presentAll);
            for (int i = 0; i < k + r; i++)
            {
                Assert.True(presentAll[i], "shard " + i + " present after reconstruct (" + tag + ")");
                Assert.True(ByteArraysEqual(original[i], all[i]), "reconstruct shard " + i + " (" + tag + ")");
            }

            Assert.True(rs.Verify(all), "verify after reconstruct (" + tag + ")");
        }

        [Fact]
        public void SubmatrixMatchesFullMatrixAcrossShapesAndLosses()
        {
            var rnd = new Random(Seed);
            // Shapes span small edges through K in the hundreds (kept modest so the legacy O(K^3)
            // oracle stays fast); includes R > K and balanced R = K.
            int[][] shapes =
            {
                new[] { 1, 1 }, new[] { 2, 3 }, new[] { 3, 2 }, new[] { 4, 2 }, new[] { 5, 5 }, new[] { 6, 1 },
                new[] { 2, 6 }, new[] { 3, 9 }, // R > K
                new[] { 8, 4 }, new[] { 16, 8 }, new[] { 32, 32 }, new[] { 64, 16 }, new[] { 128, 32 },
                new[] { 256, 64 }, new[] { 300, 90 },
            };

            foreach (int[] shape in shapes)
            {
                int k = shape[0];
                int r = shape[1];
                int shardLen = 2 + 2 * rnd.Next(20); // even, 2..40

                // E = 1 (minimum solve), if any data loss is possible.
                if (r >= 1 && k >= 1)
                {
                    AssertEquivalent(k, r, shardLen, new[] { 0 }, rnd);
                }

                int maxDataLoss = Math.Min(r, k);
                if (maxDataLoss >= 1)
                {
                    // E = R worst case: drop the last min(R,K) data shards.
                    AssertEquivalent(k, r, shardLen, LastDataIndices(k, maxDataLoss), rnd);
                    // E ~ R/2 partial data loss.
                    int half = Math.Max(1, maxDataLoss / 2);
                    AssertEquivalent(k, r, shardLen, LastDataIndices(k, half), rnd);
                }

                if (r >= 1)
                {
                    // Mixed data+parity loss totalling exactly R (maximum recoverable).
                    AssertEquivalent(k, r, shardLen, MixedDrop(k, r, rnd), rnd);
                    // Parity-only loss (no data missing -> trivial path in both).
                    AssertEquivalent(k, r, shardLen, FirstParityIndices(k, r), rnd);
                }
            }
        }

        /// <summary>The E == K corner: every data shard lost, rebuilt entirely from parity.</summary>
        [Fact]
        public void SubmatrixEqualsFullMatrixWhenAllDataLost()
        {
            var rnd = new Random(Seed + 1);
            int[][] shapes =
            {
                new[] { 1, 1 }, new[] { 4, 4 }, new[] { 32, 32 }, new[] { 128, 128 }, new[] { 256, 256 },
            };

            foreach (int[] shape in shapes)
            {
                int k = shape[0];
                int r = shape[1];
                Assert.True(r >= k, "need R >= K to lose all data");
                AssertEquivalent(k, r, 16, FirstDataIndices(k), rnd);
            }
        }

        /// <summary>A larger K to exercise the real recovery path once (small shardLen keeps it quick).</summary>
        [Fact]
        public void SubmatrixMatchesAtLargerK()
        {
            var rnd = new Random(Seed + 2);
            int k = 1024;
            int r = 160;
            // Drop 160 data shards (E = R) with a short symbol so the legacy K^3 inversion
            // (done once by the oracle) stays within a second or two.
            AssertEquivalent(k, r, 8, LastDataIndices(k, r), rnd);
        }

        // ---- helpers ----

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int[] FirstDataIndices(int k)
        {
            var outArr = new int[k];
            for (int i = 0; i < k; i++)
            {
                outArr[i] = i;
            }

            return outArr;
        }

        private static int[] LastDataIndices(int k, int count)
        {
            var outArr = new int[count];
            for (int i = 0; i < count; i++)
            {
                outArr[i] = k - 1 - i;
            }

            return outArr;
        }

        private static int[] FirstParityIndices(int k, int count)
        {
            var outArr = new int[count];
            for (int i = 0; i < count; i++)
            {
                outArr[i] = k + i;
            }

            return outArr;
        }

        /// <summary>Exactly R distinct indices, favouring data shards first, then parity.</summary>
        private static int[] MixedDrop(int k, int r, Random rnd)
        {
            var pool = new List<int>();
            for (int i = 0; i < k + r; i++)
            {
                pool.Add(i);
            }

            // Partial Fisher-Yates to pick R distinct indices, but bias the first pick toward a
            // data shard so a solve is actually forced when k >= 1.
            int firstDataPick = rnd.Next(k);
            pool.Remove(firstDataPick);
            var outArr = new int[r];
            outArr[0] = firstDataPick;
            for (int i = 1; i < r; i++)
            {
                int j = rnd.Next(pool.Count);
                outArr[i] = pool[j];
                pool.RemoveAt(j);
            }

            return outArr;
        }
    }
}
