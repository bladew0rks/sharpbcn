using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpBcn;

internal static class Bc7EncoderSimd
{
    public const int Lanes = 8;

    private const int RefineIterations = 2;
    private const int PowerIterations = 8;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void EncodeRow(ReadOnlySpan<byte> src, int width, int height, int by, int blocksX, Span<byte> row)
    {
        Span<byte> blocks = stackalloc byte[64 * Lanes];
        Span<byte> encoded = stackalloc byte[16 * Lanes];
        Span<Vector256<float>> pixels = stackalloc Vector256<float>[64];

        for (int bx = 0; bx < blocksX; bx += Lanes)
        {
            int count = Math.Min(Lanes, blocksX - bx);

            for (int lane = 0; lane < Lanes; lane++)
            {
                int sourceLane = Math.Min(lane, count - 1);
                BcEncoder.GatherBlock(src, width, height, (bx + sourceLane) * 4, by * 4, blocks.Slice(lane * 64, 64));
            }

            Load(blocks, pixels);
            EncodeMode6(pixels, encoded);
            encoded[..(count * 16)].CopyTo(row[(bx * 16)..]);
        }
    }

    private static void Load(ReadOnlySpan<byte> blocks, Span<Vector256<float>> pixels)
    {
        ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(blocks);
        Span<uint> lane = stackalloc uint[Lanes];
        Vector256<int> mask = Vector256.Create(0xFF);

        for (int i = 0; i < 16; i++)
        {
            for (int l = 0; l < Lanes; l++)
            {
                lane[l] = source[l * 16 + i];
            }

            Vector256<int> p = Vector256.Create<uint>(lane).AsInt32();
            pixels[i] = Vector256.ConvertToSingle(p & mask);
            pixels[16 + i] = Vector256.ConvertToSingle((p >>> 8) & mask);
            pixels[32 + i] = Vector256.ConvertToSingle((p >>> 16) & mask);
            pixels[48 + i] = Vector256.ConvertToSingle(p >>> 24);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeMode6(ReadOnlySpan<Vector256<float>> pixels, Span<byte> output)
    {
        Span<Vector256<float>> endpoints = stackalloc Vector256<float>[8];
        Span<Vector256<int>> codes = stackalloc Vector256<int>[8];
        Span<Vector256<int>> bestCodes = stackalloc Vector256<int>[8];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> bestIndices = stackalloc Vector256<int>[16];
        Span<Vector256<float>> expanded = stackalloc Vector256<float>[8];
        Vector256<int> bestPBit0 = Vector256<int>.Zero;
        Vector256<int> bestPBit1 = Vector256<int>.Zero;
        Vector256<float> bestError = Vector256.Create(float.MaxValue);

        PrincipalEndpoints(pixels, endpoints);

        for (int iteration = 0; ; iteration++)
        {
            for (int pBits = 0; pBits < 4; pBits++)
            {
                Vector256<int> pBit0 = Vector256.Create(pBits & 1);
                Vector256<int> pBit1 = Vector256.Create(pBits >> 1);

                for (int c = 0; c < 4; c++)
                {
                    codes[c] = QuantizeWithPBit(endpoints[c], 7, pBit0, out expanded[c]);
                    codes[4 + c] = QuantizeWithPBit(endpoints[4 + c], 7, pBit1, out expanded[4 + c]);
                }

                Vector256<float> error = MatchIndices(pixels, expanded, 4, indices);
                Vector256<int> better = Vector256.LessThan(error, bestError).AsInt32();
                bestError = Vector256.Min(error, bestError);
                bestPBit0 = Vector256.ConditionalSelect(better, pBit0, bestPBit0);
                bestPBit1 = Vector256.ConditionalSelect(better, pBit1, bestPBit1);

                for (int k = 0; k < 8; k++)
                {
                    bestCodes[k] = Vector256.ConditionalSelect(better, codes[k], bestCodes[k]);
                }

                for (int i = 0; i < 16; i++)
                {
                    bestIndices[i] = Vector256.ConditionalSelect(better, indices[i], bestIndices[i]);
                }
            }

            if (iteration == RefineIterations)
            {
                break;
            }

            LeastSquares(pixels, bestIndices, 4, endpoints);
        }

        Vector256<int> swap = Vector256.GreaterThanOrEqual(bestIndices[0], Vector256.Create(8));

        for (int c = 0; c < 4; c++)
        {
            Vector256<int> low = bestCodes[c];
            bestCodes[c] = Vector256.ConditionalSelect(swap, bestCodes[4 + c], low);
            bestCodes[4 + c] = Vector256.ConditionalSelect(swap, low, bestCodes[4 + c]);
        }

        Vector256<int> firstPBit = bestPBit0;
        bestPBit0 = Vector256.ConditionalSelect(swap, bestPBit1, firstPBit);
        bestPBit1 = Vector256.ConditionalSelect(swap, firstPBit, bestPBit1);

        for (int i = 0; i < 16; i++)
        {
            bestIndices[i] = Vector256.ConditionalSelect(swap, Vector256.Create(15) - bestIndices[i], bestIndices[i]);
        }

        for (int lane = 0; lane < Lanes; lane++)
        {
            BitWriter writer = new BitWriter();
            writer.Write(1 << 6, 7);

            for (int c = 0; c < 4; c++)
            {
                writer.Write(bestCodes[c].GetElement(lane), 7);
                writer.Write(bestCodes[4 + c].GetElement(lane), 7);
            }

            writer.Write(bestPBit0.GetElement(lane), 1);
            writer.Write(bestPBit1.GetElement(lane), 1);

            for (int i = 0; i < 16; i++)
            {
                writer.Write(bestIndices[i].GetElement(lane), i == 0 ? 3 : 4);
            }

            writer.CopyTo(output.Slice(lane * 16, 16));
        }
    }

    private static void PrincipalEndpoints(ReadOnlySpan<Vector256<float>> pixels, Span<Vector256<float>> endpoints)
    {
        Span<Vector256<float>> mean = stackalloc Vector256<float>[4];
        Span<Vector256<float>> covariance = stackalloc Vector256<float>[16];
        Span<Vector256<float>> axis = stackalloc Vector256<float>[4];
        Span<Vector256<float>> next = stackalloc Vector256<float>[4];

        for (int c = 0; c < 4; c++)
        {
            Vector256<float> sum = Vector256<float>.Zero;

            for (int i = 0; i < 16; i++)
            {
                sum += pixels[c * 16 + i];
            }

            mean[c] = sum * Vector256.Create(1f / 16);
        }

        for (int c = 0; c < 4; c++)
        {
            for (int d = c; d < 4; d++)
            {
                Vector256<float> sum = Vector256<float>.Zero;

                for (int i = 0; i < 16; i++)
                {
                    sum += (pixels[c * 16 + i] - mean[c]) * (pixels[d * 16 + i] - mean[d]);
                }

                covariance[c * 4 + d] = sum;
                covariance[d * 4 + c] = sum;
            }
        }

        Vector256<float> largest = covariance[0];

        for (int c = 0; c < 4; c++)
        {
            axis[c] = covariance[c];
        }

        for (int row = 1; row < 4; row++)
        {
            Vector256<float> larger = Vector256.GreaterThan(covariance[row * 4 + row], largest);
            largest = Vector256.Max(covariance[row * 4 + row], largest);

            for (int c = 0; c < 4; c++)
            {
                axis[c] = Vector256.ConditionalSelect(larger, covariance[row * 4 + c], axis[c]);
            }
        }

        for (int iteration = 0; iteration < PowerIterations; iteration++)
        {
            Vector256<float> magnitude = Vector256<float>.Zero;

            for (int c = 0; c < 4; c++)
            {
                next[c] = covariance[c * 4] * axis[0] + covariance[c * 4 + 1] * axis[1] + covariance[c * 4 + 2] * axis[2] + covariance[c * 4 + 3] * axis[3];
                magnitude = Vector256.Max(magnitude, Vector256.Abs(next[c]));
            }

            Vector256<float> scale = SafeReciprocal(magnitude);

            for (int c = 0; c < 4; c++)
            {
                axis[c] = next[c] * scale;
            }
        }

        Vector256<float> length = Vector256.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2] + axis[3] * axis[3]);
        Vector256<float> inverseLength = SafeReciprocal(length);

        for (int c = 0; c < 4; c++)
        {
            axis[c] *= inverseLength;
        }

        Vector256<float> low = Vector256.Create(float.MaxValue);
        Vector256<float> high = Vector256.Create(float.MinValue);

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> t = Vector256<float>.Zero;

            for (int c = 0; c < 4; c++)
            {
                t += (pixels[c * 16 + i] - mean[c]) * axis[c];
            }

            low = Vector256.Min(low, t);
            high = Vector256.Max(high, t);
        }

        for (int c = 0; c < 4; c++)
        {
            endpoints[c] = Clamp255(mean[c] + low * axis[c]);
            endpoints[4 + c] = Clamp255(mean[c] + high * axis[c]);
        }
    }

    private static Vector256<float> SafeReciprocal(Vector256<float> value)
    {
        Vector256<float> positive = Vector256.GreaterThan(value, Vector256.Create(1e-20f));
        return Vector256.ConditionalSelect(positive, Vector256.Create(1f) / Vector256.Max(value, Vector256.Create(1e-20f)), Vector256<float>.Zero);
    }

    private static Vector256<float> Clamp255(Vector256<float> value) => Vector256.Min(Vector256.Max(value, Vector256<float>.Zero), Vector256.Create(255f));

    private static Vector256<int> QuantizeWithPBit(Vector256<float> value, int bits, Vector256<int> pBit, out Vector256<float> expanded)
    {
        int max = (1 << bits) - 1;
        Vector256<float> scaled = value * Vector256.Create(((1 << (bits + 1)) - 1) / 255f);
        Vector256<float> half = (scaled - Vector256.ConvertToSingle(pBit)) * Vector256.Create(0.5f);
        Vector256<int> code = Vector256.ConvertToInt32(Vector256.Round(half));
        code = Vector256.Min(Vector256.Max(code, Vector256<int>.Zero), Vector256.Create(max));
        expanded = Vector256.ConvertToSingle(Unquantize((code << 1) | pBit, bits + 1));
        return code;
    }

    private static Vector256<int> Unquantize(Vector256<int> value, int bits) => bits >= 8 ? value : (value << (8 - bits)) | (value >>> (2 * bits - 8));

    private static Vector256<float> Weight(Vector256<int> index, int indexBits)
    {
        int levels = (1 << indexBits) - 1;
        int bias = indexBits switch { 2 => 1, 3 => 3, _ => 7 };
        Vector256<float> numerator = Vector256.ConvertToSingle(index * Vector256.Create(64) + Vector256.Create(bias));
        return Vector256.Floor(numerator / Vector256.Create((float)levels));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> MatchIndices(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> endpoints, int indexBits, Span<Vector256<int>> indices)
    {
        int count = 1 << indexBits;
        Span<Vector256<float>> palette = stackalloc Vector256<float>[16 * 4];

        for (int k = 0; k < count; k++)
        {
            Vector256<float> weight = Weight(Vector256.Create(k), indexBits);
            Vector256<float> inverse = Vector256.Create(64f) - weight;

            for (int c = 0; c < 4; c++)
            {
                palette[k * 4 + c] = Vector256.Floor((inverse * endpoints[c] + weight * endpoints[4 + c] + Vector256.Create(32f)) * Vector256.Create(1f / 64));
            }
        }

        Vector256<float> total = Vector256<float>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> r = pixels[i], g = pixels[16 + i], b = pixels[32 + i], a = pixels[48 + i];
            Vector256<float> best = Vector256.Create(float.MaxValue);
            Vector256<int> bestIndex = Vector256<int>.Zero;

            for (int k = 0; k < count; k++)
            {
                Vector256<float> dr = r - palette[k * 4], dg = g - palette[k * 4 + 1], db = b - palette[k * 4 + 2], da = a - palette[k * 4 + 3];
                Vector256<float> error = dr * dr + dg * dg + db * db + da * da;
                Vector256<int> better = Vector256.LessThan(error, best).AsInt32();
                best = Vector256.Min(error, best);
                bestIndex = Vector256.ConditionalSelect(better, Vector256.Create(k), bestIndex);
            }

            indices[i] = bestIndex;
            total += best;
        }

        return total;
    }

    private static void LeastSquares(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<int>> indices, int indexBits, Span<Vector256<float>> endpoints)
    {
        Vector256<float> aa = Vector256<float>.Zero, ab = Vector256<float>.Zero, bb = Vector256<float>.Zero;
        Span<Vector256<float>> sumA = stackalloc Vector256<float>[4];
        Span<Vector256<float>> sumB = stackalloc Vector256<float>[4];
        sumA.Clear();
        sumB.Clear();

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> t = Weight(indices[i], indexBits) * Vector256.Create(1f / 64);
            Vector256<float> s = Vector256.Create(1f) - t;
            aa += s * s;
            ab += s * t;
            bb += t * t;

            for (int c = 0; c < 4; c++)
            {
                sumA[c] += s * pixels[c * 16 + i];
                sumB[c] += t * pixels[c * 16 + i];
            }
        }

        Vector256<float> determinant = aa * bb - ab * ab;
        Vector256<float> solvable = Vector256.GreaterThan(determinant, Vector256.Create(1e-6f));
        Vector256<float> inverse = Vector256.Create(1f) / Vector256.ConditionalSelect(solvable, determinant, Vector256.Create(1f));

        for (int c = 0; c < 4; c++)
        {
            Vector256<float> low = Clamp255((bb * sumA[c] - ab * sumB[c]) * inverse);
            Vector256<float> high = Clamp255((aa * sumB[c] - ab * sumA[c]) * inverse);
            endpoints[c] = Vector256.ConditionalSelect(solvable, low, endpoints[c]);
            endpoints[4 + c] = Vector256.ConditionalSelect(solvable, high, endpoints[4 + c]);
        }
    }

    private ref struct BitWriter
    {
        private UInt128 value;
        private int position;

        public void Write(int bits, int count)
        {
            value |= (UInt128)(uint)bits << position;
            position += count;
        }

        public readonly void CopyTo(Span<byte> destination) => BinaryPrimitives.WriteUInt128LittleEndian(destination, value);
    }
}
