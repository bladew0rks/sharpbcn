using System.Buffers.Binary;
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
                BcEncoder.GatherBlock(src, width, height, (bx + Math.Min(lane, count - 1)) * 4, by * 4, blocks.Slice(lane * 64, 64));
            }

            Load(blocks, r, g, b, a);
            Span<byte> dest = row[(bx * blockSize)..];

            switch (kind)
            {
                case BcEncoder.BlockKind.Bc1:
                    if (alphaThreshold > 0)
                    {
                        for (int lane = 0; lane < count; lane++)
                        {
                            BcEncoder.CompressColorBlock(blocks.Slice(lane * 64, 64), dest.Slice(lane * 8, 8), false, alphaThreshold);
                        }
                    }
                    else
                    {
                        WriteColorBlocks(blocks, r, g, b, count, dest, 8);
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
        Span<Vector256<int>> indices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> bestIndices = stackalloc Vector256<int>[16];
        Span<Vector256<int>> refinedIndices = stackalloc Vector256<int>[16];
        Span<Vector256<float>> rf = stackalloc Vector256<float>[16];
        Span<Vector256<float>> gf = stackalloc Vector256<float>[16];
        Span<Vector256<float>> bf = stackalloc Vector256<float>[16];

        for (int i = 0; i < 16; i++)
        {
            rf[i] = Vector256.ConvertToSingle(r[i]);
            gf[i] = Vector256.ConvertToSingle(g[i]);
            bf[i] = Vector256.ConvertToSingle(b[i]);
        }

        Endpoints best = default;
        Vector256<int> bestError = default;

        Endpoints principal = PrincipalEndpoints(r, g, b, rf, gf, bf);

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
            Vector256<int> active = Vector256<int>.AllBitsSet;

            for (int iteration = 0; iteration < 3; iteration++)
            {
                Vector256<int> solved = RefineFourColor(rf, gf, bf, indices, out Endpoints refined);
                active &= solved;

                if (active == Vector256<int>.Zero)
                {
                    break;
                }

                QuantizeEndpoints(ref refined);
                Vector256<int> refinedError = MatchFourColor(r, g, b, refined, refinedIndices);

                active &= Vector256.LessThan(refinedError, error);

                if (active == Vector256<int>.Zero)
                {
                    break;
                }

                Select(active, ref endpoints, refined);
                error = Vector256.ConditionalSelect(active, refinedError, error);

                for (int i = 0; i < 16; i++)
                {
                    indices[i] = Vector256.ConditionalSelect(active, refinedIndices[i], indices[i]);
                }
            }

            if (candidate == 0)
            {
                best = endpoints;
                bestError = error;
                indices.CopyTo(bestIndices);
            }
            else
            {
                Vector256<int> better = Vector256.LessThan(error, bestError);
                Select(better, ref best, endpoints);
                bestError = Vector256.ConditionalSelect(better, error, bestError);

                for (int i = 0; i < 16; i++)
                {
                    bestIndices[i] = Vector256.ConditionalSelect(better, indices[i], bestIndices[i]);
                }
            }
        }

        Vector256<int> retry = ClusterFit(r, g, b, bestIndices, ref best, ref bestError, BcEncoder.ClusterOrderingsPerBlock, Vector256<int>.AllBitsSet);

        if (retry != Vector256<int>.Zero)
        {
            ClusterFit(r, g, b, bestIndices, ref best, ref bestError, 0, retry);
        }

        SearchColorEndpoints(r, g, b, ref best, ref bestError);
        MatchFourColor(r, g, b, best, bestIndices);

        Vector256<int> c0 = Pack565(best.R0, best.G0, best.B0);
        Vector256<int> c1 = Pack565(best.R1, best.G1, best.B1);
        Vector256<int> mask = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            mask |= bestIndices[i] << (i * 2);
        }

        Vector256<int> swap = Vector256.LessThan(c0, c1);
        Vector256<int> equal = Vector256.Equals(c0, c1);
        Vector256<int> swapped0 = Vector256.ConditionalSelect(swap, c1, c0);
        Vector256<int> swapped1 = Vector256.ConditionalSelect(swap, c0, c1);
        mask = Vector256.ConditionalSelect(swap, mask ^ Vector256.Create(0x55555555), mask);
        mask = Vector256.AndNot(mask, equal);

        swapped0.CopyTo(color0);
        swapped1.CopyTo(color1);
        mask.AsUInt32().CopyTo(masks);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Vector256<int> ClusterFit(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<int>> indices, ref Endpoints best, ref Vector256<int> bestError, int orderingCount, Vector256<int> active)
    {
        Span<Vector256<int>> keys = stackalloc Vector256<int>[16];
        Span<Vector256<int>> rg = stackalloc Vector256<int>[16];
        Span<Vector256<int>> bl = stackalloc Vector256<int>[16];
        Vector256<int> dr = best.R1 - best.R0, dg = best.G1 - best.G0, db = best.B1 - best.B0;

        for (int i = 0; i < 16; i++)
        {
            keys[i] = ((r[i] * dr + g[i] * dg + b[i] * db) << 4) | Vector256.Create(i);
            rg[i] = r[i] | (g[i] << 16);
            bl[i] = b[i];
        }

        foreach ((int first, int second) in BcEncoder.SortNetwork)
        {
            Vector256<int> k0 = keys[first], k1 = keys[second];
            Vector256<int> swap = Vector256.GreaterThan(k0, k1);
            keys[first] = Vector256.ConditionalSelect(swap, k1, k0);
            keys[second] = Vector256.ConditionalSelect(swap, k0, k1);
            Vector256<int> x0 = rg[first], x1 = rg[second];
            rg[first] = Vector256.ConditionalSelect(swap, x1, x0);
            rg[second] = Vector256.ConditionalSelect(swap, x0, x1);
            x0 = bl[first];
            x1 = bl[second];
            bl[first] = Vector256.ConditionalSelect(swap, x1, x0);
            bl[second] = Vector256.ConditionalSelect(swap, x0, x1);
        }

        Vector256<int> totalRg = Vector256<int>.Zero, totalB = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            totalRg += rg[i];
            totalB += bl[i];
        }

        Endpoints fit = best;
        Vector256<int> fitError = Vector256.Create(int.MaxValue);

        if (orderingCount > 0)
        {
            Vector256<int> count0 = Vector256<int>.Zero, count1 = Vector256<int>.Zero, count2 = Vector256<int>.Zero;

            for (int i = 0; i < 16; i++)
            {
                count0 -= Vector256.Equals(indices[i], Vector256<int>.Zero);
                count1 -= Vector256.Equals(indices[i], Vector256.Create(2));
                count2 -= Vector256.Equals(indices[i], Vector256.Create(3));
            }

            Span<int> plan = stackalloc int[3 * Lanes * orderingCount];
            ReadOnlySpan<byte> histograms = ClusterTables.Histograms;
            ReadOnlySpan<ushort> orderings = ClusterTables.BestOrderings;

            for (int lane = 0; lane < Lanes; lane++)
            {
                int start = BcEncoder.HistogramIndex(count0.GetElement(lane), count1.GetElement(lane), count2.GetElement(lane)) * ClusterTables.OrderingsPerHistogram;

                for (int q = 0; q < orderingCount; q++)
                {
                    int split = orderings[start + q] * 4;
                    plan[q * 3 * Lanes + lane] = histograms[split];
                    plan[(q * 3 + 1) * Lanes + lane] = histograms[split + 1];
                    plan[(q * 3 + 2) * Lanes + lane] = histograms[split + 2];
                }
            }

            for (int q = 0; q < orderingCount; q++)
            {
                TrySplit(r, g, b, rg, bl, totalRg, totalB, Vector256.Create<int>(plan.Slice(q * 3 * Lanes, Lanes)),
                    Vector256.Create<int>(plan.Slice((q * 3 + 1) * Lanes, Lanes)), Vector256.Create<int>(plan.Slice((q * 3 + 2) * Lanes, Lanes)),
                    active, ref fit, ref fitError);
            }
        }

        Span<Vector256<int>> sizes = stackalloc Vector256<int>[4];
        Vector256<int> group = Vector256<int>.Zero;
        sizes.Clear();
        sizes[0] = Vector256<int>.AllBitsSet;

        for (int i = 1; i < 16; i++)
        {
            group -= ~(Vector256.Equals(rg[i], rg[i - 1]) & Vector256.Equals(bl[i], bl[i - 1]));

            for (int j = 0; j < 4; j++)
            {
                sizes[j] += Vector256.Equals(group, Vector256.Create(j));
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

            Vector256<int> h0 = Vector256<int>.Zero, h1 = Vector256<int>.Zero, h2 = Vector256<int>.Zero;

            for (int j = 0; j < pattern.Length; j++)
            {
                switch (pattern[j])
                {
                    case 0: h0 -= sizes[j]; break;
                    case 1: h1 -= sizes[j]; break;
                    case 2: h2 -= sizes[j]; break;
                }
            }

            TrySplit(r, g, b, rg, bl, totalRg, totalB, h0, h1, h2, valid, ref fit, ref fitError);
        }

        Vector256<int> accept = active & Vector256.LessThan(fitError, bestError);
        Select(accept, ref best, fit);
        bestError = Vector256.ConditionalSelect(accept, fitError, bestError);
        return accept;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TrySplit(ReadOnlySpan<Vector256<int>> r, ReadOnlySpan<Vector256<int>> g, ReadOnlySpan<Vector256<int>> b,
        ReadOnlySpan<Vector256<int>> rg, ReadOnlySpan<Vector256<int>> bl, Vector256<int> totalRg, Vector256<int> totalB,
        Vector256<int> h0, Vector256<int> h1, Vector256<int> h2, Vector256<int> valid, ref Endpoints fit, ref Vector256<int> fitError)
    {
        Vector256<int> h3 = Vector256.Create(16) - h0 - h1 - h2;
        Vector256<int> f1 = h0, f2 = h0 + h1, f3 = f2 + h2;
        Vector256<int> p1Rg = Vector256<int>.Zero, p2Rg = Vector256<int>.Zero, p3Rg = Vector256<int>.Zero;
        Vector256<int> p1B = Vector256<int>.Zero, p2B = Vector256<int>.Zero, p3B = Vector256<int>.Zero;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> position = Vector256.Create(i);
            Vector256<int> in1 = Vector256.GreaterThan(f1, position);
            Vector256<int> in2 = Vector256.GreaterThan(f2, position);
            Vector256<int> in3 = Vector256.GreaterThan(f3, position);
            p1Rg += rg[i] & in1;
            p2Rg += rg[i] & in2;
            p3Rg += rg[i] & in3;
            p1B += bl[i] & in1;
            p2B += bl[i] & in2;
            p3B += bl[i] & in3;
        }

        Vector256<int> a2 = Vector256.Create(9) * h0 + Vector256.Create(4) * h1 + h2;
        Vector256<int> b2 = h1 + Vector256.Create(4) * h2 + Vector256.Create(9) * h3;
        Vector256<int> ab = Vector256.Create(2) * (h1 + h2);
        Vector256<int> detInt = a2 * b2 - ab * ab;
        Vector256<float> det = Vector256.ConvertToSingle(detInt);
        Vector256<int> low = Vector256.Create(0xFFFF);
        valid &= ~Vector256.Equals(detInt, Vector256<int>.Zero);

        if (valid == Vector256<int>.Zero)
        {
            return;
        }

        Endpoints trial;
        (trial.R0, trial.R1) = FitChannel(p1Rg & low, p2Rg & low, p3Rg & low, totalRg & low, a2, b2, ab, det, 5);
        (trial.G0, trial.G1) = FitChannel(p1Rg >>> 16, p2Rg >>> 16, p3Rg >>> 16, totalRg >>> 16, a2, b2, ab, det, 6);
        (trial.B0, trial.B1) = FitChannel(p1B, p2B, p3B, totalB, a2, b2, ab, det, 5);
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
    private static (Vector256<int>, Vector256<int>) FitChannel(Vector256<int> p1, Vector256<int> p2, Vector256<int> p3, Vector256<int> total,
        Vector256<int> a2, Vector256<int> b2, Vector256<int> ab, Vector256<float> det, int bits)
    {
        Vector256<int> two = Vector256.Create(2), three = Vector256.Create(3);
        Vector256<int> ax = three * p1 + two * (p2 - p1) + (p3 - p2);
        Vector256<int> bx = (p2 - p1) + two * (p3 - p2) + three * (total - p3);
        Vector256<float> n0 = Vector256.ConvertToSingle(three * (ax * b2 - bx * ab));
        Vector256<float> n1 = Vector256.ConvertToSingle(three * (bx * a2 - ax * ab));
        Vector256<int> c0 = Clamp255(Vector256.ConvertToInt32(Vector256.Round(n0 / det)));
        Vector256<int> c1 = Clamp255(Vector256.ConvertToInt32(Vector256.Round(n1 / det)));
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
                int bits = e % 3 == 1 ? 6 : 5;
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
            Vector256<int> lower = Vector256.LessThan(dot, minDot).AsInt32();
            Vector256<int> higher = Vector256.GreaterThan(dot, maxDot).AsInt32();

            minDot = Vector256.ConditionalSelect(lower.AsSingle(), dot, minDot);
            maxDot = Vector256.ConditionalSelect(higher.AsSingle(), dot, maxDot);

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
            best = Vector256.ConditionalSelect(lower, Vector256.Create(1), best);

            error = Distance(r[i], g[i], b[i], p2r, p2g, p2b);
            lower = Vector256.LessThan(error, bestError);
            bestError = Vector256.ConditionalSelect(lower, error, bestError);
            best = Vector256.ConditionalSelect(lower, Vector256.Create(2), best);

            error = Distance(r[i], g[i], b[i], p3r, p3g, p3b);
            lower = Vector256.LessThan(error, bestError);
            bestError = Vector256.ConditionalSelect(lower, error, bestError);
            best = Vector256.ConditionalSelect(lower, Vector256.Create(3), best);

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
    private static Vector256<int> RefineFourColor(ReadOnlySpan<Vector256<float>> rf, ReadOnlySpan<Vector256<float>> gf, ReadOnlySpan<Vector256<float>> bf,
        ReadOnlySpan<Vector256<int>> indices, out Endpoints result)
    {
        Vector256<float> one = Vector256.Create(1f);
        Vector256<float> twoThirds = Vector256.Create(2f / 3f);
        Vector256<float> oneThird = Vector256.Create(1f / 3f);
        Vector256<float> aa = default, bb = default, ab = default;
        Vector256<float> ar = default, ag = default, ab2 = default, br = default, bg = default, bb2 = default;

        for (int i = 0; i < 16; i++)
        {
            Vector256<int> index = indices[i];
            Vector256<float> a = Vector256.ConditionalSelect(Vector256.Equals(index, Vector256<int>.Zero).AsSingle(), one,
                Vector256.ConditionalSelect(Vector256.Equals(index, Vector256.Create(2)).AsSingle(), twoThirds,
                    Vector256.ConditionalSelect(Vector256.Equals(index, Vector256.Create(3)).AsSingle(), oneThird, Vector256<float>.Zero)));
            Vector256<float> b = one - a;

            aa += a * a;
            bb += b * b;
            ab += a * b;
            ar += a * rf[i];
            ag += a * gf[i];
            ab2 += a * bf[i];
            br += b * rf[i];
            bg += b * gf[i];
            bb2 += b * bf[i];
        }

        Vector256<float> det = aa * bb - ab * ab;
        Vector256<int> solvable = Vector256.GreaterThanOrEqual(Vector256.Abs(det), Vector256.Create(1e-6f)).AsInt32();
        Vector256<float> inv = one / det;

        result.R0 = RoundToInt((ar * bb - br * ab) * inv);
        result.G0 = RoundToInt((ag * bb - bg * ab) * inv);
        result.B0 = RoundToInt((ab2 * bb - bb2 * ab) * inv);
        result.R1 = RoundToInt((br * aa - ar * ab) * inv);
        result.G1 = RoundToInt((bg * aa - ag * ab) * inv);
        result.B1 = RoundToInt((bb2 * aa - ab2 * ab) * inv);
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
        Vector256<int> low = Vector256.Create(0xFFFF);

        for (int i = 0; i < 8; i++)
        {
            Vector256<short> v = pairs[i].AsInt16();
            Vector256<short> nearest = Vector256.Min(Vector256.Min(Vector256.Min(Vector256.Abs(v - p0), Vector256.Abs(v - p1)),
                Vector256.Min(Vector256.Abs(v - p2), Vector256.Abs(v - p3))),
                Vector256.Min(Vector256.Min(Vector256.Abs(v - p4), Vector256.Abs(v - p5)),
                Vector256.Min(Vector256.Abs(v - p6), Vector256.Abs(v - p7))));
            Vector256<int> squares = (nearest * nearest).AsInt32();
            total += (squares & low) + (squares >>> 16);
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Duplicate(Vector256<int> value)
    {
        return (value | (value << 16)).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PackPairs(ReadOnlySpan<Vector256<int>> values, Span<Vector256<int>> pairs)
    {
        for (int i = 0; i < 8; i++)
        {
            pairs[i] = values[i] | (values[i + 8] << 16);
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
}
