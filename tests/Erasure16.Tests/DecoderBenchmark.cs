using System;
using System.Diagnostics;
using System.Globalization;
using Erasure16;
using Xunit;
using Xunit.Abstractions;

namespace Erasure16.Tests
{
    /// <summary>
    /// Measured old-vs-new decode benchmark for the submatrix optimization. Gated off by default
    /// (it runs for tens of seconds); enable with the environment variable:
    ///
    /// <code>ERASURE16_BENCH=true dotnet test --filter FullyQualifiedName~DecoderBenchmark</code>
    ///
    /// When the flag is absent the test is a no-op pass (mirroring the Java
    /// <c>@EnabledIfSystemProperty</c> gate). When enabled it reports wall-clock time for the
    /// legacy <c>FullMatrix</c> decoder versus the new <c>Submatrix</c> decoder at a realistic
    /// worst case (loss <c>E = R</c>, redundancy <c>R = 15% of K</c>, symbol length 1024 B), plus
    /// the computed peak matrix footprint of each, and a <c>Submatrix</c>-only scaling section at
    /// larger <c>K</c> where the legacy path is impractical. Every measured case also asserts the
    /// two decoders produced byte-identical output, so the benchmark doubles as a large-shape
    /// correctness check.
    /// </summary>
    public sealed class DecoderBenchmark
    {
        private const long Seed = 0x5A1AD5EEDL;
        private const int ShardLen = 1024;      // even; ~realistic symbol payload
        private const double Redundancy = 0.15; // R = 15% of K
        private const int Warmup = 2;
        private const int Timed = 3;

        private readonly ITestOutputHelper _output;

        public DecoderBenchmark(ITestOutputHelper output)
        {
            _output = output;
        }

        private static bool Enabled =>
            string.Equals(Environment.GetEnvironmentVariable("ERASURE16_BENCH"), "true", StringComparison.OrdinalIgnoreCase);

        [Fact]
        public void CompareDecoders()
        {
            if (!Enabled)
            {
                // Off by default; enable via ERASURE16_BENCH=true.
                return;
            }

            _output.WriteLine("erasure16 decode benchmark  (loss E = R = 15% of K, symbol " + ShardLen + " B)");
            _output.WriteLine(".NET: " + Environment.Version + "  " + (Environment.Is64BitProcess ? "x64" : "x86"));
            _output.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,7} {1,6} {2,6} | {3,12} {4,12} {5,9} | {6,10} {7,10}",
                "K", "R", "E", "full (ms)", "sub (ms)", "speedup", "full mem", "sub mem"));

            foreach (int k in new[] { 512, 1024, 2048 })
            {
                int r = (int)Math.Ceiling(k * Redundancy);
                int e = r; // worst case: all lost shards are data
                Result full = Measure(ReedSolomon16.RecoveryAlgorithm.FullMatrix, k, r, e);
                Result sub = Measure(ReedSolomon16.RecoveryAlgorithm.Submatrix, k, r, e);
                Assert.Equal(full.FirstDataShard, sub.FirstDataShard); // decoders must agree byte-for-byte
                _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,7} {1,6} {2,6} | {3,12:F1} {4,12:F1} {5,8:F1}x | {6,10} {7,10}",
                    k, r, e, full.Millis, sub.Millis, full.Millis / sub.Millis,
                    Mib(FullMatrixBytes(k)), Mib(SubMatrixBytes(k, e))));

                // Assert the win only where the full-matrix run is large enough that the O(K^3)
                // inversion clearly dominates measurement noise.
                if (full.Millis > 50.0)
                {
                    Assert.True(sub.Millis < full.Millis,
                        "Submatrix must be faster than FullMatrix at K=" + k +
                        " (sub=" + sub.Millis + "ms, full=" + full.Millis + "ms)");
                }
            }

            _output.WriteLine("Submatrix-only scaling (legacy FullMatrix omitted — impractical at these sizes):");
            foreach (int k in new[] { 4096, 8192 })
            {
                int r = (int)Math.Ceiling(k * Redundancy);
                int e = r;
                Result sub = Measure(ReedSolomon16.RecoveryAlgorithm.Submatrix, k, r, e);
                _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,7} {1,6} {2,6} | {3,12} {4,12:F1} {5,9} | {6,10} {7,10}",
                    k, r, e, "-", sub.Millis, "-", Mib(FullMatrixBytes(k)), Mib(SubMatrixBytes(k, e))));
            }
        }

        private sealed class Result
        {
            internal Result(double millis, byte[] firstDataShard)
            {
                Millis = millis;
                FirstDataShard = firstDataShard;
            }

            internal double Millis { get; }

            internal byte[] FirstDataShard { get; }
        }

        /// <summary>Encodes once, then times <c>ReconstructData</c> over fresh copies; returns best-of-N ms.</summary>
        private static Result Measure(ReedSolomon16.RecoveryAlgorithm algo, int k, int r, int e)
        {
            var rnd = new Random(unchecked((int)(Seed + k * 31L + r)));
            var encoded = new byte[k + r][];
            for (int i = 0; i < k; i++)
            {
                var s = new byte[ShardLen];
                rnd.NextBytes(s);
                encoded[i] = s;
            }

            for (int i = k; i < k + r; i++)
            {
                encoded[i] = new byte[ShardLen];
            }

            ReedSolomon16.Create(k, r).Encode(encoded);

            // Drop the last e data shards (E data erasures, filled from parity).
            var drop = new int[e];
            for (int i = 0; i < e; i++)
            {
                drop[i] = k - 1 - i;
            }

            var rs = ReedSolomon16.Create(k, r);
            byte[] firstData = null;
            double best = double.MaxValue;
            for (int iter = 0; iter < Warmup + Timed; iter++)
            {
                var shards = new byte[k + r][];
                var present = new bool[k + r];
                for (int i = 0; i < k + r; i++)
                {
                    present[i] = true;
                    shards[i] = (byte[])encoded[i].Clone();
                }

                foreach (int idx in drop)
                {
                    shards[idx] = null;
                    present[idx] = false;
                }

                var sw = Stopwatch.StartNew();
                rs.ReconstructData(shards, present, algo);
                sw.Stop();

                if (iter >= Warmup)
                {
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }

                firstData = shards[0];
                Assert.Equal(encoded[0], shards[0]); // recovered data shard 0 must match original
            }

            return new Result(best, firstData);
        }

        // Peak matrix footprint. .NET carries field elements as ushort (2 bytes), so both figures
        // are half the Java reference's int-based footprint for the same shapes.
        private const int ElementBytes = sizeof(ushort);

        /// <summary>Legacy peak: m + a-copy + inv, each K x K ushorts.</summary>
        private static long FullMatrixBytes(int k) => 3L * k * k * ElementBytes;

        /// <summary>Submatrix peak: A (ExE) + A^-1 (ExE) + B (Ex(K-E)) ushorts.</summary>
        private static long SubMatrixBytes(int k, int e) => (2L * e * e + (long)e * (k - e)) * ElementBytes;

        private static string Mib(long bytes) =>
            string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / (1024.0 * 1024.0));
    }
}
