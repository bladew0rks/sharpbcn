using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpBcn;

public static class BcEncoder
{
    private const int ParallelThresholdBlocks = 1024;

    private static readonly byte[] SingleColor5 = BuildSingleColorTable(5);
    private static readonly byte[] SingleColor6 = BuildSingleColorTable(6);

    public static int GetBlockSize(BcFormat format) => format is BcFormat.Bc1 or BcFormat.Bc1Alpha or BcFormat.Bc4 ? 8 : 16;

    public static int GetCompressedSize(int width, int height, BcFormat format) => GetCompressedSize(width, height, GetBlockSize(format));

    public static int GetCompressedSize(int width, int height, int blockSize)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        return blocksX * blocksY * blockSize;
    }

    public static byte[] EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height, int alphaThreshold = 0, bool parallel = true)
    {
        byte[] output = GC.AllocateUninitializedArray<byte>(GetCompressedSize(width, height, 8));
        EncodeBlocks<Bc1Block>(rgba, width, height, output, alphaThreshold, parallel, PreferredSimdWidth);
        return output;
    }

    public static byte[] EncodeBc2(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true) => Encode(BcFormat.Bc2, rgba, width, height, parallel: parallel);

    public static byte[] EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true) => Encode(BcFormat.Bc3, rgba, width, height, parallel: parallel);

    public static byte[] EncodeBc4(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true) => Encode(BcFormat.Bc4, rgba, width, height, parallel: parallel);

    public static byte[] EncodeBc5(ReadOnlySpan<byte> rgba, int width, int height, bool parallel = true) => Encode(BcFormat.Bc5, rgba, width, height, parallel: parallel);

    public static byte[] Encode(BcFormat format, ReadOnlySpan<byte> rgba, int width, int height, int alphaThreshold = 128, bool parallel = true)
    {
        byte[] output = GC.AllocateUninitializedArray<byte>(GetCompressedSize(width, height, format));
        Encode(format, rgba, width, height, output, alphaThreshold, parallel);
        return output;
    }

    public static void Encode(BcFormat format, ReadOnlySpan<byte> rgba, int width, int height, Span<byte> output, int alphaThreshold = 128, bool parallel = true)
    {
        int simdWidth = PreferredSimdWidth;

        switch (format)
        {
            case BcFormat.Bc1:
                EncodeBlocks<Bc1Block>(rgba, width, height, output, 0, parallel, simdWidth);
                break;
            case BcFormat.Bc1Alpha:
                EncodeBlocks<Bc1Block>(rgba, width, height, output, Math.Max(1, alphaThreshold), parallel, simdWidth);
                break;
            case BcFormat.Bc2:
                EncodeBlocks<Bc2Block>(rgba, width, height, output, 0, parallel, simdWidth);
                break;
            case BcFormat.Bc3:
                EncodeBlocks<Bc3Block>(rgba, width, height, output, 0, parallel, simdWidth);
                break;
            case BcFormat.Bc4:
                EncodeBlocks<Bc4Block>(rgba, width, height, output, 0, parallel, simdWidth);
                break;
            case BcFormat.Bc5:
                EncodeBlocks<Bc5Block>(rgba, width, height, output, 0, parallel, simdWidth);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    internal enum BlockKind
    {
        Bc1,
        Bc2,
        Bc3,
        Bc4,
        Bc5,
    }

    private interface IBlockEncoder
    {
        static abstract int BlockSize { get; }
        static abstract BlockKind Kind { get; }
        static abstract void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold);
    }

    private struct Bc1Block : IBlockEncoder
    {
        public static int BlockSize => 8;
        public static BlockKind Kind => BlockKind.Bc1;
        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold) => CompressColorBlock(block, dest, false, alphaThreshold);
    }

    private struct Bc2Block : IBlockEncoder
    {
        public static int BlockSize => 16;
        public static BlockKind Kind => BlockKind.Bc2;

        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold)
        {
            CompressExplicitAlphaBlock(block, dest[..8]);
            CompressColorBlock(block, dest[8..], true, 0);
        }
    }

    private struct Bc3Block : IBlockEncoder
    {
        public static int BlockSize => 16;
        public static BlockKind Kind => BlockKind.Bc3;

        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold)
        {
            CompressSingleChannelBlock(block, 3, dest[..8]);
            CompressColorBlock(block, dest[8..], true, 0);
        }
    }

    private struct Bc4Block : IBlockEncoder
    {
        public static int BlockSize => 8;
        public static BlockKind Kind => BlockKind.Bc4;
        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold) => CompressSingleChannelBlock(block, 0, dest);
    }

    private struct Bc5Block : IBlockEncoder
    {
        public static int BlockSize => 16;
        public static BlockKind Kind => BlockKind.Bc5;

        public static void Encode(ReadOnlySpan<byte> block, Span<byte> dest, int alphaThreshold)
        {
            CompressSingleChannelBlock(block, 0, dest[..8]);
            CompressSingleChannelBlock(block, 1, dest[8..]);
        }
    }

    private static int PreferredSimdWidth => BcEncoderSimd512.IsSupported ? 512 : BcEncoderSimd.IsSupported ? 256 : 0;

    private static unsafe void EncodeBlocks<T>(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> output, int alphaThreshold, bool parallel, int simdWidth)
        where T : struct, IBlockEncoder
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("Source buffer is smaller than width * height * 4.", nameof(rgba));
        }

        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        int blockSize = T.BlockSize;

        if (blocksX * blocksY * blockSize > output.Length)
        {
            throw new ArgumentException("Output buffer is too small.", nameof(output));
        }

        fixed (byte* sourcePtr = rgba)
        fixed (byte* outputPtr = output)
        {
            nint source = (nint)sourcePtr;
            nint destination = (nint)outputPtr;
            int sourceLength = rgba.Length;

            void EncodeRow(int by)
            {
                ReadOnlySpan<byte> src = new ReadOnlySpan<byte>((void*)source, sourceLength);
                Span<byte> row = new Span<byte>((byte*)destination + by * blocksX * blockSize, blocksX * blockSize);

                if (simdWidth == 512)
                {
                    BcEncoderSimd512.EncodeRow(T.Kind, src, width, height, by, blocksX, row, alphaThreshold);
                    return;
                }

                if (simdWidth == 256)
                {
                    BcEncoderSimd.EncodeRow(T.Kind, src, width, height, by, blocksX, row, alphaThreshold);
                    return;
                }

                Span<byte> block = stackalloc byte[64];

                for (int bx = 0; bx < blocksX; bx++)
                {
                    GatherBlock(src, width, height, bx * 4, by * 4, block);
                    T.Encode(block, row.Slice(bx * blockSize, blockSize), alphaThreshold);
                }
            }

            if (parallel && blocksX * blocksY >= ParallelThresholdBlocks)
            {
                Parallel.For(0, blocksY, EncodeRow);
            }
            else
            {
                for (int by = 0; by < blocksY; by++)
                {
                    EncodeRow(by);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void GatherBlock(ReadOnlySpan<byte> src, int width, int height, int x0, int y0, Span<byte> block)
    {
        for (int y = 0; y < 4; y++)
        {
            int sy = Math.Min(y0 + y, height - 1);

            for (int x = 0; x < 4; x++)
            {
                int sx = Math.Min(x0 + x, width - 1);
                src.Slice((sy * width + sx) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4, 4));
            }
        }
    }

    #region Color (BC1)

    internal static void CompressColorBlock(ReadOnlySpan<byte> block, Span<byte> dest, bool forceFourColor, int alphaThreshold)
    {
        Span<int> colors = stackalloc int[48];
        Span<bool> transparent = stackalloc bool[16];
        bool anyTransparent = false;
        bool allTransparent = true;

        for (int i = 0; i < 16; i++)
        {
            colors[i * 3] = block[i * 4];
            colors[i * 3 + 1] = block[i * 4 + 1];
            colors[i * 3 + 2] = block[i * 4 + 2];
            transparent[i] = !forceFourColor && alphaThreshold > 0 && block[i * 4 + 3] < alphaThreshold;
            anyTransparent |= transparent[i];
            allTransparent &= transparent[i];
        }

        if (allTransparent)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], 0xFFFFFFFF);
            return;
        }

        if (anyTransparent)
        {
            CompressThreeColorBlock(colors, transparent, dest);
            return;
        }

        if (IsSolidColor(colors))
        {
            WriteSolidColorBlock(colors[0], colors[1], colors[2], dest);
            return;
        }

        Span<int> endpoints = stackalloc int[6];
        Span<int> bestEndpoints = stackalloc int[6];
        Span<byte> indices = stackalloc byte[16];
        Span<byte> bestIndices = stackalloc byte[16];
        Span<int> refined = stackalloc int[6];
        Span<byte> refinedIndices = stackalloc byte[16];
        long bestError = long.MaxValue;

        for (int candidate = 0; candidate < 3; candidate++)
        {
            if (candidate == 1)
            {
                FindBoundingBoxEndpoints(colors, endpoints);
            }
            else
            {
                FindPrincipalEndpoints(colors, endpoints);

                if (candidate == 2)
                {
                    ExtrapolateEndpoints(endpoints);
                }
            }

            QuantizeEndpoints(endpoints);
            long error = MatchFourColor(colors, endpoints, indices);

            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (!RefineFourColor(colors, indices, refined))
                {
                    break;
                }

                QuantizeEndpoints(refined);
                long refinedError = MatchFourColor(colors, refined, refinedIndices);

                if (refinedError >= error)
                {
                    break;
                }

                refined.CopyTo(endpoints);
                refinedIndices.CopyTo(indices);
                error = refinedError;
            }

            if (error < bestError)
            {
                bestError = error;
                endpoints.CopyTo(bestEndpoints);
                indices.CopyTo(bestIndices);
            }
        }

        if (ClusterFit(colors, bestIndices, bestEndpoints, ref bestError, ClusterOrderingsPerBlock))
        {
            ClusterFit(colors, bestIndices, bestEndpoints, ref bestError, 0);
        }

        SearchColorEndpoints(colors, bestEndpoints, ref bestError);
        MatchFourColor(colors, bestEndpoints, bestIndices);
        WriteFourColorBlock(bestEndpoints, bestIndices, dest);
    }

    internal const int EndpointSearchRounds = 4;

    internal static readonly (int First, int Second)[] SortNetwork = BuildSortNetwork();

    internal const int ClusterOrderingsPerBlock = 8;

    private static readonly short[] HistogramLookup = BuildHistogramLookup();

    internal static int HistogramIndex(int h0, int h1, int h2) => HistogramLookup[h0 + 17 * (h1 + 17 * h2)];

    private static short[] BuildHistogramLookup()
    {
        short[] lookup = new short[17 * 17 * 17];
        ReadOnlySpan<byte> histograms = ClusterTables.Histograms;

        for (int i = 0; i < histograms.Length / 4; i++)
        {
            lookup[histograms[i * 4] + 17 * (histograms[i * 4 + 1] + 17 * histograms[i * 4 + 2])] = (short)i;
        }

        return lookup;
    }

    private static (int, int)[] BuildSortNetwork()
    {
        var pairs = new List<(int, int)>();

        for (int p = 1; p < 16; p <<= 1)
        {
            for (int k = p; k >= 1; k >>= 1)
            {
                for (int j = k % p; j <= 15 - k; j += 2 * k)
                {
                    for (int i = 0; i <= Math.Min(k - 1, 15 - j - k); i++)
                    {
                        if ((i + j) / (2 * p) == (i + j + k) / (2 * p))
                        {
                            pairs.Add((i + j, i + j + k));
                        }
                    }
                }
            }
        }

        return [.. pairs];
    }

    internal static readonly int[][] GroupPatterns =
    [
        [0, 1], [0, 2], [0, 3], [1, 2], [1, 3], [2, 3],
        [0, 1, 2], [0, 1, 3], [0, 2, 3], [1, 2, 3],
        [0, 1, 2, 3],
    ];

    private static bool ClusterFit(ReadOnlySpan<int> colors, ReadOnlySpan<byte> indices, Span<int> endpoints, ref long error, int orderingCount)
    {
        Span<int> keys = stackalloc int[16];
        int dr = endpoints[3] - endpoints[0], dg = endpoints[4] - endpoints[1], db = endpoints[5] - endpoints[2];

        for (int i = 0; i < 16; i++)
        {
            keys[i] = ((colors[i * 3] * dr + colors[i * 3 + 1] * dg + colors[i * 3 + 2] * db) << 4) | i;
        }

        keys.Sort();
        Span<int> sorted = stackalloc int[48];
        Span<int> totals = stackalloc int[3];
        totals.Clear();

        for (int i = 0; i < 16; i++)
        {
            int pixel = keys[i] & 15;

            for (int c = 0; c < 3; c++)
            {
                sorted[i * 3 + c] = colors[pixel * 3 + c];
                totals[c] += colors[pixel * 3 + c];
            }
        }

        Span<int> fit = stackalloc int[6];
        endpoints.CopyTo(fit);
        long fitError = int.MaxValue;

        if (orderingCount > 0)
        {
            int count0 = 0, count1 = 0, count2 = 0;

            for (int i = 0; i < 16; i++)
            {
                count0 += indices[i] == 0 ? 1 : 0;
                count1 += indices[i] == 2 ? 1 : 0;
                count2 += indices[i] == 3 ? 1 : 0;
            }

            int start = HistogramIndex(count0, count1, count2) * ClusterTables.OrderingsPerHistogram;
            ReadOnlySpan<byte> histograms = ClusterTables.Histograms;

            for (int q = 0; q < orderingCount; q++)
            {
                int split = ClusterTables.BestOrderings[start + q] * 4;
                TrySplit(colors, sorted, totals, histograms[split], histograms[split + 1], histograms[split + 2], fit, ref fitError);
            }
        }

        Span<int> sizes = stackalloc int[4];
        int group = 0;
        sizes.Clear();
        sizes[0] = 1;

        for (int i = 1; i < 16; i++)
        {
            if (sorted[i * 3] != sorted[i * 3 - 3] || sorted[i * 3 + 1] != sorted[i * 3 - 2] || sorted[i * 3 + 2] != sorted[i * 3 - 1])
            {
                group++;
            }

            if (group < 4)
            {
                sizes[group]++;
            }
        }

        int distinct = group + 1;
        Span<int> h = stackalloc int[4];

        foreach (int[] pattern in GroupPatterns)
        {
            if (pattern.Length != distinct)
            {
                continue;
            }

            h.Clear();

            for (int j = 0; j < pattern.Length; j++)
            {
                h[pattern[j]] += sizes[j];
            }

            TrySplit(colors, sorted, totals, h[0], h[1], h[2], fit, ref fitError);
        }

        if (fitError >= error)
        {
            return false;
        }

        error = fitError;
        fit.CopyTo(endpoints);
        return true;
    }

    private static void TrySplit(ReadOnlySpan<int> colors, ReadOnlySpan<int> sorted, ReadOnlySpan<int> totals, int h0, int h1, int h2,
        Span<int> fit, ref long fitError)
    {
        int h3 = 16 - h0 - h1 - h2;
        int a2 = 9 * h0 + 4 * h1 + h2, b2 = h1 + 4 * h2 + 9 * h3, ab = 2 * (h1 + h2);
        int det = a2 * b2 - ab * ab;

        if (det == 0)
        {
            return;
        }

        Span<int> trial = stackalloc int[6];

        for (int c = 0; c < 3; c++)
        {
            int p1 = 0, p2 = 0, p3 = 0;

            for (int i = 0; i < 16; i++)
            {
                int value = sorted[i * 3 + c];
                p1 += i < h0 ? value : 0;
                p2 += i < h0 + h1 ? value : 0;
                p3 += i < h0 + h1 + h2 ? value : 0;
            }

            int ax = 3 * p1 + 2 * (p2 - p1) + (p3 - p2);
            int bx = (p2 - p1) + 2 * (p3 - p2) + 3 * (totals[c] - p3);
            int c0 = Math.Clamp((int)MathF.Round((float)(3 * (ax * b2 - bx * ab)) / det), 0, 255);
            int c1 = Math.Clamp((int)MathF.Round((float)(3 * (bx * a2 - ax * ab)) / det), 0, 255);
            trial[c] = c == 1 ? Expand6(Quantize(c0, 6)) : Expand5(Quantize(c0, 5));
            trial[3 + c] = c == 1 ? Expand6(Quantize(c1, 6)) : Expand5(Quantize(c1, 5));
        }

        long error = MatchFourColorError(colors, trial);

        if (error < fitError)
        {
            fitError = error;
            trial.CopyTo(fit);
        }
    }

    private static void SearchColorEndpoints(ReadOnlySpan<int> colors, Span<int> endpoints, ref long error)
    {
        Span<int> trial = stackalloc int[6];

        for (int round = 0; round < EndpointSearchRounds && error > 0; round++)
        {
            bool improved = false;

            for (int e = 0; e < 6; e++)
            {
                int bits = e % 3 == 1 ? 6 : 5;
                int max = (1 << bits) - 1;

                for (int delta = -1; delta <= 1; delta += 2)
                {
                    int level = Quantize(endpoints[e], bits) + delta;

                    if (level < 0 || level > max)
                    {
                        continue;
                    }

                    endpoints.CopyTo(trial);
                    trial[e] = bits == 6 ? Expand6(level) : Expand5(level);
                    long trialError = MatchFourColorError(colors, trial);

                    if (trialError < error)
                    {
                        error = trialError;
                        trial.CopyTo(endpoints);
                        improved = true;
                    }
                }
            }

            if (!improved)
            {
                break;
            }
        }
    }

    internal static bool TryWriteSolidColorBlock(ReadOnlySpan<byte> block, Span<byte> dest)
    {
        for (int i = 1; i < 16; i++)
        {
            if (block[i * 4] != block[0] || block[i * 4 + 1] != block[1] || block[i * 4 + 2] != block[2])
            {
                return false;
            }
        }

        WriteSolidColorBlock(block[0], block[1], block[2], dest);
        return true;
    }

    private static bool IsSolidColor(ReadOnlySpan<int> colors)
    {
        for (int i = 1; i < 16; i++)
        {
            if (colors[i * 3] != colors[0] || colors[i * 3 + 1] != colors[1] || colors[i * 3 + 2] != colors[2])
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteSolidColorBlock(int r, int g, int b, Span<byte> dest)
    {
        int c0 = (SingleColor5[r * 2] << 11) | (SingleColor6[g * 2] << 5) | SingleColor5[b * 2];
        int c1 = (SingleColor5[r * 2 + 1] << 11) | (SingleColor6[g * 2 + 1] << 5) | SingleColor5[b * 2 + 1];
        uint mask = 0xAAAAAAAA;

        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            mask = 0xFFFFFFFF;
        }
        else if (c0 == c1)
        {
            mask = 0;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], mask);
    }

    private static void FindPrincipalEndpoints(ReadOnlySpan<int> colors, Span<int> endpoints)
    {
        float meanR = 0, meanG = 0, meanB = 0;

        for (int i = 0; i < 16; i++)
        {
            meanR += colors[i * 3];
            meanG += colors[i * 3 + 1];
            meanB += colors[i * 3 + 2];
        }

        meanR /= 16f;
        meanG /= 16f;
        meanB /= 16f;

        float rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;

        for (int i = 0; i < 16; i++)
        {
            float r = colors[i * 3] - meanR;
            float g = colors[i * 3 + 1] - meanG;
            float b = colors[i * 3 + 2] - meanB;
            rr += r * r;
            rg += r * g;
            rb += r * b;
            gg += g * g;
            gb += g * b;
            bb += b * b;
        }

        float vr = rr, vg = gg, vb = bb;

        if (vr + vg + vb <= 0f)
        {
            vr = vg = vb = 1f;
        }

        for (int iteration = 0; iteration < 8; iteration++)
        {
            float nr = vr * rr + vg * rg + vb * rb;
            float ng = vr * rg + vg * gg + vb * gb;
            float nb = vr * rb + vg * gb + vb * bb;
            float length = MathF.Max(MathF.Max(MathF.Abs(nr), MathF.Abs(ng)), MathF.Abs(nb));

            if (length < 1e-6f)
            {
                break;
            }

            vr = nr / length;
            vg = ng / length;
            vb = nb / length;
        }

        int minIndex = 0, maxIndex = 0;
        float minDot = float.MaxValue, maxDot = float.MinValue;

        for (int i = 0; i < 16; i++)
        {
            float dot = colors[i * 3] * vr + colors[i * 3 + 1] * vg + colors[i * 3 + 2] * vb;

            if (dot < minDot)
            {
                minDot = dot;
                minIndex = i;
            }

            if (dot > maxDot)
            {
                maxDot = dot;
                maxIndex = i;
            }
        }

        endpoints[0] = colors[maxIndex * 3];
        endpoints[1] = colors[maxIndex * 3 + 1];
        endpoints[2] = colors[maxIndex * 3 + 2];
        endpoints[3] = colors[minIndex * 3];
        endpoints[4] = colors[minIndex * 3 + 1];
        endpoints[5] = colors[minIndex * 3 + 2];
    }

    private static void ExtrapolateEndpoints(Span<int> endpoints)
    {
        for (int c = 0; c < 3; c++)
        {
            int high = endpoints[c], low = endpoints[3 + c];
            endpoints[c] = Math.Clamp(2 * high - low, 0, 255);
            endpoints[3 + c] = Math.Clamp(2 * low - high, 0, 255);
        }
    }

    private static void FindBoundingBoxEndpoints(ReadOnlySpan<int> colors, Span<int> endpoints)
    {
        for (int c = 0; c < 3; c++)
        {
            int min = 255, max = 0;

            for (int i = 0; i < 16; i++)
            {
                min = Math.Min(min, colors[i * 3 + c]);
                max = Math.Max(max, colors[i * 3 + c]);
            }

            int inset = (max - min) >> 4;
            endpoints[c] = max - inset;
            endpoints[3 + c] = min + inset;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quantize(int value, int bits)
    {
        int max = (1 << bits) - 1;
        return Math.Clamp((value * max + 127) / 255, 0, max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Expand5(int value) => (value << 3) | (value >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Expand6(int value) => (value << 2) | (value >> 4);

    private static void QuantizeEndpoints(Span<int> endpoints)
    {
        for (int e = 0; e < 2; e++)
        {
            endpoints[e * 3] = Expand5(Quantize(Math.Clamp(endpoints[e * 3], 0, 255), 5));
            endpoints[e * 3 + 1] = Expand6(Quantize(Math.Clamp(endpoints[e * 3 + 1], 0, 255), 6));
            endpoints[e * 3 + 2] = Expand5(Quantize(Math.Clamp(endpoints[e * 3 + 2], 0, 255), 5));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pack565(int r, int g, int b)
    {
        return (Quantize(r, 5) << 11) | (Quantize(g, 6) << 5) | Quantize(b, 5);
    }

    private static long MatchFourColorError(ReadOnlySpan<int> colors, ReadOnlySpan<int> endpoints)
    {
        int r2 = (2 * endpoints[0] + endpoints[3]) / 3, g2 = (2 * endpoints[1] + endpoints[4]) / 3, b2 = (2 * endpoints[2] + endpoints[5]) / 3;
        int r3 = (endpoints[0] + 2 * endpoints[3]) / 3, g3 = (endpoints[1] + 2 * endpoints[4]) / 3, b3 = (endpoints[2] + 2 * endpoints[5]) / 3;
        long total = 0;

        for (int i = 0; i < 16; i++)
        {
            int r = colors[i * 3], g = colors[i * 3 + 1], b = colors[i * 3 + 2];
            int e0 = Square(r - endpoints[0]) + Square(g - endpoints[1]) + Square(b - endpoints[2]);
            int e1 = Square(r - endpoints[3]) + Square(g - endpoints[4]) + Square(b - endpoints[5]);
            int e2 = Square(r - r2) + Square(g - g2) + Square(b - b2);
            int e3 = Square(r - r3) + Square(g - g3) + Square(b - b3);
            total += Math.Min(Math.Min(e0, e1), Math.Min(e2, e3));
        }

        return total;
    }

    private static int Square(int value) => value * value;

    private static long MatchFourColor(ReadOnlySpan<int> colors, ReadOnlySpan<int> endpoints, Span<byte> indices)
    {
        Span<int> palette = stackalloc int[12];

        for (int c = 0; c < 3; c++)
        {
            int c0 = endpoints[c];
            int c1 = endpoints[3 + c];
            palette[c] = c0;
            palette[3 + c] = c1;
            palette[6 + c] = (2 * c0 + c1) / 3;
            palette[9 + c] = (c0 + 2 * c1) / 3;
        }

        long total = 0;

        for (int i = 0; i < 16; i++)
        {
            int r = colors[i * 3], g = colors[i * 3 + 1], b = colors[i * 3 + 2];
            int best = 0;
            int bestError = int.MaxValue;

            for (int p = 0; p < 4; p++)
            {
                int dr = r - palette[p * 3];
                int dg = g - palette[p * 3 + 1];
                int db = b - palette[p * 3 + 2];
                int error = dr * dr + dg * dg + db * db;

                if (error < bestError)
                {
                    bestError = error;
                    best = p;
                }
            }

            indices[i] = (byte)best;
            total += bestError;
        }

        return total;
    }

    private static bool RefineFourColor(ReadOnlySpan<int> colors, ReadOnlySpan<byte> indices, Span<int> endpoints)
    {
        ReadOnlySpan<float> weights = [1f, 0f, 2f / 3f, 1f / 3f];
        float aa = 0, bb = 0, ab = 0;
        float ar = 0, ag = 0, ab2 = 0, br = 0, bg = 0, bb2 = 0;

        for (int i = 0; i < 16; i++)
        {
            float a = weights[indices[i]];
            float b = 1f - a;
            aa += a * a;
            bb += b * b;
            ab += a * b;
            ar += a * colors[i * 3];
            ag += a * colors[i * 3 + 1];
            ab2 += a * colors[i * 3 + 2];
            br += b * colors[i * 3];
            bg += b * colors[i * 3 + 1];
            bb2 += b * colors[i * 3 + 2];
        }

        float det = aa * bb - ab * ab;

        if (MathF.Abs(det) < 1e-6f)
        {
            return false;
        }

        float inv = 1f / det;
        endpoints[0] = (int)MathF.Round((ar * bb - br * ab) * inv);
        endpoints[1] = (int)MathF.Round((ag * bb - bg * ab) * inv);
        endpoints[2] = (int)MathF.Round((ab2 * bb - bb2 * ab) * inv);
        endpoints[3] = (int)MathF.Round((br * aa - ar * ab) * inv);
        endpoints[4] = (int)MathF.Round((bg * aa - ag * ab) * inv);
        endpoints[5] = (int)MathF.Round((bb2 * aa - ab2 * ab) * inv);
        return true;
    }

    private static void WriteFourColorBlock(ReadOnlySpan<int> endpoints, ReadOnlySpan<byte> indices, Span<byte> dest)
    {
        int c0 = Pack565(endpoints[0], endpoints[1], endpoints[2]);
        int c1 = Pack565(endpoints[3], endpoints[4], endpoints[5]);
        uint mask = 0;

        for (int i = 0; i < 16; i++)
        {
            mask |= (uint)indices[i] << (i * 2);
        }

        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            mask ^= 0x55555555;
        }
        else if (c0 == c1)
        {
            mask = 0;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], mask);
    }

    private static void CompressThreeColorBlock(ReadOnlySpan<int> colors, ReadOnlySpan<bool> transparent, Span<byte> dest)
    {
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;

        for (int i = 0; i < 16; i++)
        {
            if (transparent[i])
            {
                continue;
            }

            minR = Math.Min(minR, colors[i * 3]);
            minG = Math.Min(minG, colors[i * 3 + 1]);
            minB = Math.Min(minB, colors[i * 3 + 2]);
            maxR = Math.Max(maxR, colors[i * 3]);
            maxG = Math.Max(maxG, colors[i * 3 + 1]);
            maxB = Math.Max(maxB, colors[i * 3 + 2]);
        }

        int c0 = Pack565(minR, minG, minB);
        int c1 = Pack565(maxR, maxG, maxB);

        if (c0 > c1)
        {
            (c0, c1) = (c1, c0);
        }

        Span<int> palette = stackalloc int[9];
        int r0 = Expand5(c0 >> 11), g0 = Expand6((c0 >> 5) & 63), b0 = Expand5(c0 & 31);
        int r1 = Expand5(c1 >> 11), g1 = Expand6((c1 >> 5) & 63), b1 = Expand5(c1 & 31);
        palette[0] = r0; palette[1] = g0; palette[2] = b0;
        palette[3] = r1; palette[4] = g1; palette[5] = b1;
        palette[6] = (r0 + r1) / 2; palette[7] = (g0 + g1) / 2; palette[8] = (b0 + b1) / 2;

        uint mask = 0;

        for (int i = 0; i < 16; i++)
        {
            int index = 3;

            if (!transparent[i])
            {
                int bestError = int.MaxValue;

                for (int p = 0; p < 3; p++)
                {
                    int dr = colors[i * 3] - palette[p * 3];
                    int dg = colors[i * 3 + 1] - palette[p * 3 + 1];
                    int db = colors[i * 3 + 2] - palette[p * 3 + 2];
                    int error = dr * dr + dg * dg + db * db;

                    if (error < bestError)
                    {
                        bestError = error;
                        index = p;
                    }
                }
            }

            mask |= (uint)index << (i * 2);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], mask);
    }

    private static byte[] BuildSingleColorTable(int bits)
    {
        int levels = 1 << bits;
        byte[] table = new byte[256 * 2];

        for (int value = 0; value < 256; value++)
        {
            int bestError = int.MaxValue;

            for (int a = 0; a < levels; a++)
            {
                int ea = bits == 5 ? Expand5(a) : Expand6(a);

                for (int b = 0; b < levels; b++)
                {
                    int eb = bits == 5 ? Expand5(b) : Expand6(b);
                    int interpolated = (2 * ea + eb) / 3;
                    int error = Math.Abs(interpolated - value) * 100 + Math.Abs(ea - eb);

                    if (error < bestError)
                    {
                        bestError = error;
                        table[value * 2] = (byte)a;
                        table[value * 2 + 1] = (byte)b;
                    }
                }
            }
        }

        return table;
    }

    #endregion

    #region Alpha (BC2 explicit, BC3/BC4 interpolated)

    internal static void CompressExplicitAlphaBlock(ReadOnlySpan<byte> block, Span<byte> dest)
    {
        for (int i = 0; i < 8; i++)
        {
            int a0 = (block[(i * 2) * 4 + 3] * 15 + 127) / 255;
            int a1 = (block[(i * 2 + 1) * 4 + 3] * 15 + 127) / 255;
            dest[i] = (byte)(a0 | (a1 << 4));
        }
    }

    internal static void CompressSingleChannelBlock(ReadOnlySpan<byte> block, int channel, Span<byte> dest)
    {
        Span<int> values = stackalloc int[16];
        int min = 255, max = 0;
        int innerMin = 255, innerMax = 0;

        for (int i = 0; i < 16; i++)
        {
            int value = block[i * 4 + channel];
            values[i] = value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);

            if (value != 0 && value != 255)
            {
                innerMin = Math.Min(innerMin, value);
                innerMax = Math.Max(innerMax, value);
            }
        }

        if (min == max)
        {
            dest[0] = (byte)min;
            dest[1] = (byte)min;
            dest[2..8].Clear();
            return;
        }

        Span<int> palette = stackalloc int[8];
        Span<byte> bestIndices = stackalloc byte[16];

        BuildEightValuePalette(max, min, palette);
        long bestError = MatchError(values, palette);
        int best0 = max, best1 = min;

        if (min == 0 || max == 255)
        {
            int lo = innerMin <= innerMax ? innerMin : min;
            int hi = innerMin <= innerMax ? innerMax : max;
            BuildSixValuePalette(lo, hi, palette);
            long error = MatchError(values, palette);

            if (error < bestError)
            {
                bestError = error;
                best0 = lo;
                best1 = hi;
            }
        }

        bool eightValues = best0 > best1;
        SearchAlphaEndpoints(values, ref best0, ref best1, ref bestError);
        SearchAlphaRange(values, ref best0, ref best1, ref bestError);

        if (eightValues)
        {
            BuildEightValuePalette(best0, best1, palette);
        }
        else
        {
            BuildSixValuePalette(best0, best1, palette);
        }

        MatchSingleChannel(values, palette, bestIndices);

        dest[0] = (byte)best0;
        dest[1] = (byte)best1;

        ulong bits = 0;

        for (int i = 0; i < 16; i++)
        {
            bits |= (ulong)bestIndices[i] << (i * 3);
        }

        for (int i = 0; i < 6; i++)
        {
            dest[2 + i] = (byte)(bits >> (i * 8));
        }
    }

    internal static void BuildEightValuePalette(int a0, int a1, Span<int> palette)
    {
        palette[0] = a0;
        palette[1] = a1;

        for (int i = 1; i < 7; i++)
        {
            palette[1 + i] = ((7 - i) * a0 + i * a1 + 3) / 7;
        }
    }

    internal static void BuildSixValuePalette(int a0, int a1, Span<int> palette)
    {
        palette[0] = a0;
        palette[1] = a1;

        for (int i = 1; i < 5; i++)
        {
            palette[1 + i] = ((5 - i) * a0 + i * a1 + 2) / 5;
        }

        palette[6] = 0;
        palette[7] = 255;
    }

    private static void SearchAlphaEndpoints(ReadOnlySpan<int> values, ref int a0, ref int a1, ref long error)
    {
        bool eightValues = a0 > a1;
        Span<int> palette = stackalloc int[8];

        for (int round = 0; round < EndpointSearchRounds && error > 0; round++)
        {
            bool improved = false;

            for (int e = 0; e < 2; e++)
            {
                for (int delta = -1; delta <= 1; delta += 2)
                {
                    int t0 = e == 0 ? a0 + delta : a0;
                    int t1 = e == 1 ? a1 + delta : a1;

                    if (t0 < 0 || t0 > 255 || t1 < 0 || t1 > 255 || (t0 > t1) != eightValues)
                    {
                        continue;
                    }

                    if (eightValues)
                    {
                        BuildEightValuePalette(t0, t1, palette);
                    }
                    else
                    {
                        BuildSixValuePalette(t0, t1, palette);
                    }

                    long trialError = MatchError(values, palette);

                    if (trialError < error)
                    {
                        error = trialError;
                        a0 = t0;
                        a1 = t1;
                        improved = true;
                    }
                }
            }

            if (!improved)
            {
                break;
            }
        }
    }

    internal const int AlphaSearchStep = 32;
    internal const int AlphaSearchRounds = 2;

    private static void SearchAlphaRange(ReadOnlySpan<int> values, ref int a0, ref int a1, ref long error)
    {
        bool eightValues = a0 > a1;
        Span<int> palette = stackalloc int[8];

        for (int step = AlphaSearchStep; step >= 1; step >>= 1)
        {
            for (int round = 0; round < AlphaSearchRounds && error > 0; round++)
            {
                bool improved = false;

                for (int d0 = -step; d0 <= step; d0 += step)
                {
                    for (int d1 = -step; d1 <= step; d1 += step)
                    {
                        int t0 = a0 + d0;
                        int t1 = a1 + d1;

                        if ((d0 == 0 && d1 == 0) || t0 < 0 || t0 > 255 || t1 < 0 || t1 > 255 || (t0 > t1) != eightValues)
                        {
                            continue;
                        }

                        if (eightValues)
                        {
                            BuildEightValuePalette(t0, t1, palette);
                        }
                        else
                        {
                            BuildSixValuePalette(t0, t1, palette);
                        }

                        long trialError = MatchError(values, palette);

                        if (trialError < error)
                        {
                            error = trialError;
                            a0 = t0;
                            a1 = t1;
                            improved = true;
                        }
                    }
                }

                if (!improved)
                {
                    break;
                }
            }
        }
    }

    private static long MatchError(ReadOnlySpan<int> values, ReadOnlySpan<int> palette)
    {
        long total = 0;

        for (int i = 0; i < 16; i++)
        {
            int nearest = Math.Abs(values[i] - palette[0]);

            for (int p = 1; p < 8; p++)
            {
                nearest = Math.Min(nearest, Math.Abs(values[i] - palette[p]));
            }

            total += nearest * nearest;
        }

        return total;
    }

    private static long MatchSingleChannel(ReadOnlySpan<int> values, ReadOnlySpan<int> palette, Span<byte> indices)
    {
        long total = 0;

        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            int bestError = int.MaxValue;

            for (int p = 0; p < 8; p++)
            {
                int diff = values[i] - palette[p];
                int error = diff * diff;

                if (error < bestError)
                {
                    bestError = error;
                    best = p;
                }
            }

            indices[i] = (byte)best;
            total += bestError;
        }

        return total;
    }

    #endregion
}
