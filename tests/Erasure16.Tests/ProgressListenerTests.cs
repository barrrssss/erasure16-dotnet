using System;
using System.Collections.Generic;
using Erasure16;
using Xunit;

namespace Erasure16.Tests
{
    /// <summary>
    /// Verifies the <see cref="ProgressListener"/> contract on the optimized decode path: progress
    /// is monotonic, bounded by <c>[0, total]</c>, reports a constant positive <c>total</c>, and
    /// ends on an exact <c>completed == total</c> tick. Also checks that throwing from the callback
    /// cancels the decode, and that a null listener matches the two-argument overload.
    /// </summary>
    public sealed class ProgressListenerTests
    {
        private const int Seed = 0xBEEF;

        private static byte[][] EncodedWithDataLoss(int k, int r, int shardLen, int dataDrops,
            bool[] presentOut, Random rnd)
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

            for (int i = 0; i < k + r; i++)
            {
                presentOut[i] = true;
            }

            for (int i = 0; i < dataDrops; i++)
            {
                shards[i] = null;         // drop data shards 0..dataDrops-1
                presentOut[i] = false;
            }

            return shards;
        }

        [Fact]
        public void ProgressIsMonotonicBoundedAndEndsAtTotal()
        {
            var rnd = new Random(Seed);
            int k = 300;
            int r = 90;
            int shardLen = 256; // enough words that the per-word phase emits several ticks
            var present = new bool[k + r];
            byte[][] shards = EncodedWithDataLoss(k, r, shardLen, r, present, rnd); // E = R

            var calls = new List<(long completed, long total)>();
            ProgressListener rec = (completed, total) => calls.Add((completed, total));
            ReedSolomon16.Create(k, r).ReconstructData(shards, present, rec);

            Assert.NotEmpty(calls);

            long grandTotal = calls[0].total;
            Assert.True(grandTotal > 0, "total must be positive");

            long prev = -1;
            foreach ((long completed, long total) in calls)
            {
                Assert.Equal(grandTotal, total); // total must be constant within a decode
                Assert.True(completed >= 0 && completed <= grandTotal, "completed in [0, total]: " + completed);
                Assert.True(completed >= prev, "completed must be monotonic non-decreasing");
                prev = completed;
            }

            Assert.Equal(grandTotal, calls[calls.Count - 1].completed); // final tick reports completed == total

            // Throttled: comfortably bounded above (design target ~200 + phase-boundary ticks).
            Assert.True(calls.Count <= 512, "progress callbacks must be throttled, got " + calls.Count);
        }

        [Fact]
        public void TrivialCasesStillReportCompletion()
        {
            var rnd = new Random(Seed + 1);
            int k = 6;
            int r = 4;
            int shardLen = 16;

            // Drop only parity: no data missing -> trivial recover-data path.
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
            var present = new bool[k + r];
            for (int i = 0; i < k + r; i++)
            {
                present[i] = true;
            }

            present[k] = false;
            shards[k] = null;

            var calls = new List<(long completed, long total)>();
            ProgressListener rec = (completed, total) => calls.Add((completed, total));
            ReedSolomon16.Create(k, r).ReconstructData(shards, present, rec);

            Assert.NotEmpty(calls); // trivial path must still emit a completion tick
            (long lastCompleted, long lastTotal) = calls[calls.Count - 1];
            Assert.Equal(lastTotal, lastCompleted); // trivial completion reports completed == total
        }

        private sealed class CancelException : Exception
        {
        }

        [Fact]
        public void ThrowingFromListenerCancelsDecode()
        {
            var rnd = new Random(Seed + 2);
            int k = 400;
            int r = 200;
            int shardLen = 512;
            var present = new bool[k + r];
            byte[][] shards = EncodedWithDataLoss(k, r, shardLen, r, present, rnd);

            ProgressListener canceller = (completed, total) => throw new CancelException();

            // Throwing from the callback must propagate out and abort the decode.
            Assert.Throws<CancelException>(
                () => ReedSolomon16.Create(k, r).ReconstructData(shards, present, canceller));
        }

        [Fact]
        public void NullListenerBehavesLikeNoArgOverload()
        {
            var rnd = new Random(Seed + 3);
            int k = 32;
            int r = 16;
            int shardLen = 64;
            var present = new bool[k + r];
            byte[][] shards = EncodedWithDataLoss(k, r, shardLen, r, present, rnd);

            // Passing a null listener must be accepted and equivalent to the two-arg form.
            var expected = new byte[k + r][];
            var present2 = new bool[k + r];
            Array.Copy(present, present2, present.Length);
            for (int i = 0; i < shards.Length; i++)
            {
                expected[i] = shards[i] == null ? null : (byte[])shards[i].Clone();
            }

            ReedSolomon16.Create(k, r).ReconstructData(shards, present, (ProgressListener)null);
            ReedSolomon16.Create(k, r).ReconstructData(expected, present2);

            for (int j = 0; j < k; j++)
            {
                Assert.Equal(expected[j], shards[j]);
            }

            Assert.True(present[0]);
        }
    }
}
