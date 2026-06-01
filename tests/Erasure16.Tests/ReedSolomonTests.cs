using System;
using System.Collections.Generic;
using Erasure16;
using Xunit;

namespace Erasure16.Tests
{
    /// <summary>
    /// End-to-end coder tests: encode, knock out shards, reconstruct, and compare against
    /// the originals across many (K, R, loss) shapes, plus split/join round-trips and
    /// the rejection paths (odd lengths, mismatched lengths, too few shards, bad params).
    /// </summary>
    public sealed class ReedSolomonTests
    {
        private static byte[][] MakeData(int k, int shardLen, int seed)
        {
            var rng = new Random(seed);
            var shards = new byte[k][];
            for (int i = 0; i < k; i++)
            {
                var s = new byte[shardLen];
                rng.NextBytes(s);
                shards[i] = s;
            }

            return shards;
        }

        private static byte[][] CloneAll(byte[][] src)
        {
            var dst = new byte[src.Length][];
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = (byte[])src[i].Clone();
            }

            return dst;
        }

        /// <summary>
        /// Encodes K data + R parity, drops the given shard indices, reconstructs, and
        /// asserts every shard matches the freshly-encoded full set.
        /// </summary>
        private static void RoundTrip(int k, int r, int shardLen, int seed, IEnumerable<int> drop)
        {
            var rs = ReedSolomon16.Create(k, r);
            int n = k + r;

            // Build the full encoded set (data + parity) as the golden reference.
            var golden = new byte[n][];
            byte[][] data = MakeData(k, shardLen, seed);
            for (int i = 0; i < k; i++)
            {
                golden[i] = data[i];
            }

            for (int p = 0; p < r; p++)
            {
                golden[k + p] = new byte[shardLen];
            }

            rs.Encode(golden);

            // Working copy with the dropped shards removed.
            var shards = CloneAll(golden);
            var present = new bool[n];
            for (int i = 0; i < n; i++)
            {
                present[i] = true;
            }

            foreach (int d in drop)
            {
                shards[d] = null;
                present[d] = false;
            }

            rs.Reconstruct(shards, present);

            for (int i = 0; i < n; i++)
            {
                Assert.True(present[i], "shard " + i + " not marked present after reconstruct");
                Assert.Equal(golden[i], shards[i]);
            }

            // The reconstructed set must verify.
            Assert.True(rs.Verify(shards));
        }

        [Theory]
        [InlineData(4, 2, 8, new[] { 0 })]            // drop one data shard
        [InlineData(4, 2, 8, new[] { 0, 1 })]         // drop two data shards (== R)
        [InlineData(4, 2, 8, new[] { 4 })]            // drop one parity shard
        [InlineData(4, 2, 8, new[] { 0, 5 })]         // drop one data + one parity (mixed)
        [InlineData(4, 2, 16, new[] { 2, 3 })]        // drop the last two data shards
        [InlineData(2, 3, 16, new[] { 0 })]           // R > K, drop one data
        [InlineData(2, 3, 16, new[] { 0, 1 })]        // R > K, drop ALL data shards
        [InlineData(2, 3, 16, new[] { 0, 1, 4 })]     // R > K, drop both data + one parity
        [InlineData(1, 1, 32, new[] { 0 })]           // K=1, drop the only data shard
        [InlineData(1, 1, 32, new[] { 1 })]           // K=1, drop the parity shard
        [InlineData(1, 3, 8, new[] { 0 })]            // K=1, R>K
        [InlineData(8, 8, 64, new[] { 0, 2, 4, 6 })]  // drop-only-data, several
        [InlineData(8, 8, 64, new[] { 1, 9, 3, 11 })] // mixed data + parity
        public void EncodeReconstructRoundTrip(int k, int r, int shardLen, int[] drop)
        {
            RoundTrip(k, r, shardLen, seed: 1234 + k * 31 + r * 7 + shardLen, drop);
        }

        [Fact]
        public void LargerK_32_AllRecoverableDropPatterns()
        {
            const int k = 32;
            const int r = 8;
            const int shardLen = 64;

            // Drop exactly R shards in a few shapes (the maximum recoverable loss).
            RoundTrip(k, r, shardLen, 9001, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });               // first 8 data
            RoundTrip(k, r, shardLen, 9002, new[] { 24, 25, 26, 27, 28, 29, 30, 31 });       // last 8 data
            RoundTrip(k, r, shardLen, 9003, new[] { 0, 5, 10, 15, 32, 35, 38, 39 });          // mixed data+parity
            RoundTrip(k, r, shardLen, 9004, new[] { 32, 33, 34, 35, 36, 37, 38, 39 });        // all parity
        }

        [Fact]
        public void LargerK_200_RoundTrips()
        {
            const int k = 200;
            const int r = 20;
            const int shardLen = 32;

            RoundTrip(k, r, shardLen, 555, new[] { 0, 50, 100, 150, 199, 200, 210, 219 });
            // Drop a contiguous block of 20 data shards (== R).
            var drop = new int[20];
            for (int i = 0; i < 20; i++)
            {
                drop[i] = 90 + i;
            }

            RoundTrip(k, r, shardLen, 556, drop);
        }

        [Fact]
        public void NoParity_R0_EncodeIsNoOpAndVerifies()
        {
            var rs = ReedSolomon16.Create(4, 0);
            byte[][] shards = MakeData(4, 16, 42);
            var before = CloneAll(shards);

            rs.Encode(shards); // no parity rows; data untouched
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(before[i], shards[i]);
            }

            Assert.True(rs.Verify(shards));

            // Reconstruct with all data present is a no-op success.
            var present = new[] { true, true, true, true };
            rs.Reconstruct(shards, present);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(before[i], shards[i]);
            }
        }

        [Fact]
        public void NoParity_R0_MissingDataIsUnrecoverable()
        {
            var rs = ReedSolomon16.Create(4, 0);
            byte[][] shards = MakeData(4, 16, 43);
            shards[1] = null;
            var present = new[] { true, false, true, true };

            Assert.Throws<Erasure16Exception>(() => rs.Reconstruct(shards, present));
        }

        [Fact]
        public void ReconstructData_LeavesMissingParityAlone()
        {
            const int k = 4;
            const int r = 2;
            const int shardLen = 16;
            var rs = ReedSolomon16.Create(k, r);

            var golden = new byte[k + r][];
            byte[][] data = MakeData(k, shardLen, 77);
            for (int i = 0; i < k; i++)
            {
                golden[i] = data[i];
            }

            for (int p = 0; p < r; p++)
            {
                golden[k + p] = new byte[shardLen];
            }

            rs.Encode(golden);

            // Drop data shard 1 and parity shard 5.
            var shards = CloneAll(golden);
            shards[1] = null;
            shards[5] = null;
            var present = new[] { true, false, true, true, true, false };

            rs.ReconstructData(shards, present);

            // Data shard 1 is recovered; parity shard 5 is left missing.
            Assert.Equal(golden[1], shards[1]);
            Assert.True(present[1]);
            Assert.False(present[5]);
            Assert.Null(shards[5]);
        }

        [Fact]
        public void Verify_DetectsCorruptParity()
        {
            const int k = 3;
            const int r = 2;
            const int shardLen = 8;
            var rs = ReedSolomon16.Create(k, r);

            var golden = new byte[k + r][];
            byte[][] data = MakeData(k, shardLen, 88);
            for (int i = 0; i < k; i++)
            {
                golden[i] = data[i];
            }

            for (int p = 0; p < r; p++)
            {
                golden[k + p] = new byte[shardLen];
            }

            rs.Encode(golden);
            Assert.True(rs.Verify(golden));

            // Flip one byte in a parity shard.
            golden[k][0] ^= 0xFF;
            Assert.False(rs.Verify(golden));
        }

        [Fact]
        public void EncodeIsDeterministic()
        {
            const int k = 5;
            const int r = 3;
            const int shardLen = 32;
            var rs = ReedSolomon16.Create(k, r);

            byte[][] data = MakeData(k, shardLen, 222);

            byte[][] a = CloneAll(data);
            byte[][] b = CloneAll(data);
            for (int p = 0; p < r; p++)
            {
                Array.Resize(ref a, k + r);
                Array.Resize(ref b, k + r);
            }

            for (int p = 0; p < r; p++)
            {
                a[k + p] = new byte[shardLen];
                b[k + p] = new byte[shardLen];
            }

            rs.Encode(a);
            rs.Encode(b);

            for (int i = 0; i < k + r; i++)
            {
                Assert.Equal(a[i], b[i]);
            }
        }

        // ----- split / join -----

        [Fact]
        public void SplitJoinRoundTrip_ExactFit()
        {
            var data = new byte[24];
            new Random(1).NextBytes(data);

            byte[][] shards = ReedSolomon16.Split(data, 3, 8);
            Assert.Equal(3, shards.Length);
            foreach (var s in shards)
            {
                Assert.Equal(8, s.Length);
            }

            byte[] joined = ReedSolomon16.Join(shards, data.Length);
            Assert.Equal(data, joined);
        }

        [Fact]
        public void SplitZeroPadsTail_AndJoinTrims()
        {
            var data = new byte[20]; // less than 3*8 = 24
            new Random(2).NextBytes(data);

            byte[][] shards = ReedSolomon16.Split(data, 3, 8);
            // Last 4 bytes of the third shard must be zero padding.
            for (int i = 4; i < 8; i++)
            {
                Assert.Equal(0, shards[2][i]);
            }

            byte[] joined = ReedSolomon16.Join(shards, 20);
            Assert.Equal(data, joined);
        }

        [Fact]
        public void SplitRejectsOddShardSize()
        {
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Split(new byte[10], 2, 5));
        }

        [Fact]
        public void SplitRejectsZeroShardSize()
        {
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Split(new byte[10], 2, 0));
        }

        [Fact]
        public void SplitRejectsOversizeData()
        {
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Split(new byte[25], 3, 8));
        }

        [Fact]
        public void JoinRejectsOutputLongerThanData()
        {
            byte[][] shards = ReedSolomon16.Split(new byte[8], 2, 8);
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Join(shards, 100));
        }

        // ----- parameter and input validation -----

        [Fact]
        public void Create_RejectsKLessThanOne()
        {
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Create(0, 2));
        }

        [Fact]
        public void Create_RejectsNegativeR()
        {
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Create(2, -1));
        }

        [Fact]
        public void Create_RejectsTotalAbove65536()
        {
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Create(65536, 1));
            Assert.Throws<Erasure16Exception>(() => ReedSolomon16.Create(40000, 30000));
        }

        [Fact]
        public void Create_AllowsExactly65536Total()
        {
            var rs = ReedSolomon16.Create(65535, 1);
            Assert.Equal(65536, rs.TotalShardCount);
        }

        [Fact]
        public void Encode_RejectsOddShardLength()
        {
            var rs = ReedSolomon16.Create(2, 1);
            var shards = new[] { new byte[7], new byte[7], new byte[7] };
            Assert.Throws<Erasure16Exception>(() => rs.Encode(shards));
        }

        [Fact]
        public void Encode_RejectsZeroShardLength()
        {
            var rs = ReedSolomon16.Create(2, 1);
            var shards = new[] { new byte[0], new byte[0], new byte[0] };
            Assert.Throws<Erasure16Exception>(() => rs.Encode(shards));
        }

        [Fact]
        public void Encode_RejectsMismatchedLengths()
        {
            var rs = ReedSolomon16.Create(2, 1);
            var shards = new[] { new byte[8], new byte[10], new byte[8] };
            Assert.Throws<Erasure16Exception>(() => rs.Encode(shards));
        }

        [Fact]
        public void Encode_RejectsWrongShardCount()
        {
            var rs = ReedSolomon16.Create(2, 1); // expects 3
            var shards = new[] { new byte[8], new byte[8] };
            Assert.Throws<Erasure16Exception>(() => rs.Encode(shards));
        }

        [Fact]
        public void Reconstruct_RejectsTooFewPresent()
        {
            var rs = ReedSolomon16.Create(3, 2);
            var shards = new byte[5][];
            for (int i = 0; i < 5; i++)
            {
                shards[i] = new byte[8];
            }

            // Only 2 present (< K = 3).
            var present = new[] { true, true, false, false, false };
            Assert.Throws<Erasure16Exception>(() => rs.Reconstruct(shards, present));
        }

        [Fact]
        public void Reconstruct_RejectsOddPresentLength()
        {
            var rs = ReedSolomon16.Create(2, 1);
            var shards = new byte[3][];
            shards[0] = new byte[7];
            shards[1] = new byte[7];
            shards[2] = null;
            var present = new[] { true, true, false };
            Assert.Throws<Erasure16Exception>(() => rs.Reconstruct(shards, present));
        }

        [Fact]
        public void Reconstruct_RejectsMismatchedPresentLengths()
        {
            var rs = ReedSolomon16.Create(2, 1);
            var shards = new byte[3][];
            shards[0] = new byte[8];
            shards[1] = new byte[10];
            shards[2] = null;
            var present = new[] { true, true, false };
            Assert.Throws<Erasure16Exception>(() => rs.Reconstruct(shards, present));
        }
    }
}
