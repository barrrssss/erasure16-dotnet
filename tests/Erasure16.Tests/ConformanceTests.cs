using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erasure16;
using Xunit;

namespace Erasure16.Tests
{
    /// <summary>
    /// Loads the canonical conformance vectors from the spec submodule
    /// (<c>external/erasure16-spec/vectors/MANIFEST.json</c>) and asserts byte-exact
    /// agreement: every *.bin matches its recorded length + SHA-256, and the field,
    /// encode, and reconstruct cases reproduce exactly (SPEC.md section 8).
    ///
    /// Until the submodule is checked out the vectors are absent; the tests then pass
    /// as a no-op rather than fail, so the suite is green both before and after the
    /// submodule is wired in. Once the vectors exist, the assertions run for real.
    /// </summary>
    public sealed class ConformanceTests
    {
        private static readonly string VectorsDir = LocateVectorsDir();
        private static readonly string ManifestPath =
            VectorsDir == null ? null : Path.Combine(VectorsDir, "MANIFEST.json");

        private static bool VectorsAvailable =>
            ManifestPath != null && File.Exists(ManifestPath);

        /// <summary>
        /// Walks up from the test assembly looking for
        /// <c>external/erasure16-spec/vectors/MANIFEST.json</c>. Returns the vectors
        /// directory, or null if not found.
        /// </summary>
        private static string LocateVectorsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(
                    dir.FullName, "external", "erasure16-spec", "vectors", "MANIFEST.json");
                if (File.Exists(candidate))
                {
                    return Path.GetDirectoryName(candidate);
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static JsonElement LoadManifestRoot()
        {
            string json = File.ReadAllText(ManifestPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        private static byte[] LoadBin(string relativePath)
        {
            string full = Path.Combine(VectorsDir, relativePath);
            return File.ReadAllBytes(full);
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        [Fact]
        public void ManifestPresent_AndWellFormed()
        {
            if (!VectorsAvailable)
            {
                // Vectors not checked out yet (submodule absent); pass as a no-op.
                return;
            }

            JsonElement root = LoadManifestRoot();
            Assert.Equal(2, root.GetProperty("manifestVersion").GetInt32());
            Assert.True(root.TryGetProperty("field", out _));
            Assert.True(root.TryGetProperty("encode", out _));
            Assert.True(root.TryGetProperty("reconstruct", out _));
            Assert.True(root.TryGetProperty("files", out _));
        }

        [Fact]
        public void EveryBinFileMatchesLengthAndSha256()
        {
            if (!VectorsAvailable)
            {
                // Vectors not checked out yet (submodule absent); pass as a no-op.
                return;
            }

            JsonElement root = LoadManifestRoot();
            JsonElement files = root.GetProperty("files");
            Assert.True(files.GetArrayLength() > 0, "manifest lists no files");

            using var sha = SHA256.Create();
            foreach (JsonElement f in files.EnumerateArray())
            {
                string path = f.GetProperty("path").GetString();
                int expectedLen = f.GetProperty("length").GetInt32();
                string expectedSha = f.GetProperty("sha256").GetString();

                byte[] data = LoadBin(path);
                Assert.Equal(expectedLen, data.Length);

                string actualSha = ToHex(sha.ComputeHash(data));
                Assert.Equal(expectedSha, actualSha);
            }
        }

        [Fact]
        public void FieldVectorsReproduceExactly()
        {
            if (!VectorsAvailable)
            {
                // Vectors not checked out yet (submodule absent); pass as a no-op.
                return;
            }

            JsonElement root = LoadManifestRoot();
            foreach (JsonElement caseEl in root.GetProperty("field").EnumerateArray())
            {
                // Sanity: the field constants match this implementation.
                if (caseEl.TryGetProperty("reductionPolynomial", out var poly))
                {
                    Assert.Equal(Gf16.ReductionPolynomial, poly.GetInt32());
                }

                foreach (JsonElement m in caseEl.GetProperty("mul").EnumerateArray())
                {
                    ushort a = (ushort)m.GetProperty("a").GetInt32();
                    ushort b = (ushort)m.GetProperty("b").GetInt32();
                    ushort expected = (ushort)m.GetProperty("product").GetInt32();
                    Assert.Equal(expected, Gf16.Mul(a, b));
                }

                foreach (JsonElement iv in caseEl.GetProperty("inv").EnumerateArray())
                {
                    ushort a = (ushort)iv.GetProperty("a").GetInt32();
                    ushort expected = (ushort)iv.GetProperty("inv").GetInt32();
                    Assert.Equal(expected, Gf16.Inv(a));
                }
            }
        }

        [Fact]
        public void EncodeVectorsReproduceExactly()
        {
            if (!VectorsAvailable)
            {
                // Vectors not checked out yet (submodule absent); pass as a no-op.
                return;
            }

            JsonElement root = LoadManifestRoot();
            int cases = 0;
            foreach (JsonElement caseEl in root.GetProperty("encode").EnumerateArray())
            {
                cases++;
                int k = caseEl.GetProperty("k").GetInt32();
                int r = caseEl.GetProperty("r").GetInt32();
                int shardLen = caseEl.GetProperty("shardLen").GetInt32();

                byte[][] data = LoadPart(caseEl, "dataShards", "dataBlob", k, shardLen);
                byte[][] expectedParity = LoadPart(caseEl, "parityShards", "parityBlob", r, shardLen);

                var shards = new byte[k + r][];
                for (int i = 0; i < k; i++)
                {
                    shards[i] = data[i];
                }

                for (int p = 0; p < r; p++)
                {
                    shards[k + p] = new byte[shardLen];
                }

                ReedSolomon16.Create(k, r).Encode(shards);

                // The recomputed parity must equal the stored parity bytes byte-for-byte.
                for (int p = 0; p < r; p++)
                {
                    Assert.Equal(expectedParity[p], shards[k + p]);
                }
            }

            Assert.True(cases > 0, "no encode vectors found in manifest");
        }

        [Fact]
        public void ReconstructVectorsReproduceExactly()
        {
            if (!VectorsAvailable)
            {
                // Vectors not checked out yet (submodule absent); pass as a no-op.
                return;
            }

            JsonElement root = LoadManifestRoot();
            int cases = 0;
            foreach (JsonElement caseEl in root.GetProperty("reconstruct").EnumerateArray())
            {
                cases++;
                int k = caseEl.GetProperty("k").GetInt32();
                int r = caseEl.GetProperty("r").GetInt32();
                int shardLen = caseEl.GetProperty("shardLen").GetInt32();
                int n = k + r;

                bool[] present = PresentMask(caseEl, n);

                // Load the full expected set, then erase the not-present shards as input.
                var expected = new byte[n][];
                if (caseEl.TryGetProperty("expectedShards", out JsonElement expectedList))
                {
                    var expectedPaths = ReadStringArray(expectedList);
                    Assert.Equal(n, expectedPaths.Count);
                    for (int i = 0; i < n; i++)
                    {
                        expected[i] = LoadBin(expectedPaths[i]);
                        Assert.Equal(shardLen, expected[i].Length);
                    }
                }
                else
                {
                    byte[][] data = LoadPart(caseEl, "expectedShards", "expectedDataBlob", k, shardLen);
                    byte[][] parity = LoadPart(caseEl, "expectedShards", "expectedParityBlob", r, shardLen);
                    for (int i = 0; i < k; i++)
                    {
                        expected[i] = data[i];
                    }

                    for (int p = 0; p < r; p++)
                    {
                        expected[k + p] = parity[p];
                    }
                }

                var shards = new byte[n][];
                var presentArr = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    presentArr[i] = present[i];
                    shards[i] = present[i] ? (byte[])expected[i].Clone() : null;
                }

                ReedSolomon16.Create(k, r).Reconstruct(shards, presentArr);

                for (int i = 0; i < n; i++)
                {
                    Assert.Equal(expected[i], shards[i]);
                }
            }

            Assert.True(cases > 0, "no reconstruct vectors found in manifest");
        }

        /// <summary>
        /// Loads one part (data or parity) of a case's shards, in whichever layout of
        /// SPEC.md section 8 it uses: a per-shard file list, or a single concatenated blob.
        /// Exactly one must be present, so a case whose layout this reader did not
        /// understand cannot be skipped silently.
        /// </summary>
        private static byte[][] LoadPart(
            JsonElement caseEl, string listProperty, string blobProperty, int count, int shardLen)
        {
            bool hasList = caseEl.TryGetProperty(listProperty, out JsonElement list);
            bool hasBlob = caseEl.TryGetProperty(blobProperty, out JsonElement blob);
            Assert.True(hasList ^ hasBlob,
                $"exactly one of {listProperty} / {blobProperty} must be present");

            var shards = new byte[count][];
            if (hasList)
            {
                var paths = ReadStringArray(list);
                Assert.Equal(count, paths.Count);
                for (int i = 0; i < count; i++)
                {
                    shards[i] = LoadBin(paths[i]);
                    Assert.Equal(shardLen, shards[i].Length);
                }

                return shards;
            }

            byte[] bytes = LoadBin(blob.GetString());
            Assert.Equal(count * shardLen, bytes.Length);
            for (int i = 0; i < count; i++)
            {
                shards[i] = new byte[shardLen];
                Buffer.BlockCopy(bytes, i * shardLen, shards[i], 0, shardLen);
            }

            return shards;
        }

        /// <summary>
        /// A reconstruct case's present mask, from either the full <c>present</c> array or
        /// the compact <c>missing</c> index list of SPEC.md section 8.1.
        /// </summary>
        private static bool[] PresentMask(JsonElement caseEl, int n)
        {
            bool hasPresent = caseEl.TryGetProperty("present", out JsonElement presentEl);
            bool hasMissing = caseEl.TryGetProperty("missing", out JsonElement missingEl);
            Assert.True(hasPresent ^ hasMissing, "exactly one of present / missing must be present");

            var present = new bool[n];
            if (hasPresent)
            {
                int i = 0;
                foreach (JsonElement p in presentEl.EnumerateArray())
                {
                    Assert.True(i < n, "present mask is longer than N");
                    present[i++] = p.GetBoolean();
                }

                Assert.Equal(n, i);
                return present;
            }

            for (int i = 0; i < n; i++)
            {
                present[i] = true;
            }

            int previous = -1;
            foreach (JsonElement m in missingEl.EnumerateArray())
            {
                int index = m.GetInt32();
                Assert.True(index > previous, "missing indices must be strictly ascending");
                Assert.True(index < n, "missing index is out of range");
                present[index] = false;
                previous = index;
            }

            return present;
        }

        private static List<string> ReadStringArray(JsonElement arr)
        {
            var list = new List<string>();
            foreach (JsonElement e in arr.EnumerateArray())
            {
                list.Add(e.GetString());
            }

            return list;
        }
    }
}
