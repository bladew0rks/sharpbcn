# SharpBcn

Fast, high quality BC1–BC5 texture compression for .NET, written in plain C#.

- Formats: BC1 (DXT1, with optional 1-bit alpha), BC2 (DXT3), BC3 (DXT5), BC4 (ATI1N), BC5 (ATI2N)
- Encodes 16 blocks at once with AVX-512, 8 with AVX2, and falls back to scalar code on other CPUs.
  All three paths produce byte-identical output.
- Multithreaded by default.
- No native dependencies.

## Usage

```csharp
using SharpBcn;

byte[] rgba = ...; // width * height * 4 bytes, RGBA order

byte[] dxt5 = BcEncoder.Encode(BcFormat.Bc3, rgba, width, height);
byte[] back = BcDecoder.Decode(BcFormat.Bc3, dxt5, width, height);

// Or write into your own buffer, e.g. one mip level of a larger file:
BcEncoder.Encode(BcFormat.Bc1, rgba, width, height, output.AsSpan(offset));
```

`BcFormat.Bc1Alpha` treats pixels with alpha below `alphaThreshold` (default 128) as transparent.
Pass `parallel: false` to encode on the calling thread only.

## How it works

Color blocks are fit from three starting candidates (principal axis, inset bounding box and an extended
principal axis), refined with least squares, then improved with cluster fit and a local endpoint search.
Blocks with four or fewer distinct colors try every ordered placement of those colors, so textures that
were already DXT-compressed once can usually be re-encoded without loss.

Alpha and single-channel blocks try both the 8-value and the 6-value (with fixed 0 and 255) modes, then a
coarse-to-fine search over endpoint pairs.

All error math is integer and scored against the exact palette a decoder produces.

## Performance

DXT5, 219 textures (623 megapixels), Ryzen 7 9800X3D, all cores, measured with SSIMULACRA 2
(higher is better):

| Encoder | Encode time | Color, fresh textures | Color, re-encoded DXT | Alpha |
|---|---|---|---|---|
| SharpBcn | 1.9 s | 84.07 | 94.67 | 94.7 |
| rgbcx, level 18 | ~51 s | 84.54 | 94.79 | 92.4 |
| Compressonator, quality 1.0 | ~18 s | 78.07 | 88.28 | 94.6 |
| ISPC TexComp | 0.16 s | 78.22 | 80.55 | 90.5 |

## License

MIT, see [LICENSE](LICENSE). Includes tables from rgbcx; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
