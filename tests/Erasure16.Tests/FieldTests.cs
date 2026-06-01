using System;
using Erasure16;
using Xunit;

namespace Erasure16.Tests
{
    /// <summary>
    /// Exercises GF(2^16) arithmetic: full-period self-check, the field laws
    /// (commutativity, associativity, distributivity, inverses), and the explicit
    /// table-build relationships from SPEC.md section 1.
    /// </summary>
    public sealed class FieldTests
    {
        // A spread of representative non-zero elements used across the law checks.
        private static readonly ushort[] Samples =
        {
            1, 2, 3, 4, 5, 255, 256, 257, 1000, 4096, 0x8000, 0xABCD, 0xFFFE, 0xFFFF,
        };

        [Fact]
        public void ExpTableHasFullPeriod_AllNonZeroValuesAppearExactlyOnce()
        {
            var seen = new bool[Gf16.FieldSize];
            for (int i = 0; i < Gf16.Order; i++)
            {
                ushort v = Gf16.Exp(i);
                Assert.NotEqual(0, v);
                Assert.False(seen[v], "value " + v + " appeared twice in the exp table at index " + i);
                seen[v] = true;
            }

            for (int v = 1; v < Gf16.FieldSize; v++)
            {
                Assert.True(seen[v], "value " + v + " missing from exp table (period not full)");
            }
        }

        [Fact]
        public void ExpAndLogAreInverseRelations()
        {
            // log(exp(i)) == i for every i in 0..Order-1.
            for (int i = 0; i < Gf16.Order; i++)
            {
                ushort e = Gf16.Exp(i);
                Assert.Equal(i, Gf16.Log(e));
            }

            // exp(log(a)) == a for every non-zero a.
            for (int a = 1; a < Gf16.FieldSize; a++)
            {
                Assert.Equal((ushort)a, Gf16.Exp(Gf16.Log((ushort)a)));
            }
        }

        [Fact]
        public void Exp0IsOne_And_Exp1IsGenerator2()
        {
            Assert.Equal((ushort)1, Gf16.Exp(0));
            Assert.Equal((ushort)2, Gf16.Exp(1)); // alpha = 2
        }

        [Fact]
        public void TableBuildMatchesSpecPseudocode()
        {
            // Re-derive exp/log via the SPEC.md section 1 pseudocode and compare against
            // the library's published table (through Exp/Log).
            int x = 1;
            for (int i = 0; i < Gf16.Order; i++)
            {
                Assert.Equal((ushort)x, Gf16.Exp(i));
                Assert.Equal(i, Gf16.Log((ushort)x));

                x <<= 1;
                if ((x & 0x10000) != 0)
                {
                    x ^= 0x1100B;
                }
            }

            // After the full loop the field must wrap back to 1.
            Assert.Equal(1, x);
        }

        [Fact]
        public void MultiplyByZeroIsZero()
        {
            foreach (ushort a in Samples)
            {
                Assert.Equal((ushort)0, Gf16.Mul(0, a));
                Assert.Equal((ushort)0, Gf16.Mul(a, 0));
            }

            Assert.Equal((ushort)0, Gf16.Mul(0, 0));
        }

        [Fact]
        public void MultiplyByOneIsIdentity()
        {
            foreach (ushort a in Samples)
            {
                Assert.Equal(a, Gf16.Mul(a, 1));
                Assert.Equal(a, Gf16.Mul(1, a));
            }
        }

        [Fact]
        public void MultiplicationIsCommutative()
        {
            foreach (ushort a in Samples)
            {
                foreach (ushort b in Samples)
                {
                    Assert.Equal(Gf16.Mul(a, b), Gf16.Mul(b, a));
                }
            }
        }

        [Fact]
        public void MultiplicationIsAssociative()
        {
            foreach (ushort a in Samples)
            {
                foreach (ushort b in Samples)
                {
                    foreach (ushort c in Samples)
                    {
                        ushort left = Gf16.Mul(Gf16.Mul(a, b), c);
                        ushort right = Gf16.Mul(a, Gf16.Mul(b, c));
                        Assert.Equal(left, right);
                    }
                }
            }
        }

        [Fact]
        public void MultiplicationDistributesOverAddition()
        {
            // a * (b + c) == (a*b) + (a*c), with addition = XOR.
            foreach (ushort a in Samples)
            {
                foreach (ushort b in Samples)
                {
                    foreach (ushort c in Samples)
                    {
                        ushort left = Gf16.Mul(a, (ushort)(b ^ c));
                        ushort right = (ushort)(Gf16.Mul(a, b) ^ Gf16.Mul(a, c));
                        Assert.Equal(left, right);
                    }
                }
            }
        }

        [Fact]
        public void InverseTimesSelfIsOne_ForEveryNonZeroElement()
        {
            for (int a = 1; a < Gf16.FieldSize; a++)
            {
                ushort inv = Gf16.Inv((ushort)a);
                Assert.Equal((ushort)1, Gf16.Mul((ushort)a, inv));
            }
        }

        [Fact]
        public void InverseOfOneIsOne()
        {
            Assert.Equal((ushort)1, Gf16.Inv(1));
        }

        [Fact]
        public void DivisionIsMultiplicationByInverse_AndInvertsMultiplication()
        {
            foreach (ushort a in Samples)
            {
                foreach (ushort b in Samples)
                {
                    // div(a, b) == mul(a, inv(b))
                    Assert.Equal(Gf16.Mul(a, Gf16.Inv(b)), Gf16.Div(a, b));

                    // (a * b) / b == a
                    ushort product = Gf16.Mul(a, b);
                    Assert.Equal(a, Gf16.Div(product, b));
                }
            }
        }

        [Fact]
        public void DivisionOfZeroNumeratorIsZero()
        {
            foreach (ushort b in Samples)
            {
                Assert.Equal((ushort)0, Gf16.Div(0, b));
            }
        }

        [Fact]
        public void ZeroHasNoInverse_Throws()
        {
            Assert.Throws<Erasure16Exception>(() => Gf16.Inv(0));
        }

        [Fact]
        public void DivisionByZero_Throws()
        {
            Assert.Throws<Erasure16Exception>(() => Gf16.Div(1234, 0));
        }

        [Fact]
        public void LogOfZero_Throws()
        {
            Assert.Throws<Erasure16Exception>(() => Gf16.Log(0));
        }

        [Theory]
        [InlineData(2, 2, 4)]            // x * x = x^2
        [InlineData(2, 3, 6)]           // x * (x+1) = x^2 + x
        [InlineData(0x8000, 2, 0x100B)] // x^15 * x = x^16 = reduction tail (0x1100B & 0xFFFF)
        public void KnownMultiplications(int a, int b, int expected)
        {
            Assert.Equal((ushort)expected, Gf16.Mul((ushort)a, (ushort)b));
        }
    }
}
