using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpBcn;

internal static class BcEncoderSimd
{
    public const int Lanes = 8;

    public static bool IsSupported => Vector256.IsHardwareAccelerated;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void EncodeRow(BcEncoder.BlockKind kind, ReadOnlySpan<byte> src, int width, int height, int by, int blocksX, Span<byte> row, int alphaThreshold)
    {
        if (kind == BcEncoder.BlockKind.Bc7)
        {
            EncodeBc7Row(src, width, height, by, blocksX, row);
            return;
        }

        if (kind is BcEncoder.BlockKind.Bc6hUnsigned or BcEncoder.BlockKind.Bc6hSigned)
        {
            EncodeBc6hRow(src, width, height, by, blocksX, row, kind == BcEncoder.BlockKind.Bc6hSigned);
            return;
        }

        int blockSize = kind is BcEncoder.BlockKind.Bc1 or BcEncoder.BlockKind.Bc4 ? 8 : 16;
        Span<byte> blocks = stackalloc byte[64 * Lanes];
        Span<Vector256<int>> r = stackalloc Vector256<int>[16];
        Span<Vector256<int>> g = stackalloc Vector256<int>[16];
        Span<Vector256<int>> b = stackalloc Vector256<int>[16];
        Span<Vector256<int>> a = stackalloc Vector256<int>[16];

        for (int bx = 0; bx < blocksX; bx += Lanes)
        {
            int count = Math.Min(Lanes, blocksX - bx);

            for (int lane = 0; lane < Lanes; lane++)
            {
                int sourceLane = Math.Min(lane, count - 1);
                BcEncoder.GatherBlock(src, width, height, (bx + sourceLane) * 4, by * 4, blocks.Slice(lane * 64, 64));
            }

            Load(blocks, r, g, b, a);
            Span<byte> dest = row[(bx * blockSize)..];

            switch (kind)
            {
                case BcEncoder.BlockKind.Bc1:
                    WriteColorBlocks(blocks, r, g, b, count, dest, 8);

                    if (alphaThreshold > 0)
                    {
                        ulong transparent = TransparentLanes(a, alphaThreshold);

                        while (transparent != 0)
                        {
                            int lane = BitOperations.TrailingZeroCount(transparent);
                            transparent &= transparent - 1;

                            if (lane < count)
                            {
                                BcEncoder.CompressColorBlock(blocks.Slice(lane * 64, 64), dest.Slice(lane * 8, 8), false, alphaThreshold);
                            }
                        }
                    }

                    break;
                case BcEncoder.BlockKind.Bc2:
                    for (int lane = 0; lane < count; lane++)
                    {
                        BcEncoder.CompressExplicitAlphaBlock(blocks.Slice(lane * 64, 64), dest.Slice(lane * 16, 8));
                    }

                    WriteColorBlocks(blocks, r, g, b, count, dest[8..], 16);
                    break;
                case BcEncoder.BlockKind.Bc3:
                    EncodeSingleChannel(a, count, dest, 16);
                    WriteColorBlocks(blocks, r, g, b, count, dest[8..], 16);
                    break;
                case BcEncoder.BlockKind.Bc4:
                    EncodeSingleChannel(r, count, dest, 8);
                    break;
                case BcEncoder.BlockKind.Bc5:
                    EncodeSingleChannel(r, count, dest, 16);
                    EncodeSingleChannel(g, count, dest[8..], 16);
                    break;
            }
        }
    }

    private struct Endpoints
    {
        public Vector256<int> R0, G0, B0, R1, G1, B1;
    }

    private static ulong TransparentLanes(ReadOnlySpan<Vector256<int>> a, int alphaThreshold)
    {
        Vector256<int> threshold = Vector256.Create(alphaThreshold);
        Vector256<int> any = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            any |= Vector256.LessThan(a[i], threshold);
        }

        return any.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Load(ReadOnlySpan<byte> blocks, Span<Vector256<int>> r, Span<Vector256<int>> g, Span<Vector256<int>> b, Span<Vector256<int>> a)
    {
        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(blocks);
        Span<uint> lane = stackalloc uint[Lanes];
        Vector256<int> mask = Vector256.Create(0xFF);

        for (int i = 0; i < 16; i++)
        {
            for (int l = 0; l < Lanes; l++)
            {
                lane[l] = pixels[l * 16 + i];
            }

            Vector256<int> p = Vector256.Create<uint>(lane).AsInt32();
            r[i] = p & mask;
            g[i] = (p >>> 8) & mask;
            b[i] = (p >>> 16) & mask;
            a[i] = p >>> 24;
        }
    }

    #region Color

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void EncodeColor(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        Span<int> color0, Span<int> color1, Span<uint> masks)
    {
        Span<Vector256<int>> bestIndices = stackalloc Vector256<int>[16];
        Vector256<int> bestError = FitBestCandidate(r, g, b, bestIndices, out Endpoints best);
        ApplyClusterFit(r, g, b, bestIndices, ref best, ref bestError);
        SearchColorEndpoints(r, g, b, ref best, ref bestError);
        MatchFourColor(r, g, b, best, bestIndices);
        PackColorBlocks(best, bestIndices, color0, color1, masks);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<int> FitBestCandidate(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        Span<Vector256<int>> bestIndices, out Endpoints best)
    {
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Span<Vector256<float>> red = stackalloc Vector256<float>[16];
        Span<Vector256<float>> green = stackalloc Vector256<float>[16];
        Span<Vector256<float>> blue = stackalloc Vector256<float>[16];

        for (int i = 0; i < 16; i++)
        {
            red[i] = Vector256.ConvertToSingle(r[i]);
            green[i] = Vector256.ConvertToSingle(g[i]);
            blue[i] = Vector256.ConvertToSingle(b[i]);
        }

        best = default;
        Vector256<int> bestError = default;
        Endpoints principal = PrincipalEndpoints(r, g, b, red, green, blue);

        for (int candidate = 0; candidate < 3; candidate++)
        {
            Endpoints endpoints = candidate switch
            {
                0 => principal,
                1 => BoundingBoxEndpoints(r, g, b),
                _ => Extrapolate(principal),
            };
            QuantizeEndpoints(ref endpoints);
            Vector256<int> error = MatchFourColor(r, g, b, endpoints, indices);
            RefineCandidate(r, g, b, red, green, blue, ref endpoints, ref error, indices);

            if (candidate == 0)
            {
                best = endpoints;
                bestError = error;
                indices.CopyTo(bestIndices);
                continue;
            }

            Vector256<int> better = Vector256.LessThan(error, bestError);
            Select(better, ref best, endpoints);
            bestError = Vector256.ConditionalSelect(better, error, bestError);

            for (int i = 0; i < 16; i++)
            {
                bestIndices[i] = Vector256.ConditionalSelect(better, indices[i], bestIndices[i]);
            }
        }

        return bestError;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RefineCandidate(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<float>> red, ReadOnlySpan<Vector256<float>> green, ReadOnlySpan<Vector256<float>> blue,
        ref Endpoints endpoints, ref Vector256<int> error, Span<Vector256<int>> indices)
    {
        Span<Vector256<int>> refinedIndices = stackalloc Vector256<int>[16];
        Vector256<int> active = Vector256<int>.AllBitsSet;

        for (int iteration = 0; iteration < 3; iteration++)
        {
            active &= RefineFourColor(red, green, blue, indices, out Endpoints refined);

            if (active == Vector256<int>.Zero)
            {
                return;
            }

            QuantizeEndpoints(ref refined);
            Vector256<int> refinedError = MatchFourColor(r, g, b, refined, refinedIndices);
            active &= Vector256.LessThan(refinedError, error);

            if (active == Vector256<int>.Zero)
            {
                return;
            }

            Select(active, ref endpoints, refined);
            error = Vector256.ConditionalSelect(active, refinedError, error);

            for (int i = 0; i < 16; i++)
            {
                indices[i] = Vector256.ConditionalSelect(active, refinedIndices[i], indices[i]);
            }
        }
    }

    private static void ApplyClusterFit(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<int>> indices, ref Endpoints best, ref Vector256<int> bestError)
    {
        Vector256<int> improved = ClusterFit(r, g, b, indices, ref best, ref bestError, BcEncoder.ClusterOrderingsPerBlock, Vector256<int>.AllBitsSet);

        if (improved != Vector256<int>.Zero)
        {
            ClusterFit(r, g, b, indices, ref best, ref bestError, 0, improved);
        }
    }

    private static void PackColorBlocks(in Endpoints best, ReadOnlySpan<Vector256<int>> indices, Span<int> color0, Span<int> color1, Span<uint> masks)
    {
        Vector256<int> c0 = Pack565(best.R0, best.G0, best.B0);
        Vector256<int> c1 = Pack565(best.R1, best.G1, best.B1);
        Vector256<int> mask = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            mask |= indices[i] << (i * 2);
        }

        Vector256<int> swap = Vector256.LessThan(c0, c1);
        Vector256<int> equal = Vector256.Equals(c0, c1);
        mask = Vector256.ConditionalSelect(swap, mask ^ Vector256.Create((int)BcEncoder.SwapEndpointsIndexFlip), mask);
        mask = Vector256.AndNot(mask, equal);

        Vector256.ConditionalSelect(swap, c1, c0).CopyTo(color0);
        Vector256.ConditionalSelect(swap, c0, c1).CopyTo(color1);
        mask.AsUInt32().CopyTo(masks);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<int> ClusterFit(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<int>> indices, ref Endpoints best, ref Vector256<int> bestError, int orderingCount, Vector256<int> active)
    {
        Span<Vector256<int>> redGreen = stackalloc Vector256<int>[16];
        Span<Vector256<int>> blue = stackalloc Vector256<int>[16];
        SortAlongAxis(r, g, b, best, redGreen, blue);

        Vector256<int> totalRedGreen = Vector256<int>.Zero, totalBlue = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            totalRedGreen += redGreen[i];
            totalBlue += blue[i];
        }

        Endpoints fit = best;
        Vector256<int> fitError = Vector256.Create(int.MaxValue);

        if (orderingCount > 0)
        {
            TryTableOrderings(r, g, b, indices, redGreen, blue, totalRedGreen, totalBlue, orderingCount, active, ref fit, ref fitError);
        }

        TryColorGroups(r, g, b, redGreen, blue, totalRedGreen, totalBlue, active, ref fit, ref fitError);

        Vector256<int> accept = active & Vector256.LessThan(fitError, bestError);
        Select(accept, ref best, fit);
        bestError = Vector256.ConditionalSelect(accept, fitError, bestError);
        return accept;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SortAlongAxis(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b, in Endpoints axis,
        Span<Vector256<int>> redGreen, Span<Vector256<int>> blue)
    {
        Span<Vector256<int>> keys = stackalloc Vector256<int>[16];
        Vector256<int> axisRed = axis.R1 - axis.R0, axisGreen = axis.G1 - axis.G0, axisBlue = axis.B1 - axis.B0;

        for (int i = 0; i < 16; i++)
        {
            keys[i] = PixelSortKey(r[i] * axisRed + g[i] * axisGreen + b[i] * axisBlue, i);
            redGreen[i] = PackHalves(r[i], g[i]);
            blue[i] = b[i];
        }

        foreach ((int first, int second) in BcEncoder.SortNetwork)
        {
            Vector256<int> k0 = keys[first], k1 = keys[second];
            Vector256<int> swap = Vector256.GreaterThan(k0, k1);
            keys[first] = Vector256.ConditionalSelect(swap, k1, k0);
            keys[second] = Vector256.ConditionalSelect(swap, k0, k1);
            Vector256<int> x0 = redGreen[first], x1 = redGreen[second];
            redGreen[first] = Vector256.ConditionalSelect(swap, x1, x0);
            redGreen[second] = Vector256.ConditionalSelect(swap, x0, x1);
            x0 = blue[first];
            x1 = blue[second];
            blue[first] = Vector256.ConditionalSelect(swap, x1, x0);
            blue[second] = Vector256.ConditionalSelect(swap, x0, x1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TryTableOrderings(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<int>> indices, ReadOnlySpan<Vector256<int>> redGreen, ReadOnlySpan<Vector256<int>> blue,
        Vector256<int> totalRedGreen, Vector256<int> totalBlue, int orderingCount, Vector256<int> active, ref Endpoints fit, ref Vector256<int> fitError)
    {
        Span<int> plan = stackalloc int[3 * Lanes * orderingCount];
        BuildOrderingPlan(indices, orderingCount, plan);

        for (int ordering = 0; ordering < orderingCount; ordering++)
        {
            TrySplit(r, g, b, redGreen, blue, totalRedGreen, totalBlue, Vector256.Create<int>(plan.Slice(ordering * 3 * Lanes, Lanes)),
                Vector256.Create<int>(plan.Slice((ordering * 3 + 1) * Lanes, Lanes)), Vector256.Create<int>(plan.Slice((ordering * 3 + 2) * Lanes, Lanes)),
                active, ref fit, ref fitError);
        }
    }

    private static void BuildOrderingPlan(ReadOnlySpan<Vector256<int>> indices, int orderingCount, Span<int> plan)
    {
        Vector256<int> countC0 = Vector256<int>.Zero, countNearC0 = Vector256<int>.Zero, countNearC1 = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            countC0 -= Vector256.Equals(indices[i], Vector256.Create(BcEncoder.IndexC0));
            countNearC0 -= Vector256.Equals(indices[i], Vector256.Create(BcEncoder.IndexNearC0));
            countNearC1 -= Vector256.Equals(indices[i], Vector256.Create(BcEncoder.IndexNearC1));
        }

        ReadOnlySpan<byte> histograms = ClusterTables.Histograms;
        ReadOnlySpan<ushort> orderings = ClusterTables.BestOrderings;

        for (int lane = 0; lane < Lanes; lane++)
        {
            int start = BcEncoder.HistogramIndex(countC0.GetElement(lane), countNearC0.GetElement(lane), countNearC1.GetElement(lane)) * ClusterTables.OrderingsPerHistogram;

            for (int ordering = 0; ordering < orderingCount; ordering++)
            {
                int histogramOffset = orderings[start + ordering] * 4;
                plan[ordering * 3 * Lanes + lane] = histograms[histogramOffset];
                plan[(ordering * 3 + 1) * Lanes + lane] = histograms[histogramOffset + 1];
                plan[(ordering * 3 + 2) * Lanes + lane] = histograms[histogramOffset + 2];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TryColorGroups(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<int>> redGreen, ReadOnlySpan<Vector256<int>> blue, Vector256<int> totalRedGreen, Vector256<int> totalBlue,
        Vector256<int> active, ref Endpoints fit, ref Vector256<int> fitError)
    {
        Span<Vector256<int>> groupSizes = stackalloc Vector256<int>[4];
        Vector256<int> group = Vector256<int>.Zero;
        groupSizes.Clear();
        groupSizes[0] = Vector256<int>.AllBitsSet;

        for (int i = 1; i < 16; i++)
        {
            group -= ~(Vector256.Equals(redGreen[i], redGreen[i - 1]) & Vector256.Equals(blue[i], blue[i - 1]));

            for (int j = 0; j < 4; j++)
            {
                groupSizes[j] += Vector256.Equals(group, Vector256.Create(j));
            }
        }

        Vector256<int> distinct = group + Vector256<int>.One;

        foreach (int[] pattern in BcEncoder.GroupPatterns)
        {
            Vector256<int> valid = active & Vector256.Equals(distinct, Vector256.Create(pattern.Length));

            if (valid == Vector256<int>.Zero)
            {
                continue;
            }

            Vector256<int> countC0 = Vector256<int>.Zero, countNearC0 = Vector256<int>.Zero, countNearC1 = Vector256<int>.Zero;

            for (int j = 0; j < pattern.Length; j++)
            {
                switch (pattern[j])
                {
                    case 0: countC0 -= groupSizes[j]; break;
                    case 1: countNearC0 -= groupSizes[j]; break;
                    case 2: countNearC1 -= groupSizes[j]; break;
                }
            }

            TrySplit(r, g, b, redGreen, blue, totalRedGreen, totalBlue, countC0, countNearC0, countNearC1, valid, ref fit, ref fitError);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TrySplit(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<int>> redGreen, ReadOnlySpan<Vector256<int>> blue, Vector256<int> totalRedGreen, Vector256<int> totalBlue,
        Vector256<int> countC0, Vector256<int> countNearC0, Vector256<int> countNearC1, Vector256<int> valid, ref Endpoints fit, ref Vector256<int> fitError)
    {
        Vector256<int> countC1 = Vector256.Create(16) - countC0 - countNearC0 - countNearC1;
        Vector256<int> end0 = countC0, end1 = countC0 + countNearC0, end2 = end1 + countNearC1;
        Vector256<int> sumThrough0RedGreen = Vector256<int>.Zero, sumThrough1RedGreen = Vector256<int>.Zero, sumThrough2RedGreen = Vector256<int>.Zero;
        Vector256<int> sumThrough0Blue = Vector256<int>.Zero, sumThrough1Blue = Vector256<int>.Zero, sumThrough2Blue = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> position = Vector256.Create(i);
            Vector256<int> beforeEnd0 = Vector256.GreaterThan(end0, position);
            Vector256<int> beforeEnd1 = Vector256.GreaterThan(end1, position);
            Vector256<int> beforeEnd2 = Vector256.GreaterThan(end2, position);
            sumThrough0RedGreen += redGreen[i] & beforeEnd0;
            sumThrough1RedGreen += redGreen[i] & beforeEnd1;
            sumThrough2RedGreen += redGreen[i] & beforeEnd2;
            sumThrough0Blue += blue[i] & beforeEnd0;
            sumThrough1Blue += blue[i] & beforeEnd1;
            sumThrough2Blue += blue[i] & beforeEnd2;
        }

        Vector256<int> sum9W0W0 = Vector256.Create(9) * countC0 + Vector256.Create(4) * countNearC0 + countNearC1;
        Vector256<int> sum9W1W1 = countNearC0 + Vector256.Create(4) * countNearC1 + Vector256.Create(9) * countC1;
        Vector256<int> sum9W0W1 = Vector256.Create(2) * (countNearC0 + countNearC1);
        Vector256<int> determinant = sum9W0W0 * sum9W1W1 - sum9W0W1 * sum9W0W1;
        Vector256<float> determinantFloat = Vector256.ConvertToSingle(determinant);
        valid &= ~Vector256.Equals(determinant, Vector256<int>.Zero);

        if (valid == Vector256<int>.Zero)
        {
            return;
        }

        Endpoints trial;
        (trial.R0, trial.R1) = FitChannel(LowHalf(sumThrough0RedGreen), LowHalf(sumThrough1RedGreen), LowHalf(sumThrough2RedGreen), LowHalf(totalRedGreen), sum9W0W0, sum9W1W1, sum9W0W1, determinantFloat, 5);
        (trial.G0, trial.G1) = FitChannel(HighHalf(sumThrough0RedGreen), HighHalf(sumThrough1RedGreen), HighHalf(sumThrough2RedGreen), HighHalf(totalRedGreen), sum9W0W0, sum9W1W1, sum9W0W1, determinantFloat, 6);
        (trial.B0, trial.B1) = FitChannel(sumThrough0Blue, sumThrough1Blue, sumThrough2Blue, totalBlue, sum9W0W0, sum9W1W1, sum9W0W1, determinantFloat, 5);
        Vector256<int> error = MatchFourColorError(r, g, b, trial);
        Vector256<int> better = valid & Vector256.LessThan(error, fitError);

        if (better == Vector256<int>.Zero)
        {
            return;
        }

        Select(better, ref fit, trial);
        fitError = Vector256.ConditionalSelect(better, error, fitError);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector256<int>, Vector256<int>) FitChannel(Vector256<int> sumThrough0, Vector256<int> sumThrough1, Vector256<int> sumThrough2, Vector256<int> total,
        Vector256<int> sum9W0W0, Vector256<int> sum9W1W1, Vector256<int> sum9W0W1, Vector256<float> determinant, int bits)
    {
        Vector256<int> two = Vector256.Create(2), three = Vector256.Create(3);
        Vector256<int> sum3W0X = three * sumThrough0 + two * (sumThrough1 - sumThrough0) + (sumThrough2 - sumThrough1);
        Vector256<int> sum3W1X = (sumThrough1 - sumThrough0) + two * (sumThrough2 - sumThrough1) + three * (total - sumThrough2);
        Vector256<float> numerator0 = Vector256.ConvertToSingle(three * (sum3W0X * sum9W1W1 - sum3W1X * sum9W0W1));
        Vector256<float> numerator1 = Vector256.ConvertToSingle(three * (sum3W1X * sum9W0W0 - sum3W0X * sum9W0W1));
        Vector256<int> c0 = Clamp255(Vector256.ConvertToInt32(Vector256.Round(numerator0 / determinant)));
        Vector256<int> c1 = Clamp255(Vector256.ConvertToInt32(Vector256.Round(numerator1 / determinant)));
        return bits == 6 ? (Expand6(Quantize(c0, 6)), Expand6(Quantize(c1, 6))) : (Expand5(Quantize(c0, 5)), Expand5(Quantize(c1, 5)));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SearchColorEndpoints(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ref Endpoints best, ref Vector256<int> error)
    {
        for (int round = 0; round < BcEncoder.EndpointSearchRounds; round++)
        {
            Vector256<int> improved = Vector256<int>.Zero;

            for (int e = 0; e < 6; e++)
            {
                int bits = BcEncoder.EndpointBits(e);
                Vector256<int> max = Vector256.Create((1 << bits) - 1);

                for (int delta = -1; delta <= 1; delta += 2)
                {
                    Vector256<int> level = Quantize(Get(best, e), bits) + Vector256.Create(delta);
                    Vector256<int> valid = Vector256.GreaterThanOrEqual(level, Vector256<int>.Zero) & Vector256.LessThanOrEqual(level, max);
                    level = Vector256.Min(Vector256.Max(level, Vector256<int>.Zero), max);

                    Endpoints trial = best;
                    Set(ref trial, e, bits == 6 ? Expand6(level) : Expand5(level));
                    Vector256<int> trialError = MatchFourColorError(r, g, b, trial);
                    Vector256<int> accept = valid & Vector256.LessThan(trialError, error);

                    if (accept == Vector256<int>.Zero)
                    {
                        continue;
                    }

                    Select(accept, ref best, trial);
                    error = Vector256.ConditionalSelect(accept, trialError, error);
                    improved |= accept;
                }
            }

            if (improved == Vector256<int>.Zero)
            {
                break;
            }
        }
    }

    private static Vector256<int> Get(in Endpoints e, int index) => index switch
    {
        0 => e.R0,
        1 => e.G0,
        2 => e.B0,
        3 => e.R1,
        4 => e.G1,
        _ => e.B1,
    };

    private static void Set(ref Endpoints e, int index, Vector256<int> value)
    {
        switch (index)
        {
            case 0: e.R0 = value; break;
            case 1: e.G0 = value; break;
            case 2: e.B0 = value; break;
            case 3: e.R1 = value; break;
            case 4: e.G1 = value; break;
            default: e.B1 = value; break;
        }
    }

    private static void Select(Vector256<int> mask, ref Endpoints target, in Endpoints source)
    {
        target.R0 = Vector256.ConditionalSelect(mask, source.R0, target.R0);
        target.G0 = Vector256.ConditionalSelect(mask, source.G0, target.G0);
        target.B0 = Vector256.ConditionalSelect(mask, source.B0, target.B0);
        target.R1 = Vector256.ConditionalSelect(mask, source.R1, target.R1);
        target.G1 = Vector256.ConditionalSelect(mask, source.G1, target.G1);
        target.B1 = Vector256.ConditionalSelect(mask, source.B1, target.B1);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Endpoints PrincipalEndpoints(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<float>> rf, ReadOnlySpan<Vector256<float>> gf, ReadOnlySpan<Vector256<float>> bf)
    {
        Vector256<float> meanR = Vector256<float>.Zero, meanG = Vector256<float>.Zero, meanB = Vector256<float>.Zero;

        for (int i = 0; i < 16; i++)
        {
            meanR += rf[i];
            meanG += gf[i];
            meanB += bf[i];
        }

        Vector256<float> sixteen = Vector256.Create(16f);
        meanR /= sixteen;
        meanG /= sixteen;
        meanB /= sixteen;

        Vector256<float> rr = default, rg = default, rb = default, gg = default, gb = default, bb = default;

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> dr = rf[i] - meanR;
            Vector256<float> dg = gf[i] - meanG;
            Vector256<float> db = bf[i] - meanB;
            rr += dr * dr;
            rg += dr * dg;
            rb += dr * db;
            gg += dg * dg;
            gb += dg * db;
            bb += db * db;
        }

        Vector256<float> vr = rr, vg = gg, vb = bb;
        Vector256<float> one = Vector256.Create(1f);
        Vector256<float> degenerate = Vector256.LessThanOrEqual(vr + vg + vb, Vector256<float>.Zero);
        vr = Vector256.ConditionalSelect(degenerate, one, vr);
        vg = Vector256.ConditionalSelect(degenerate, one, vg);
        vb = Vector256.ConditionalSelect(degenerate, one, vb);

        Vector256<float> active = Vector256<float>.AllBitsSet;
        Vector256<float> epsilon = Vector256.Create(1e-6f);

        for (int iteration = 0; iteration < 8; iteration++)
        {
            Vector256<float> nr = vr * rr + vg * rg + vb * rb;
            Vector256<float> ng = vr * rg + vg * gg + vb * gb;
            Vector256<float> nb = vr * rb + vg * gb + vb * bb;
            Vector256<float> length = Vector256.Max(Vector256.Max(Vector256.Abs(nr), Vector256.Abs(ng)), Vector256.Abs(nb));

            active &= Vector256.GreaterThanOrEqual(length, epsilon);

            if (active == Vector256<float>.Zero)
            {
                break;
            }

            vr = Vector256.ConditionalSelect(active, nr / length, vr);
            vg = Vector256.ConditionalSelect(active, ng / length, vg);
            vb = Vector256.ConditionalSelect(active, nb / length, vb);
        }

        Vector256<float> minDot = Vector256.Create(float.MaxValue);
        Vector256<float> maxDot = Vector256.Create(float.MinValue);
        Endpoints result = default;

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> dot = rf[i] * vr + gf[i] * vg + bf[i] * vb;
            Vector256<float> lowerFloat = Vector256.LessThan(dot, minDot);
            Vector256<float> higherFloat = Vector256.GreaterThan(dot, maxDot);
            minDot = Vector256.ConditionalSelect(lowerFloat, dot, minDot);
            maxDot = Vector256.ConditionalSelect(higherFloat, dot, maxDot);

            Vector256<int> lower = lowerFloat.AsInt32();
            Vector256<int> higher = higherFloat.AsInt32();

            result.R0 = Vector256.ConditionalSelect(higher, r[i], result.R0);
            result.G0 = Vector256.ConditionalSelect(higher, g[i], result.G0);
            result.B0 = Vector256.ConditionalSelect(higher, b[i], result.B0);
            result.R1 = Vector256.ConditionalSelect(lower, r[i], result.R1);
            result.G1 = Vector256.ConditionalSelect(lower, g[i], result.G1);
            result.B1 = Vector256.ConditionalSelect(lower, b[i], result.B1);
        }

        return result;
    }

    private static Endpoints Extrapolate(in Endpoints e)
    {
        return new Endpoints
        {
            R0 = Clamp255(e.R0 + e.R0 - e.R1),
            G0 = Clamp255(e.G0 + e.G0 - e.G1),
            B0 = Clamp255(e.B0 + e.B0 - e.B1),
            R1 = Clamp255(e.R1 + e.R1 - e.R0),
            G1 = Clamp255(e.G1 + e.G1 - e.G0),
            B1 = Clamp255(e.B1 + e.B1 - e.B0),
        };
    }

    private static Endpoints BoundingBoxEndpoints(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b)
    {
        Endpoints result = default;
        Inset(r, out result.R0, out result.R1);
        Inset(g, out result.G0, out result.G1);
        Inset(b, out result.B0, out result.B1);
        return result;

        static void Inset(ReadOnlySpan<Vector256<int>> channel, out Vector256<int> high, out Vector256<int> low)
        {
            Vector256<int> min = Vector256.Create(255);
            Vector256<int> max = Vector256<int>.Zero;

            for (int i = 0; i < 16; i++)
            {
                min = Vector256.Min(min, channel[i]);
                max = Vector256.Max(max, channel[i]);
            }

            Vector256<int> inset = (max - min) >> 4;
            high = max - inset;
            low = min + inset;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> PixelSortKey(Vector256<int> projection, int pixel) => (projection << 4) | Vector256.Create(pixel);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> PackHalves(Vector256<int> low, Vector256<int> high) => low | (high << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> LowHalf(Vector256<int> packed) => packed & Vector256.Create(0xFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> HighHalf(Vector256<int> packed) => packed >>> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Divide255(Vector256<int> x)
    {
        return (x + Vector256.Create(1) + (x >> 8)) >> 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Divide3(Vector256<int> x)
    {
        return (x * Vector256.Create(43691)) >>> 17;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Quantize(Vector256<int> value, int bits)
    {
        Vector256<int> max = Vector256.Create((1 << bits) - 1);
        Vector256<int> quantized = Divide255(value * max + Vector256.Create(127));
        return Vector256.Min(Vector256.Max(quantized, Vector256<int>.Zero), max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Clamp255(Vector256<int> value)
    {
        return Vector256.Min(Vector256.Max(value, Vector256<int>.Zero), Vector256.Create(255));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Expand5(Vector256<int> q) => (q << 3) | (q >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Expand6(Vector256<int> q) => (q << 2) | (q >> 4);

    private static void QuantizeEndpoints(ref Endpoints e)
    {
        e.R0 = Expand5(Quantize(Clamp255(e.R0), 5));
        e.G0 = Expand6(Quantize(Clamp255(e.G0), 6));
        e.B0 = Expand5(Quantize(Clamp255(e.B0), 5));
        e.R1 = Expand5(Quantize(Clamp255(e.R1), 5));
        e.G1 = Expand6(Quantize(Clamp255(e.G1), 6));
        e.B1 = Expand5(Quantize(Clamp255(e.B1), 5));
    }

    private static Vector256<int> Pack565(Vector256<int> r, Vector256<int> g, Vector256<int> b)
    {
        return (Quantize(r, 5) << 11) | (Quantize(g, 6) << 5) | Quantize(b, 5);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<int> MatchFourColor(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        in Endpoints e, Span<Vector256<int>> indices)
    {
        Vector256<int> two = Vector256.Create(2);
        Vector256<int> p2r = Divide3(two * e.R0 + e.R1), p2g = Divide3(two * e.G0 + e.G1), p2b = Divide3(two * e.B0 + e.B1);
        Vector256<int> p3r = Divide3(e.R0 + two * e.R1), p3g = Divide3(e.G0 + two * e.G1), p3b = Divide3(e.B0 + two * e.B1);
        Vector256<int> total = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> bestError = Distance(r[i], g[i], b[i], e.R0, e.G0, e.B0);
            Vector256<int> best = Vector256<int>.Zero;

            Vector256<int> error = Distance(r[i], g[i], b[i], e.R1, e.G1, e.B1);
            Vector256<int> lower = Vector256.LessThan(error, bestError);
            bestError = Vector256.ConditionalSelect(lower, error, bestError);
            best = Vector256.ConditionalSelect(lower, Vector256.Create(BcEncoder.IndexC1), best);

            error = Distance(r[i], g[i], b[i], p2r, p2g, p2b);
            lower = Vector256.LessThan(error, bestError);
            bestError = Vector256.ConditionalSelect(lower, error, bestError);
            best = Vector256.ConditionalSelect(lower, Vector256.Create(BcEncoder.IndexNearC0), best);

            error = Distance(r[i], g[i], b[i], p3r, p3g, p3b);
            lower = Vector256.LessThan(error, bestError);
            bestError = Vector256.ConditionalSelect(lower, error, bestError);
            best = Vector256.ConditionalSelect(lower, Vector256.Create(BcEncoder.IndexNearC1), best);

            indices[i] = best;
            total += bestError;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> MatchFourColorError(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        in Endpoints e)
    {
        Vector256<int> two = Vector256.Create(2);
        Vector256<int> p2r = Divide3(two * e.R0 + e.R1), p2g = Divide3(two * e.G0 + e.G1), p2b = Divide3(two * e.B0 + e.B1);
        Vector256<int> p3r = Divide3(e.R0 + two * e.R1), p3g = Divide3(e.G0 + two * e.G1), p3b = Divide3(e.B0 + two * e.B1);
        Vector256<int> total = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> nearest = Vector256.Min(
                Vector256.Min(Distance(r[i], g[i], b[i], e.R0, e.G0, e.B0), Distance(r[i], g[i], b[i], e.R1, e.G1, e.B1)),
                Vector256.Min(Distance(r[i], g[i], b[i], p2r, p2g, p2b), Distance(r[i], g[i], b[i], p3r, p3g, p3b)));
            total += nearest;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Distance(Vector256<int> r, Vector256<int> g, Vector256<int> b, Vector256<int> pr, Vector256<int> pg, Vector256<int> pb)
    {
        Vector256<int> dr = r - pr, dg = g - pg, db = b - pb;
        return dr * dr + dg * dg + db * db;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<int> RefineFourColor(ReadOnlySpan<Vector256<float>> red, ReadOnlySpan<Vector256<float>> green, ReadOnlySpan<Vector256<float>> blue,
        ReadOnlySpan<Vector256<int>> indices, out Endpoints result)
    {
        Vector256<float> one = Vector256.Create(1f);
        Vector256<float> twoThirds = Vector256.Create(2f / 3f);
        Vector256<float> oneThird = Vector256.Create(1f / 3f);
        Vector256<float> sumW0W0 = default, sumW1W1 = default, sumW0W1 = default;
        Vector256<float> sumW0Red = default, sumW0Green = default, sumW0Blue = default, sumW1Red = default, sumW1Green = default, sumW1Blue = default;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> index = indices[i];
            Vector256<float> w0 = Vector256.ConditionalSelect(Vector256.Equals(index, Vector256.Create(BcEncoder.IndexC0)).AsSingle(), one,
                Vector256.ConditionalSelect(Vector256.Equals(index, Vector256.Create(BcEncoder.IndexNearC0)).AsSingle(), twoThirds,
                    Vector256.ConditionalSelect(Vector256.Equals(index, Vector256.Create(BcEncoder.IndexNearC1)).AsSingle(), oneThird, Vector256<float>.Zero)));
            Vector256<float> w1 = one - w0;

            sumW0W0 += w0 * w0;
            sumW1W1 += w1 * w1;
            sumW0W1 += w0 * w1;
            sumW0Red += w0 * red[i];
            sumW0Green += w0 * green[i];
            sumW0Blue += w0 * blue[i];
            sumW1Red += w1 * red[i];
            sumW1Green += w1 * green[i];
            sumW1Blue += w1 * blue[i];
        }

        Vector256<float> determinant = sumW0W0 * sumW1W1 - sumW0W1 * sumW0W1;
        Vector256<int> solvable = Vector256.GreaterThanOrEqual(Vector256.Abs(determinant), Vector256.Create(1e-6f)).AsInt32();
        Vector256<float> inverse = one / determinant;

        result.R0 = RoundToInt((sumW0Red * sumW1W1 - sumW1Red * sumW0W1) * inverse);
        result.G0 = RoundToInt((sumW0Green * sumW1W1 - sumW1Green * sumW0W1) * inverse);
        result.B0 = RoundToInt((sumW0Blue * sumW1W1 - sumW1Blue * sumW0W1) * inverse);
        result.R1 = RoundToInt((sumW1Red * sumW0W0 - sumW0Red * sumW0W1) * inverse);
        result.G1 = RoundToInt((sumW1Green * sumW0W0 - sumW0Green * sumW0W1) * inverse);
        result.B1 = RoundToInt((sumW1Blue * sumW0W0 - sumW0Blue * sumW0W1) * inverse);
        return solvable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> RoundToInt(Vector256<float> value)
    {
        Vector256<float> limited = Vector256.Min(Vector256.Max(Vector256.Round(value), Vector256.Create(-1e6f)), Vector256.Create(1e6f));
        return Vector256.ConvertToInt32(limited);
    }

    #endregion

    #region Single channel (BC3 alpha, BC4, BC5)

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void EncodeSingleChannel(ReadOnlySpan<Vector256<int>> values, int count, Span<byte> dest, int stride)
    {
        Vector256<int> zero = Vector256<int>.Zero;
        Vector256<int> full = Vector256.Create(255);
        Vector256<int> min = full, max = zero, innerMin = full, innerMax = zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> v = values[i];
            min = Vector256.Min(min, v);
            max = Vector256.Max(max, v);

            Vector256<int> inner = ~(Vector256.Equals(v, zero) | Vector256.Equals(v, full));
            innerMin = Vector256.ConditionalSelect(inner, Vector256.Min(innerMin, v), innerMin);
            innerMax = Vector256.ConditionalSelect(inner, Vector256.Max(innerMax, v), innerMax);
        }

        Span<Vector256<int>> palette = stackalloc Vector256<int>[8];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> pairs = stackalloc Vector256<int>[8];
        PackPairs(values, pairs);

        Vector256<int> a0 = max, a1 = min;
        palette[0] = max;
        palette[1] = min;

        for (int i = 1; i < 7; i++)
        {
            palette[1 + i] = Divide7(Vector256.Create(7 - i) * max + Vector256.Create(i) * min + Vector256.Create(3));
        }

        Vector256<int> error = MatchError(pairs, palette);

        Vector256<int> hasInner = Vector256.LessThanOrEqual(innerMin, innerMax);
        Vector256<int> lo = Vector256.ConditionalSelect(hasInner, innerMin, min);
        Vector256<int> hi = Vector256.ConditionalSelect(hasInner, innerMax, max);
        palette[0] = lo;
        palette[1] = hi;

        for (int i = 1; i < 5; i++)
        {
            palette[1 + i] = Divide5(Vector256.Create(5 - i) * lo + Vector256.Create(i) * hi + Vector256.Create(2));
        }

        palette[6] = zero;
        palette[7] = full;
        Vector256<int> altError = MatchError(pairs, palette);

        Vector256<int> useAlt = (Vector256.Equals(min, zero) | Vector256.Equals(max, full)) & Vector256.LessThan(altError, error);
        a0 = Vector256.ConditionalSelect(useAlt, lo, a0);
        a1 = Vector256.ConditionalSelect(useAlt, hi, a1);
        error = Vector256.ConditionalSelect(useAlt, altError, error);

        SearchAlphaEndpoints(pairs, ref a0, ref a1, ref error);
        SearchAlphaRange(pairs, ref a0, ref a1, ref error);
        BuildAlphaPalette(a0, a1, Vector256.GreaterThan(a0, a1), palette);
        MatchSingleChannel(values, palette, indices);

        Vector256<int> low = zero, high = zero;

        for (int i = 0; i < 8; i++)
        {
            low |= indices[i] << (i * 3);
            high |= indices[i + 8] << (i * 3);
        }

        Vector256<int> solid = Vector256.Equals(min, max);

        for (int lane = 0; lane < count; lane++)
        {
            Span<byte> block = dest.Slice(lane * stride, 8);

            if (solid.GetElement(lane) != 0)
            {
                block[0] = (byte)min.GetElement(lane);
                block[1] = (byte)min.GetElement(lane);
                block[2..8].Clear();
                continue;
            }

            block[0] = (byte)a0.GetElement(lane);
            block[1] = (byte)a1.GetElement(lane);
            ulong bits = (uint)low.GetElement(lane) | ((ulong)(uint)high.GetElement(lane) << 24);

            for (int i = 0; i < 6; i++)
            {
                block[2 + i] = (byte)(bits >> (i * 8));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SearchAlphaEndpoints(ReadOnlySpan<Vector256<int>> pairs, ref Vector256<int> a0, ref Vector256<int> a1, ref Vector256<int> error)
    {
        Span<Vector256<int>> palette = stackalloc Vector256<int>[8];
        Vector256<int> zero = Vector256<int>.Zero;
        Vector256<int> full = Vector256.Create(255);
        Vector256<int> eightValues = Vector256.GreaterThan(a0, a1);

        for (int round = 0; round < BcEncoder.EndpointSearchRounds && error != zero; round++)
        {
            Vector256<int> improved = zero;

            for (int e = 0; e < 2; e++)
            {
                for (int delta = -1; delta <= 1; delta += 2)
                {
                    Vector256<int> t0 = e == 0 ? a0 + Vector256.Create(delta) : a0;
                    Vector256<int> t1 = e == 1 ? a1 + Vector256.Create(delta) : a1;
                    Vector256<int> valid = Vector256.GreaterThanOrEqual(t0, zero) & Vector256.LessThanOrEqual(t0, full)
                        & Vector256.GreaterThanOrEqual(t1, zero) & Vector256.LessThanOrEqual(t1, full)
                        & ~(Vector256.GreaterThan(t0, t1) ^ eightValues);

                    BuildAlphaPalette(t0, t1, eightValues, palette);
                    Vector256<int> trialError = MatchError(pairs, palette);
                    Vector256<int> accept = valid & Vector256.LessThan(trialError, error);

                    if (accept == zero)
                    {
                        continue;
                    }

                    a0 = Vector256.ConditionalSelect(accept, t0, a0);
                    a1 = Vector256.ConditionalSelect(accept, t1, a1);
                    error = Vector256.ConditionalSelect(accept, trialError, error);

                    improved |= accept;
                }
            }

            if (improved == zero)
            {
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SearchAlphaRange(ReadOnlySpan<Vector256<int>> pairs, ref Vector256<int> a0, ref Vector256<int> a1, ref Vector256<int> error)
    {
        Span<Vector256<int>> palette = stackalloc Vector256<int>[8];
        Vector256<int> zero = Vector256<int>.Zero;
        Vector256<int> full = Vector256.Create(255);
        Vector256<int> eightValues = Vector256.GreaterThan(a0, a1);

        for (int step = BcEncoder.AlphaSearchStep; step >= 1; step >>= 1)
        {
            for (int round = 0; round < BcEncoder.AlphaSearchRounds && error != zero; round++)
            {
                Vector256<int> improved = zero;

                for (int d0 = -step; d0 <= step; d0 += step)
                {
                    for (int d1 = -step; d1 <= step; d1 += step)
                    {
                        if (d0 == 0 && d1 == 0)
                        {
                            continue;
                        }

                        Vector256<int> t0 = a0 + Vector256.Create(d0);
                        Vector256<int> t1 = a1 + Vector256.Create(d1);
                        Vector256<int> valid = Vector256.GreaterThanOrEqual(t0, zero) & Vector256.LessThanOrEqual(t0, full)
                            & Vector256.GreaterThanOrEqual(t1, zero) & Vector256.LessThanOrEqual(t1, full)
                            & ~(Vector256.GreaterThan(t0, t1) ^ eightValues);

                        if (valid == zero)
                        {
                            continue;
                        }

                        BuildAlphaPalette(t0, t1, eightValues, palette);
                        Vector256<int> trialError = MatchError(pairs, palette);
                        Vector256<int> accept = valid & Vector256.LessThan(trialError, error);

                        if (accept == zero)
                        {
                            continue;
                        }

                        a0 = Vector256.ConditionalSelect(accept, t0, a0);
                        a1 = Vector256.ConditionalSelect(accept, t1, a1);
                        error = Vector256.ConditionalSelect(accept, trialError, error);

                        improved |= accept;
                    }
                }

                if (improved == zero)
                {
                    break;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildAlphaPalette(Vector256<int> t0, Vector256<int> t1, Vector256<int> eightValues, Span<Vector256<int>> palette)
    {
        Vector256<int> zero = Vector256<int>.Zero;
        Vector256<int> full = Vector256.Create(255);
        Vector256<int> c0 = Vector256.Min(Vector256.Max(t0, zero), full);
        Vector256<int> c1 = Vector256.Min(Vector256.Max(t1, zero), full);
        Vector256<int> diff = c1 - c0;
        palette[0] = c0;
        palette[1] = c1;

        if (eightValues == Vector256<int>.AllBitsSet)
        {
            Vector256<int> eight = Vector256.Create(7) * c0 + Vector256.Create(3);

            for (int i = 1; i < 7; i++)
            {
                eight += diff;
                palette[1 + i] = Divide7(eight);
            }

            return;
        }

        if (eightValues == zero)
        {
            Vector256<int> six = Vector256.Create(5) * c0 + Vector256.Create(2);

            for (int i = 1; i < 5; i++)
            {
                six += diff;
                palette[1 + i] = Divide5(six);
            }

            palette[6] = zero;
            palette[7] = full;
            return;
        }

        Vector256<int> sevenths = Vector256.Create(7) * c0 + Vector256.Create(3);
        Vector256<int> fifths = Vector256.Create(5) * c0 + Vector256.Create(2);

        for (int i = 1; i < 7; i++)
        {
            sevenths += diff;
            fifths += diff;
            Vector256<int> six = i < 5 ? Divide5(fifths) : (i == 5 ? zero : full);
            palette[1 + i] = Vector256.ConditionalSelect(eightValues, Divide7(sevenths), six);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Divide7(Vector256<int> x)
    {
        return (x * Vector256.Create(9363)) >>> 16;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Divide5(Vector256<int> x)
    {
        return (x * Vector256.Create(13108)) >>> 16;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> MatchError(ReadOnlySpan<Vector256<int>> pairs, ReadOnlySpan<Vector256<int>> palette)
    {
        Vector256<short> p0 = Duplicate(palette[0]), p1 = Duplicate(palette[1]), p2 = Duplicate(palette[2]), p3 = Duplicate(palette[3]);
        Vector256<short> p4 = Duplicate(palette[4]), p5 = Duplicate(palette[5]), p6 = Duplicate(palette[6]), p7 = Duplicate(palette[7]);
        Vector256<int> total = Vector256<int>.Zero;

        for (int i = 0; i < 8; i++)
        {
            Vector256<short> v = pairs[i].AsInt16();
            Vector256<short> nearest = Vector256.Min(Vector256.Min(Vector256.Min(Vector256.Abs(v - p0), Vector256.Abs(v - p1)),
                Vector256.Min(Vector256.Abs(v - p2), Vector256.Abs(v - p3))),
                Vector256.Min(Vector256.Min(Vector256.Abs(v - p4), Vector256.Abs(v - p5)),
                Vector256.Min(Vector256.Abs(v - p6), Vector256.Abs(v - p7))));
            Vector256<int> squares = (nearest * nearest).AsInt32();
            total += LowHalf(squares) + HighHalf(squares);
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Duplicate(Vector256<int> value)
    {
        return PackHalves(value, value).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PackPairs(ReadOnlySpan<Vector256<int>> values, Span<Vector256<int>> pairs)
    {
        for (int i = 0; i < 8; i++)
        {
            pairs[i] = PackHalves(values[i], values[i + 8]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<int> MatchSingleChannel(ReadOnlySpan<Vector256<int>> values, ReadOnlySpan<Vector256<int>> palette, Span<Vector256<int>> indices)
    {
        Vector256<int> total = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> diff = values[i] - palette[0];
            Vector256<int> bestError = diff * diff;
            Vector256<int> best = Vector256<int>.Zero;

            for (int p = 1; p < 8; p++)
            {
                diff = values[i] - palette[p];
                Vector256<int> error = diff * diff;
                Vector256<int> lower = Vector256.LessThan(error, bestError);
                bestError = Vector256.ConditionalSelect(lower, error, bestError);
                best = Vector256.ConditionalSelect(lower, Vector256.Create(p), best);
            }

            indices[i] = best;
            total += bestError;
        }

        return total;
    }

    #endregion

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void WriteColorBlocks(ReadOnlySpan<byte> blocks, ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        int count, Span<byte> dest, int stride)
    {
        Span<int> color0 = stackalloc int[Lanes];
        Span<int> color1 = stackalloc int[Lanes];
        Span<uint> masks = stackalloc uint[Lanes];
        EncodeColor(r, g, b, color0, color1, masks);

        for (int lane = 0; lane < count; lane++)
        {
            Span<byte> block = dest.Slice(lane * stride, 8);

            if (BcEncoder.TryWriteSolidColorBlock(blocks.Slice(lane * 64, 64), block))
            {
                continue;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)color0[lane]);
            BinaryPrimitives.WriteUInt16LittleEndian(block[2..], (ushort)color1[lane]);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], masks[lane]);
        }
    }

    #region BC6H

    private const float MaxHalfMagnitude = 0x7BFF;
    private const float TwoRegionErrorThreshold = 4000;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeBc6hRow(ReadOnlySpan<byte> src, int width, int height, int by, int blocksX, Span<byte> row, bool signed)
    {
        ReadOnlySpan<ulong> source = MemoryMarshal.Cast<byte, ulong>(src);
        Span<ulong> blocks = stackalloc ulong[16 * Lanes];
        Span<byte> encoded = stackalloc byte[16 * Lanes];
        Span<Vector256<float>> pixels = stackalloc Vector256<float>[48];
        Span<Vector256<float>> ones = stackalloc Vector256<float>[16];
        Span<Vector256<int>> shapes = stackalloc Vector256<int>[2];
        Span<Vector256<float>> oneRegionEndpoints = stackalloc Vector256<float>[6];
        Span<Vector256<float>> twoRegionEndpoints = stackalloc Vector256<float>[12];
        Span<Vector256<float>> regionWeights = stackalloc Vector256<float>[2 * 16];
        Span<Vector256<int>> anchors = stackalloc Vector256<int>[2];
        float minimum = signed ? -MaxHalfMagnitude : 0;
        ones.Fill(Vector256.Create(1f));

        for (int bx = 0; bx < blocksX; bx += Lanes)
        {
            int count = Math.Min(Lanes, blocksX - bx);

            for (int lane = 0; lane < Lanes; lane++)
            {
                int x0 = (bx + Math.Min(lane, count - 1)) * 4;

                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, height - 1);

                    for (int x = 0; x < 4; x++)
                    {
                        blocks[lane * 16 + y * 4 + x] = source[sy * width + Math.Min(x0 + x, width - 1)];
                    }
                }
            }

            LoadHalfPlanes(blocks, pixels, signed);
            Vector256<float> bestError = Vector256.Create(float.MaxValue);

            FitPrincipalAxis(pixels, ones, 3, oneRegionEndpoints, minimum, MaxHalfMagnitude);

            for (int mode = 10; mode < 14; mode++)
            {
                EncodeBc6hMode(pixels, ones, oneRegionEndpoints, mode, signed, Vector256<float>.AllBitsSet, ref bestError, encoded);
            }

            Vector256<float> needsRegions = Vector256.GreaterThan(bestError, Vector256.Create(TwoRegionErrorThreshold));

            if (needsRegions != Vector256<float>.Zero)
            {
                EstimatePartitions(pixels, 3, 2, 32, shapes);
                PrepareRegions(pixels, shapes[1], minimum, regionWeights, anchors, twoRegionEndpoints);

                for (int mode = 0; mode < 10; mode++)
                {
                    EncodeBc6hTwoRegionMode(pixels, regionWeights, anchors, twoRegionEndpoints, mode, shapes[1], signed, needsRegions, ref bestError, encoded);
                }
            }

            encoded[..(count * 16)].CopyTo(row[(bx * 16)..]);
        }
    }

    private static void LoadHalfPlanes(ReadOnlySpan<ulong> blocks, Span<Vector256<float>> pixels, bool signed)
    {
        Span<int> lane = stackalloc int[Lanes];

        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 16; i++)
            {
                for (int l = 0; l < Lanes; l++)
                {
                    int bits = (int)(blocks[l * 16 + i] >> (c * 16)) & 0xFFFF;
                    int magnitude = Math.Min(bits & 0x7FFF, 0x7BFF);
                    bool negative = (bits & 0x8000) != 0;
                    lane[l] = negative ? signed ? -magnitude : 0 : magnitude;
                }

                pixels[c * 16 + i] = Vector256.ConvertToSingle(Vector256.Create<int>(lane));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeBc6hMode(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, ReadOnlySpan<Vector256<float>> initialEndpoints,
        int mode, bool signed, Vector256<float> allowed, ref Vector256<float> encodedError, Span<byte> output)
    {
        BcDecoder.Bc6hModeInfo info = BcDecoder.Bc6hModes[mode];
        float minimum = signed ? -MaxHalfMagnitude : 0;
        Span<Vector256<float>> endpoints = stackalloc Vector256<float>[6];
        Span<Vector256<int>> codes = stackalloc Vector256<int>[6];
        Span<Vector256<int>> bestCodes = stackalloc Vector256<int>[6];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> bestIndices = stackalloc Vector256<int>[16];
        Vector256<float> bestError = Vector256.Create(float.MaxValue);
        initialEndpoints.CopyTo(endpoints);

        for (int iteration = 0; ; iteration++)
        {
            for (int c = 0; c < 3; c++)
            {
                codes[c] = QuantizeBc6hEndpoint(endpoints[c], info.EndpointBits, signed);
                codes[3 + c] = QuantizeBc6hEndpoint(endpoints[3 + c], info.EndpointBits, signed);

                if (info.Transformed)
                {
                    int limit = (1 << (info.DeltaBits(c) - 1)) - 1;
                    codes[3 + c] = codes[c] + Vector256.Min(Vector256.Max(codes[3 + c] - codes[c], Vector256.Create(-limit)), Vector256.Create(limit));
                }
            }

            Vector256<float> error = MatchBc6hIndices(pixels, weights, codes, info, signed, Vector256.Create(-1), indices);
            Vector256<int> better = Vector256.LessThan(error, bestError).AsInt32();
            bestError = Vector256.Min(error, bestError);

            for (int k = 0; k < 6; k++)
            {
                bestCodes[k] = Vector256.ConditionalSelect(better, codes[k], bestCodes[k]);
            }

            for (int i = 0; i < 16; i++)
            {
                bestIndices[i] = Vector256.ConditionalSelect(better, indices[i], bestIndices[i]);
            }

            if (iteration == RefineIterations)
            {
                break;
            }

            LeastSquares(pixels, weights, 3, bestIndices, info.IndexBits, endpoints, minimum, MaxHalfMagnitude);
        }

        Span<Vector256<int>> pBits = stackalloc Vector256<int>[2];
        FixAnchor(3, info.IndexBits, Vector256<int>.Zero, bestCodes, pBits, bestIndices);
        Span<int> slots = stackalloc int[12];

        ulong improved = Improve(bestError, allowed, ref encodedError);

        while (improved != 0)
        {
            int lane = BitOperations.TrailingZeroCount(improved);
            improved &= improved - 1;
            for (int c = 0; c < 3; c++)
            {
                int low = bestCodes[c].GetElement(lane);
                int high = bestCodes[3 + c].GetElement(lane);
                slots[c] = low & ((1 << info.EndpointBits) - 1);
                slots[3 + c] = info.Transformed ? (high - low) & ((1 << info.DeltaBits(c)) - 1) : high & ((1 << info.EndpointBits) - 1);
            }

            WriteBc6hBlock(mode, 0, slots, bestIndices, lane, output.Slice(lane * 16, 16));
        }
    }

    private static void WriteBc6hBlock(int mode, int shape, ReadOnlySpan<int> slots, ReadOnlySpan<Vector256<int>> indices, int lane, Span<byte> output)
    {
        BcDecoder.Bc6hModeInfo info = BcDecoder.Bc6hModes[mode];
        ReadOnlySpan<byte> layout = Bc6hTables.HeaderBits.Slice(mode * 82, 82);
        UInt128 value = (uint)info.Code;

        for (int i = 0; i < info.HeaderBits; i++)
        {
            byte field = layout[i];

            if (field == 0xFF)
            {
                continue;
            }

            int source = field >> 4 == 0 ? shape : slots[(field >> 4) - 1];

            if (((source >> (field & 15)) & 1) != 0)
            {
                value |= UInt128.One << i;
            }
        }

        int position = info.HeaderBits;

        for (int i = 0; i < 16; i++)
        {
            value |= (UInt128)(uint)indices[i].GetElement(lane) << position;
            position += info.IndexBits - (BcDecoder.Bc7IsAnchor(info.Regions, shape, i) ? 1 : 0);
        }

        BinaryPrimitives.WriteUInt128LittleEndian(output, value);
    }

    private static Vector256<int> QuantizeBc6hEndpoint(Vector256<float> value, int bits, bool signed)
    {
        if (!signed)
        {
            Vector256<float> unquantized = value * Vector256.Create(64f / 31);
            Vector256<float> scaled = bits >= 15 ? unquantized : unquantized * Vector256.Create((float)(1 << bits) / 65536) - Vector256.Create(0.5f);
            Vector256<int> code = Vector256.ConvertToInt32(Vector256.Round(scaled));
            return Vector256.Min(Vector256.Max(code, Vector256<int>.Zero), Vector256.Create((1 << bits) - 1));
        }

        Vector256<float> magnitude = Vector256.Abs(value) * Vector256.Create(32f / 31);
        int limit = bits >= 16 ? 0x7FFF : (1 << (bits - 1)) - 1;
        Vector256<float> scaledMagnitude = bits >= 16 ? magnitude : magnitude * Vector256.Create((float)(1 << (bits - 1)) / 32768) - Vector256.Create(0.5f);
        Vector256<int> codeMagnitude = Vector256.Min(Vector256.Max(Vector256.ConvertToInt32(Vector256.Round(scaledMagnitude)), Vector256<int>.Zero), Vector256.Create(limit));
        Vector256<int> negative = Vector256.LessThan(value, Vector256<float>.Zero).AsInt32();
        return Vector256.ConditionalSelect(negative, -codeMagnitude, codeMagnitude);
    }

    private static Vector256<int> UnquantizeBc6hEndpoint(Vector256<int> code, int bits, bool signed)
    {
        if (!signed)
        {
            if (bits >= 15)
            {
                return code;
            }

            Vector256<int> value = ((code << 16) + Vector256.Create(0x8000)) >> bits;
            value = Vector256.ConditionalSelect(Vector256.Equals(code, Vector256<int>.Zero), Vector256<int>.Zero, value);
            return Vector256.ConditionalSelect(Vector256.Equals(code, Vector256.Create((1 << bits) - 1)), Vector256.Create(0xFFFF), value);
        }

        if (bits >= 16)
        {
            return code;
        }

        Vector256<int> magnitude = Vector256.Abs(code);
        Vector256<int> result = ((magnitude << 15) + Vector256.Create(0x4000)) >> (bits - 1);
        result = Vector256.ConditionalSelect(Vector256.Equals(magnitude, Vector256<int>.Zero), Vector256<int>.Zero, result);
        result = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(magnitude, Vector256.Create((1 << (bits - 1)) - 1)), Vector256.Create(0x7FFF), result);
        return Vector256.ConditionalSelect(Vector256.LessThan(code, Vector256<int>.Zero), -result, result);
    }

    private static Vector256<int> FinishBc6hInterpolation(Vector256<int> value, bool signed)
    {
        if (!signed)
        {
            return (value * Vector256.Create(31)) >> 6;
        }

        Vector256<int> magnitude = (Vector256.Abs(value) * Vector256.Create(31)) >> 5;
        return Vector256.ConditionalSelect(Vector256.LessThan(value, Vector256<int>.Zero), -magnitude, magnitude);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<float> MatchBc6hIndices(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, ReadOnlySpan<Vector256<int>> codes,
        BcDecoder.Bc6hModeInfo info, bool signed, Vector256<int> anchor, Span<Vector256<int>> indices)
    {
        int count = 1 << info.IndexBits;
        Span<Vector256<float>> palette = stackalloc Vector256<float>[16 * 3];
        Span<Vector256<int>> unquantized = stackalloc Vector256<int>[6];

        for (int k = 0; k < 6; k++)
        {
            unquantized[k] = UnquantizeBc6hEndpoint(codes[k], info.EndpointBits, signed);
        }

        for (int k = 0; k < count; k++)
        {
            int weight = (k * 64 + (info.IndexBits == 3 ? 3 : 7)) / ((1 << info.IndexBits) - 1);

            for (int c = 0; c < 3; c++)
            {
                Vector256<int> blended = (unquantized[c] * Vector256.Create(64 - weight) + unquantized[3 + c] * Vector256.Create(weight) + Vector256.Create(32)) >> 6;
                palette[k * 3 + c] = Vector256.ConvertToSingle(FinishBc6hInterpolation(blended, signed));
            }
        }

        Vector256<float> total = Vector256<float>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<float> best = Vector256.Create(float.MaxValue);
            Vector256<int> bestIndex = Vector256<int>.Zero;
            Vector256<float> isAnchor = Vector256.Equals(anchor, Vector256.Create(i)).AsSingle();

            for (int k = 0; k < count; k++)
            {
                Vector256<float> dr = pixels[i] - palette[k * 3];
                Vector256<float> dg = pixels[16 + i] - palette[k * 3 + 1];
                Vector256<float> db = pixels[32 + i] - palette[k * 3 + 2];
                Vector256<float> error = dr * dr + dg * dg + db * db;

                if (k >= count / 2)
                {
                    error = Vector256.ConditionalSelect(isAnchor, Vector256.Create(float.MaxValue), error);
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PrepareRegions(ReadOnlySpan<Vector256<float>> pixels, Vector256<int> shape, float minimum, Span<Vector256<float>> weights,
        Span<Vector256<int>> anchors, Span<Vector256<float>> endpoints)
    {
        Span<int> shapeOf = stackalloc int[Lanes];
        Span<int> lanes = stackalloc int[Lanes];
        shape.CopyTo(shapeOf);

        for (int i = 0; i < 16; i++)
        {
            for (int lane = 0; lane < Lanes; lane++)
            {
                lanes[lane] = BcDecoder.Bc7Subset(2, shapeOf[lane], i);
            }

            Vector256<float> second = Vector256.ConvertToSingle(Vector256.Create<int>(lanes));
            weights[i] = Vector256.Create(1f) - second;
            weights[16 + i] = second;
        }

        for (int lane = 0; lane < Lanes; lane++)
        {
            lanes[lane] = Bc7Tables.AnchorSecondSubset[shapeOf[lane]];
        }

        anchors[0] = Vector256<int>.Zero;
        anchors[1] = Vector256.Create<int>(lanes);

        for (int region = 0; region < 2; region++)
        {
            Span<Vector256<float>> regionEndpoints = endpoints.Slice(region * 6, 6);
            FitPrincipalAxis(pixels, weights.Slice(region * 16, 16), 3, regionEndpoints, minimum, MaxHalfMagnitude);
            OrientTowardAnchor(pixels, regionEndpoints, anchors[region]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeBc6hTwoRegionMode(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, ReadOnlySpan<Vector256<int>> anchors,
        ReadOnlySpan<Vector256<float>> initialEndpoints, int mode, Vector256<int> shape, bool signed, Vector256<float> allowed, ref Vector256<float> encodedError, Span<byte> output)
    {
        BcDecoder.Bc6hModeInfo info = BcDecoder.Bc6hModes[mode];
        float minimum = signed ? -MaxHalfMagnitude : 0;
        Span<Vector256<float>> endpoints = stackalloc Vector256<float>[12];
        Span<Vector256<int>> codes = stackalloc Vector256<int>[12];
        Span<Vector256<int>> bestCodes = stackalloc Vector256<int>[12];
        Span<Vector256<int>> indices = stackalloc Vector256<int>[2 * 16];
        Span<Vector256<int>> bestIndices = stackalloc Vector256<int>[2 * 16];
        initialEndpoints.CopyTo(endpoints);

        Vector256<float> bestError = Vector256.Create(float.MaxValue);

        for (int iteration = 0; ; iteration++)
        {
            for (int k = 0; k < 12; k++)
            {
                codes[k] = QuantizeBc6hEndpoint(endpoints[k], info.EndpointBits, signed);
            }

            if (info.Transformed)
            {
                for (int k = 3; k < 12; k++)
                {
                    int limit = (1 << (info.DeltaBits(k % 3) - 1)) - 1;
                    Vector256<int> delta = Vector256.Min(Vector256.Max(codes[k] - codes[k % 3], Vector256.Create(-limit)), Vector256.Create(limit));
                    codes[k] = codes[k % 3] + delta;
                }
            }

            Vector256<float> error = Vector256<float>.Zero;

            for (int region = 0; region < 2; region++)
            {
                error += MatchBc6hIndices(pixels, weights.Slice(region * 16, 16), codes.Slice(region * 6, 6), info, signed, anchors[region], indices.Slice(region * 16, 16));
            }

            Vector256<int> better = Vector256.LessThan(error, bestError).AsInt32();
            bestError = Vector256.Min(error, bestError);

            for (int k = 0; k < 12; k++)
            {
                bestCodes[k] = Vector256.ConditionalSelect(better, codes[k], bestCodes[k]);
            }

            for (int i = 0; i < 32; i++)
            {
                bestIndices[i] = Vector256.ConditionalSelect(better, indices[i], bestIndices[i]);
            }

            if (iteration == PartitionRefineIterations)
            {
                break;
            }

            for (int region = 0; region < 2; region++)
            {
                LeastSquares(pixels, weights.Slice(region * 16, 16), 3, bestIndices.Slice(region * 16, 16), info.IndexBits, endpoints.Slice(region * 6, 6),
                    minimum, MaxHalfMagnitude);
            }
        }

        Span<Vector256<int>> combined = stackalloc Vector256<int>[16];

        for (int i = 0; i < 16; i++)
        {
            combined[i] = Vector256.ConditionalSelect(Vector256.Equals(weights[16 + i], Vector256.Create(1f)).AsInt32(), bestIndices[16 + i], bestIndices[i]);
        }

        Span<int> slots = stackalloc int[12];

        ulong improved = Improve(bestError, allowed, ref encodedError);

        while (improved != 0)
        {
            int lane = BitOperations.TrailingZeroCount(improved);
            improved &= improved - 1;
            for (int k = 0; k < 12; k++)
            {
                int c = k % 3;
                int value = bestCodes[k].GetElement(lane);
                slots[k] = k < 3 || !info.Transformed ? value & ((1 << info.EndpointBits) - 1) : (value - bestCodes[c].GetElement(lane)) & ((1 << info.DeltaBits(c)) - 1);
            }

            WriteBc6hBlock(mode, shape.GetElement(lane), slots, combined, lane, output.Slice(lane * 16, 16));
        }
    }

    private static void OrientTowardAnchor(ReadOnlySpan<Vector256<float>> pixels, Span<Vector256<float>> endpoints, Vector256<int> anchor)
    {
        Vector256<float> projection = Vector256<float>.Zero;
        Vector256<float> lengthSquared = Vector256<float>.Zero;

        for (int c = 0; c < 3; c++)
        {
            Vector256<float> value = Vector256<float>.Zero;

            for (int i = 0; i < 16; i++)
            {
                value = Vector256.ConditionalSelect(Vector256.Equals(anchor, Vector256.Create(i)).AsSingle(), pixels[c * 16 + i], value);
            }

            Vector256<float> direction = endpoints[3 + c] - endpoints[c];
            projection += (value - endpoints[c]) * direction;
            lengthSquared += direction * direction;
        }

        Vector256<float> swap = Vector256.GreaterThan(projection * Vector256.Create(2f), lengthSquared);

        for (int c = 0; c < 3; c++)
        {
            Vector256<float> low = endpoints[c];
            endpoints[c] = Vector256.ConditionalSelect(swap, endpoints[3 + c], low);
            endpoints[3 + c] = Vector256.ConditionalSelect(swap, low, endpoints[3 + c]);
        }
    }

    #endregion

    #region BC7

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
    private static void EncodeBc7Row(ReadOnlySpan<byte> src, int width, int height, int by, int blocksX, Span<byte> row)
    {
        Span<byte> blocks = stackalloc byte[64 * Lanes];
        Span<byte> encoded = stackalloc byte[16 * Lanes];
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

            LoadPlanes(blocks, pixels);
            Vector256<float> alphaError = OpaqueAlphaError(pixels);
            Vector256<float> translucent = Vector256.GreaterThan(alphaError, Vector256<float>.Zero);
            bool anyTranslucent = translucent != Vector256<float>.Zero;
            int estimateChannels = anyTranslucent ? 4 : 3;
            Vector256<float> bestError = Vector256.Create(float.MaxValue);
            EncodeEndpointMode(pixels, 6, Vector256<int>.Zero, alphaError, Vector256<float>.AllBitsSet, ref bestError, encoded);

            for (int rotation = anyTranslucent ? 0 : 1; rotation < 4; rotation++)
            {
                Vector256<float> allowed = rotation == 0 ? translucent : Vector256<float>.AllBitsSet;
                Rotate(pixels, rotation, rotated);
                EncodeMode5(rotated, ones, rotation, allowed, ref bestError, encoded);

                for (int indexSelection = 0; indexSelection < 2; indexSelection++)
                {
                    EncodeMode4(rotated, ones, rotation, indexSelection, allowed, ref bestError, encoded);
                }
            }

            EstimatePartitions(pixels, estimateChannels, 2, 64, partitions);
            EncodeEndpointMode(pixels, 1, partitions[1], alphaError, Vector256<float>.AllBitsSet, ref bestError, encoded);
            EncodeEndpointMode(pixels, 3, partitions[1], alphaError, Vector256<float>.AllBitsSet, ref bestError, encoded);

            if (anyTranslucent)
            {
                EncodeEndpointMode(pixels, 7, partitions[1], alphaError, translucent, ref bestError, encoded);
            }

            Vector256<float> needsThreeSubsets = Vector256.GreaterThan(bestError, Vector256.Create(ThreeSubsetErrorThreshold));

            if (needsThreeSubsets != Vector256<float>.Zero)
            {
                EstimatePartitions(pixels, estimateChannels, 3, 64, partitions);
                EncodeEndpointMode(pixels, 0, partitions[0], alphaError, needsThreeSubsets, ref bestError, encoded);
                EncodeEndpointMode(pixels, 2, partitions[1], alphaError, needsThreeSubsets, ref bestError, encoded);
            }

            encoded[..(count * 16)].CopyTo(row[(bx * 16)..]);
        }
    }

    private static void LoadPlanes(ReadOnlySpan<byte> blocks, Span<Vector256<float>> pixels)
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

    private static ulong Improve(Vector256<float> error, Vector256<float> allowed, ref Vector256<float> encodedError)
    {
        error = Vector256.ConditionalSelect(allowed, error, Vector256.Create(float.MaxValue));
        ulong improved = Vector256.LessThan(error, encodedError).ExtractMostSignificantBits();
        encodedError = Vector256.Min(error, encodedError);
        return improved;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EstimatePartitions(ReadOnlySpan<Vector256<float>> pixels, int channels, int subsets, int partitionCount, Span<Vector256<int>> best)
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

        for (int partition = 0; partition < partitionCount; partition++)
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
    private static void EncodeEndpointMode(ReadOnlySpan<Vector256<float>> pixels, int mode, Vector256<int> partition, Vector256<float> alphaError,
        Vector256<float> allowed, ref Vector256<float> encodedError, Span<byte> output)
    {
        BcDecoder.Bc7ModeInfo info = BcDecoder.Bc7Modes[mode];
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
                lanes[lane] = BcDecoder.Bc7Subset(info.Subsets, partitionOf[lane], i);
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

        ulong improved = Improve(error, allowed, ref encodedError);

        while (improved != 0)
        {
            int lane = BitOperations.TrailingZeroCount(improved);
            improved &= improved - 1;
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
                int subset = BcDecoder.Bc7Subset(info.Subsets, p, i);
                writer.Write(indices[subset * 16 + i].GetElement(lane), info.IndexBits - (BcDecoder.Bc7IsAnchor(info.Subsets, p, i) ? 1 : 0));
            }

            writer.CopyTo(output.Slice(lane * 16, 16));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeMode5(ReadOnlySpan<Vector256<float>> rotated, ReadOnlySpan<Vector256<float>> ones, int rotation, Vector256<float> allowed, ref Vector256<float> encodedError, Span<byte> output)
    {
        Span<Vector256<int>> colorCodes = stackalloc Vector256<int>[6];
        Span<Vector256<int>> alphaCodes = stackalloc Vector256<int>[2];
        Span<Vector256<int>> pBits = stackalloc Vector256<int>[2];
        Span<Vector256<int>> colorIndices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> alphaIndices = stackalloc Vector256<int>[16];
        Vector256<float> error = FitEndpoints(rotated[..48], ones, 3, 7, PBits.None, 2, RefineIterations, Vector256<int>.Zero, colorCodes, pBits, colorIndices)
            + FitEndpoints(rotated[48..], ones, 1, 8, PBits.None, 2, RefineIterations, Vector256<int>.Zero, alphaCodes, pBits, alphaIndices);

        ulong improved = Improve(error, allowed, ref encodedError);

        while (improved != 0)
        {
            int lane = BitOperations.TrailingZeroCount(improved);
            improved &= improved - 1;
            BitWriter writer = new BitWriter();
            writer.Write(1 << 5, 6);
            writer.Write(rotation, 2);
            WriteEndpoints(ref writer, colorCodes, 3, 7, lane);
            WriteEndpoints(ref writer, alphaCodes, 1, 8, lane);
            WriteIndices(ref writer, colorIndices, 2, lane);
            WriteIndices(ref writer, alphaIndices, 2, lane);
            writer.CopyTo(output.Slice(lane * 16, 16));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EncodeMode4(ReadOnlySpan<Vector256<float>> rotated, ReadOnlySpan<Vector256<float>> ones, int rotation, int indexSelection, Vector256<float> allowed, ref Vector256<float> encodedError, Span<byte> output)
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

        ulong improved = Improve(error, allowed, ref encodedError);

        while (improved != 0)
        {
            int lane = BitOperations.TrailingZeroCount(improved);
            improved &= improved - 1;
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

        FitPrincipalAxis(pixels, weights, channels, endpoints, 0, 255);

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
                        codes[c] = QuantizeEndpoint(endpoints[c], bits, out expanded[c]);
                        codes[channels + c] = QuantizeEndpoint(endpoints[channels + c], bits, out expanded[channels + c]);
                    }
                    else
                    {
                        codes[c] = QuantizeEndpointWithPBit(endpoints[c], bits, pBit0, out expanded[c]);
                        codes[channels + c] = QuantizeEndpointWithPBit(endpoints[channels + c], bits, pBit1, out expanded[channels + c]);
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

            LeastSquares(pixels, weights, channels, bestIndices, indexBits, endpoints, 0, 255);
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

    private static void FitPrincipalAxis(ReadOnlySpan<Vector256<float>> pixels, ReadOnlySpan<Vector256<float>> weights, int channels, Span<Vector256<float>> endpoints,
        float minimum, float maximum)
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
            endpoints[c] = Clamp(mean[c] + low * axis[c], minimum, maximum);
            endpoints[channels + c] = Clamp(mean[c] + high * axis[c], minimum, maximum);
        }
    }

    private static Vector256<float> SafeReciprocal(Vector256<float> value)
    {
        Vector256<float> positive = Vector256.GreaterThan(value, Vector256.Create(1e-20f));
        return Vector256.ConditionalSelect(positive, Vector256.Create(1f) / Vector256.Max(value, Vector256.Create(1e-20f)), Vector256<float>.Zero);
    }

    private static Vector256<float> Clamp(Vector256<float> value, float minimum, float maximum) => Vector256.Min(Vector256.Max(value, Vector256.Create(minimum)), Vector256.Create(maximum));

    private static Vector256<int> QuantizeEndpoint(Vector256<float> value, int bits, out Vector256<float> expanded)
    {
        int max = (1 << bits) - 1;
        Vector256<int> code = Vector256.ConvertToInt32(Vector256.Round(value * Vector256.Create(max / 255f)));
        code = Vector256.Min(Vector256.Max(code, Vector256<int>.Zero), Vector256.Create(max));
        expanded = Vector256.ConvertToSingle(Unquantize(code, bits));
        return code;
    }

    private static Vector256<int> QuantizeEndpointWithPBit(Vector256<float> value, int bits, Vector256<int> pBit, out Vector256<float> expanded)
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
        Span<Vector256<float>> endpoints, float minimum, float maximum)
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
            Vector256<float> low = Clamp((bb * sumA[c] - ab * sumB[c]) * inverse, minimum, maximum);
            Vector256<float> high = Clamp((aa * sumB[c] - ab * sumA[c]) * inverse, minimum, maximum);
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

    #endregion
}
