# README demo: capture and encoding

The [demo and still gallery](../images/demo/README.md) show the app workflow.
This page describes how to reproduce the animation.

The current animation lasts **25.32 seconds**, half the original 50.64 seconds.
Every scene hold and transition was halved. All 14 source scenes and their captured
pixels are unchanged. Scientific comparisons use clean cuts.

## Capture provenance

The captures show the 0.2.1 UI-repair candidates on Ubuntu 24.04 under WSLg with
the experimental Mesa Dozen 25.2.8 GPU add-on. Stage and figure scenes came from
v2; source, processing-menu and processing scenes came from v3. Per-scene package
identities and original capture hashes are retained in the
[manifest](../images/demo/demo-manifest.json).

## Rebuild

From the repository root, with `Pillow==12.3.0` installed:

```sh
python scripts/create_demo.py docs/images/demo/storyboard.json --output-dir docs/images/demo --overwrite
python scripts/check_documentation.py
```

The [storyboard](../images/demo/storyboard.json) uses the gallery PNGs as portable
inputs. Each scene defines its source, caption and hold time in milliseconds.
Captions appear in the gallery. The encoder keeps the original dimensions and
colors, and verifies the decoded APNG holds against the source pixels.

For a new capture sequence:

1. Use public photographs from `test_images/`. Record the app version, platform
   and package manifest hash, including any experimental GPU add-on.
2. Select dark mode and keep a consistent window size. Capture the app in sRGB,
   excluding unrelated windows and private paths.
3. Capture loaded, detected, processing, stage-preview, figure and batch states.
   Expand analysis figures with the app's splitter.
4. Save full-color PNGs and list them in the storyboard. Hold times must be at
   least 1000 ms and multiples of 10 ms. Optional transitions are 0–250 ms;
   scientific comparisons and their adjacent scenes use clean cuts.

Example scene:

```json
{
  "id": "05-ffc",
  "source": "stills/05-ffc.png",
  "caption": "After flat-field correction",
  "duration_ms": 1750,
  "comparison": true
}
```

For an isolated dialog or popup, use `presentation: "dialog-closeup"` or
`"popup-closeup"` and declare a canvas large enough for the capture and its label.
The manifest records the capture rectangle; the gallery retains the original
capture dimensions. Per-scene `platform` and `package_manifest_sha256` values
override capture-level metadata.

The GIF uses per-frame 256-color palettes. Optional `quality_regions` map names
to `[left, top, right, bottom]` rectangles for separate palette-error measurements.
The APNG and gallery retain full-color pixels. After rebuilding, inspect playback
and text readability in Chromium and Firefox at desktop and narrow widths.

## Browser history

[Earlier browser observations](https://github.com/collinswakholi/ColorCorrectionPackage_UI/blob/b9d630ecdc8ec46307a3529d8467b53bc4f1bbc4/docs/images/demo/README.md#browser-verification--26-october-2026)
cover the original 50.64-second animation in Chromium 151 and Firefox 153.
The current manifest records the shortened animation's timing, hashes and
encoded-pixel comparisons.