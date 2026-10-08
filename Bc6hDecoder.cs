using System.Buffers.Binary;

namespace SharpBcn;

internal static class Bc6hDecoder
{
    internal readonly record struct ModeInfo(int Code, int Regions, bool Transformed, int EndpointBits, int DeltaRedBits, int DeltaGreenBits, int DeltaBlueBits)
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

    internal static readonly ModeInfo[] Modes =
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

    private const byte NoField = 0xFF;

    internal static int FindMode(int code)
    {
        for (int i = 0; i < Modes.Length; i++)
        {
            if (Modes[i].Code == code)
            {
                return i;
            }
        }

        return -1;
    }

    internal static int ReadModeCode(UInt128 bits) => (int)(ulong)(bits & 3) < 2 ? (int)(ulong)(bits & 3) : (int)(ulong)(bits & 31);

    internal static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    internal static int Unquantize(int value, int bits, bool signed)
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

    internal static Half ToHalf(int value, bool signed)
    {
        if (signed)
        {
            value = value < 0 ? -((-value * 31) >> 5) : (value * 31) >> 5;
            return BitConverter.UInt16BitsToHalf((ushort)(value < 0 ? 0x8000 | -value : value));
        }

        return BitConverter.UInt16BitsToHalf((ushort)((value * 31) >> 6));
    }

    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<Half> pixels, bool signed)
    {
        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(block);
        int mode = FindMode(ReadModeCode(bits));

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

        ModeInfo info = Modes[mode];
        ReadOnlySpan<byte> layout = Bc6hTables.HeaderBits.Slice(mode * 82, 82);
        Span<int> endpoints = stackalloc int[4 * 3];
        endpoints.Clear();
        int shape = 0;

        for (int i = 0; i < info.HeaderBits; i++)
        {
            byte field = layout[i];

            if (field == NoField || ((bits >> i) & 1) == 0)
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
                endpoints[e * 3 + channel] = Unquantize(endpoints[e * 3 + channel], info.EndpointBits, signed);
            }
        }

        int position = info.HeaderBits;

        for (int i = 0; i < 16; i++)
        {
            int indexBits = info.IndexBits - (Bc7Decoder.IsAnchor(info.Regions, shape, i) ? 1 : 0);
            int index = (int)(ulong)(bits >> position) & ((1 << indexBits) - 1);
            position += indexBits;

            int e0 = Bc7Decoder.Subset(info.Regions, shape, i) * 2 * 3;

            for (int channel = 0; channel < 3; channel++)
            {
                int value = Bc7Decoder.Interpolate(endpoints[e0 + channel], endpoints[e0 + 3 + channel], index, info.IndexBits);
                pixels[i * 4 + channel] = ToHalf(value, signed);
            }

            pixels[i * 4 + 3] = Half.One;
        }
    }
}
