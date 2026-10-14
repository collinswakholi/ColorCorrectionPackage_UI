//! Load 8-bit stills (EXIF-oriented) and sample Opt2 `patch_obbs` (inner 0.70, 9×9).

use crate::color::{clamp01, Vec3};
use crate::refs::{ChartPatch, ChartSpec, Quad};
use crate::{Error, Result, SATURATION_THRESHOLD};
use image::{DynamicImage, ImageDecoder, RgbImage as Gray8};
use std::io::Cursor;
use std::path::Path;

const RAW_EXTS: &[&str] = &[
    "cr2", "cr3", "nef", "arw", "dng", "raf", "orf", "rw2", "pef", "srw", "raw", "3fr", "fff",
    "mef", "mos", "x3f", "erf", "kdc", "dcr", "mrw", "nrw", "sr2",
];

#[derive(Clone, Debug)]
pub struct RgbImage {
    pub width: u32,
    pub height: u32,
    pub rgb: Vec<f32>,
}

impl RgbImage {
    pub fn new(width: u32, height: u32, rgb: Vec<f32>) -> Result<Self> {
        let expected = width as usize * height as usize * 3;
        if rgb.len() != expected {
            return Err(Error::InvalidArgument(format!(
                "rgb buffer length {} != {expected}",
                rgb.len()
            )));
        }
        Ok(Self { width, height, rgb })
    }

    pub fn from_srgb_u8(img: &Gray8) -> Self {
        let raw = img.as_raw();
        let mut rgb = Vec::with_capacity(raw.len());
        for &b in raw {
            rgb.push(b as f32 * (1.0 / 255.0));
        }
        Self {
            width: img.width(),
            height: img.height(),
            rgb,
        }
    }

    pub fn view(&self) -> RgbView<'_> {
        RgbView {
            width: self.width,
            height: self.height,
            rgb: &self.rgb,
        }
    }

    pub fn pixel(&self, x: i32, y: i32) -> Vec3 {
        self.view().pixel(x, y)
    }
}

/// Contiguous f32 RGB view (H×W×3). Same bilinear math as [`RgbImage`].
#[derive(Clone, Copy, Debug)]
pub struct RgbView<'a> {
    pub width: u32,
    pub height: u32,
    pub rgb: &'a [f32],
}

impl<'a> RgbView<'a> {
    pub fn from_rgb(width: u32, height: u32, rgb: &'a [f32]) -> Result<Self> {
        let expected = width as usize * height as usize * 3;
        if rgb.len() != expected {
            return Err(Error::InvalidArgument(format!(
                "rgb buffer length {} != {expected}",
                rgb.len()
            )));
        }
        Ok(Self { width, height, rgb })
    }

    pub fn pixel(&self, x: i32, y: i32) -> Vec3 {
        let x = x.clamp(0, self.width as i32 - 1) as usize;
        let y = y.clamp(0, self.height as i32 - 1) as usize;
        let i = (y * self.width as usize + x) * 3;
        [
            self.rgb[i] as f64,
            self.rgb[i + 1] as f64,
            self.rgb[i + 2] as f64,
        ]
    }
}

#[derive(Clone, Copy, Debug)]
pub struct SamplingOptions {
    pub samples_per_axis: i32,
    pub inner_fraction: f64,
}

impl Default for SamplingOptions {
    fn default() -> Self {
        Self {
            samples_per_axis: 9,
            inner_fraction: 0.70,
        }
    }
}

pub fn load_image(path: impl AsRef<Path>) -> Result<RgbImage> {
    load_image_with_icc(path, true)
}

/// Orient pixels consistently and, for the sRGB working pipeline, honor the
/// embedded ICC profile. Explicit non-sRGB input encodings use unmanaged bytes.
pub fn load_image_with_icc(path: impl AsRef<Path>, honor_icc: bool) -> Result<RgbImage> {
    let path = path.as_ref();
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    if RAW_EXTS.contains(&ext.as_str()) {
        return Err(Error::UnsupportedRaw {
            path: path.display().to_string(),
            ext,
        });
    }
    let bytes = std::fs::read(path)?;
    if matches!(ext.as_str(), "tif" | "tiff") && tiff_is_16bit(&bytes) {
        return Err(Error::Unsupported16BitTiff {
            path: path.display().to_string(),
        });
    }
    let mut decoder = image::ImageReader::new(Cursor::new(bytes))
        .with_guessed_format()?
        .into_decoder()?;
    let orientation = decoder.orientation()?;
    let icc = if honor_icc {
        decoder.icc_profile()?
    } else {
        None
    };
    let mut loaded = DynamicImage::from_decoder(decoder)?;
    if matches!(
        loaded.color(),
        image::ColorType::L16
            | image::ColorType::La16
            | image::ColorType::Rgb16
            | image::ColorType::Rgba16
    ) {
        return Err(Error::Unsupported16BitTiff {
            path: path.display().to_string(),
        });
    }
    loaded.apply_orientation(orientation);
    let mut result = RgbImage::from_srgb_u8(&loaded.to_rgb8());
    if let Some(icc) = icc {
        let source = moxcms::ColorProfile::new_from_slice(&icc)
            .map_err(|e| Error::InvalidArgument(format!("Invalid embedded ICC profile: {e}")))?;
        let destination = moxcms::ColorProfile::new_srgb();
        let transform = source
            .create_transform_f32(
                moxcms::Layout::Rgb,
                &destination,
                moxcms::Layout::Rgb,
                moxcms::TransformOptions::default(),
            )
            .map_err(|e| {
                Error::InvalidArgument(format!("Cannot convert embedded ICC profile to sRGB: {e}"))
            })?;
        let mut converted = vec![0.0f32; result.rgb.len()];
        transform
            .transform(&result.rgb, &mut converted)
            .map_err(|e| Error::InvalidArgument(format!("ICC conversion to sRGB failed: {e}")))?;
        for value in &mut converted {
            *value = (*value).clamp(0.0, 1.0);
        }
        result.rgb = converted;
    }
    Ok(result)
}

pub fn sample_bilinear(image: &RgbImage, x: f64, y: f64) -> Vec3 {
    sample_bilinear_view(&image.view(), x, y)
}

pub fn sample_bilinear_view(image: &RgbView<'_>, mut x: f64, mut y: f64) -> Vec3 {
    let max_x = (image.width.saturating_sub(1)) as f64;
    let max_y = (image.height.saturating_sub(1)) as f64;
    x = x.clamp(0.0, max_x);
    y = y.clamp(0.0, max_y);
    let x0 = x.floor() as i32;
    let y0 = y.floor() as i32;
    let x1 = (x0 + 1).min(image.width as i32 - 1);
    let y1 = (y0 + 1).min(image.height as i32 - 1);
    let tx = x - x0 as f64;
    let ty = y - y0 as f64;
    let c00 = image.pixel(x0, y0);
    let c10 = image.pixel(x1, y0);
    let c01 = image.pixel(x0, y1);
    let c11 = image.pixel(x1, y1);
    let mut out = [0.0; 3];
    for i in 0..3 {
        let top = c00[i] * (1.0 - tx) + c10[i] * tx;
        let bottom = c01[i] * (1.0 - tx) + c11[i] * tx;
        out[i] = top * (1.0 - ty) + bottom * ty;
    }
    out
}

fn sample_one_patch(
    image: &RgbView<'_>,
    quad: Quad,
    name: &str,
    index: usize,
    reference_lab_d50: crate::color::Lab,
    reference_srgb: Vec3,
    options: SamplingOptions,
) -> Result<ChartPatch> {
    sample_one_patch_with(
        image,
        quad,
        name,
        index,
        reference_lab_d50,
        reference_srgb,
        options,
        &|rgb, _, _| rgb,
    )
}

#[allow(clippy::too_many_arguments)]
fn sample_one_patch_with<F>(
    image: &RgbView<'_>,
    quad: Quad,
    name: &str,
    index: usize,
    reference_lab_d50: crate::color::Lab,
    reference_srgb: Vec3,
    options: SamplingOptions,
    transform: &F,
) -> Result<ChartPatch>
where
    F: Fn(Vec3, f64, f64) -> Vec3,
{
    if options.samples_per_axis <= 0 {
        return Err(Error::InvalidArgument(
            "samples_per_axis must be positive".into(),
        ));
    }
    let center = quad.bilerp(0.5, 0.5);
    let half = options.samples_per_axis / 2;
    let denom = half.max(1) as f64;
    let mut sum = [0.0; 3];
    let mut count = 0;
    for sy in 0..options.samples_per_axis {
        for sx in 0..options.samples_per_axis {
            let ox = (sx - half) as f64 / denom;
            let oy = (sy - half) as f64 / denom;
            let u = 0.5 + ox * 0.5 * options.inner_fraction;
            let v = 0.5 + oy * 0.5 * options.inner_fraction;
            let point = quad.bilerp(u, v);
            let sample = transform(
                sample_bilinear_view(image, point.x, point.y),
                point.x,
                point.y,
            );
            for c in 0..3 {
                sum[c] += sample[c];
            }
            count += 1;
        }
    }
    let measured = [
        sum[0] / count as f64,
        sum[1] / count as f64,
        sum[2] / count as f64,
    ];
    let saturated = measured.iter().any(|&c| c >= SATURATION_THRESHOLD);
    Ok(ChartPatch {
        name: name.to_string(),
        index,
        reference_lab_d50,
        reference_srgb,
        measured_srgb: measured,
        original_measured_srgb: measured,
        center,
        saturated,
    })
}

/// Sample each Opt2 `patch_obb` with bilinear 9×9 inside the inner 0.70 box.
pub fn sample_patch_obbs(
    image: &RgbImage,
    spec: &ChartSpec,
    patch_obbs: &[Quad],
    options: SamplingOptions,
) -> Result<Vec<ChartPatch>> {
    sample_patch_obbs_view(&image.view(), spec, patch_obbs, options)
}

/// Same as [`sample_patch_obbs`] on a borrowed f32 RGB buffer (no full-frame clone).
pub fn sample_patch_obbs_view(
    image: &RgbView<'_>,
    spec: &ChartSpec,
    patch_obbs: &[Quad],
    options: SamplingOptions,
) -> Result<Vec<ChartPatch>> {
    let expected = spec.patch_count();
    if patch_obbs.len() != expected {
        return Err(Error::InvalidArgument(format!(
            "{} expected {expected} patch_obbs, got {}",
            spec.id,
            patch_obbs.len()
        )));
    }
    if spec.names.len() != expected
        || spec.reference_lab_d50.len() != expected
        || spec.reference_srgb.len() != expected
    {
        return Err(Error::InvalidArgument(
            "chart spec is missing names or reference colors".into(),
        ));
    }
    let mut patches = Vec::with_capacity(expected);
    for (index, obb) in patch_obbs.iter().enumerate() {
        patches.push(sample_one_patch(
            image,
            *obb,
            &spec.names[index],
            index,
            spec.reference_lab_d50[index],
            spec.reference_srgb[index],
            options,
        )?);
    }
    Ok(patches)
}

/// Sample patches while transforming each sampled RGB value. This allows FFC
/// fitting to operate on only the chart samples instead of a full-frame pass.
pub fn sample_patch_obbs_view_transform<F>(
    image: &RgbView<'_>,
    spec: &ChartSpec,
    patch_obbs: &[Quad],
    options: SamplingOptions,
    transform: &F,
) -> Result<Vec<ChartPatch>>
where
    F: Fn(Vec3, f64, f64) -> Vec3,
{
    let expected = spec.patch_count();
    if patch_obbs.len() != expected {
        return Err(Error::InvalidArgument(format!(
            "{} expected {expected} patch_obbs, got {}",
            spec.id,
            patch_obbs.len()
        )));
    }
    let mut patches = Vec::with_capacity(expected);
    for (index, obb) in patch_obbs.iter().enumerate() {
        patches.push(sample_one_patch_with(
            image,
            *obb,
            &spec.names[index],
            index,
            spec.reference_lab_d50[index],
            spec.reference_srgb[index],
            options,
            transform,
        )?);
    }
    Ok(patches)
}

fn read_u16(data: &[u8], offset: usize, le: bool) -> Option<u16> {
    let b = data.get(offset..offset + 2)?;
    Some(if le {
        u16::from_le_bytes([b[0], b[1]])
    } else {
        u16::from_be_bytes([b[0], b[1]])
    })
}

fn read_u32(data: &[u8], offset: usize, le: bool) -> Option<u32> {
    let b = data.get(offset..offset + 4)?;
    Some(if le {
        u32::from_le_bytes([b[0], b[1], b[2], b[3]])
    } else {
        u32::from_be_bytes([b[0], b[1], b[2], b[3]])
    })
}

fn tiff_ifd0_tag(data: &[u8], tag: u16) -> Option<(u16, u32, u32)> {
    if data.len() < 8 {
        return None;
    }
    let le = data[0] == b'I' && data[1] == b'I';
    let be = data[0] == b'M' && data[1] == b'M';
    if !le && !be {
        return None;
    }
    if read_u16(data, 2, le)? != 42 {
        return None;
    }
    let ifd = read_u32(data, 4, le)? as usize;
    let count = read_u16(data, ifd, le)? as usize;
    for i in 0..count {
        let e = ifd + 2 + i * 12;
        if read_u16(data, e, le)? == tag {
            let typ = read_u16(data, e + 2, le)?;
            let cnt = read_u32(data, e + 4, le)?;
            let val = read_u32(data, e + 8, le)?;
            return Some((typ, cnt, val));
        }
    }
    None
}

fn tiff_is_16bit(data: &[u8]) -> bool {
    match tiff_ifd0_tag(data, 258) {
        Some((3, 1, val)) => (val as u16) == 16 || ((val >> 16) as u16) == 16,
        Some((3, count, offset)) if count >= 1 => {
            let le = data[0] == b'I';
            let off = offset as usize;
            (0..count as usize).any(|i| read_u16(data, off + i * 2, le).unwrap_or(8) == 16)
        }
        _ => false,
    }
}

pub fn clamp_srgb(rgb: Vec3) -> Vec3 {
    [clamp01(rgb[0]), clamp01(rgb[1]), clamp01(rgb[2])]
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::refs::Vec2;

    #[test]
    fn jpeg_exif_endianness_and_all_orientations_are_applied() {
        // Inject either TIFF byte order into the same compressed 3x2 JPEG.
        // Corner identifiers are derived from decoded bytes, avoiding JPEG loss.
        let raw = image::RgbImage::from_fn(3, 2, |x, y| {
            image::Rgb([(30 + x * 70) as u8, (40 + y * 150) as u8, 60])
        });
        let mut jpeg = Vec::new();
        image::codecs::jpeg::JpegEncoder::new_with_quality(&mut jpeg, 100)
            .encode_image(&raw)
            .unwrap();
        let decoded = image::load_from_memory(&jpeg).unwrap().to_rgb8();
        let expected_first = [
            (0, 0),
            (2, 0),
            (2, 1),
            (0, 1),
            (0, 0),
            (0, 1),
            (2, 1),
            (2, 0),
        ];
        for big in [false, true] {
            for orientation in 1..=8u8 {
                let tiff = if big {
                    vec![
                        b'M',
                        b'M',
                        0,
                        42,
                        0,
                        0,
                        0,
                        8,
                        0,
                        1,
                        1,
                        18,
                        0,
                        3,
                        0,
                        0,
                        0,
                        1,
                        0,
                        orientation,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                    ]
                } else {
                    vec![
                        b'I',
                        b'I',
                        42,
                        0,
                        8,
                        0,
                        0,
                        0,
                        1,
                        0,
                        18,
                        1,
                        3,
                        0,
                        1,
                        0,
                        0,
                        0,
                        orientation,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                    ]
                };
                let mut payload = b"Exif\0\0".to_vec();
                payload.extend(tiff);
                let mut encoded = jpeg[..2].to_vec();
                encoded.extend([255, 225]);
                encoded.extend(((payload.len() + 2) as u16).to_be_bytes());
                encoded.extend(payload);
                encoded.extend(&jpeg[2..]);
                let path = std::env::temp_dir().join(format!(
                    "ccp-exif-{}-{big}-{orientation}.jpg",
                    std::process::id()
                ));
                std::fs::write(&path, encoded).unwrap();
                let loaded = load_image(&path).unwrap();
                std::fs::remove_file(path).unwrap();
                assert_eq!(
                    (loaded.width, loaded.height),
                    if orientation >= 5 { (2, 3) } else { (3, 2) }
                );
                let (x, y) = expected_first[orientation as usize - 1];
                for channel in 0..3 {
                    assert!(
                        (loaded.rgb[channel] * 255.0 - decoded.get_pixel(x, y)[channel] as f32)
                            .abs()
                            < 0.001
                    );
                }
            }
        }
    }

    #[test]
    fn embedded_p3_normalizes_to_srgb_and_manual_encoding_can_bypass() {
        use image::ImageEncoder;
        let source = moxcms::ColorProfile::new_display_p3();
        let mut png = Vec::new();
        let mut encoder = image::codecs::png::PngEncoder::new(&mut png);
        encoder.set_icc_profile(source.encode().unwrap()).unwrap();
        encoder
            .write_image(&[102, 77, 51], 1, 1, image::ExtendedColorType::Rgb8)
            .unwrap();
        let path = std::env::temp_dir().join(format!("ccp-icc-{}.png", std::process::id()));
        std::fs::write(&path, png).unwrap();
        let managed = load_image(&path).unwrap();
        let raw = load_image_with_icc(&path, false).unwrap();
        std::fs::remove_file(path).unwrap();
        let white = crate::color::parse_white_point("d65").unwrap();
        let p3 = crate::color::parse_rgb_color_space("display-p3", &white).unwrap();
        let srgb = crate::color::parse_rgb_color_space("srgb", &white).unwrap();
        let rgb = [102.0 / 255.0, 77.0 / 255.0, 51.0 / 255.0];
        let expected = crate::color::convert_encoded_rgb(rgb, &p3, &srgb).unwrap();
        for channel in 0..3 {
            assert!((managed.rgb[channel] as f64 - expected[channel]).abs() < 0.001);
            assert!((raw.rgb[channel] as f64 - rgb[channel]).abs() < 1e-7);
        }
        assert!((managed.rgb[0] - raw.rgb[0]).abs() > 0.01);
    }

    #[test]
    fn view_bilinear_matches_owned_image() {
        let mut rgb = Vec::with_capacity(8 * 6 * 3);
        for y in 0..6u32 {
            for x in 0..8u32 {
                rgb.push(x as f32 / 7.0);
                rgb.push(y as f32 / 5.0);
                rgb.push(((x + y) as f32) / 12.0);
            }
        }
        let image = RgbImage::new(8, 6, rgb.clone()).unwrap();
        let view = RgbView::from_rgb(8, 6, &rgb).unwrap();
        for (x, y) in [(0.0, 0.0), (3.25, 1.75), (7.0, 5.0), (4.5, 2.5)] {
            let a = sample_bilinear(&image, x, y);
            let b = sample_bilinear_view(&view, x, y);
            for i in 0..3 {
                assert!(
                    (a[i] - b[i]).abs() < 1e-15,
                    "channel {i} at ({x},{y}): {a:?} vs {b:?}"
                );
            }
        }
        let quad = Quad {
            top_left: Vec2 { x: 1.0, y: 1.0 },
            top_right: Vec2 { x: 6.0, y: 1.0 },
            bottom_right: Vec2 { x: 6.0, y: 4.0 },
            bottom_left: Vec2 { x: 1.0, y: 4.0 },
        };
        let spec = crate::refs::chart_spec_from_name("classic").unwrap();
        let from_img = sample_one_patch(
            &image.view(),
            quad,
            "A1",
            0,
            spec.reference_lab_d50[0],
            spec.reference_srgb[0],
            SamplingOptions::default(),
        )
        .unwrap();
        let from_view = sample_one_patch(
            &view,
            quad,
            "A1",
            0,
            spec.reference_lab_d50[0],
            spec.reference_srgb[0],
            SamplingOptions::default(),
        )
        .unwrap();
        for i in 0..3 {
            assert!((from_img.measured_srgb[i] - from_view.measured_srgb[i]).abs() < 1e-15);
        }
    }
}
