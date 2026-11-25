# ColorCorrect Studio 0.5.0 tour

[Animated tour (lossless PNG)](studio.png) · [GIF version](studio.gif) · [Project README](../../../README.md)

The tour shows settled screens of the real 0.5.0 app on Windows, with the area that matters
in each step spotlighted at the app's own pixels. It uses the public photos in
[`test_images/`](../../../test_images). The animated PNG is lossless: every frame keeps the
app's exact colors. The GIF version is limited to 256 colors per frame, so its colors are
close but not exact. The screenshots below are full size.

## 1. Add photos and detect the chart

The Classic 24 chart is found and its 24 patches are outlined.

![The chart detected, with its patch overlay](stills/01-detected.png)

## 2. Processing settings

Auto times the CPU and the GPU on the first large image and uses the faster one.

![The Processing settings popup](stills/02-processing-settings.png)

## 3. Color correction settings

The model, its degree and the color spaces.

![The Color correction settings dialog](stills/03-cc-settings.png)

## 4. Flat-field correction

Blank.JPG, a photo of a blank white board, is the white reference that evens out the lighting.

![FFC on with Blank.JPG as the white reference](stills/04-ffc-white.png)

## 5. Fit + apply

Mean ΔE on the chart drops from 7.10 to 1.42.

![The corrected photo and its ΔE summary](stills/05-corrected.png)

## 6. Inspect each stage

Original, FFC, GC, WB and CC each show the photo after that stage. This is the FFC stage.

![The FFC stage](stills/06-stage-ffc.png)

## 7. Read and copy colors

Hovering shows RGB, hex and Lab under the pointer. Right-click copies them.

![The color readout under the pointer](stills/07-hover-readout.png)

![The right-click Copy menu](stills/08-right-click-copy.png)

## 8. Check the analysis

Drag the divider up to enlarge the plots.

![ΔE by stage](stills/09-analysis-delta-e.png)

![Color analysis: scatter and residuals](stills/10-analysis-color.png)

![Chart rendition: original and corrected against the reference](stills/11-analysis-rendition.png)

## 9. Batch

Apply the model to every other photo in the session.

![The Batch options](stills/12-batch-options.png)

## 10. The Guide

F1 opens the in-app Guide.

![The Guide's Get started topic](stills/13-guide.png)
