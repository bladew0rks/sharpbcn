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
    private const int PartitionRefineIterations = 1;
    private const int PowerIterations = 8;
    private const float ThreeSubsetErrorThreshold = 200;

    private enum PBits
    {
        None,
        Shared,
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
        Span<Vector256<float>> ones = stackalloc Vector256<float>[16];
        Span<Vector256<int>> partitions = stackalloc Vector256<int>[2];
        ones.Fill(Vector256.Create(1f));

        for (int bx = 0; bx < blocksX; bx += Lanes)
        {
            int count = Math.Min(Lanes, blocksX - bx);

            for (int lane = 0; lane < Lanes; lane++)
            {
                int sourceLane = Math.Min(lane, count - 1);
                BcEncoder.GatherBlock(src, width, height, (bx + sourceLane) * 4, by * 4, blocks.Slice(lane * 64, 64));
            }

            Load(blocks, pixels);
            Vector256<float> alphaError = OpaqueAlphaError(pixels);
            Vector256<float> translucent = Vector256.GreaterThan(alphaError, Vector256<float>.Zero);
            bool anyTranslucent = translucent != Vector256<float>.Zero;
            int estimateChannels = anyTranslucent ? 4 : 3;
            Vector256<float> bestError = EncodeEndpointMode(pixels, 6, Vector256<int>.Zero, alphaError, encoded);

            for (int rotation = anyTranslucent ? 0 : 1; rotation < 4; rotation++)
            {
                Vector256<float> allowed = rotation == 0 ? translucent : Vector256<float>.AllBitsSet;
                Rotate(pixels, rotation, rotated);
                Keep(EncodeMode5(rotated, ones, rotation, candidate), allowed, ref bestError, candidate, encoded);

                for (int indexSelection = 0; indexSelection < 2; indexSelection++)
                {
                    Keep(EncodeMode4(rotated, ones, rotation, indexSelection, candidate), allowed, ref bestError, candidate, encoded);
                }
            }

            EstimatePartitions(pixels, estimateChannels, 2, partitions);
            Keep(EncodeEndpointMode(pixels, 1, partitions[1], alphaError, candidate), Vector256<float>.AllBitsSet, ref bestError, candidate, encoded);
            Keep(EncodeEndpointMode(pixels, 3, partitions[1], alphaError, candidate), Vector256<float>.AllBitsSet, ref bestError, candidate, encoded);

            if (anyTranslucent)
            {
                Keep(EncodeEndpointMode(pixels, 7, partitions[1], alphaError, candidate), translucent, ref bestError, candidate, encoded);
            }

            Vector256<float> needsThreeSubsets = Vector256.GreaterThan(bestError, Vector256.Create(ThreeSubsetErrorThreshold));

            if (needsThreeSubsets != Vector256<float>.Zero)
            {
                EstimatePartitions(pixels, estimateChannels, 3, partitions);
                Keep(EncodeEndpointMode(pixels, 0, partitions[0], alphaError, candidate), needsThreeSubsets, ref bestError, candidate, encoded);
                Keep(EncodeEndpointMode(pixels, 2, partitions[1], alphaError, candidate), needsThreeSubsets, ref bestError, candidate, encoded);
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

    private static Vector256<float> OpaqueAlphaError(ReadOnlySpan<Vector256<float>> pixels)
    {
        Vector256<float> error = Vector256<float>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> difference = Vector256.Create(255f) - pixels[48 + i];
            error += difference * difference;
        }

        return error;
    }

    private static void Keep(Vector256<float> error, Vector256<float> allowed, ref Vector256<float> bestError, ReadOnlySpan<byte> candidate, Span<byte> encoded)
    {
        error = Vector256.ConditionalSelect(allowed, error, Vector256.Create(float.MaxValue));
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
    private static void EstimatePartitions(ReadOnlySpan<Vector256<float>> pixels, int channels, int subsets, Span<Vector256<int>> best)
    {
        int momentCount = channels + channels * (channels + 1) / 2;
        Span<Vector256<float>> moments = stackalloc Vector256<float>[16 * 14];
        Span<Vector256<float>> total = stackalloc Vector256<float>[14];
        Span<Vector256<float>> sums = stackalloc Vector256<float>[3 * 14];
        Span<int> counts = stackalloc int[3];
        Vector256<float> scale = Vector256.Create(1f / 255);
        total.Clear();

        for (int i = 0; i < 16; i++)
        {
            Span<Vector256<float>> moment = moments.Slice(i * momentCount, momentCount);
            int k = channels;

            for (int c = 0; c < channels; c++)
            {
                moment[c] = pixels[c * 16 + i] * scale;
            }

            for (int c = 0; c < channels; c++)
            {
                for (int d = c; d < channels; d++)
                {
                    moment[k++] = moment[c] * moment[d];
                }
            }

            for (int m = 0; m < momentCount; m++)
            {
                total[m] += moment[m];
            }
        }

        Vector256<float> bestEstimate = Vector256.Create(float.MaxValue);
        best[0] = Vector256<int>.Zero;
        best[1] = Vector256<int>.Zero;

        for (int partition = 0; partition < 64; partition++)
        {
            ReadOnlySpan<byte> members = Bc7Tables.SubsetMembers(subsets, partition);
            counts[1] = members[0];
            counts[2] = subsets == 3 ? members[1 + members[0]] : 0;

            for (int subset = 1; subset < subsets; subset++)
            {
                ReadOnlySpan<byte> list = subset == 1 ? members.Slice(1, members[0]) : members.Slice(2 + members[0], counts[2]);
                Span<Vector256<float>> sum = sums.Slice(subset * 14, momentCount);
                moments.Slice(list[0] * momentCount, momentCount).CopyTo(sum);

                for (int j = 1; j < list.Length; j++)
                {
                    ReadOnlySpan<Vector256<float>> moment = moments.Slice(list[j] * momentCount, momentCount);

                    for (int m = 0; m < momentCount; m++)
                    {
                        sum[m] += moment[m];
                    }
                }
            }

            counts[0] = 16 - counts[1] - counts[2];

            for (int m = 0; m < momentCount; m++)
            {
                sums[m] = subsets == 3 ? total[m] - sums[14 + m] - sums[28 + m] : total[m] - sums[14 + m];
            }

            Vector256<float> estimate = Vector256<float>.Zero;

            for (int subset = 0; subset < subsets; subset++)
            {
                estimate += LineResidual(sums.Slice(subset * 14, momentCount), counts[subset], channels);
            }

            Vector256<int> better = Vector256.LessThan(estimate, bestEstimate).AsInt32();
            bestEstimate = Vector256.Min(estimate, bestEstimate);
            best[1] = Vector256.ConditionalSelect(better, Vector256.Create(partition), best[1]);

            if (partition == 15)
            {
                best[0] = best[1];
            }
        }
    }

    private static Vector256<float> LineResidual(ReadOnlySpan<Vector256<float>> sums, int count, int channels)
    {
        Span<Vector256<float>> covariance = stackalloc Vector256<float>[16];
        Span<Vector256<float>> axis = stackalloc Vector256<float>[4];
        Span<Vector256<float>> next = stackalloc Vector256<float>[4];
        Vector256<float> inverseCount = Vector256.Create(1f / count);
        Vector256<float> trace = Vector256<float>.Zero;
        int k = channels;

        for (int c = 0; c < channels; c++)
        {
            for (int d = c; d < channels; d++)
            {
                Vector256<float> value = sums[k++] - sums[c] * sums[d] * inverseCount;
                covariance[c * 4 + d] = value;
                covariance[d * 4 + c] = value;
            }

            trace += covariance[c * 4 + c];
        }

        LargestRow(covariance, channels, axis);
        Multiply(covariance, channels, axis, next);
        Vector256<float> numerator = Vector256<float>.Zero;
        Vector256<float> denominator = Vector256<float>.Zero;

        for (int c = 0; c < channels; c++)
        {
            numerator += axis[c] * next[c];
            denominator += axis[c] * axis[c];
        }

        return trace - numerator * SafeReciprocal(denominator);
    }

    private static void Multiply(ReadOnlySpan<Vector256<float>> covariance, int channels, ReadOnlySpan<Vector256<float>> vector, Span<Vector256<float>> result)
    {
        for (int c = 0; c < channels; c++)
        {
            Vector256<float> sum = Vector256<float>.Zero;

            for (int d = 0; d < channels; d++)
            {
                sum += covariance[c * 4 + d] * vector[d];
            }

            result[c] = sum;
        }
    }

    private static void LargestRow(ReadOnlySpan<Vector256<float>> covariance, int channels, Span<Vector256<float>> axis)
    {
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
    }

    private static void MultiplyNormalized(ReadOnlySpan<Vector256<float>> covariance, int channels, Span<Vector256<float>> axis, Span<Vector256<float>> next)
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> EncodeEndpointMode(ReadOnlySpan<Vector256<float>> pixels, int mode, Vector256<int> partition, Vector256<float> alphaError,
        Span<byte> output)
    {
        Bc7Decoder.ModeInfo info = Bc7Decoder.Modes[mode];
        int channels = info.AlphaBits > 0 ? 4 : 3;
        PBits pBitMode = info.EndpointPBits != 0 ? PBits.Unique : info.SharedPBits != 0 ? PBits.Shared : PBits.None;
        Span<Vector256<int>> codes = stackalloc Vector256<int>[3 * 8];
        Span<Vector256<int>> pBits = stackalloc Vector256<int>[3 * 2];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[3 * 16];
        Span<Vector256<int>> subsetOf = stackalloc Vector256<int>[16];
        Span<Vector256<int>> anchors = stackalloc Vector256<int>[3];
        Span<Vector256<float>> weights = stackalloc Vector256<float>[16];
        Span<int> partitionOf = stackalloc int[Lanes];
        Span<int> lanes = stackalloc int[Lanes];
        partition.CopyTo(partitionOf);

        for (int i = 0; i < 16; i++)
        {
            for (int lane = 0; lane < Lanes; lane++)
            {
                lanes[lane] = Bc7Decoder.Subset(info.Subsets, partitionOf[lane], i);
            }

            subsetOf[i] = Vector256.Create<int>(lanes);
        }

        anchors[0] = Vector256<int>.Zero;

        for (int subset = 1; subset < info.Subsets; subset++)
        {
            for (int lane = 0; lane < Lanes; lane++)
            {
                int p = partitionOf[lane];
                lanes[lane] = info.Subsets == 2 ? Bc7Tables.AnchorSecondSubset[p] : subset == 1 ? Bc7Tables.AnchorThirdSubsetFirst[p] : Bc7Tables.AnchorThirdSubsetSecond[p];
            }

            anchors[subset] = Vector256.Create<int>(lanes);
        }

        Vector256<float> error = channels == 3 ? alphaError : Vector256<float>.Zero;

        for (int subset = 0; subset < info.Subsets; subset++)
        {
            for (int i = 0; i < 16; i++)
            {
                weights[i] = Vector256.ConditionalSelect(Vector256.Equals(subsetOf[i], Vector256.Create(subset)).AsSingle(), Vector256.Create(1f), Vector256<float>.Zero);
            }

            error += FitEndpoints(pixels[..(channels * 16)], weights, channels, info.ColorBits, pBitMode, info.IndexBits,
                info.Subsets == 1 ? RefineIterations : PartitionRefineIterations, anchors[subset],
                codes.Slice(subset * 8, 8), pBits.Slice(subset * 2, 2), indices.Slice(subset * 16, 16));
        }

        for (int lane = 0; lane < Lanes; lane++)
        {
            int p = partitionOf[lane];
            BitWriter writer = new BitWriter();
            writer.Write(1 << mode, mode + 1);
            writer.Write(p, info.PartitionBits);

            for (int c = 0; c < channels; c++)
            {
                for (int subset = 0; subset < info.Subsets; subset++)
                {
                    writer.Write(codes[subset * 8 + c].GetElement(lane), info.ColorBits);
                    writer.Write(codes[subset * 8 + channels + c].GetElement(lane), info.ColorBits);
                }
            }

            for (int subset = 0; subset < info.Subsets; subset++)
            {
                if (pBitMode != PBits.None)
                {
                    writer.Write(pBits[subset * 2].GetElement(lane), 1);
                }

                if (pBitMode == PBits.Unique)
                {
                    writer.Write(pBits[subset * 2 + 1].GetElement(lane), 1);
                }
            }

            for (int i = 0; i < 16; i++)
            {
                int subset = Bc7Decoder.Subset(info.Subsets, p, i);
                writer.Write(indices[subset * 16 + i].GetElement(lane), info.IndexBits - (Bc7Decoder.IsAnchor(info.Subsets, p, i) ? 1 : 0));
            }

            writer.CopyTo(output.Slice(lane * 16, 16));
        }

        return error;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> EncodeMode5(ReadOnlySpan<Vector256<float>> rotated, ReadOnlySpan<Vector256<float>> ones, int rotation, Span<byte> output)
    {
        Span<Vector256<int>> colorCodes = stackalloc Vector256<int>[6];
        Span<Vector256<int>> alphaCodes = stackalloc Vector256<int>[2];
        Span<Vector256<int>> pBits = stackalloc Vector256<int>[2];
        Span<Vector256<int>> colorIndices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> alphaIndices = stackalloc Vector256<int>[16];
        Vector256<float> error = FitEndpoints(rotated[..48], ones, 3, 7, PBits.None, 2, RefineIterations, Vector256<int>.Zero, colorCodes, pBits, colorIndices)
            + FitEndpoints(rotated[48..], ones, 1, 8, PBits.None, 2, RefineIterations, Vector256<int>.Zero, alphaCodes, pBits, alphaIndices);

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
    private static Vector256<float> EncodeMode4(ReadOnlySpan<Vector256<float>> rotated, ReadOnlySpan<Vector256<float>> ones, int rotation, int indexSelection, Span<byte> output)
    {
        Span<Vector256<int>> colorCodes = stackalloc Vector256<int>[6];
        Span<Vector256<int>> alphaCodes = stackalloc Vector256<int>[2];
        Span<Vector256<int>> pBits = stackalloc Vector256<int>[2];
        Span<Vector256<int>> colorIndices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> alphaIndices = stackalloc Vector256<int>[16];
        int colorIndexBits = indexSelection == 0 ? 2 : 3;
        int alphaIndexBits = indexSelection == 0 ? 3 : 2;
        Vector256<float> error = FitEndpoints(rotated[..48], ones, 3, 5, PBits.None, colorIndexBits, RefineIterations, Vector256<int>.Zero, colorCodes, pBits, colorIndices)
            + FitEndpoints(rotated[48..], ones, 1, 6, PBits.None, alphaIndexBits, RefineIterations, Vector256<int>.Zero, alphaCodes, pBits, alphaIndices);

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
    private static Vector256<float> FitEndpoints(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, int channels, int bits,
        PBits pBitMode, int indexBits, int refineIterations, Vector256<int> anchor, Span<Vector256<int>> bestCodes, Span<Vector256<int>> bestPBits, Span<Vector256<int>> bestIndices)
    {
        Span<Vector256<float>> endpoints = stackalloc Vector256<float>[8];
        Span<Vector256<float>> expanded = stackalloc Vector256<float>[8];
        Span<Vector256<int>> codes = stackalloc Vector256<int>[8];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Vector256<float> bestError = Vector256.Create(float.MaxValue);
        int pBitCombinations = pBitMode switch { PBits.Unique => 4, PBits.Shared => 2, _ => 1 };

        PrincipalEndpoints(pixels, weights, channels, endpoints);

        for (int iteration = 0; ; iteration++)
        {
            for (int combination = 0; combination < pBitCombinations; combination++)
            {
                Vector256<int> pBit0 = Vector256.Create(combination & 1);
                Vector256<int> pBit1 = Vector256.Create(pBitMode == PBits.Shared ? combination : combination >> 1);

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

                Vector256<float> error = MatchIndices(pixels, weights, channels, expanded, indexBits, indices);
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

            if (iteration == refineIterations)
            {
                break;
            }

            LeastSquares(pixels, weights, channels, bestIndices, indexBits, endpoints);
        }

        FixAnchor(channels, indexBits, anchor, bestCodes, bestPBits, bestIndices);
        return bestError;
    }

    private static void FixAnchor(int channels, int indexBits, Vector256<int> anchor, Span<Vector256<int>> codes, Span<Vector256<int>> pBits,
        Span<Vector256<int>> indices)
    {
        int highest = (1 << indexBits) - 1;
        Vector256<int> anchorIndex = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            anchorIndex = Vector256.ConditionalSelect(Vector256.Equals(anchor, Vector256.Create(i)), indices[i], anchorIndex);
        }

        Vector256<int> swap = Vector256.GreaterThan(anchorIndex, Vector256.Create(highest >> 1));

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

    private static void PrincipalEndpoints(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, int channels, Span<Vector256<float>> endpoints)
    {
        Span<Vector256<float>> mean = stackalloc Vector256<float>[4];
        Span<Vector256<float>> covariance = stackalloc Vector256<float>[16];
        Span<Vector256<float>> axis = stackalloc Vector256<float>[4];
        Span<Vector256<float>> next = stackalloc Vector256<float>[4];
        Vector256<float> count = Vector256<float>.Zero;

        for (int i = 0; i < 16; i++)
        {
            count += weights[i];
        }

        Vector256<float> inverseCount = SafeReciprocal(count);

        for (int c = 0; c < channels; c++)
        {
            Vector256<float> sum = Vector256<float>.Zero;

            for (int i = 0; i < 16; i++)
            {
                sum += pixels[c * 16 + i] * weights[i];
            }

            mean[c] = sum * inverseCount;
        }

        for (int c = 0; c < channels; c++)
        {
            for (int d = c; d < channels; d++)
            {
                Vector256<float> sum = Vector256<float>.Zero;

                for (int i = 0; i < 16; i++)
                {
                    sum += (pixels[c * 16 + i] - mean[c]) * (pixels[d * 16 + i] - mean[d]) * weights[i];
                }

                covariance[c * 4 + d] = sum;
                covariance[d * 4 + c] = sum;
            }
        }

        LargestRow(covariance, channels, axis);

        for (int iteration = 0; iteration < PowerIterations; iteration++)
        {
            MultiplyNormalized(covariance, channels, axis, next);
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

            Vector256<float> member = Vector256.GreaterThan(weights[i], Vector256<float>.Zero);
            low = Vector256.ConditionalSelect(member, Vector256.Min(low, t), low);
            high = Vector256.ConditionalSelect(member, Vector256.Max(high, t), high);
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
    private static Vector256<float> MatchIndices(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, int channels, ReadOnlySpan<Vector256<float>> endpoints, int indexBits,
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
            total += best * weights[i];
        }

        return total;
    }

    private static void LeastSquares(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, int channels, ReadOnlySpan<Vector256<int>> indices, int indexBits,
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
            Vector256<float> weightedS = s * weights[i];
            Vector256<float> weightedT = t * weights[i];
            aa += weightedS * s;
            ab += weightedS * t;
            bb += weightedT * t;

            for (int c = 0; c < channels; c++)
            {
                sumA[c] += weightedS * pixels[c * 16 + i];
                sumB[c] += weightedT * pixels[c * 16 + i];
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
