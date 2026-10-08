using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpBcn;

public static class BcDecoder
{
    public static byte[] Decode(BcFormat format, ReadOnlySpan<byte> data, int width, int height) => format switch
    {
        BcFormat.Bc1 or BcFormat.Bc1Alpha => DecodeBc1(data, width, height),
        BcFormat.Bc2 => DecodeBc2(data, width, height),
        BcFormat.Bc3 => DecodeBc3(data, width, height),
        BcFormat.Bc4 => DecodeBc4(data, width, height),
        BcFormat.Bc5 => DecodeBc5(data, width, height),
        BcFormat.Bc6hUnsigned => MemoryMarshal.AsBytes(DecodeBc6h(data, width, height, false).AsSpan()).ToArray(),
        BcFormat.Bc6hSigned => MemoryMarshal.AsBytes(DecodeBc6h(data, width, height, true).AsSpan()).ToArray(),
        BcFormat.Bc7 => DecodeBc7(data, width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static byte[] DecodeBc1(ReadOnlySpan<byte> data, int width, int height, bool oneBitAlpha = true)
    {
        return DecodeBlocks<byte>(data, width, height, 8, (block, pixels) => DecodeColorBlock(block, pixels, !oneBitAlpha));
    }

    public static byte[] DecodeBc2(ReadOnlySpan<byte> data, int width, int height)
    {
        return DecodeBlocks<byte>(data, width, height, 16, (block, pixels) =>
        {
            DecodeColorBlock(block[8..], pixels, true);

            for (int i = 0; i < 16; i++)
            {
                int nibble = (block[i / 2] >> ((i & 1) * 4)) & 0xF;
                pixels[i * 4 + 3] = (byte)(nibble * 17);
            }
        });
    }

    public static byte[] DecodeBc3(ReadOnlySpan<byte> data, int width, int height)
    {
        return DecodeBlocks<byte>(data, width, height, 16, (block, pixels) =>
        {
            DecodeColorBlock(block[8..], pixels, true);
            DecodeSingleChannelBlock(block[..8], pixels, 3);
        });
    }

    public static byte[] DecodeBc4(ReadOnlySpan<byte> data, int width, int height)
    {
        return DecodeBlocks<byte>(data, width, height, 8, (block, pixels) =>
        {
            DecodeSingleChannelBlock(block, pixels, 0);

            for (int i = 0; i < 16; i++)
            {
                pixels[i * 4 + 1] = pixels[i * 4];
                pixels[i * 4 + 2] = pixels[i * 4];
                pixels[i * 4 + 3] = 255;
            }
        });
    }

    public static byte[] DecodeBc5(ReadOnlySpan<byte> data, int width, int height)
    {
        return DecodeBlocks<byte>(data, width, height, 16, (block, pixels) =>
        {
            DecodeSingleChannelBlock(block[..8], pixels, 0);
            DecodeSingleChannelBlock(block[8..], pixels, 1);

            for (int i = 0; i < 16; i++)
            {
                float x = pixels[i * 4] / 127.5f - 1f;
                float y = pixels[i * 4 + 1] / 127.5f - 1f;
                float z = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y));
                pixels[i * 4 + 2] = (byte)Math.Clamp((int)MathF.Round((z + 1f) * 127.5f), 0, 255);
                pixels[i * 4 + 3] = 255;
            }
        });
    }

    public static Half[] DecodeBc6h(ReadOnlySpan<byte> data, int width, int height, bool signed = false)
    {
        return DecodeBlocks<Half>(data, width, height, 16, (block, pixels) => DecodeBc6hBlock(block, pixels, signed));
    }

    public static byte[] DecodeBc7(ReadOnlySpan<byte> data, int width, int height)
    {
        return DecodeBlocks<byte>(data, width, height, 16, DecodeBc7Block);
    }

    private delegate void BlockDecoder<T>(ReadOnlySpan<byte> block, Span<T> pixels);

    private static unsafe T[] DecodeBlocks<T>(ReadOnlySpan<byte> data, int width, int height, int blockSize, BlockDecoder<T> decoder) where T : unmanaged
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);

        if (data.Length < blocksX * blocksY * blockSize)
        {
            throw new ArgumentException("Compressed buffer is too small for the image dimensions.", nameof(data));
        }

        T[] output = new T[width * height * 4];

        fixed (byte* dataPtr = data)
        {
            nint source = (nint)dataPtr;
            int sourceLength = data.Length;

            void DecodeRow(int by)
            {
                ReadOnlySpan<byte> src = new ReadOnlySpan<byte>((void*)source, sourceLength);
                Span<T> pixels = stackalloc T[64];

                for (int bx = 0; bx < blocksX; bx++)
                {
                    decoder(src.Slice((by * blocksX + bx) * blockSize, blockSize), pixels);

                    for (int y = 0; y < 4; y++)
                    {
                        int py = by * 4 + y;

                        if (py >= height)
                        {
                            break;
                        }

                        for (int x = 0; x < 4; x++)
                        {
                            int px = bx * 4 + x;

                            if (px >= width)
                            {
                                break;
                            }

                            pixels.Slice((y * 4 + x) * 4, 4).CopyTo(output.AsSpan((py * width + px) * 4, 4));
                        }
                    }
                }
            }

            if (blocksX * blocksY >= 1024)
            {
                Parallel.For(0, blocksY, DecodeRow);
            }
            else
            {
                for (int by = 0; by < blocksY; by++)
                {
                    DecodeRow(by);
                }
            }
        }

        return output;
    }

    internal static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<byte> pixels, bool forceFourColor)
    {
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        int c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

        Span<int> palette = stackalloc int[16];
        Unpack565(c0, palette[..4]);
        Unpack565(c1, palette[4..8]);

        if (c0 > c1 || forceFourColor)
        {
            for (int c = 0; c < 3; c++)
            {
                palette[8 + c] = (2 * palette[c] + palette[4 + c]) / 3;
                palette[12 + c] = (palette[c] + 2 * palette[4 + c]) / 3;
            }

            palette[11] = 255;
            palette[15] = 255;
        }
        else
        {
            for (int c = 0; c < 3; c++)
            {
                palette[8 + c] = (palette[c] + palette[4 + c]) / 2;
                palette[12 + c] = 0;
            }

            palette[11] = 255;
            palette[15] = 0;
        }

        for (int i = 0; i < 16; i++)
        {
            int index = (int)((mask >> (i * 2)) & 3);
            pixels[i * 4] = (byte)palette[index * 4];
            pixels[i * 4 + 1] = (byte)palette[index * 4 + 1];
            pixels[i * 4 + 2] = (byte)palette[index * 4 + 2];
            pixels[i * 4 + 3] = (byte)palette[index * 4 + 3];
        }
    }

    private static void Unpack565(int color, Span<int> rgba)
    {
        int r = (color >> 11) & 31;
        int g = (color >> 5) & 63;
        int b = color & 31;
        rgba[0] = (r << 3) | (r >> 2);
        rgba[1] = (g << 2) | (g >> 4);
        rgba[2] = (b << 3) | (b >> 2);
        rgba[3] = 255;
    }

    internal static void DecodeSingleChannelBlock(ReadOnlySpan<byte> block, Span<byte> pixels, int channel)
    {
        int a0 = block[0];
        int a1 = block[1];
        Span<int> palette = stackalloc int[8];

        if (a0 > a1)
        {
            BcEncoder.BuildEightValuePalette(a0, a1, palette);
        }
        else
        {
            BcEncoder.BuildSixValuePalette(a0, a1, palette);
        }

        ulong bits = 0;

        for (int i = 0; i < 6; i++)
        {
            bits |= (ulong)block[2 + i] << (i * 8);
        }

        for (int i = 0; i < 16; i++)
        {
            pixels[i * 4 + channel] = (byte)palette[(int)((bits >> (i * 3)) & 7)];
        }
    }

    #region BC6H

    internal readonly record struct Bc6hModeInfo(int Code, int Regions, bool Transformed, int EndpointBits, int DeltaRedBits, int DeltaGreenBits, int DeltaBlueBits)
    {
        public int IndexBits => Regions == 2 ? 3 : 4;

        public int HeaderBits => Regions == 2 ? 82 : 65;

        public int DeltaBits(int channel) => channel switch
        {
            0 => DeltaRedBits,
            1 => DeltaGreenBits,
            _ => DeltaBlueBits,
        };
    }

    internal static readonly Bc6hModeInfo[] Bc6hModes =
    [
        new(0x00, 2, true, 10, 5, 5, 5),
        new(0x01, 2, true, 7, 6, 6, 6),
        new(0x02, 2, true, 11, 5, 4, 4),
        new(0x06, 2, true, 11, 4, 5, 4),
        new(0x0A, 2, true, 11, 4, 4, 5),
        new(0x0E, 2, true, 9, 5, 5, 5),
        new(0x12, 2, true, 8, 6, 5, 5),
        new(0x16, 2, true, 8, 5, 6, 5),
        new(0x1A, 2, true, 8, 5, 5, 6),
        new(0x1E, 2, false, 6, 6, 6, 6),
        new(0x03, 1, false, 10, 10, 10, 10),
        new(0x07, 1, true, 11, 9, 9, 9),
        new(0x0B, 1, true, 12, 8, 8, 8),
        new(0x0F, 1, true, 16, 4, 4, 4),
    ];

    private const byte Bc6hNoField = 0xFF;

    internal static int FindBc6hMode(int code)
    {
        for (int i = 0; i < Bc6hModes.Length; i++)
        {
            if (Bc6hModes[i].Code == code)
            {
                return i;
            }
        }

        return -1;
    }

    internal static int ReadBc6hModeCode(UInt128 bits) => (int)(ulong)(bits & 3) < 2 ? (int)(ulong)(bits & 3) : (int)(ulong)(bits & 31);

    internal static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    internal static int UnquantizeBc6h(int value, int bits, bool signed)
    {
        if (signed)
        {
            if (bits >= 16)
            {
                return value;
            }

            int magnitude = Math.Abs(value);
            int result = magnitude == 0 ? 0 : magnitude >= (1 << (bits - 1)) - 1 ? 0x7FFF : ((magnitude << 15) + 0x4000) >> (bits - 1);
            return value < 0 ? -result : result;
        }

        if (bits >= 15 || value == 0)
        {
            return value;
        }

        return value == (1 << bits) - 1 ? 0xFFFF : ((value << 16) + 0x8000) >> bits;
    }

    internal static Half Bc6hToHalf(int value, bool signed)
    {
        if (signed)
        {
            value = value < 0 ? -((-value * 31) >> 5) : (value * 31) >> 5;
            return BitConverter.UInt16BitsToHalf((ushort)(value < 0 ? 0x8000 | -value : value));
        }

        return BitConverter.UInt16BitsToHalf((ushort)((value * 31) >> 6));
    }

    public static void DecodeBc6hBlock(ReadOnlySpan<byte> block, Span<Half> pixels, bool signed)
    {
        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(block);
        int mode = FindBc6hMode(ReadBc6hModeCode(bits));

        if (mode < 0)
        {
            for (int i = 0; i < 16; i++)
            {
                pixels[i * 4] = Half.Zero;
                pixels[i * 4 + 1] = Half.Zero;
                pixels[i * 4 + 2] = Half.Zero;
                pixels[i * 4 + 3] = Half.One;
            }

            return;
        }

        Bc6hModeInfo info = Bc6hModes[mode];
        ReadOnlySpan<byte> layout = Bc6hTables.HeaderBits.Slice(mode * 82, 82);
        Span<int> endpoints = stackalloc int[4 * 3];
        endpoints.Clear();
        int shape = 0;

        for (int i = 0; i < info.HeaderBits; i++)
        {
            byte field = layout[i];

            if (field == Bc6hNoField || ((bits >> i) & 1) == 0)
            {
                continue;
            }

            if (field >> 4 == 0)
            {
                shape |= 1 << (field & 15);
            }
            else
            {
                endpoints[(field >> 4) - 1] |= 1 << (field & 15);
            }
        }

        int endpointCount = info.Regions * 2;

        for (int channel = 0; channel < 3; channel++)
        {
            if (signed)
            {
                endpoints[channel] = SignExtend(endpoints[channel], info.EndpointBits);
            }

            for (int e = 1; e < endpointCount; e++)
            {
                int value = endpoints[e * 3 + channel];

                if (signed || info.Transformed)
                {
                    value = SignExtend(value, info.DeltaBits(channel));
                }

                if (info.Transformed)
                {
                    value = (value + endpoints[channel]) & ((1 << info.EndpointBits) - 1);

                    if (signed)
                    {
                        value = SignExtend(value, info.EndpointBits);
                    }
                }

                endpoints[e * 3 + channel] = value;
            }

            for (int e = 0; e < endpointCount; e++)
            {
                endpoints[e * 3 + channel] = UnquantizeBc6h(endpoints[e * 3 + channel], info.EndpointBits, signed);
            }
        }

        int position = info.HeaderBits;

        for (int i = 0; i < 16; i++)
        {
            int indexBits = info.IndexBits - (Bc7IsAnchor(info.Regions, shape, i) ? 1 : 0);
            int index = (int)(ulong)(bits >> position) & ((1 << indexBits) - 1);
            position += indexBits;

            int e0 = Bc7Subset(info.Regions, shape, i) * 2 * 3;

            for (int channel = 0; channel < 3; channel++)
            {
                int value = Bc7Interpolate(endpoints[e0 + channel], endpoints[e0 + 3 + channel], index, info.IndexBits);
                pixels[i * 4 + channel] = Bc6hToHalf(value, signed);
            }

            pixels[i * 4 + 3] = Half.One;
        }
    }

    #endregion

    #region BC7

    internal readonly record struct Bc7ModeInfo(int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits,
        int ColorBits, int AlphaBits, int EndpointPBits, int SharedPBits, int IndexBits, int SecondaryIndexBits);

    internal static readonly Bc7ModeInfo[] Bc7Modes =
    [
        new(3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
        new(2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
        new(3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
        new(2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
        new(1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
        new(1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
        new(1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
        new(2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
    ];

    private interface IBc7Mode
    {
        static abstract int Number { get; }
        static abstract Bc7ModeInfo Info { get; }
    }

    private struct Bc7Mode0 : IBc7Mode
    {
        public static int Number => 0;
        public static Bc7ModeInfo Info => new(3, 4, 0, 0, 4, 0, 1, 0, 3, 0);
    }

    private struct Bc7Mode1 : IBc7Mode
    {
        public static int Number => 1;
        public static Bc7ModeInfo Info => new(2, 6, 0, 0, 6, 0, 0, 1, 3, 0);
    }

    private struct Bc7Mode2 : IBc7Mode
    {
        public static int Number => 2;
        public static Bc7ModeInfo Info => new(3, 6, 0, 0, 5, 0, 0, 0, 2, 0);
    }

    private struct Bc7Mode3 : IBc7Mode
    {
        public static int Number => 3;
        public static Bc7ModeInfo Info => new(2, 6, 0, 0, 7, 0, 1, 0, 2, 0);
    }

    private struct Bc7Mode4 : IBc7Mode
    {
        public static int Number => 4;
        public static Bc7ModeInfo Info => new(1, 0, 2, 1, 5, 6, 0, 0, 2, 3);
    }

    private struct Bc7Mode5 : IBc7Mode
    {
        public static int Number => 5;
        public static Bc7ModeInfo Info => new(1, 0, 2, 0, 7, 8, 0, 0, 2, 2);
    }

    private struct Bc7Mode6 : IBc7Mode
    {
        public static int Number => 6;
        public static Bc7ModeInfo Info => new(1, 0, 0, 0, 7, 7, 1, 0, 4, 0);
    }

    private struct Bc7Mode7 : IBc7Mode
    {
        public static int Number => 7;
        public static Bc7ModeInfo Info => new(2, 6, 0, 0, 5, 5, 1, 0, 2, 0);
    }

    private struct BitReader(ReadOnlySpan<byte> block)
    {
        private ulong low = BinaryPrimitives.ReadUInt64LittleEndian(block);
        private ulong high = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            int value = (int)(low & ((1UL << count) - 1));
            low = (low >> count) | (high << (64 - count));
            high >>= count;
            return value;
        }
    }

    internal static int Bc7Subset(int subsets, int partition, int pixel) => subsets switch
    {
        2 => Bc7Tables.Partitions2[partition * 16 + pixel],
        3 => Bc7Tables.Partitions3[partition * 16 + pixel],
        _ => 0,
    };

    internal static bool Bc7IsAnchor(int subsets, int partition, int pixel) => pixel == 0 || subsets switch
    {
        2 => pixel == Bc7Tables.AnchorSecondSubset[partition],
        3 => pixel == Bc7Tables.AnchorThirdSubsetFirst[partition] || pixel == Bc7Tables.AnchorThirdSubsetSecond[partition],
        _ => false,
    };

    internal static int Bc7Interpolate(int e0, int e1, int index, int indexBits)
    {
        int weight = indexBits switch
        {
            2 => Bc7Tables.Weights2[index],
            3 => Bc7Tables.Weights3[index],
            _ => Bc7Tables.Weights4[index],
        };

        return ((64 - weight) * e0 + weight * e1 + 32) >> 6;
    }

    internal static int UnquantizeBc7(int value, int bits) => bits >= 8 ? value : (value << (8 - bits)) | (value >> (2 * bits - 8));

    public static void DecodeBc7Block(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        switch (System.Numerics.BitOperations.TrailingZeroCount(block[0] | 0x100))
        {
            case 0: DecodeBc7Block<Bc7Mode0>(block, pixels); break;
            case 1: DecodeBc7Block<Bc7Mode1>(block, pixels); break;
            case 2: DecodeBc7Block<Bc7Mode2>(block, pixels); break;
            case 3: DecodeBc7Block<Bc7Mode3>(block, pixels); break;
            case 4: DecodeBc7Block<Bc7Mode4>(block, pixels); break;
            case 5: DecodeBc7Block<Bc7Mode5>(block, pixels); break;
            case 6: DecodeBc7Block<Bc7Mode6>(block, pixels); break;
            case 7: DecodeBc7Block<Bc7Mode7>(block, pixels); break;
            default: pixels[..64].Clear(); break;
        }
    }

    internal static int Bc7BlockError(ReadOnlySpan<byte> block, ReadOnlySpan<byte> source, int limit)
    {
        switch (System.Numerics.BitOperations.TrailingZeroCount(block[0] | 0x100))
        {
            case 0: return Bc7BlockError<Bc7Mode0>(block, source, limit);
            case 1: return Bc7BlockError<Bc7Mode1>(block, source, limit);
            case 2: return Bc7BlockError<Bc7Mode2>(block, source, limit);
            case 3: return Bc7BlockError<Bc7Mode3>(block, source, limit);
            case 4: return Bc7BlockError<Bc7Mode4>(block, source, limit);
            case 5: return Bc7BlockError<Bc7Mode5>(block, source, limit);
            case 6: return Bc7BlockError<Bc7Mode6>(block, source, limit);
            case 7: return Bc7BlockError<Bc7Mode7>(block, source, limit);
        }

        int sum = 0;

        for (int i = 0; i < 64; i++)
        {
            sum += source[i] * source[i];
        }

        return sum;
    }

    private static void DecodeBc7Block<TMode>(ReadOnlySpan<byte> block, Span<byte> pixels) where TMode : IBc7Mode =>
        DecodeBc7<TMode, Bc7Pixels>(block, pixels, default, 0);

    private static int Bc7BlockError<TMode>(ReadOnlySpan<byte> block, ReadOnlySpan<byte> source, int limit) where TMode : IBc7Mode =>
        DecodeBc7<TMode, Bc7Error>(block, default, source, limit);

    private interface IBc7Output
    {
        static abstract bool Measure { get; }
    }

    private struct Bc7Pixels : IBc7Output
    {
        public static bool Measure => false;
    }

    private struct Bc7Error : IBc7Output
    {
        public static bool Measure => true;
    }

    [InlineArray(24)]
    private struct Bc7Endpoints
    {
        private int value;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int DecodeBc7<TMode, TOutput>(ReadOnlySpan<byte> block, Span<byte> pixels, ReadOnlySpan<byte> source, int limit)
        where TMode : IBc7Mode
        where TOutput : IBc7Output
    {
        Bc7ModeInfo info = TMode.Info;
        BitReader bits = new BitReader(block);
        bits.Read(TMode.Number + 1);
        int partition = bits.Read(info.PartitionBits);
        int rotation = bits.Read(info.RotationBits);
        int indexSelection = bits.Read(info.IndexSelectionBits);

        Bc7Endpoints endpointBuffer = default;
        Span<int> endpoints = endpointBuffer;

        for (int channel = 0; channel < 4; channel++)
        {
            int channelBits = channel < 3 ? info.ColorBits : info.AlphaBits;

            for (int e = 0; e < info.Subsets * 2; e++)
            {
                endpoints[e * 4 + channel] = channelBits == 0 ? 255 : bits.Read(channelBits);
            }
        }

        if (info.EndpointPBits != 0 || info.SharedPBits != 0)
        {
            int pBit = 0;

            for (int e = 0; e < info.Subsets * 2; e++)
            {
                if (info.EndpointPBits != 0 || e % 2 == 0)
                {
                    pBit = bits.Read(1);
                }

                for (int channel = 0; channel < (info.AlphaBits != 0 ? 4 : 3); channel++)
                {
                    endpoints[e * 4 + channel] = (endpoints[e * 4 + channel] << 1) | pBit;
                }
            }
        }

        int pBitExtra = info.EndpointPBits != 0 || info.SharedPBits != 0 ? 1 : 0;

        for (int e = 0; e < info.Subsets * 2; e++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                endpoints[e * 4 + channel] = UnquantizeBc7(endpoints[e * 4 + channel], info.ColorBits + pBitExtra);
            }

            if (info.AlphaBits != 0)
            {
                endpoints[e * 4 + 3] = UnquantizeBc7(endpoints[e * 4 + 3], info.AlphaBits + pBitExtra);
            }
        }

        ReadOnlySpan<byte> subsets = info.Subsets switch
        {
            2 => Bc7Tables.Partitions2.Slice(partition * 16, 16),
            3 => Bc7Tables.Partitions3.Slice(partition * 16, 16),
            _ => Bc7Tables.Partitions2[..16],
        };

        int anchor1 = info.Subsets switch
        {
            2 => Bc7Tables.AnchorSecondSubset[partition],
            3 => Bc7Tables.AnchorThirdSubsetFirst[partition],
            _ => -1,
        };

        int anchor2 = info.Subsets == 3 ? Bc7Tables.AnchorThirdSubsetSecond[partition] : -1;
        BitReader secondaryBits = bits;

        if (info.SecondaryIndexBits != 0)
        {
            secondaryBits.Read(16 * info.IndexBits - 1);
        }

        bool alphaFromSecondary = info.SecondaryIndexBits != 0 && indexSelection == 0;
        bool colorFromSecondary = info.SecondaryIndexBits != 0 && indexSelection != 0;
        ReadOnlySpan<byte> colorWeights = Bc7Weights(colorFromSecondary ? info.SecondaryIndexBits : info.IndexBits);
        ReadOnlySpan<byte> alphaWeights = Bc7Weights(alphaFromSecondary ? info.SecondaryIndexBits : info.IndexBits);
        int sum = 0;

        for (int i = 0; i < 16; i++)
        {
            int primary = bits.Read(info.IndexBits - (i == 0 || i == anchor1 || i == anchor2 ? 1 : 0));
            int secondary = info.SecondaryIndexBits != 0 ? secondaryBits.Read(info.SecondaryIndexBits - (i == 0 ? 1 : 0)) : primary;
            int e0 = info.Subsets == 1 ? 0 : subsets[i] * 8;
            int wc = colorWeights[colorFromSecondary ? secondary : primary];
            int wa = alphaWeights[alphaFromSecondary ? secondary : primary];
            int r = ((64 - wc) * endpoints[e0] + wc * endpoints[e0 + 4] + 32) >> 6;
            int g = ((64 - wc) * endpoints[e0 + 1] + wc * endpoints[e0 + 5] + 32) >> 6;
            int b = ((64 - wc) * endpoints[e0 + 2] + wc * endpoints[e0 + 6] + 32) >> 6;
            int a = ((64 - wa) * endpoints[e0 + 3] + wa * endpoints[e0 + 7] + 32) >> 6;

            if (rotation == 1)
            {
                (r, a) = (a, r);
            }
            else if (rotation == 2)
            {
                (g, a) = (a, g);
            }
            else if (rotation == 3)
            {
                (b, a) = (a, b);
            }

            if (TOutput.Measure)
            {
                int dr = source[i * 4] - r;
                int dg = source[i * 4 + 1] - g;
                int db = source[i * 4 + 2] - b;
                int da = source[i * 4 + 3] - a;
                sum += dr * dr + dg * dg + db * db + da * da;

                if (sum > limit)
                {
                    return sum;
                }
            }
            else
            {
                pixels[i * 4] = (byte)r;
                pixels[i * 4 + 1] = (byte)g;
                pixels[i * 4 + 2] = (byte)b;
                pixels[i * 4 + 3] = (byte)a;
            }
        }

        return sum;
    }

    private static ReadOnlySpan<byte> Bc7Weights(int bits) => bits switch
    {
        2 => Bc7Tables.Weights2,
        3 => Bc7Tables.Weights3,
        _ => Bc7Tables.Weights4,
    };

    #endregion
}
