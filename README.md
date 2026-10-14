# Gsplat

[![Changelog](https://img.shields.io/badge/changelog-f15d30.svg)](./CHANGELOG.md) [![Version](https://img.shields.io/badge/version-v1.4.0-blue.svg)](https://github.com/wuyize25/gsplat-unity/releases/tag/v1.4.0) [![License](https://img.shields.io/badge/license-MIT-green.svg)](./LICENSE.md)

A Unity package for rendering [3D Gaussian Splatting](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/) (3DGS). Supports Unity 2021 and later. 

![lego](Documentation~/Images/lego.png)

The 3DGS rendering pipeline design of this package is inspired by [PlayCanvas](https://github.com/playcanvas/engine), which treats 3DGS objects similarly to transparent meshes that use a custom shader. With this approach, only an additional sorting pass needs to be inserted into each camera's command buffer. This design makes it easier to integrate 3DGS rendering into an existing pipeline, allows the draw calls for 3DGS objects to be correctly inserted into the existing rendering queue for transparent meshes (based on their bounding boxes), rather than rendering all 3DGS objects to a separate render texture as is done in [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting). 

That sounds great, but at what cost?

Most 3DGS assets are trained in Gamma space, following the official implementation. This means that the alpha blending for the Gaussians is also performed in Gamma space.  Since there is no longer an additional render texture that would allow us to convert the color space after the alpha blending of 3DGS, you must ensure your project's color space (`Edit > Project Settings > Player > Other Settings > Rendering > Color Space`) is set to "Gamma" for the 3DGS assets to be rendered correctly (be aware that HDRP doesn't support Gamma mode). For projects using a linear color space, you must retrain the 3DGS asset with linear-space images. While this plugin offers a `Gamma To Linear` option as a workaround, converting the color space before alpha blending leads to incorrect results and will lower the 3DGS rendering quality.

## Highlights

- Supports Built-in Render Pipeline (BiRP), URP and HDRP

- Gaussians can be correctly blended with transparent meshes based on their bounding boxes

- Supports reading & rendering PLY files with SH degrees 0-3

- Supports SPZ versions 1-4 with SH degrees 0-4

- Supports PlayCanvas SOG v2 ZIP bundles with SH bands 0-3

- Supports orthographic projection

- Compatible with MSAA

- Compatible with XR

  - | XR Render Mode        | BiRP | URP  | HDRP |
    | --------------------- | ---- | ---- | ---- |
    | Multi-pass            | ✓    | ✓    | ✗    |
    | Single Pass Instanced | ✗    | ✓    | ✗    |

- Cutouts to edit Gaussians Dynamically

- Supports global sorting

## Platform Compatibility

The sorting pass, built upon [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting), requires wave / subgroups operations which are only available in D3D12, Metal or Vulkan graphics APIs. WebGPU supports the subgroup operations but Unity has not implemented it. Anything using other graphics APIs will not work. I have only tested on Windows, Mac and Android, but the compatibility of this package should be similar to [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting).

## Usage

### Install

SOG support depends on `com.netpyoung.webp`, which is distributed through OpenUPM. Before installing Gsplat, add the following scoped registry to your project's `Packages/manifest.json`:

```json
"scopedRegistries": [
  {
    "name": "OpenUPM",
    "url": "https://package.openupm.com",
    "scopes": ["com.netpyoung.webp"]
  }
]
```

After cloning or downloading this repository, open your Unity project (or create a new one). Navigate to `Window > Package Manager`, click the `+` button, select `Install package from disk...`, and then choose the `package.json` file from this repository.

### Setup

First, ensure your project is using a supported Graphics API. For Windows: in `Edit > Project Settings > Player > Other Settings`, uncheck `Auto Graphics API for Windows`. Then, in the `Graphics APIs for Windows` list, add `Vulkan` or `Direct3D12` and remove any other options. Unity will require a restart to switch the Graphics API. You may need to perform similar steps for other platforms. 

Note that for Android, you also need to uncheck `Apply display rotation during rendering` in `Player > Settings for Android > Other Settings > Vulkan Settings`, as this package currently does not support rendering in the native display orientation.

The next steps depend on the Render Pipeline you are using:

- BiRP: Does not need any extra setup.
- URP: Add `Gsplat URP Feature` to the URP renderer settings.
  - Find the `Universal Renderer Data` your project is using (this will default to `Assets/Settings/PC_Renderer` in a new URP project), click the `Add Renderer Feature` button, and choose `Gsplat URP Feature.`
  - If you are using Unity 6 or later, the Render Graph "Compatibility Mode" in URP settings must be turned off! This is off by default in new URP projects.
- HDRP: Add `Custom Pass` volume object in your scene and a `Gsplat HDRP Pass` entry to it. The injection Point should be set to `Before Transparent`.

### Import Assets

Copy or drag & drop a PLY, SPZ, or SOG file anywhere into your project's `Assets` folder. The package will then automatically read the file and import it as a derived class of `Gsplat Asset`. The package supports two compression modes for the asset: `Uncompressed` and `Spark` (packed). The default mode is `Spark`, which is inspired by [spark.js](https://github.com/sparkjsdev/spark). You can change the compression mode in the inspector of the imported `Gsplat Asset`.

The `Opacity Prune Threshold` import option removes splats whose opacity (after sigmoid) is below the threshold at import time. Since overdraw of many overlapping translucent splats is the main rendering cost of Gaussian Splatting, pruning nearly transparent splats can significantly improve frame rate, and also reduces memory usage and load time. Set the slider in the inspector of the imported asset and press `Apply` to reimport; the Console then logs how many splats were pruned (e.g. `[Gsplat Import] scene.ply: pruned N / M splats (x.x%)`). Start around `0.01`–`0.05` and increase while checking the visual result (thin fog-like details disappear first — as a reference, a threshold of `0.05` removed about 40% of the splats on one of our scans with little visible change). Setting it back to `0` and reimporting fully restores the original data. Currently only supported for `.ply` import.

SOG import supports PlayCanvas SOG version 2 bundled `.sog` ZIP files containing `meta.json` and lossless WebP property images. Directory-style SOG datasets are not imported directly. SOG WebP decoding uses [`unity.webp`](https://github.com/netpyoung/unity.webp)'s native libwebp bindings, so the same decoder path is available for editor import and runtime loading on supported platforms including Android.

For runtime SOG loading, create a `GsplatAssetSog` or `GsplatAssetSogUncompressed` instance and call `LoadFromSog`, then assign it to a `GsplatRenderer`. Runtime SOG loading performs ZIP, JSON, WebP decode, and splat packing on-device; after loading, rendering uses the same Spark or Uncompressed pipeline as imported assets.

### Add Gsplat Renderer

Create or choose a game object in your scene, and add the `Gsplat Renderer` component on it. Point the `Gsplat Asset` field to one of your imported Gsplat Assets. Then it should appear in the viewport.

The `SH Degree` option sets the order of SH coefficients used for rendering. The final value is capped by the Gsplat Asset's `SH Bands`.

The `Gamma To Linear` option is offered as a workaround to render Gamma Space Gsplat Assets in a project using the Linear Space. This will degrade the rendering quality, so changing the color space of the project or retraining the 3DGS asset is the recommended approach. If your project uses a linear color space and you do not wish to retrain your 3DGS assets, it is recommended to use [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting).

The `Brightness` option allows post-hoc scaling of the Gsplat Asset's brightness. This is functional regardless of color space choices, but will degrade quality if the project and assets are in Gamma Space. This can be most smoothly controlled via the `Log Brightness` slider.

The `Async Upload` option enables streaming data from RAM to VRAM, which can help reduce lags when loading the `GsplatRenderer` or setting its enable property to true. When enabled, the renderer can optionally draw before upload completes (`Render Before Upload Complete`), which will render the asset with whatever data has been uploaded so far.

### Streamed LOD (SOG)

1. Make a LOD tree with PlayCanvas splat-transform 3.10.1 (`lod-meta.json` version 1):
   `splat-transform in.ply -d 25% lvl1.ply` (one run per coarser level), then
   `splat-transform in.ply -l 0 lvl1.ply -l 1 lvl2.ply -l 2 out/lod-meta.json`
2. Put `out/` under `Assets/StreamingAssets/` (or anywhere on disk).
3. Add `Gsplat Streamed Renderer` to a GameObject; set `Lod Meta Path` (relative to StreamingAssets or absolute) and
   `Source Coordinates`.
4. Budget and LOD distances: `Project Settings > Gsplat > Streaming`.
5. Optional: set `GsplatStreamingManager.LodCamera` when the LOD camera is not `Camera.main`.

All streamed renderers share one splat budget and one sorted draw. They are not depth-merged with ordinary
`Gsplat Renderer`s. Requires compute shaders. The draw is submitted just before `PostLateUpdate`: a manual
`Camera.Render()` from `Update`, `LateUpdate` or a coroutine misses it. The draw appears on every camera, regardless of layer or culling mask.

## Additional Documentation

- [Implementation Details](./Documentation~/Implementation%20Details.md)

## Project Using Gsplat

- [HiFi-Human/DynGsplat-unity](https://github.com/HiFi-Human/DynGsplat-unity) - A Unity package for rendering and playing dynamic gaussian splatting sequences

## License

This project is released under the MIT license. It is built upon several other open-source projects:

-   [playcanvas/engine](https://github.com/playcanvas/engine), MIT License (c) 2011-2024 PlayCanvas Ltd
-   [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting), MIT License (c) 2023 Aras Pranckevičius
-   [b0nes164/GPUSorting](https://github.com/b0nes164/GPUSorting), MIT License (c) 2024 Thomas Smith
-   [sparkjsdev/spark](https://github.com/sparkjsdev/spark), MIT License (c) 2025 WORLD LABS TECHNOLOGIES, INC.
-   [nianticlabs/spz](https://github.com/nianticlabs/spz), MIT License (c) 2024 Niantic Labs
-   [oleg-st/ZstdSharp](https://github.com/oleg-st/ZstdSharp), MIT License (c) 2021 Oleg Stepanischev
