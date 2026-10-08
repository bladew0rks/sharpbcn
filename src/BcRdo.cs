using System.Runtime.CompilerServices;

namespace SharpBcn;

public static class BcRdo
{
    private const int LookbackBytes = 128;
    private const int ChunkBlocks = 4096;
    private const int MinMatchLength = 3;
    private const int HashSize = 8192;
    private const float LiteralBits = 13f;
    private const float MatchContinueBits = 1f;
    private const float MaxSmoothBlockStdDev = 18f;
    private const float MaxMseIncreaseRatio = 100f;
    private const float UltrasmoothStdDev = 2.9f;
    private const float UltrasmoothScale = 120f;
    private const int UltrasmoothMinRegion = 64;

    public static bool IsSupported(BcFormat format) => format is not (BcFormat.Bc6hUnsigned or BcFormat.Bc6hSigned);

    public static void Optimize(BcFormat format, ReadOnlySpan<byte> rgba, int width, int height, Span<byte> blocks, float lambda, int alphaThreshold = 128, bool parallel = true)
    {
        if (!IsSupported(format))
        {
            throw new NotSupportedException($"RDO is not supported for {format}.");
        }

        if (lambda <= 0f)
        {
            return;
        }

        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("Source buffer is smaller than width * height * 4.", nameof(rgba));
        }

        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        int count = blocksX * blocksY;
        int blockSize = BcEncoder.GetBlockSize(format);

        if (blocks.Length < count * blockSize)
        {
            throw new ArgumentException("Compressed buffer is too small for the image dimensions.", nameof(blocks));
        }

        byte[] pixels = GatherBlocks(rgba, width, height, blocksX, blocksY);
        float colorSmooth = Lerp(15f, 50f, Math.Min(1f, lambda / 8f));
        float channelSmooth = Lerp(10f, 30f, Math.Min(1f, lambda / 4f));

        switch (format)
        {
            case BcFormat.Bc1:
            case BcFormat.Bc1Alpha:
                SetAlpha(pixels, format == BcFormat.Bc1Alpha ? Math.Max(1, alphaThreshold) : 0);
                Run<Bc1Unpacker>(blocks, count, 8, 8, 0, pixels, [1, 1, 1, 1], 3, lambda, colorSmooth, UltrasmoothScales(pixels, blocksX, blocksY, colorSmooth, lambda), parallel);
                break;
            case BcFormat.Bc2:
                Run<Bc2AlphaUnpacker>(blocks, count, 16, 8, 0, pixels, [0, 0, 0, 1], 1, lambda, channelSmooth, null, parallel);
                Run<Bc1FourColorUnpacker>(blocks, count, 16, 8, 8, pixels, [1, 1, 1, 0], 3, lambda, colorSmooth, UltrasmoothScales(pixels, blocksX, blocksY, colorSmooth, lambda), parallel);
                break;
            case BcFormat.Bc3:
                Run<AlphaUnpacker>(blocks, count, 16, 8, 0, pixels, [0, 0, 0, 1], 1, lambda, channelSmooth, null, parallel);
                Run<Bc1FourColorUnpacker>(blocks, count, 16, 8, 8, pixels, [1, 1, 1, 0], 3, lambda, colorSmooth, UltrasmoothScales(pixels, blocksX, blocksY, colorSmooth, lambda), parallel);
                break;
            case BcFormat.Bc4:
                Run<RedUnpacker>(blocks, count, 8, 8, 0, pixels, [1, 0, 0, 0], 1, lambda, channelSmooth, null, parallel);
                break;
            case BcFormat.Bc5:
                Run<RedUnpacker>(blocks, count, 16, 8, 0, pixels, [1, 0, 0, 0], 1, lambda, channelSmooth, null, parallel);
                Run<GreenUnpacker>(blocks, count, 16, 8, 8, pixels, [0, 1, 0, 0], 1, lambda, channelSmooth, null, parallel);
                break;
            case BcFormat.Bc7:
                float bc7Smooth = Lerp(15f, 50f, Math.Min(1f, lambda / 4f));
                Run<Bc7Unpacker>(blocks, count, 16, 16, 0, pixels, [1, 1, 1, 1], 4, lambda, bc7Smooth, UltrasmoothScales(pixels, blocksX, blocksY, bc7Smooth, lambda), parallel);
                break;
        }
    }

    private interface IUnpacker
    {
        static abstract void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels);
    }

    private struct Bc1Unpacker : IUnpacker
    {
        public static void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels) => BcDecoder.DecodeColorBlock(block, pixels, false);
    }

    private struct Bc1FourColorUnpacker : IUnpacker
    {
        public static void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels) => BcDecoder.DecodeColorBlock(block, pixels, true);
    }

    private struct Bc2AlphaUnpacker : IUnpacker
    {
        public static void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels)
        {
            for (int i = 0; i < 16; i++)
            {
                pixels[i * 4 + 3] = (byte)(((block[i / 2] >> ((i & 1) * 4)) & 0xF) * 17);
            }
        }
    }

    private struct AlphaUnpacker : IUnpacker
    {
        public static void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels) => BcDecoder.DecodeSingleChannelBlock(block, pixels, 3);
    }

    private struct RedUnpacker : IUnpacker
    {
        public static void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels) => BcDecoder.DecodeSingleChannelBlock(block, pixels, 0);
    }

    private struct GreenUnpacker : IUnpacker
    {
        public static void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels) => BcDecoder.DecodeSingleChannelBlock(block, pixels, 1);
    }

    private struct Bc7Unpacker : IUnpacker
    {
        public static void Unpack(ReadOnlySpan<byte> block, Span<byte> pixels) => BcDecoder.DecodeBc7Block(block, pixels);
    }

    private static byte[] GatherBlocks(ReadOnlySpan<byte> rgba, int width, int height, int blocksX, int blocksY)
    {
        byte[] pixels = GC.AllocateUninitializedArray<byte>(blocksX * blocksY * 64);

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                BcEncoder.GatherBlock(rgba, width, height, bx * 4, by * 4, pixels.AsSpan((by * blocksX + bx) * 64, 64));
            }
        }

        return pixels;
    }

    private static void SetAlpha(byte[] pixels, int alphaThreshold)
    {
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = alphaThreshold == 0 || pixels[i] >= alphaThreshold ? (byte)255 : (byte)0;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static unsafe void Run<T>(Span<byte> blocks, int count, int stride, int size, int offset, byte[] pixels, int[] weights, int channels, float lambda, float smoothScale, float[]? blockScales, bool parallel)
        where T : struct, IUnpacker
    {
        int chunks = (count + ChunkBlocks - 1) / ChunkBlocks;

        fixed (byte* blocksPtr = blocks)
        {
            nint start = (nint)(blocksPtr + offset);

            void Chunk(int chunk)
            {
                int first = chunk * ChunkBlocks;
                OptimizeChunk<T>((byte*)start + first * stride, first, Math.Min(ChunkBlocks, count - first), stride, size, pixels, weights, channels, lambda, smoothScale, blockScales);
            }

            if (parallel && chunks > 1)
            {
                Parallel.For(0, chunks, Chunk);
            }
            else
            {
                for (int chunk = 0; chunk < chunks; chunk++)
                {
                    Chunk(chunk);
                }
            }
        }
    }

    private static unsafe void OptimizeChunk<T>(byte* blocks, int first, int count, int stride, int size, byte[] pixels, int[] weights, int channels, float lambda, float smoothScale, float[]? blockScales)
        where T : struct, IUnpacker
    {
        int window = Math.Max(1, LookbackBytes / stride);
        float inverseTotal = 1f / (16 * channels);
        uint* hash = stackalloc uint[HashSize];
        Span<byte> decoded = stackalloc byte[64];
        Span<byte> best = stackalloc byte[16];
        Span<byte> trial = stackalloc byte[16];
        Span<byte> single = stackalloc byte[16];
        int favorContinue = -1;

        for (int b = 0; b < count; b++)
        {
            if ((b & 0xFF) == 0)
            {
                new Span<uint>(hash, HashSize).Clear();
            }

            Span<byte> current = new Span<byte>(blocks + b * stride, size);
            ReadOnlySpan<byte> source = pixels.AsSpan((first + b) * 64, 64);

            T.Unpack(current, decoded);
            float currentMse = Error(source, decoded, weights) * inverseTotal;
            float scale = SmoothBlockScale(source, weights, smoothScale);

            if (blockScales != null && blockScales[first + b] > 0f)
            {
                scale = blockScales[first + b];
            }

            float currentCost = currentMse * scale + LiteralBits * size * lambda;
            float bestCost = currentCost;
            float threshold = MaxMseIncreaseRatio * Math.Max(currentMse, 1f);
            int bestLength = 0, bestOffset = 0, bestSource = 0;
            float bestMatchBits = 0f;
            current.CopyTo(best);

            for (int prev = b - 1; prev >= Math.Max(0, b - window); prev--)
            {
                byte* previous = blocks + prev * stride;
                int distance = (b - prev) * stride;
                UInt128 previousBits = size == 16 ? Unsafe.ReadUnaligned<UInt128>(previous) : Unsafe.ReadUnaligned<ulong>(previous);

                for (int length = size; length >= MinMatchLength; length--)
                {
                    float matchBits = MatchBits(distance, length);

                    for (int ofs = 0; ofs <= size - length; ofs++)
                    {
                        int sourceOffset = prev * stride + ofs;
                        float bits = matchBits;
                        uint h = Hash(previousBits, ofs, length);
                        ref uint slot = ref hash[h & (HashSize - 1)];

                        if (sourceOffset == favorContinue && ofs == 0)
                        {
                            bits = MatchContinueBits;
                        }
                        else if ((slot & 0xFF) == (uint)(b & 0xFF) && (slot >> 8) == (h >> 8))
                        {
                            continue;
                        }

                        slot = (h & 0xFFFFFF00) | (uint)(b & 0xFF);
                        float bitsCost = ((size - length) * LiteralBits + bits) * lambda;

                        if (bitsCost >= bestCost)
                        {
                            continue;
                        }

                        current.CopyTo(trial);
                        new ReadOnlySpan<byte>(previous + ofs, length).CopyTo(trial[ofs..]);
                        int limit = ErrorLimit(Math.Min(threshold, (bestCost - bitsCost) / scale), inverseTotal);
                        int sum = TrialError<T>(trial[..size], source, decoded, weights, limit);

                        if (sum > limit)
                        {
                            continue;
                        }

                        float mse = sum * inverseTotal;

                        if (mse >= threshold)
                        {
                            continue;
                        }

                        float cost = mse * scale + bitsCost;

                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            trial[..size].CopyTo(best);
                            bestLength = length;
                            bestOffset = ofs;
                            bestSource = sourceOffset;
                            bestMatchBits = bits;
                        }
                    }
                }
            }

            if (bestCost >= currentCost)
            {
                favorContinue = -1;
                continue;
            }

            int secondLength = 0, secondOffset = 0, secondSource = 0;

            if (bestLength <= size - MinMatchLength)
            {
                best[..size].CopyTo(single);

                for (int prev = b - 1; prev >= Math.Max(0, b - window); prev--)
                {
                    byte* previous = blocks + prev * stride;
                    int distance = (b - prev) * stride;

                    for (int length = MinMatchLength; length <= size - bestLength; length++)
                    {
                        float bitsLambda = ((size - length - bestLength) * LiteralBits + MatchBits(distance, length) + bestMatchBits) * lambda;

                        if (bitsLambda >= bestCost)
                        {
                            continue;
                        }

                        for (int ofs = 0; ofs <= size - length; ofs++)
                        {
                            if (ofs < bestOffset + bestLength && ofs + length > bestOffset)
                            {
                                continue;
                            }

                            single[..size].CopyTo(trial);
                            new ReadOnlySpan<byte>(previous + ofs, length).CopyTo(trial[ofs..]);
                            int limit = ErrorLimit(Math.Min(threshold, (bestCost - bitsLambda) / scale), inverseTotal);
                            int sum = TrialError<T>(trial[..size], source, decoded, weights, limit);

                            if (sum > limit)
                            {
                                continue;
                            }

                            float mse = sum * inverseTotal;

                            if (mse >= threshold)
                            {
                                continue;
                            }

                            float cost = mse * scale + bitsLambda;

                            if (cost < bestCost)
                            {
                                bestCost = cost;
                                trial[..size].CopyTo(best);
                                secondLength = length;
                                secondOffset = ofs;
                                secondSource = prev * stride + ofs;
                            }
                        }
                    }
                }
            }

            best[..size].CopyTo(current);

            if (size == stride)
            {
                bool useFirst = secondLength == 0 || bestOffset > secondOffset;
                int endOffset = useFirst ? bestOffset + bestLength : secondOffset + secondLength;
                favorContinue = endOffset == stride ? (useFirst ? bestSource + bestLength : secondSource + secondLength) : -1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TrialError<T>(ReadOnlySpan<byte> block, ReadOnlySpan<byte> source, Span<byte> decoded, int[] weights, int limit)
        where T : struct, IUnpacker
    {
        if (typeof(T) == typeof(Bc7Unpacker))
        {
            return BcDecoder.Bc7BlockError(block, source, limit);
        }

        T.Unpack(block, decoded);
        return Error(source, decoded, weights);
    }

    private static int ErrorLimit(float maxMse, float inverseTotal)
    {
        float limit = maxMse / inverseTotal;
        return limit >= int.MaxValue / 2 ? int.MaxValue / 2 : (int)limit + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Error(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int[] weights)
    {
        int w0 = weights[0], w1 = weights[1], w2 = weights[2], w3 = weights[3];
        int sum = 0;

        for (int i = 0; i < 64; i += 4)
        {
            int d0 = a[i] - b[i];
            int d1 = a[i + 1] - b[i + 1];
            int d2 = a[i + 2] - b[i + 2];
            int d3 = a[i + 3] - b[i + 3];
            sum += w0 * d0 * d0 + w1 * d1 * d1 + w2 * d2 * d2 + w3 * d3 * d3;
        }

        return sum;
    }

    private static float SmoothBlockScale(ReadOnlySpan<byte> source, int[] weights, float maxScale)
    {
        float maxStdDev = 0f;

        for (int c = 0; c < 4; c++)
        {
            if (weights[c] != 0)
            {
                maxStdDev = Math.Max(maxStdDev, StdDev(source, c));
            }
        }

        float t = Math.Clamp(maxStdDev / MaxSmoothBlockStdDev, 0f, 1f);
        return Lerp(maxScale, 1f, t * t);
    }

    private static float StdDev(ReadOnlySpan<byte> source, int channel)
    {
        long sum = 0, sum2 = 0;

        for (int i = 0; i < 16; i++)
        {
            int v = source[i * 4 + channel];
            sum += v;
            sum2 += v * v;
        }

        return MathF.Sqrt(16 * sum2 - sum * sum) / 16f;
    }

    private static float MatchBits(int distance, int length)
    {
        int lengthBits = length >= 12 ? 9 : length >= 8 ? 8 : length >= 6 ? 7 : 6;
        int distanceBits = 5 + (distance < 4 ? 0 : System.Numerics.BitOperations.Log2((uint)distance) - 1);
        return lengthBits + distanceBits;
    }

    private static uint Hash(UInt128 block, int offset, int length)
    {
        UInt128 bytes = block >> (offset * 8);

        if (length < 16)
        {
            bytes &= (UInt128.One << (length * 8)) - 1;
        }

        ulong h = ((ulong)bytes * 0x9E3779B97F4A7C15UL) ^ ((ulong)(bytes >> 64) * 0xC2B2AE3D27D4EB4FUL) ^ (ulong)(length << 8 | offset);
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        return (uint)h;
    }

    private static float[] UltrasmoothScales(byte[] pixels, int blocksX, int blocksY, float smoothScale, float lambda)
    {
        int count = blocksX * blocksY;
        byte[] map = new byte[count];

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> block = pixels.AsSpan(i * 64, 64);
            float maxStdDev = Math.Max(StdDev(block, 0), Math.Max(StdDev(block, 1), StdDev(block, 2)));
            float t = Math.Clamp(maxStdDev / UltrasmoothStdDev, 0f, 1f);
            t *= t;
            int lumaSum = 0;

            for (int p = 0; p < 16; p++)
            {
                lumaSum += (int)((13938u * block[p * 4] + 46869u * block[p * 4 + 1] + 4729u * block[p * 4 + 2] + 32768u) >> 16);
            }

            float luma = lumaSum / 16f;

            if (luma < 13f || luma >= 222f)
            {
                t = 1f;
            }

            map[i] = (byte)Math.Min((int)(t * 255f + 0.5f), 255);
        }

        byte[] next = new byte[count];

        for (int y = 0; y < blocksY; y++)
        {
            for (int x = 0; x < blocksX; x++)
            {
                next[y * blocksX + x] = NeighborsAtMax(map, blocksX, blocksY, x, y) > 0 ? (byte)255 : (byte)0;
            }
        }

        (map, next) = (next, map);

        for (int pass = 0; pass < 32; pass++)
        {
            map.CopyTo(next, 0);
            bool changed = false;

            for (int y = 0; y < blocksY; y++)
            {
                for (int x = 0; x < blocksX; x++)
                {
                    if (map[y * blocksX + x] < 255 && NeighborsAtMax(map, blocksX, blocksY, x, y) >= 5)
                    {
                        next[y * blocksX + x] = 255;
                        changed = true;
                    }
                }
            }

            (map, next) = (next, map);

            if (!changed)
            {
                break;
            }
        }

        bool[] visited = new bool[count];
        List<int> region = [];
        Stack<int> stack = new Stack<int>();
        float ultrasmooth = Math.Max(smoothScale, UltrasmoothScale * Math.Min(lambda, 3f));
        float[] scales = new float[count];
        Array.Fill(scales, -1f);

        for (int start = 0; start < count; start++)
        {
            if (map[start] != 0 || visited[start])
            {
                continue;
            }

            region.Clear();
            stack.Push(start);
            visited[start] = true;

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                region.Add(i);
                int x = i % blocksX, y = i / blocksX;

                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            if (region.Count >= UltrasmoothMinRegion)
            {
                foreach (int i in region)
                {
                    scales[i] = ultrasmooth;
                }
            }
        }

        return scales;

        void Visit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= blocksX || y >= blocksY)
            {
                return;
            }

            int i = y * blocksX + x;

            if (map[i] == 0 && !visited[i])
            {
                visited[i] = true;
                stack.Push(i);
            }
        }
    }

    private static int NeighborsAtMax(byte[] map, int blocksX, int blocksY, int x, int y)
    {
        int count = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int sx = Math.Clamp(x + dx, 0, blocksX - 1);
                int sy = Math.Clamp(y + dy, 0, blocksY - 1);
                count += map[sy * blocksX + sx] == 255 ? 1 : 0;
            }
        }

        return count;
    }
}
