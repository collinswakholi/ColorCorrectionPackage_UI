# ColorCorrect Studio: the app in dark mode

[Animated PNG](studio-dark.png) · [GIF alternative](studio-dark.gif) · [Project README](../../../README.md)

These are captures of the running application using the public sample photographs.
The gallery below has no animation. Open a screenshot at full size to read small
controls and expanded figures.

The sequence shows locally validated **0.2.1 UI-repair candidates** on Ubuntu
24.04 under WSLg, using the separately inventoried experimental Mesa Dozen
25.2.8 GPU add-on. It combines actual v2 stage/figure captures with v3 source,
processing-menu and processing captures. The per-frame package identities are
recorded in the manifest; the published 0.2.0 downloads have not been replaced.

The animated PNG and still screenshots preserve the captured sRGB pixels. The
GIF uses a limited color palette; use the PNGs when inspecting colors. A visual
comparison illustrates the workflow and does not independently validate color
accuracy.

## Still screenshot gallery

<!-- demo-gallery:start -->

### A public chart photograph loaded

![A public chart photograph loaded](stills/01-photo.png)

### Detected chart and patch overlay

![Detected chart and patch overlay](stills/02-detected.png)

### Simple correction device and detection-engine settings

![Simple correction device and detection-engine settings](stills/03-processing-menu.png)

### Color-correction settings

![Color-correction settings](stills/03-cc-options.png)

### Actual correction in progress

![Actual correction in progress](stills/04-processing.png)

### Original photograph

![Original photograph](stills/05-original.png)

### After flat-field correction

![After flat-field correction](stills/06-ffc.png)

### After tone-response correction

![After tone-response correction](stills/07-gc.png)

### After white balance

![After white balance](stills/08-wb.png)

### Corrected result

![Corrected result](stills/09-corrected.png)

### Expanded color-difference analysis

![Expanded color-difference analysis](stills/10-deltae.png)

### Expanded color analysis

![Expanded color analysis](stills/11-color-analysis.png)

### Expanded chart rendition

![Expanded chart rendition](stills/12-rendition.png)

### Four-image batch with previews disabled

![Four-image batch with previews disabled](stills/13-batch-options.png)

<!-- demo-gallery:end -->

## Reproduce the animation

1. Use a verified app package, a clean profile, and public photographs from
   `test_images/`. Record the package identity and app version. On WSL, identify
   the separate experimental GPU add-on when used.
2. Select dark mode. Keep the same application-window size throughout. Capture
   the full application with dialogs in context; exclude desktop/VM chrome,
   private paths, and unrelated windows. Set capture/display output to sRGB.
3. Record actual loaded/detected/settings/processing/stage/figure/batch states.
   Expand analysis figures with the application's splitter. Capture completed
   output only after processing succeeds. Do not fabricate processing states.
4. Save RGB PNGs without resizing or color adjustments. Create a JSON storyboard
   with explicit sources, captions and hold times. Paths are relative to that
   storyboard unless absolute. Keep scientific stage comparisons as clean cuts.
5. Install `Pillow==12.3.0` in a development environment, then run:

```sh
python scripts/create_demo.py /path/to/storyboard.json --output-dir docs/images/demo
# Add --overwrite only for an intentional rebuild of existing demo files.
```

The checked-in [`storyboard.json`](storyboard.json) uses the lossless gallery
frames as portable inputs. It reproduces the displayed pixels and timings;
the manifest also retains the original capture-file hashes.

Example storyboard structure (add the remaining real captures to make a complete
45–55 second sequence):

```json
{
  "source_color_space": "srgb",
  "capture": {
    "application_version": "record-the-tested-version",
    "platform": "record-the-test-environment",
    "fixture": "ColorCard_Big.JPG"
  },
  "frames": [
    {
      "id": "01-loaded",
      "source": "captures/01-loaded.png",
      "caption": "A chart photograph loaded",
      "duration_ms": 4000
    },
    {
      "id": "02-detected",
      "source": "captures/02-detected.png",
      "caption": "The detected chart and patch overlay",
      "duration_ms": 4000,
      "transition_ms": 160
    },
    {
      "id": "05-ffc",
      "source": "captures/05-ffc.png",
      "caption": "After flat-field correction",
      "duration_ms": 3500,
      "comparison": true
    }
  ]
}
```

If a sequence includes captures from both guest systems, set `platform` and
`package_manifest_sha256` on each frame. Frame values override the corresponding
capture-level metadata; the manifest keeps the operating-system provenance
explicit without adding text to the application image.

Captions appear in this gallery; they are not overlaid on the application. When
an owned dialog cannot be captured together with its main window, an explicit
`"presentation": "dialog-closeup"` frame may place its untouched capture on a
plain black canvas. Set `"canvas": [1440, 900]` at the storyboard top level. A
label outside the dialog identifies this close-up; it does not pretend to be an
in-context app screenshot. The gallery retains the original dialog dimensions,
and the manifest records its exact capture rectangle and pixel verification.
Use `"presentation": "popup-closeup"` for an actual menu popup; the outside label
then identifies it as a popup rather than a dialog.

Short crossfades of up to 250 ms are optional between ordinary scenes. Scientific
comparison scenes and their adjacent transitions use clean cuts. The encoder
retains full-size source pixels, optimizes changed animation regions, and verifies
decoded APNG holds and gallery pixels against each source. It records source and
artifact hashes, dimensions, duration, and GIF palette error in
[`demo-manifest.json`](demo-manifest.json).

For a closer GIF check, add `"quality_regions": {"chart": [left, top, right,
bottom]}` to a frame using the chart's actual pixel rectangle. The manifest then
reports that region separately from the surrounding UI. These encoded-pixel
differences describe palette loss; they are not perceptual or physical accuracy
scores.

After encoding, inspect the actual GitHub README in Chromium and Firefox at
desktop and narrow widths. Check movement, text readability, and color consistency
against the lossless stills. Browser/render verification is separate from encoder
verification; consult the current validation evidence for its tested status.
