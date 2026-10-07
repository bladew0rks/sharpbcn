using System.Buffers.Binary;

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
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static byte[] DecodeBc1(ReadOnlySpan<byte> data, int width, int height, bool oneBitAlpha = true)
    {
        return DecodeBlocks(data, width, height, 8, (block, pixels) => DecodeColorBlock(block, pixels, !oneBitAlpha));
    }

    public static byte[] DecodeBc2(ReadOnlySpan<byte> data, int width, int height)
    {
        return DecodeBlocks(data, width, height, 16, (block, pixels) =>
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
        return DecodeBlocks(data, width, height, 16, (block, pixels) =>
        {
            DecodeColorBlock(block[8..], pixels, true);
            DecodeSingleChannelBlock(block[..8], pixels, 3);
        });
    }

    public static byte[] DecodeBc4(ReadOnlySpan<byte> data, int width, int height)
    {
        return DecodeBlocks(data, width, height, 8, (block, pixels) =>
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
        return DecodeBlocks(data, width, height, 16, (block, pixels) =>
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

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<byte> pixels);

    private static unsafe byte[] DecodeBlocks(ReadOnlySpan<byte> data, int width, int height, int blockSize, BlockDecoder decoder)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);

        if (data.Length < blocksX * blocksY * blockSize)
        {
            throw new ArgumentException("Compressed buffer is too small for the image dimensions.", nameof(data));
        }

        byte[] output = new byte[width * height * 4];

        fixed (byte* dataPtr = data)
        {
            nint source = (nint)dataPtr;
            int sourceLength = data.Length;

            void DecodeRow(int by)
            {
                ReadOnlySpan<byte> src = new ReadOnlySpan<byte>((void*)source, sourceLength);
                Span<byte> pixels = stackalloc byte[64];

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

    private static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<byte> pixels, bool forceFourColor)
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

    private static void DecodeSingleChannelBlock(ReadOnlySpan<byte> block, Span<byte> pixels, int channel)
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
}
