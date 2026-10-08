using System.Buffers.Binary;

namespace SharpBcn;

internal static class Bc7Decoder
{
    internal readonly record struct ModeInfo(int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits,
        int ColorBits, int AlphaBits, int EndpointPBits, int SharedPBits, int IndexBits, int SecondaryIndexBits);

    internal static readonly ModeInfo[] Modes =
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

    private ref struct BitReader(ReadOnlySpan<byte> block)
    {
        private readonly ulong low = BinaryPrimitives.ReadUInt64LittleEndian(block);
        private readonly ulong high = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        private int position;

        public int Read(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            ulong value = position >= 64 ? high >> (position - 64) : (low >> position) | (position == 0 ? 0 : high << (64 - position));
            position += count;
            return (int)(value & ((1UL << count) - 1));
        }
    }

    internal static int Subset(int subsets, int partition, int pixel) => subsets switch
    {
        2 => Bc7Tables.Partitions2[partition * 16 + pixel],
        3 => Bc7Tables.Partitions3[partition * 16 + pixel],
        _ => 0,
    };

    internal static bool IsAnchor(int subsets, int partition, int pixel) => pixel == 0 || subsets switch
    {
        2 => pixel == Bc7Tables.AnchorSecondSubset[partition],
        3 => pixel == Bc7Tables.AnchorThirdSubsetFirst[partition] || pixel == Bc7Tables.AnchorThirdSubsetSecond[partition],
        _ => false,
    };

    internal static int Interpolate(int e0, int e1, int index, int indexBits)
    {
        int weight = indexBits switch
        {
            2 => Bc7Tables.Weights2[index],
            3 => Bc7Tables.Weights3[index],
            _ => Bc7Tables.Weights4[index],
        };

        return ((64 - weight) * e0 + weight * e1 + 32) >> 6;
    }

    internal static int Unquantize(int value, int bits) => bits >= 8 ? value : (value << (8 - bits)) | (value >> (2 * bits - 8));

    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        int mode = 0;

        while (mode < 8 && (block[0] & (1 << mode)) == 0)
        {
            mode++;
        }

        if (mode == 8)
        {
            pixels[..64].Clear();
            return;
        }

        ModeInfo info = Modes[mode];
        BitReader bits = new BitReader(block);
        bits.Read(mode + 1);
        int partition = bits.Read(info.PartitionBits);
        int rotation = bits.Read(info.RotationBits);
        int indexSelection = bits.Read(info.IndexSelectionBits);

        Span<int> endpoints = stackalloc int[3 * 2 * 4];

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
            Span<int> pBits = stackalloc int[6];

            for (int e = 0; e < info.Subsets * 2; e++)
            {
                pBits[e] = info.EndpointPBits != 0 ? bits.Read(1) : (e % 2 == 0 ? bits.Read(1) : pBits[e - 1]);
            }

            for (int e = 0; e < info.Subsets * 2; e++)
            {
                for (int channel = 0; channel < (info.AlphaBits != 0 ? 4 : 3); channel++)
                {
                    endpoints[e * 4 + channel] = (endpoints[e * 4 + channel] << 1) | pBits[e];
                }
            }
        }

        int pBitExtra = info.EndpointPBits != 0 || info.SharedPBits != 0 ? 1 : 0;

        for (int e = 0; e < info.Subsets * 2; e++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                endpoints[e * 4 + channel] = Unquantize(endpoints[e * 4 + channel], info.ColorBits + pBitExtra);
            }

            if (info.AlphaBits != 0)
            {
                endpoints[e * 4 + 3] = Unquantize(endpoints[e * 4 + 3], info.AlphaBits + pBitExtra);
            }
        }

        Span<int> primary = stackalloc int[16];
        Span<int> secondary = stackalloc int[16];

        for (int i = 0; i < 16; i++)
        {
            primary[i] = bits.Read(info.IndexBits - (IsAnchor(info.Subsets, partition, i) ? 1 : 0));
        }

        if (info.SecondaryIndexBits != 0)
        {
            for (int i = 0; i < 16; i++)
            {
                secondary[i] = bits.Read(info.SecondaryIndexBits - (i == 0 ? 1 : 0));
            }
        }

        for (int i = 0; i < 16; i++)
        {
            int subset = Subset(info.Subsets, partition, i);
            int e0 = subset * 2 * 4, e1 = e0 + 4;
            int colorIndex = primary[i], colorBits = info.IndexBits;
            int alphaIndex = primary[i], alphaBits = info.IndexBits;

            if (info.SecondaryIndexBits != 0)
            {
                if (indexSelection == 0)
                {
                    alphaIndex = secondary[i];
                    alphaBits = info.SecondaryIndexBits;
                }
                else
                {
                    colorIndex = secondary[i];
                    colorBits = info.SecondaryIndexBits;
                }
            }

            Span<byte> pixel = pixels.Slice(i * 4, 4);

            for (int channel = 0; channel < 3; channel++)
            {
                pixel[channel] = (byte)Interpolate(endpoints[e0 + channel], endpoints[e1 + channel], colorIndex, colorBits);
            }

            pixel[3] = (byte)Interpolate(endpoints[e0 + 3], endpoints[e1 + 3], alphaIndex, alphaBits);

            if (rotation != 0)
            {
                (pixel[rotation - 1], pixel[3]) = (pixel[3], pixel[rotation - 1]);
            }
        }
    }
}
