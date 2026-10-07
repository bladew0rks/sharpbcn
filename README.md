# SharpBcn

Fast, high quality BC1–BC5 texture compression for .NET, written in plain C#.

- Formats: BC1 (DXT1, with optional 1-bit alpha), BC2 (DXT3), BC3 (DXT5), BC4 (ATI1N), BC5 (ATI2N)
- Encodes 16 blocks at once with AVX-512, 8 with AVX2, and falls back to scalar code on other CPUs.
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

## Performance

Inputs are CC0 textures, downloaded as 4K PNGs:

- [Leaf001](https://ambientcg.com/view?id=Leaf001) (ambientCG): Color, NormalDX, AmbientOcclusion, Opacity, Roughness
- [MetalPlates006](https://ambientcg.com/view?id=MetalPlates006) (ambientCG): Color, NormalDX, Metalness, Roughness
- [rusty_metal_02](https://polyhaven.com/a/rusty_metal_02) (Poly Haven): diff, nor_dx

BC1 is measured on the six color and normal maps (Leaf001's alpha removed), BC4 on the five single-channel maps.
Time is wall clock for the whole set including PNG loading, on a Ryzen 7 9800X3D using all 16 threads.
Quality is [SSIMULACRA 2](https://github.com/cloudinary/ssimulacra2) between the source and the decoded result,
averaged over the maps (higher is better).

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

Settings: `bc7enc -1 -L18`, `bc7enc -1 -L10` and `bc7enc -4`; `compressonatorcli -fd BC1|BC4 -Quality 1.0 -nomipmap`.
[icbc](https://github.com/castano/icbc) uses the D3D10 decoder, equal color weights, 3-color mode and 3-color black;
[stb_dxt](https://github.com/nothings/stb/blob/master/stb_dxt.h) uses `STB_DXT_HIGHQUAL`. icbc, stb_dxt and the
ISPC Texture Compressor are built with AVX-512 and run from 16 threads; icbc and stb_dxt load PNGs with stb_image.

Encode time without PNG loading, for the encoders that run in-process:

| Encoder | BC1 | BC4 |
|---|---|---|
| SharpBcn | 0.27 s | 0.06 s |
| icbc, level 9 | 1.80 s | |
| icbc, level 8 | 0.51 s | |
| stb_dxt | 0.10 s | 0.04 s |
| ISPC Texture Compressor | 0.02 s | 0.03 s |

## License

MIT, see [LICENSE](LICENSE). Includes tables from rgbcx; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
