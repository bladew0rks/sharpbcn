using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpBcn;

internal static class Bc7EncoderSimd
{
    public const int Lanes = 8;

    private const int RefineIterations = 2;
    private const int PowerIterations = 8;

    private enum PBits
    {
        None,
        Unique,
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void EncodeRow(ReadOnlySpan<byte> src, int width, int height, int by, int blocksX, Span<byte> row)
    {
        Span<byte> blocks = stackalloc byte[64 * Lanes];
        Span<byte> encoded = stackalloc byte[16 * Lanes];
        Span<byte> candidate = stackalloc byte[16 * Lanes];
        Span<Vector256<float>> pixels = stackalloc Vector256<float>[64];
        Span<Vector256<float>> rotated = stackalloc Vector256<float>[64];

        for (int bx = 0; bx < blocksX; bx += Lanes)
        {
            int count = Math.Min(Lanes, blocksX - bx);

            for (int lane = 0; lane < Lanes; lane++)
            {
                int sourceLane = Math.Min(lane, count - 1);
                BcEncoder.GatherBlock(src, width, height, (bx + sourceLane) * 4, by * 4, blocks.Slice(lane * 64, 64));
            }

            Load(blocks, pixels);
            Vector256<float> bestError = EncodeMode6(pixels, encoded);

            for (int rotation = 0; rotation < 4; rotation++)
            {
                Rotate(pixels, rotation, rotated);
                Keep(EncodeMode5(rotated, rotation, candidate), ref bestError, candidate, encoded);

                for (int indexSelection = 0; indexSelection < 2; indexSelection++)
                {
                    Keep(EncodeMode4(rotated, rotation, indexSelection, candidate), ref bestError, candidate, encoded);
                }
            }

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

    private static void Rotate(ReadOnlySpan<Vector256<float>> pixels, int rotation, Span<Vector256<float>> rotated)
    {
        pixels.CopyTo(rotated);

        if (rotation != 0)
        {
            pixels.Slice((rotation - 1) * 16, 16).CopyTo(rotated[48..]);
            pixels.Slice(48, 16).CopyTo(rotated.Slice((rotation - 1) * 16, 16));
        }
    }

    private static void Keep(Vector256<float> error, ref Vector256<float> bestError, ReadOnlySpan<byte> candidate, Span<byte> encoded)
    {
        ulong better = Vector256.LessThan(error, bestError).ExtractMostSignificantBits();
        bestError = Vector256.Min(error, bestError);

        while (better != 0)
        {
            int lane = BitOperations.TrailingZeroCount(better);
            candidate.Slice(lane * 16, 16).CopyTo(encoded.Slice(lane * 16, 16));
            better &= better - 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> EncodeMode6(ReadOnlySpan<Vector256<float>> pixels, Span<byte> output)
    {
        Span<Vector256<int>> codes = stackalloc Vector256<int>[8];
        Span<Vector256<int>> pBits = stackalloc Vector256<int>[2];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Vector256<float> error = FitEndpoints(pixels, 4, 7, PBits.Unique, 4, codes, pBits, indices);

        for (int lane = 0; lane < Lanes; lane++)
        {
            BitWriter writer = new BitWriter();
            writer.Write(1 << 6, 7);
            WriteEndpoints(ref writer, codes, 4, 7, lane);
            writer.Write(pBits[0].GetElement(lane), 1);
            writer.Write(pBits[1].GetElement(lane), 1);
            WriteIndices(ref writer, indices, 4, lane);
            writer.CopyTo(output.Slice(lane * 16, 16));
        }

        return error;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> EncodeMode5(ReadOnlySpan<Vector256<float>> rotated, int rotation, Span<byte> output)
    {
        Span<Vector256<int>> colorCodes = stackalloc Vector256<int>[6];
        Span<Vector256<int>> alphaCodes = stackalloc Vector256<int>[2];
        Span<Vector256<int>> pBits = stackalloc Vector256<int>[2];
        Span<Vector256<int>> colorIndices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> alphaIndices = stackalloc Vector256<int>[16];
        Vector256<float> error = FitEndpoints(rotated[..48], 3, 7, PBits.None, 2, colorCodes, pBits, colorIndices)
            + FitEndpoints(rotated[48..], 1, 8, PBits.None, 2, alphaCodes, pBits, alphaIndices);

        for (int lane = 0; lane < Lanes; lane++)
        {
            BitWriter writer = new BitWriter();
            writer.Write(1 << 5, 6);
            writer.Write(rotation, 2);
            WriteEndpoints(ref writer, colorCodes, 3, 7, lane);
            WriteEndpoints(ref writer, alphaCodes, 1, 8, lane);
            WriteIndices(ref writer, colorIndices, 2, lane);
            WriteIndices(ref writer, alphaIndices, 2, lane);
            writer.CopyTo(output.Slice(lane * 16, 16));
        }

        return error;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> EncodeMode4(ReadOnlySpan<Vector256<float>> rotated, int rotation, int indexSelection, Span<byte> output)
    {
        Span<Vector256<int>> colorCodes = stackalloc Vector256<int>[6];
        Span<Vector256<int>> alphaCodes = stackalloc Vector256<int>[2];
        Span<Vector256<int>> pBits = stackalloc Vector256<int>[2];
        Span<Vector256<int>> colorIndices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> alphaIndices = stackalloc Vector256<int>[16];
        int colorIndexBits = indexSelection == 0 ? 2 : 3;
        int alphaIndexBits = indexSelection == 0 ? 3 : 2;
        Vector256<float> error = FitEndpoints(rotated[..48], 3, 5, PBits.None, colorIndexBits, colorCodes, pBits, colorIndices)
            + FitEndpoints(rotated[48..], 1, 6, PBits.None, alphaIndexBits, alphaCodes, pBits, alphaIndices);

        for (int lane = 0; lane < Lanes; lane++)
        {
            BitWriter writer = new BitWriter();
            writer.Write(1 << 4, 5);
            writer.Write(rotation, 2);
            writer.Write(indexSelection, 1);
            WriteEndpoints(ref writer, colorCodes, 3, 5, lane);
            WriteEndpoints(ref writer, alphaCodes, 1, 6, lane);
            WriteIndices(ref writer, indexSelection == 0 ? colorIndices : alphaIndices, 2, lane);
            WriteIndices(ref writer, indexSelection == 0 ? alphaIndices : colorIndices, 3, lane);
            writer.CopyTo(output.Slice(lane * 16, 16));
        }

        return error;
    }

    private static void WriteEndpoints(ref BitWriter writer, ReadOnlySpan<Vector256<int>> codes, int channels, int bits, int lane)
    {
        for (int c = 0; c < channels; c++)
        {
            writer.Write(codes[c].GetElement(lane), bits);
            writer.Write(codes[channels + c].GetElement(lane), bits);
        }
    }

    private static void WriteIndices(ref BitWriter writer, ReadOnlySpan<Vector256<int>> indices, int indexBits, int lane)
    {
        for (int i = 0; i < 16; i++)
        {
            writer.Write(indices[i].GetElement(lane), i == 0 ? indexBits - 1 : indexBits);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> FitEndpoints(ReadOnlySpan<Vector256<float>> pixels, int channels, int bits, PBits pBitMode, int indexBits,
        Span<Vector256<int>> bestCodes, Span<Vector256<int>> bestPBits, Span<Vector256<int>> bestIndices)
    {
        Span<Vector256<float>> endpoints = stackalloc Vector256<float>[8];
        Span<Vector256<float>> expanded = stackalloc Vector256<float>[8];
        Span<Vector256<int>> codes = stackalloc Vector256<int>[8];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Vector256<float> bestError = Vector256.Create(float.MaxValue);
        int pBitCombinations = pBitMode == PBits.Unique ? 4 : 1;

        PrincipalEndpoints(pixels, channels, endpoints);

        for (int iteration = 0; ; iteration++)
        {
            for (int combination = 0; combination < pBitCombinations; combination++)
            {
                Vector256<int> pBit0 = Vector256.Create(combination & 1);
                Vector256<int> pBit1 = Vector256.Create(combination >> 1);

                for (int c = 0; c < channels; c++)
                {
                    if (pBitMode == PBits.None)
                    {
                        codes[c] = Quantize(endpoints[c], bits, out expanded[c]);
                        codes[channels + c] = Quantize(endpoints[channels + c], bits, out expanded[channels + c]);
                    }
                    else
                    {
                        codes[c] = QuantizeWithPBit(endpoints[c], bits, pBit0, out expanded[c]);
                        codes[channels + c] = QuantizeWithPBit(endpoints[channels + c], bits, pBit1, out expanded[channels + c]);
                    }
                }

                Vector256<float> error = MatchIndices(pixels, channels, expanded, indexBits, indices);
                Vector256<int> better = Vector256.LessThan(error, bestError).AsInt32();
                bestError = Vector256.Min(error, bestError);
                bestPBits[0] = Vector256.ConditionalSelect(better, pBit0, bestPBits[0]);
                bestPBits[1] = Vector256.ConditionalSelect(better, pBit1, bestPBits[1]);

                for (int k = 0; k < channels * 2; k++)
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

            LeastSquares(pixels, channels, bestIndices, indexBits, endpoints);
        }

        FixAnchor(channels, indexBits, bestCodes, bestPBits, bestIndices);
        return bestError;
    }

    private static void FixAnchor(int channels, int indexBits, Span<Vector256<int>> codes, Span<Vector256<int>> pBits, Span<Vector256<int>> indices)
    {
        int highest = (1 << indexBits) - 1;
        Vector256<int> swap = Vector256.GreaterThan(indices[0], Vector256.Create(highest >> 1));

        for (int c = 0; c < channels; c++)
        {
            Vector256<int> low = codes[c];
            codes[c] = Vector256.ConditionalSelect(swap, codes[channels + c], low);
            codes[channels + c] = Vector256.ConditionalSelect(swap, low, codes[channels + c]);
        }

        Vector256<int> first = pBits[0];
        pBits[0] = Vector256.ConditionalSelect(swap, pBits[1], first);
        pBits[1] = Vector256.ConditionalSelect(swap, first, pBits[1]);

        for (int i = 0; i < 16; i++)
        {
            indices[i] = Vector256.ConditionalSelect(swap, Vector256.Create(highest) - indices[i], indices[i]);
        }
    }

    private static void PrincipalEndpoints(ReadOnlySpan<Vector256<float>> pixels, int channels, Span<Vector256<float>> endpoints)
    {
        Span<Vector256<float>> mean = stackalloc Vector256<float>[4];
        Span<Vector256<float>> covariance = stackalloc Vector256<float>[16];
        Span<Vector256<float>> axis = stackalloc Vector256<float>[4];
        Span<Vector256<float>> next = stackalloc Vector256<float>[4];

        for (int c = 0; c < channels; c++)
        {
            Vector256<float> sum = Vector256<float>.Zero;

            for (int i = 0; i < 16; i++)
            {
                sum += pixels[c * 16 + i];
            }

            mean[c] = sum * Vector256.Create(1f / 16);
        }

        for (int c = 0; c < channels; c++)
        {
            for (int d = c; d < channels; d++)
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

        for (int c = 0; c < channels; c++)
        {
            axis[c] = covariance[c];
        }

        for (int row = 1; row < channels; row++)
        {
            Vector256<float> larger = Vector256.GreaterThan(covariance[row * 4 + row], largest);
            largest = Vector256.Max(covariance[row * 4 + row], largest);

            for (int c = 0; c < channels; c++)
            {
                axis[c] = Vector256.ConditionalSelect(larger, covariance[row * 4 + c], axis[c]);
            }
        }

        for (int iteration = 0; iteration < PowerIterations; iteration++)
        {
            Vector256<float> magnitude = Vector256<float>.Zero;

            for (int c = 0; c < channels; c++)
            {
                Vector256<float> sum = Vector256<float>.Zero;

                for (int d = 0; d < channels; d++)
                {
                    sum += covariance[c * 4 + d] * axis[d];
                }

                next[c] = sum;
                magnitude = Vector256.Max(magnitude, Vector256.Abs(sum));
            }

            Vector256<float> scale = SafeReciprocal(magnitude);

            for (int c = 0; c < channels; c++)
            {
                axis[c] = next[c] * scale;
            }
        }

        Vector256<float> lengthSquared = Vector256<float>.Zero;

        for (int c = 0; c < channels; c++)
        {
            lengthSquared += axis[c] * axis[c];
        }

        Vector256<float> inverseLength = SafeReciprocal(Vector256.Sqrt(lengthSquared));

        for (int c = 0; c < channels; c++)
        {
            axis[c] *= inverseLength;
        }

        Vector256<float> low = Vector256.Create(float.MaxValue);
        Vector256<float> high = Vector256.Create(float.MinValue);

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> t = Vector256<float>.Zero;

            for (int c = 0; c < channels; c++)
            {
                t += (pixels[c * 16 + i] - mean[c]) * axis[c];
            }

            low = Vector256.Min(low, t);
            high = Vector256.Max(high, t);
        }

        for (int c = 0; c < channels; c++)
        {
            endpoints[c] = Clamp255(mean[c] + low * axis[c]);
            endpoints[channels + c] = Clamp255(mean[c] + high * axis[c]);
        }
    }

    private static Vector256<float> SafeReciprocal(Vector256<float> value)
    {
        Vector256<float> positive = Vector256.GreaterThan(value, Vector256.Create(1e-20f));
        return Vector256.ConditionalSelect(positive, Vector256.Create(1f) / Vector256.Max(value, Vector256.Create(1e-20f)), Vector256<float>.Zero);
    }

    private static Vector256<float> Clamp255(Vector256<float> value) => Vector256.Min(Vector256.Max(value, Vector256<float>.Zero), Vector256.Create(255f));

    private static Vector256<int> Quantize(Vector256<float> value, int bits, out Vector256<float> expanded)
    {
        int max = (1 << bits) - 1;
        Vector256<int> code = Vector256.ConvertToInt32(Vector256.Round(value * Vector256.Create(max / 255f)));
        code = Vector256.Min(Vector256.Max(code, Vector256<int>.Zero), Vector256.Create(max));
        expanded = Vector256.ConvertToSingle(Unquantize(code, bits));
        return code;
    }

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
    private static Vector256<float> MatchIndices(ReadOnlySpan<Vector256<float>> pixels, int channels, ReadOnlySpan<Vector256<float>> endpoints, int indexBits,
        Span<Vector256<int>> indices)
    {
        int count = 1 << indexBits;
        Span<Vector256<float>> palette = stackalloc Vector256<float>[16 * 4];

        for (int k = 0; k < count; k++)
        {
            Vector256<float> weight = Weight(Vector256.Create(k), indexBits);
            Vector256<float> inverse = Vector256.Create(64f) - weight;

            for (int c = 0; c < channels; c++)
            {
                palette[k * 4 + c] = Vector256.Floor((inverse * endpoints[c] + weight * endpoints[channels + c] + Vector256.Create(32f)) * Vector256.Create(1f / 64));
            }
        }

        Vector256<float> total = Vector256<float>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> best = Vector256.Create(float.MaxValue);
            Vector256<int> bestIndex = Vector256<int>.Zero;

            for (int k = 0; k < count; k++)
            {
                Vector256<float> error = Vector256<float>.Zero;

                for (int c = 0; c < channels; c++)
                {
                    Vector256<float> difference = pixels[c * 16 + i] - palette[k * 4 + c];
                    error += difference * difference;
                }

                Vector256<int> better = Vector256.LessThan(error, best).AsInt32();
                best = Vector256.Min(error, best);
                bestIndex = Vector256.ConditionalSelect(better, Vector256.Create(k), bestIndex);
            }

            indices[i] = bestIndex;
            total += best;
        }

        return total;
    }

    private static void LeastSquares(ReadOnlySpan<Vector256<float>> pixels, int channels, ReadOnlySpan<Vector256<int>> indices, int indexBits,
        Span<Vector256<float>> endpoints)
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

            for (int c = 0; c < channels; c++)
            {
                sumA[c] += s * pixels[c * 16 + i];
                sumB[c] += t * pixels[c * 16 + i];
            }
        }

        Vector256<float> determinant = aa * bb - ab * ab;
        Vector256<float> solvable = Vector256.GreaterThan(determinant, Vector256.Create(1e-6f));
        Vector256<float> inverse = Vector256.Create(1f) / Vector256.ConditionalSelect(solvable, determinant, Vector256.Create(1f));

        for (int c = 0; c < channels; c++)
        {
            Vector256<float> low = Clamp255((bb * sumA[c] - ab * sumB[c]) * inverse);
            Vector256<float> high = Clamp255((aa * sumB[c] - ab * sumA[c]) * inverse);
            endpoints[c] = Vector256.ConditionalSelect(solvable, low, endpoints[c]);
            endpoints[channels + c] = Vector256.ConditionalSelect(solvable, high, endpoints[channels + c]);
        }
    }

    private struct BitWriter
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
