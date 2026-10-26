"""Encode an authentic PNG capture storyboard; Pillow is development tooling only.

No resizing, color adjustment or fabricated UI is applied to captures. An explicit
dialog close-up can sit on a labeled neutral canvas; its captured pixels remain
untouched. APNG holds and gallery pixels are verified against their source pixels.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import tempfile

from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageSequence, PngImagePlugin, __version__ as pillow_version


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_storyboard(path: Path) -> tuple[dict, list[dict]]:
    story = json.loads(path.read_text(encoding="utf-8"))
    if story.get("source_color_space") != "srgb":
        raise ValueError("Declare source_color_space=srgb after checking capture color settings")
    canvas = story.get("canvas")
    if canvas is not None and (not isinstance(canvas, list) or len(canvas) != 2
                              or any(type(v) is not int or v < 1 for v in canvas)):
        raise ValueError("canvas must contain positive integer width and height")
    scenes, identifiers, size = [], set(), tuple(canvas) if canvas is not None else None
    for row in story.get("frames", []):
        name = row.get("id", "")
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", name) or name in identifiers:
            raise ValueError("Each frame needs a unique lowercase, hyphenated id")
        identifiers.add(name)
        source = Path(row["source"])
        source = source if source.is_absolute() else path.parent / source
        with Image.open(source) as opened:
            if opened.format != "PNG" or getattr(opened, "n_frames", 1) != 1:
                raise ValueError(f"Use a single captured PNG: {source.name}")
            if opened.mode not in ("RGB", "RGBA"):
                raise ValueError(f"Use full-color RGB captures: {source.name}")
            if opened.mode == "RGBA" and opened.getextrema()[3] != (255, 255):
                raise ValueError(f"Transparent captures require recapture: {source.name}")
            if opened.info.get("icc_profile"):
                raise ValueError("Use sRGB screenshot PNGs without an embedded competing ICC profile")
            if opened.info.get("gamma") is not None and abs(opened.info["gamma"] - 0.45455) > 0.0001:
                raise ValueError("Capture gamma differs from sRGB; recapture without color conversion")
            image = opened.convert("RGB")
        source_image = image
        presentation = row.get("presentation", "window")
        if presentation not in ("window", "dialog-closeup", "popup-closeup"):
            raise ValueError("presentation must be window, dialog-closeup or popup-closeup")
        capture_region = [0, 0, image.width, image.height]
        if presentation in ("dialog-closeup", "popup-closeup"):
            if size is None or image.width > size[0] or image.height + 80 > size[1]:
                raise ValueError("Dialog close-ups require a canvas with room for an outside close-up label")
            image = Image.new("RGB", size, (0, 0, 0))
            left, top = (size[0]-source_image.width)//2, (size[1]-source_image.height)//2
            image.paste(source_image, (left, top))
            kind = "popup" if presentation == "popup-closeup" else "dialog"
            caption = str(row.get("caption", name)) + f" (actual {kind} close-up)"
            ImageDraw.Draw(image).text((size[0]//2, 12), caption, anchor="mt",
                                      font=ImageFont.load_default(size=18), fill=(230, 230, 230))
            capture_region = [left, top, left+source_image.width, top+source_image.height]
        if size is None:
            size = image.size
        elif image.size != size:
            raise ValueError("Capture every scene at the same app-window size; do not composite dialogs")
        hold, transition = row.get("duration_ms"), row.get("transition_ms", 0)
        if type(hold) is not int or hold < 1000 or hold % 10:
            raise ValueError("duration_ms must be at least 1000 and a multiple of 10")
        if type(transition) is not int or transition < 0 or transition > 250 or transition % 10:
            raise ValueError("transition_ms must be 0–250 and a multiple of 10")
        if transition and (not scenes or row.get("comparison", False) or scenes[-1]["comparison"]):
            raise ValueError("Scientific comparison scenes require clean cuts; the first scene has no transition")
        regions = row.get("quality_regions", {})
        if not isinstance(regions, dict):
            raise ValueError("quality_regions must map names to pixel rectangles")
        for region, box in regions.items():
            if (not isinstance(region, str) or not isinstance(box, list) or len(box) != 4
                or any(type(v) is not int for v in box)
                or not 0 <= box[0] < box[2] <= image.width
                or not 0 <= box[1] < box[3] <= image.height):
                raise ValueError("Quality region rectangles must lie inside the captured frame")
        scenes.append({"id": name, "source": source, "image": image, "source_image": source_image,
            "presentation": presentation, "capture_region": capture_region, "quality_regions": regions,
            "platform": row.get("platform", story.get("capture", {}).get("platform")),
            "package_manifest_sha256": row.get("package_manifest_sha256", story.get("capture", {}).get("package_manifest_sha256")),
            "caption": str(row.get("caption", name)), "duration_ms": hold,
            "transition_ms": transition, "comparison": bool(row.get("comparison", False))})
    if len(scenes) < 2:
        raise ValueError("At least two actual application captures are required")
    if len({digest(scene["image"].tobytes()) for scene in scenes}) < 2:
        raise ValueError("An animation requires at least two distinct actual captures")
    total = sum(s["duration_ms"] + s["transition_ms"] for s in scenes)
    if not 45000 <= total <= 55000:
        raise ValueError("The complete demo must last 45–55 seconds")
    return story, scenes


def timeline(scenes: list[dict]) -> tuple[list[Image.Image], list[int], list[int]]:
    images, durations, hold_times = [], [], []
    elapsed = 0
    for index, scene in enumerate(scenes):
        transition = scene["transition_ms"]
        if transition:
            steps = max(1, transition // 40)
            unit, remainder = divmod(transition // 10, steps)
            for step in range(steps):
                images.append(Image.blend(scenes[index-1]["image"], scene["image"], (step+1)/(steps+1)))
                durations.append(10 * (unit + (step < remainder)))
            elapsed += transition
        images.append(scene["image"])
        durations.append(scene["duration_ms"])
        hold_times.append(elapsed + scene["duration_ms"] // 2)
        elapsed += scene["duration_ms"]
    return images, durations, hold_times


def decoded_holds(path: Path, times: list[int]) -> tuple[list[Image.Image], int, int]:
    holds, elapsed, frame_count = [], 0, 0
    with Image.open(path) as opened:
        for frame in ImageSequence.Iterator(opened):
            duration = round(frame.info.get("duration", 0))
            for sample in times:
                if elapsed <= sample < elapsed + duration:
                    holds.append(frame.convert("RGB").copy())
            elapsed += duration
            frame_count += 1
    if len(holds) != len(times):
        raise ValueError(f"Animation timing lost a hold frame: {path.name}")
    return holds, elapsed, frame_count


def difference(reference: Image.Image, actual: Image.Image) -> dict:
    histogram = ImageChops.difference(reference, actual).histogram()
    counts = [sum(histogram[value + channel * 256] for channel in range(3)) for value in range(256)]
    total = sum(counts)
    cumulative, percentile = 0, 0
    for value, count in enumerate(counts):
        cumulative += count
        if cumulative >= total * 0.95:
            percentile = value
            break
    return {"mean_absolute_channel_error_8bit": sum(value*count for value, count in enumerate(counts))/total,
            "p95_absolute_channel_error_8bit": percentile,
            "max_absolute_channel_error_8bit": max(value for value, count in enumerate(counts) if count),
            "rgb_sha256": digest(actual.tobytes())}


def create_demo(storyboard: Path, output: Path, *, overwrite: bool = False) -> dict:
    story, scenes = load_storyboard(storyboard)
    images, durations, hold_times = timeline(scenes)
    output.mkdir(parents=True, exist_ok=True)
    names = ["studio-dark.png", "studio-dark.gif", "demo-manifest.json"]
    names += [f"stills/{s['id']}.png" for s in scenes]
    if not overwrite and any((output/name).exists() for name in names):
        raise FileExistsError("Demo artifacts already exist; use --overwrite for an intentional rebuild")
    png_info = PngImagePlugin.PngInfo()
    png_info.add(b"sRGB", b"\x00")
    with tempfile.TemporaryDirectory(prefix=".demo-build-", dir=output.parent) as temporary:
        stage = Path(temporary)
        (stage/"stills").mkdir()
        for scene in scenes:
            scene["source_image"].save(stage/f"stills/{scene['id']}.png", pnginfo=png_info, compress_level=9)
        images[0].save(stage/names[0], save_all=True, append_images=images[1:],
            duration=durations, loop=0, disposal=0, blend=0, pnginfo=png_info, compress_level=9)
        # Local full-size palettes avoid forcing unrelated figures/photo colors
        # into a single palette. Quantization remains explicitly lossy.
        palette_frames = [im.quantize(palette=im.quantize(colors=256, method=Image.Quantize.MEDIANCUT),
                                     dither=Image.Dither.FLOYDSTEINBERG) for im in images]
        palette_frames[0].save(stage/names[1], save_all=True, append_images=palette_frames[1:],
            duration=durations, loop=0, disposal=1, optimize=True, include_color_table=True)
        apng, png_duration, png_frames = decoded_holds(stage/names[0], hold_times)
        gif, gif_duration, gif_frames = decoded_holds(stage/names[1], hold_times)
        if png_duration != sum(durations) or gif_duration != png_duration:
            raise ValueError("Encoded animation duration differs from the storyboard")
        records = []
        for scene, png_hold, gif_hold in zip(scenes, apng, gif):
            source = scene["image"]
            if source.tobytes() != png_hold.tobytes():
                raise ValueError(f"APNG changed captured hold pixels: {scene['id']}")
            with Image.open(stage/f"stills/{scene['id']}.png") as still:
                if scene["source_image"].tobytes() != still.convert("RGB").tobytes():
                    raise ValueError(f"Gallery changed captured pixels: {scene['id']}")
            if png_hold.crop(scene["capture_region"]).tobytes() != scene["source_image"].tobytes():
                raise ValueError(f"APNG changed captured dialog pixels: {scene['id']}")
            records.append({"id": scene["id"], "caption": scene["caption"],
                "source_name": scene["source"].name, "source_sha256": digest(scene["source"].read_bytes()),
                "source_rgb_sha256": digest(scene["source_image"].tobytes()),
                "rendered_rgb_sha256": digest(source.tobytes()), "presentation": scene["presentation"],
                "capture_region": scene["capture_region"], "captured_pixels_exact": True,
                "platform": scene["platform"], "package_manifest_sha256": scene["package_manifest_sha256"],
                "duration_ms": scene["duration_ms"],
                "transition_ms": scene["transition_ms"], "comparison": scene["comparison"],
                "apng_hold_rgb_exact": True, "gallery_rgb_exact": True,
                "gif_pixel_difference": difference(source, gif_hold),
                "gif_region_differences": {name: {"box": box, **difference(source.crop(box), gif_hold.crop(box))}
                                           for name, box in scene["quality_regions"].items()}})
        capture = {key: story.get("capture", {}).get(key) for key in
                   ("application_version", "platform", "package_source_commit", "package_manifest_sha256", "fixture")
                   if story.get("capture", {}).get(key) is not None}
        report = {"source_color_space": "srgb", "capture": capture,
            "encoder": {"script_sha256": digest(Path(__file__).read_bytes()), "python": sys.version,
                        "pillow": pillow_version, "apng_compression_level": 9,
                        "gif_palette": "per-frame median cut, 256 colors, Floyd-Steinberg dither"},
            "storyboard_sha256": digest(storyboard.read_bytes()), "dimensions": list(images[0].size),
            "total_duration_ms": png_duration, "loop": "continuous",
            "apng_frames": png_frames, "gif_frames": gif_frames,
            "apng_hold_pixels_exact": True, "gallery_pixels_exact": True,
            "gif_quality_note": "Encoded sRGB pixel differences measure palette loss, not physical color accuracy.",
            "frames": records, "artifacts": {name: {"bytes": (stage/name).stat().st_size,
                "sha256": digest((stage/name).read_bytes())} for name in names if name != "demo-manifest.json"}}
        (stage/"demo-manifest.json").write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
        for name in names:
            destination = output/name
            destination.parent.mkdir(parents=True, exist_ok=True)
            (stage/name).replace(destination)
    readme = output/"README.md"
    if readme.is_file():
        text = readme.read_text(encoding="utf-8")
        start, end = "<!-- demo-gallery:start -->", "<!-- demo-gallery:end -->"
        if start in text and end in text:
            gallery = "\n\n".join(f"### {s['caption']}\n\n![{s['caption']}](stills/{s['id']}.png)" for s in scenes)
            before, rest = text.split(start, 1)
            _, after = rest.split(end, 1)
            readme.write_text(before+start+"\n\n"+gallery+"\n\n"+end+after, encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("storyboard", type=Path)
    parser.add_argument("--output-dir", type=Path, default=Path("docs/images/demo"))
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()
    report = create_demo(args.storyboard.resolve(), args.output_dir.resolve(), overwrite=args.overwrite)
    print(json.dumps({key: report[key] for key in ("dimensions", "total_duration_ms", "apng_frames", "gif_frames", "apng_hold_pixels_exact", "artifacts")}, indent=2))


if __name__ == "__main__":
    main()
