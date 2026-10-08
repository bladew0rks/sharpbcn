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

Test images:

- 4K textures:
  - [Leaf001](https://ambientcg.com/view?id=Leaf001) (ambientCG): color, normal, AO, opacity, roughness
  - [MetalPlates006](https://ambientcg.com/view?id=MetalPlates006) (ambientCG): color, normal, metalness, roughness
  - [rusty_metal_02](https://polyhaven.com/a/rusty_metal_02) (Poly Haven): color, normal
- [Kodak](https://r0k.us/graphics/kodak/): 24 photos, 768x512
- [CLIC 2020](https://www.compression.cc/) professional validation set: 41 photos, about 2 megapixels each, cropped to a multiple of 4

Measurements:

- BC1 and BC7 run on the photos and on the six texture color and normal maps
- BC1 drops Leaf001's alpha, BC7 keeps it and is scored on RGB
- BC4 runs on the five grayscale texture maps
- Time covers the whole set, PNG loading included, on a Ryzen 7 9800X3D using all 16 threads
- Kodak is too small for useful times, so it only has scores
- Quality is the [SSIMULACRA 2](https://github.com/cloudinary/ssimulacra2) score against the source, averaged over the images, so higher is better

### BC1

| Encoder | Textures time | Textures | Kodak | CLIC time | CLIC |
|---|---|---|---|---|---|
| SharpBcn | 0.83 s | 83.18 | 82.78 | 0.54 s | 83.33 |
| rgbcx (bc7enc_rdo), level 18 | 9.87 s | 83.70 | 83.04 | 8.63 s | 83.57 |
| rgbcx (bc7enc_rdo), level 10 | 4.02 s | 82.93 | 82.18 | 2.40 s | 82.68 |
| icbc, level 9 | 2.07 s | 82.82 | 82.62 | 2.16 s | 82.89 |
| icbc, level 8 | 0.77 s | 81.50 | 81.84 | 0.79 s | 81.95 |
| Compressonator 4.5.52, quality 1.0 | 3.15 s | 81.12 | 79.39 | 4.36 s | 80.31 |
| stb_dxt, high quality | 0.35 s | 80.70 | 79.55 | 0.26 s | 80.00 |
| ISPC Texture Compressor | 0.42 s | 77.84 | 77.55 | 0.24 s | 77.25 |
| BCnEncoder.NET 2.3.0, best quality | 8.39 s | 82.51 | 80.36 | 9.07 s | 82.28 |
| BCnEncoder.NET 2.3.0, balanced | 9.48 s | 75.40 | 80.53 | 9.48 s | 81.87 |
| BCnEncoder.NET 2.3.0, fast | 2.79 s | 69.50 | 67.89 | 2.90 s | 71.93 |

### BC4

| Encoder | Time | Quality |
|---|---|---|
| SharpBcn | 0.34 s | 89.30 |
| Compressonator 4.5.52, quality 1.0 | 0.77 s | 89.21 |
| rgbcx (bc7enc_rdo) | 2.74 s | 88.50 |
| ISPC Texture Compressor | 0.21 s | 88.06 |
| stb_dxt, high quality | 0.18 s | 87.91 |
| BCnEncoder.NET 2.3.0, best quality | 2.43 s | 89.18 |
| BCnEncoder.NET 2.3.0, fast | 1.80 s | 89.19 |

### BC7

| Encoder | Textures time | Textures | Kodak | CLIC time | CLIC |
|---|---|---|---|---|---|
| SharpBcn | 2.91 s | 89.92 | 92.25 | 2.58 s | 91.32 |
| bc7e (bc7enc_rdo), level 4 | 5.02 s | 89.93 | 92.38 | 3.38 s | 91.49 |
| bc7e (bc7enc_rdo), level 2 | 3.81 s | 89.60 | 92.12 | 2.07 s | 91.43 |
| bc7enc (bc7enc_rdo), level 4 | 7.55 s | 89.29 | 92.15 | 5.41 s | 91.41 |
| ISPC Texture Compressor, slow | 16.81 s | 90.00 | 92.34 | 16.61 s | 91.45 |
| ISPC Texture Compressor, basic | 5.09 s | 89.87 | 92.19 | 4.84 s | 91.36 |
| ISPC Texture Compressor, veryfast | 1.38 s | 89.60 | 92.08 | 1.21 s | 91.32 |
| Compressonator 4.5.52, quality 0.05 | 95.03 s | 89.41 | 92.16 | 193.65 s | 91.42 |
| Compressonator 4.5.52, quality 0.2 | | | 92.57 | 426.31 s | 91.66 |
| Compressonator 4.5.52, quality 0.5 | | | 92.68 | | |
| BCnEncoder.NET 2.3.0, best quality | | | 92.70 | | |
| BCnEncoder.NET 2.3.0, balanced | | | 92.45 | | |
| BCnEncoder.NET 2.3.0, fast | 112.44 s | 89.42 | 92.27 | 116.57 s | 91.50 |

On Leaf001's alpha channel SharpBcn reaches 64.9 dB PSNR, against 56.3 to 57.8 dB for bc7e and the ISPC Texture Compressor.

Settings used with each encoder:

- rgbcx: `bc7enc -1 -L18`, `bc7enc -1 -L10` and `bc7enc -4`
- bc7e: `bc7enc -U -u4` and `-u2`, built with `SUPPORT_BC7E` and ISPC 1.31
- bc7enc: `bc7enc -C -u4`
- Compressonator: `compressonatorcli -fd BC1`, `-fd BC4` or `-fd BC7`, plus `-Quality` and `-nomipmap`
- [icbc](https://github.com/castano/icbc): D3D10 decoder, equal color weights, 3-color mode and 3-color black turned on
- [stb_dxt](https://github.com/nothings/stb/blob/master/stb_dxt.h): `STB_DXT_HIGHQUAL`
- ISPC Texture Compressor: the default BC1 and BC4 encoders, and the `slow`, `basic` and `veryfast` BC7 profiles, using the `alpha_` variants for Leaf001's color map
- [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET): `Quality` set to `BestQuality`, `Balanced` or `Fast`, no mipmaps, parallel with 16 tasks
- icbc, stb_dxt and the ISPC Texture Compressor were built with AVX-512 and run on 16 threads
- icbc and stb_dxt read the PNGs through stb_image

### BC6H

Test images are three 2K HDRIs from Poly Haven: [kloppenheim_06](https://polyhaven.com/a/kloppenheim_06), [venice_sunset](https://polyhaven.com/a/venice_sunset) and [studio_small_09](https://polyhaven.com/a/studio_small_09).
The signed rows use the same images multiplied by a cosine wave, so every block mixes positive and negative values.

Measurements:

- Time is encoding only for SharpBcn, the ISPC Texture Compressor and BCnEncoder.NET, since the inputs are already half floats
- Compressonator's time is the whole command line run, from half float DDS files
- Half PSNR compares the half float bit patterns, which works out to roughly a log-space error
- mPSNR is the PSNR of 8-bit tonemapped images (gamma 2.2), averaged over exposures from -4 to +4 stops
- SSIMULACRA 2 is averaged over the same tonemapped images at -4, -2, 0, +2 and +4 stops

| Encoder | Time | Half PSNR | mPSNR | SSIMULACRA 2 |
|---|---|---|---|---|
| SharpBcn | 0.10 s | 61.08 dB | 50.08 dB | 91.06 |
| ISPC Texture Compressor, veryslow | 0.79 s | 61.08 dB | 50.11 dB | 91.08 |
| ISPC Texture Compressor, slow | 0.33 s | 61.07 dB | 50.10 dB | 91.08 |
| ISPC Texture Compressor, basic | 0.07 s | 60.98 dB | 50.01 dB | 91.09 |
| ISPC Texture Compressor, fast | 0.03 s | 60.71 dB | 49.74 dB | 90.85 |
| ISPC Texture Compressor, veryfast | 0.01 s | 58.71 dB | 47.96 dB | 88.50 |
| Compressonator 4.5.52, quality 1.0 | 159.78 s | 60.14 dB | 49.18 dB | 90.01 |
| Compressonator 4.5.52, quality 0.05 | 7.39 s | 60.14 dB | 49.18 dB | 90.03 |
| BCnEncoder.NET 2.3.0, best quality | 13.17 s | 58.54 dB | 48.10 dB | 89.08 |
| BCnEncoder.NET 2.3.0, balanced | 1.17 s | 57.57 dB | 47.04 dB | 87.32 |
| BCnEncoder.NET 2.3.0, fast | 0.21 s | 57.34 dB | 46.91 dB | 87.58 |
| SharpBcn, signed | 0.12 s | 53.03 dB | 46.96 dB | 89.41 |
| Compressonator 4.5.52, signed, quality 1.0 | 122.91 s | 52.52 dB | 46.58 dB | 84.32 |
| Compressonator 4.5.52, signed, quality 0.05 | 7.74 s | 52.52 dB | 46.55 dB | 84.06 |
| BCnEncoder.NET 2.3.0, signed, best quality | 12.95 s | 46.17 dB | 45.37 dB | 87.57 |
| BCnEncoder.NET 2.3.0, signed, balanced | 1.13 s | 45.64 dB | 44.39 dB | 85.53 |

The ISPC Texture Compressor only does unsigned BC6H. BCnEncoder.NET's signed BC6H at `Fast` produced unusable output (0.35 dB half PSNR), so it's left out.

## License

MIT, see [LICENSE](LICENSE). Includes tables from rgbcx, bc7decomp and DirectXTex; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
