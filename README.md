# SharpBcn

Fast, high quality BC1–BC7 texture compression for .NET, written in plain C#.

- Formats: BC1 (DXT1, with optional 1-bit alpha), BC2 (DXT3), BC3 (DXT5), BC4 (ATI1N), BC5 (ATI2N), BC6H (unsigned and signed) and BC7
- Encodes 16 blocks at once with AVX-512, 8 with AVX2, and falls back to scalar or 128-bit vector code on other CPUs.
- Every path produces the same output.
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

With `BcFormat.Bc1Alpha`, pixels below `alphaThreshold` (128 unless you pass something else) come out transparent.

BC7 takes the same RGBA bytes:

```csharp
byte[] bc7 = BcEncoder.EncodeBc7(rgba, width, height);
```

BC6H takes half floats, 4 per pixel (alpha is ignored), and decodes back to half floats:

```csharp
Half[] hdr = ...; // width * height * 4 halves, RGBA order

byte[] bc6h = BcEncoder.EncodeBc6h(hdr, width, height, signed: false);
Half[] back = BcDecoder.DecodeBc6h(bc6h, width, height, signed: false);
```

`BcEncoder.Encode` and `BcDecoder.Decode` also accept `BcFormat.Bc6hUnsigned` and `BcFormat.Bc6hSigned`, with the half floats passed as raw bytes.

## Performance

Test textures, all 4K PNGs:

- [Leaf001](https://ambientcg.com/view?id=Leaf001) (ambientCG): color, normal, AO, opacity, roughness
- [MetalPlates006](https://ambientcg.com/view?id=MetalPlates006) (ambientCG): color, normal, metalness, roughness
- [rusty_metal_02](https://polyhaven.com/a/rusty_metal_02) (Poly Haven): color, normal

Measurements:

- BC1 runs on the six color and normal maps, with Leaf001's alpha dropped
- BC7 runs on the same six maps, keeping Leaf001's alpha, and is scored on RGB with alpha listed separately
- BC4 runs on the five grayscale maps
- Time covers the whole set, PNG loading included, on a Ryzen 7 9800X3D using all 16 threads
- Quality is the [SSIMULACRA 2](https://github.com/cloudinary/ssimulacra2) score against the source, averaged over the maps, so higher is better

### BC1 and BC4

| Encoder | BC1 time | BC1 quality | BC4 time | BC4 quality |
|---|---|---|---|---|
| SharpBcn | 0.80 s | 83.18 | 0.34 s | 89.30 |
| rgbcx (bc7enc_rdo), level 18 | 9.54 s | 83.70 | 2.74 s | 88.50 |
| rgbcx (bc7enc_rdo), level 10 | 3.78 s | 82.93 | | |
| icbc, level 9 | 2.03 s | 82.82 | | |
| icbc, level 8 | 0.73 s | 81.50 | | |
| Compressonator 4.5.52, quality 1.0 | 2.92 s | 81.12 | 0.77 s | 89.21 |
| stb_dxt, high quality | 0.32 s | 80.70 | 0.18 s | 87.91 |
| ISPC Texture Compressor | 0.36 s | 77.84 | 0.21 s | 88.06 |

Settings used with each encoder:

- rgbcx: `bc7enc -1 -L18`, `bc7enc -1 -L10` and `bc7enc -4`
- Compressonator: `compressonatorcli -fd BC1` or `-fd BC4`, plus `-Quality 1.0 -nomipmap`
- [icbc](https://github.com/castano/icbc): D3D10 decoder, equal color weights, 3-color mode and 3-color black turned on
- [stb_dxt](https://github.com/nothings/stb/blob/master/stb_dxt.h): `STB_DXT_HIGHQUAL`
- icbc, stb_dxt and the ISPC Texture Compressor were built with AVX-512 and run on 16 threads
- icbc and stb_dxt read the PNGs through stb_image

### BC7

| Encoder | Time | SSIMULACRA 2 | RGB PSNR | Leaf001 alpha PSNR |
|---|---|---|---|---|
| SharpBcn | 3.01 s | 89.92 | 52.21 dB | 64.93 dB |
| bc7e (bc7enc_rdo), level 4 | 4.95 s | 89.93 | 52.14 dB | 56.32 dB |
| bc7e (bc7enc_rdo), level 2 | 3.72 s | 89.60 | 50.78 dB | 56.73 dB |
| ISPC Texture Compressor, slow | 16.08 s | 90.00 | 52.40 dB | 57.83 dB |
| ISPC Texture Compressor, basic | 4.97 s | 89.87 | 52.18 dB | 57.31 dB |
| ISPC Texture Compressor, veryfast | 1.35 s | 89.60 | 50.83 dB | 56.62 dB |

Settings used with each encoder:

- bc7e: `bc7enc -U -u4` and `-u2`, built with `SUPPORT_BC7E` and ISPC 1.31
- ISPC Texture Compressor: the `slow`, `basic` and `veryfast` profiles, using the `alpha_` variants for Leaf001's color map

### BC6H

Test images are three 2K HDRIs from Poly Haven: [kloppenheim_06](https://polyhaven.com/a/kloppenheim_06), [venice_sunset](https://polyhaven.com/a/venice_sunset) and [studio_small_09](https://polyhaven.com/a/studio_small_09).

Measurements:

- Time is encoding only, since the inputs are already half floats
- Half PSNR compares the half float bit patterns, which works out to roughly a log-space error
- mPSNR is the PSNR of 8-bit tonemapped images (gamma 2.2), averaged over exposures from -4 to +4 stops

| Encoder | Time | Half PSNR | mPSNR |
|---|---|---|---|
| SharpBcn | 0.15 s | 61.08 dB | 50.08 dB |
| ISPC Texture Compressor, veryslow | 0.77 s | 61.08 dB | 50.11 dB |
| ISPC Texture Compressor, slow | 0.34 s | 61.07 dB | 50.10 dB |
| ISPC Texture Compressor, basic | 0.08 s | 60.98 dB | 50.01 dB |
| ISPC Texture Compressor, fast | 0.03 s | 60.71 dB | 49.74 dB |
| ISPC Texture Compressor, veryfast | 0.01 s | 58.71 dB | 47.96 dB |

The ISPC Texture Compressor only does unsigned BC6H, so signed BC6H has no comparison here.

## License

MIT, see [LICENSE](LICENSE). Includes tables from rgbcx, bc7decomp and DirectXTex; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
