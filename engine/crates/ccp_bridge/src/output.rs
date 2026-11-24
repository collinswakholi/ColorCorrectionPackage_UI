//! Image artifacts: the exported still and the preview files the app shows.
//!
//! A preview is an 8-bit `.rgb` sidecar written synchronously, which the app
//! reads first, plus a PNG encoded in the background. A request finishes its
//! own background PNGs before it replies, so every returned path exists.

use std::collections::HashMap;
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::thread::JoinHandle;

use ccp_core::color::{convert_encoded_rgb, srgb_d65_space, RgbColorSpace};
use image::codecs::png::{CompressionType, FilterType, PngEncoder};
use image::{ExtendedColorType, ImageEncoder, RgbImage as Rgb8};
use jpeg_encoder::{ColorType as JpegColorType, Encoder as JpegEncoder, SamplingFactor};
use ndarray::Array3;
use rayon::prelude::*;
use serde_json::Value;

use crate::error::{Error, ErrorCode, Result};
use crate::request::json_str;
use crate::work;

const PREVIEW_MAX_WIDTH: usize = 2560;
const PREVIEW_RGB_MAGIC: &[u8; 8] = b"CCPRGB8\n";

/// Concurrent background preview PNG encoders. Further spawns wait for one to
/// finish so a large batch cannot start an unbounded number of threads.
const MAX_PREVIEW_ENCODERS: usize = 4;

/// Which previews a request writes besides its output image.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum PreviewMode {
    None,
    Corrected,
    All,
    Stage(String),
}

impl PreviewMode {
    pub(crate) fn from_req(req: &Value) -> Self {
        match json_str(req, &["preview_mode"])
            .unwrap_or("")
            .to_ascii_lowercase()
            .as_str()
        {
            "none" | "off" | "false" => Self::None,
            "all" => Self::All,
            "corrected" | "preview" | "" => Self::Corrected,
            other => Self::Stage(other.to_string()),
        }
    }

    /// Whether the FFC, GC or WB buffer is previewed at that stage.
    pub(crate) fn wants_intermediate(&self, stage: &str) -> bool {
        (matches!(self, Self::All) || matches!(self, Self::Stage(s) if s == stage))
            && stage != "original"
            && stage != "corrected"
            && stage != "cc"
    }

    /// Whether the final buffer is written as the `corrected` preview.
    pub(crate) fn wants_corrected(&self) -> bool {
        match self {
            Self::None => false,
            Self::Corrected | Self::All => true,
            // FFC/GC/WB previews were written at their actual stage. Never
            // replace them with the final buffer, which may use the combined LUT.
            Self::Stage(stage) => stage == "corrected" || stage == "cc",
        }
    }
}

/// Reject an output extension before any work is done.
pub(crate) fn check_still_path(path: &Path) -> Result<()> {
    still_format(path).map(|_| ())
}

/// Preview of an intermediate stage, in display sRGB.
pub(crate) fn stage_preview(dir: Option<&Path>, stage: &str, work: &Array3<f32>) -> Result<()> {
    match dir {
        Some(dir) => write_preview(work, &preview_path(dir, stage), srgb_d65_space().1),
        None => Ok(()),
    }
}

/// Write the output image and the `corrected` preview of the final buffer,
/// then finish every background PNG of this request under `preview_dir`.
/// `space` is the encoding of `work`; previews convert it to display sRGB.
pub(crate) fn finish_image_writes(
    work: &Array3<f32>,
    output: &Path,
    preview_dir: Option<&Path>,
    write_output: bool,
    preview_mode: &PreviewMode,
    space: &RgbColorSpace,
) -> Result<()> {
    let mut jobs: Vec<(PathBuf, bool)> = Vec::new();
    if write_output {
        jobs.push((output.to_path_buf(), false));
    }
    if let Some(dir) = preview_dir.filter(|_| preview_mode.wants_corrected()) {
        jobs.push((preview_path(dir, "corrected"), true));
    }
    let write = |(path, preview): &(PathBuf, bool)| {
        if *preview {
            write_preview(work, path, space)
        } else {
            write_still(work, path)
        }
    };
    match jobs.as_slice() {
        [] => {}
        [job] => write(job)?,
        _ => {
            if let Some(error) = jobs
                .par_iter()
                .filter_map(|job| write(job).err())
                .collect::<Vec<_>>()
                .into_iter()
                .next()
            {
                return Err(error);
            }
        }
    }
    if let Some(dir) = preview_dir {
        pending_preview_pngs().join_under(dir)?;
    }
    Ok(())
}

/// Finish all pending preview PNGs (worker shutdown), reporting the first failure.
pub(crate) fn flush_preview_encodes() -> Result<()> {
    pending_preview_pngs().flush()
}

fn preview_path(dir: &Path, stem: &str) -> PathBuf {
    dir.join(format!("{stem}.png"))
}

fn still_format(path: &Path) -> Result<image::ImageFormat> {
    match path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_ascii_lowercase()
        .as_str()
    {
        "png" => Ok(image::ImageFormat::Png),
        "jpg" | "jpeg" => Ok(image::ImageFormat::Jpeg),
        _ => Err(Error::Message(
            "output image extension must be png, jpg or jpeg".into(),
        )),
    }
}

fn write_still(arr: &Array3<f32>, path: &Path) -> Result<()> {
    let format = still_format(path)?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let rgb = to_rgb8(arr, usize::MAX, quantize)?;
    if format == image::ImageFormat::Png {
        encode_png(&rgb, path)
    } else {
        encode_jpeg(&rgb, path, 90)
    }
}

fn write_preview(arr: &Array3<f32>, path: &Path, space: &RgbColorSpace) -> Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let rgb = if work::is_srgb_d65(space) {
        to_rgb8(arr, PREVIEW_MAX_WIDTH, quantize)?
    } else {
        // Transform floating source samples, not already quantized 8-bit output.
        // Export pixels are never modified by the display-only conversion.
        let display = srgb_d65_space().1;
        to_rgb8(arr, PREVIEW_MAX_WIDTH, |p| {
            let rgb = convert_encoded_rgb([p[0] as f64, p[1] as f64, p[2] as f64], space, display)?;
            Ok(rgb.map(|v| f32_to_u8(v as f32)))
        })?
    };
    write_preview_rgb_file(&rgb, &path.with_extension("rgb"))?;
    pending_preview_pngs().spawn(rgb, path.to_path_buf());
    Ok(())
}

fn f32_to_u8(v: f32) -> u8 {
    (v.clamp(0.0, 1.0) * 255.0 + 0.5) as u8
}

fn quantize(p: &[f32]) -> Result<[u8; 3]> {
    Ok([f32_to_u8(p[0]), f32_to_u8(p[1]), f32_to_u8(p[2])])
}

/// 8-bit image of `arr`, nearest-neighbour downscaled to at most `max_width`.
fn to_rgb8(
    arr: &Array3<f32>,
    max_width: usize,
    pixel: impl Fn(&[f32]) -> Result<[u8; 3]> + Sync,
) -> Result<Rgb8> {
    let h = arr.shape()[0];
    let w = arr.shape()[1];
    let src = work::samples(arr);
    let (out_w, out_h) = if w <= max_width {
        (w, h)
    } else {
        (max_width, ((h * max_width) / w).max(1))
    };
    let mut buf = vec![0u8; out_w * out_h * 3];
    if out_w == w {
        buf.par_chunks_exact_mut(3)
            .zip(src.par_chunks_exact(3))
            .try_for_each(|(d, s)| -> Result<()> {
                d.copy_from_slice(&pixel(s)?);
                Ok(())
            })?;
    } else {
        buf.par_chunks_exact_mut(out_w * 3)
            .enumerate()
            .try_for_each(|(y, row)| -> Result<()> {
                let src_row = (y * h / out_h) * w;
                for x in 0..out_w {
                    let i = (src_row + x * w / out_w) * 3;
                    row[x * 3..x * 3 + 3].copy_from_slice(&pixel(&src[i..i + 3])?);
                }
                Ok(())
            })?;
    }
    Rgb8::from_raw(out_w as u32, out_h as u32, buf)
        .ok_or_else(|| Error::coded(ErrorCode::InternalError, "preview buffer size mismatch"))
}

fn encode_jpeg(rgb: &Rgb8, path: &Path, quality: u8) -> Result<()> {
    let width = u16::try_from(rgb.width())
        .map_err(|_| Error::Message(format!("JPEG width {} exceeds 65535 pixels", rgb.width())))?;
    let height = u16::try_from(rgb.height()).map_err(|_| {
        Error::Message(format!("JPEG height {} exceeds 65535 pixels", rgb.height()))
    })?;
    let file = std::fs::File::create(path)?;
    let mut encoder = JpegEncoder::new(BufWriter::new(file), quality);
    // Full-chroma output, matching the previous product encoder.
    encoder.set_sampling_factor(SamplingFactor::R_4_4_4);
    encoder
        .encode(rgb.as_raw(), width, height, JpegColorType::Rgb)
        .map_err(|e| {
            Error::coded(
                ErrorCode::ProcessingFailed,
                format!("JPEG encode failed: {e}"),
            )
        })
}

fn encode_png(rgb: &Rgb8, path: &Path) -> Result<()> {
    let file = std::fs::File::create(path)?;
    PngEncoder::new_with_quality(
        BufWriter::new(file),
        CompressionType::Fast,
        FilterType::NoFilter,
    )
    .write_image(
        rgb.as_raw(),
        rgb.width(),
        rgb.height(),
        ExtendedColorType::Rgb8,
    )?;
    Ok(())
}

fn encode_preview_png(rgb: &Rgb8, path: &Path) -> Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    encode_png(rgb, path)
}

fn write_preview_rgb_file(rgb: &Rgb8, path: &Path) -> Result<()> {
    let mut file = std::fs::File::create(path)?;
    file.write_all(PREVIEW_RGB_MAGIC)?;
    file.write_all(&rgb.width().to_le_bytes())?;
    file.write_all(&rgb.height().to_le_bytes())?;
    file.write_all(rgb.as_raw())?;
    Ok(())
}

type PreviewEncode = JoinHandle<std::result::Result<(), String>>;

/// The worker-wide registry of background preview PNG encodes, keyed by path.
#[derive(Default)]
struct PreviewPngEncoders {
    pending: Mutex<HashMap<PathBuf, PreviewEncode>>,
    /// Failures observed while reaping, kept until a join reports them.
    failed: Mutex<HashMap<PathBuf, String>>,
}

fn pending_preview_pngs() -> &'static PreviewPngEncoders {
    static PENDING: OnceLock<PreviewPngEncoders> = OnceLock::new();
    PENDING.get_or_init(PreviewPngEncoders::default)
}

fn finish_preview_encode(path: &Path, handle: PreviewEncode) -> std::result::Result<(), String> {
    match handle.join() {
        Ok(result) => result,
        Err(_) => Err(format!("preview encoder for {} panicked", path.display())),
    }
}

fn preview_failure(path: &Path, error: &str) -> Error {
    Error::coded(
        ErrorCode::IoError,
        format!(
            "preview PNG {} could not be written: {error}",
            path.display()
        ),
    )
}

impl PreviewPngEncoders {
    fn record_failure(&self, path: PathBuf, result: std::result::Result<(), String>) {
        if let Err(error) = result {
            self.failed
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .insert(path, error);
        }
    }

    fn finish(&self, path: PathBuf, handle: PreviewEncode) {
        let result = finish_preview_encode(&path, handle);
        self.record_failure(path, result);
    }

    fn reap_finished(&self) {
        let handles: Vec<_> = {
            let mut pending = self.pending.lock().unwrap_or_else(|e| e.into_inner());
            let finished: Vec<_> = pending
                .iter()
                .filter(|(_, handle)| handle.is_finished())
                .map(|(path, _)| path.clone())
                .collect();
            finished
                .into_iter()
                .filter_map(|path| pending.remove(&path).map(|handle| (path, handle)))
                .collect()
        };
        // Join outside the registry lock. A finished encoder still owns a native
        // Windows thread handle until joined/dropped; unique run paths must not
        // retain those handles until shutdown. Encoders still running stay queued.
        for (path, handle) in handles {
            self.finish(path, handle);
        }
    }

    fn join(&self, path: &Path) -> Result<()> {
        // Keep registry ownership until completion. A concurrent flush/join must
        // never interpret an already-drained but still-running encode as done.
        let mut pending = self.pending.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(handle) = pending.remove(path) {
            self.finish(path.to_path_buf(), handle);
        }
        drop(pending);
        let failure = self
            .failed
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(path);
        match failure {
            Some(error) => Err(preview_failure(path, &error)),
            None => Ok(()),
        }
    }

    fn join_under(&self, dir: &Path) -> Result<()> {
        let mut pending = self.pending.lock().unwrap_or_else(|e| e.into_inner());
        let paths: Vec<PathBuf> = pending
            .keys()
            .filter(|path| path.starts_with(dir))
            .cloned()
            .collect();
        for path in paths {
            if let Some(handle) = pending.remove(&path) {
                self.finish(path, handle);
            }
        }
        drop(pending);
        self.take_first_failure(|path| path.starts_with(dir))
    }

    fn flush(&self) -> Result<()> {
        let mut pending = self.pending.lock().unwrap_or_else(|e| e.into_inner());
        for (path, handle) in pending.drain() {
            self.finish(path, handle);
        }
        drop(pending);
        self.take_first_failure(|_| true)
    }

    /// Forget every recorded failure under `selected`, reporting the first by path.
    fn take_first_failure(&self, selected: impl Fn(&Path) -> bool) -> Result<()> {
        let mut failed = self.failed.lock().unwrap_or_else(|e| e.into_inner());
        let mut paths: Vec<PathBuf> = failed.keys().filter(|p| selected(p)).cloned().collect();
        paths.sort();
        let first = paths
            .first()
            .and_then(|path| failed.get(path).map(|error| preview_failure(path, error)));
        for path in &paths {
            failed.remove(path);
        }
        first.map_or(Ok(()), Err)
    }

    fn spawn(&self, rgb: Rgb8, path: PathBuf) {
        self.reap_finished();
        // A replaced encode's outcome is moot; the new one reports for this path.
        let _ = self.join(&path);
        let mut pending = self.pending.lock().unwrap_or_else(|e| e.into_inner());
        while pending.len() >= MAX_PREVIEW_ENCODERS {
            let Some(oldest) = pending.keys().next().cloned() else {
                break;
            };
            if let Some(handle) = pending.remove(&oldest) {
                self.finish(oldest, handle);
            }
        }
        let key = path.clone();
        let spawned = std::thread::Builder::new()
            .name("ccp-preview-png".into())
            .spawn(move || encode_preview_png(&rgb, &path).map_err(|e| e.to_string()));
        match spawned {
            Ok(handle) => {
                pending.insert(key, handle);
            }
            Err(error) => {
                drop(pending);
                self.record_failure(key, Err(format!("cannot start preview encoder: {error}")));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ccp_core::color::{parse_rgb_color_space, parse_white_point};
    use std::sync::Arc;
    use std::time::Instant;

    #[test]
    fn write_preview_emits_rgb_sidecar() {
        let dir = std::env::temp_dir().join(format!("ccp-preview-rgb-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let mut arr = Array3::<f32>::zeros((20, 30, 3));
        arr[[5, 7, 0]] = 1.0;
        write_preview(&arr, &dir.join("corrected.png"), srgb_d65_space().1).unwrap();
        let bytes = std::fs::read(dir.join("corrected.rgb")).unwrap();
        assert_eq!(&bytes[..8], b"CCPRGB8\n");
        assert_eq!(&bytes[8..12], 30u32.to_le_bytes());
        assert_eq!(&bytes[12..16], 20u32.to_le_bytes());
        flush_preview_encodes().unwrap();
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn preview_encoders_reap_100_unique_outputs_without_waiting_for_running_jobs() {
        let dir = std::env::temp_dir().join(format!("ccp-preview-reap-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let encoders = PreviewPngEncoders::default();
        let (release, wait) = std::sync::mpsc::channel();
        let blocked_path = dir.join("still-running.png");
        encoders.pending.lock().unwrap().insert(
            blocked_path.clone(),
            std::thread::spawn(move || {
                let _ = wait.recv();
                Ok(())
            }),
        );
        for index in 0..100u8 {
            let path = dir.join(format!("unique-{index}.png"));
            encoders.spawn(
                Rgb8::from_pixel(8, 8, image::Rgb([index, 17, 219])),
                path.clone(),
            );
            let deadline = Instant::now() + std::time::Duration::from_secs(5);
            loop {
                let pending = encoders.pending.lock().unwrap();
                if pending.get(&path).unwrap().is_finished() {
                    break;
                }
                drop(pending);
                assert!(Instant::now() < deadline, "preview encoder did not finish");
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
            // At most the explicitly blocked task and this newest completed
            // encoder may remain. The next spawn reaps this completed handle.
            assert!(encoders.pending.lock().unwrap().len() <= 2);
        }
        encoders.reap_finished();
        assert_eq!(encoders.pending.lock().unwrap().len(), 1);
        assert!(encoders.pending.lock().unwrap().contains_key(&blocked_path));
        release.send(()).unwrap();
        encoders.flush().unwrap();
        assert!(encoders.pending.lock().unwrap().is_empty());
        for index in 0..100u8 {
            let image = image::open(dir.join(format!("unique-{index}.png")))
                .unwrap()
                .to_rgb8();
            assert_eq!(image.dimensions(), (8, 8));
            assert!(image.pixels().all(|pixel| pixel.0 == [index, 17, 219]));
        }
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn preview_encoders_reap_failed_encodes_and_recover_for_later_outputs() {
        let dir =
            std::env::temp_dir().join(format!("ccp-preview-reap-errors-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let blocker = dir.join("not-a-directory");
        std::fs::write(&blocker, b"preserve this file").unwrap();
        let failed = blocker.join("corrected.png");
        let encoders = PreviewPngEncoders::default();
        encoders.spawn(Rgb8::new(8, 8), failed.clone());
        let deadline = Instant::now() + std::time::Duration::from_secs(5);
        while !encoders
            .pending
            .lock()
            .unwrap()
            .get(&failed)
            .unwrap()
            .is_finished()
        {
            assert!(Instant::now() < deadline, "failed encoder did not finish");
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        let recovered = dir.join("recovered.png");
        encoders.spawn(
            Rgb8::from_pixel(8, 8, image::Rgb([7, 8, 9])),
            recovered.clone(),
        );
        assert!(!encoders.pending.lock().unwrap().contains_key(&failed));
        // The reaped failure is still reported once, by the next flush.
        assert!(encoders.flush().is_err());
        encoders.flush().unwrap();
        assert!(encoders.pending.lock().unwrap().is_empty());
        assert_eq!(std::fs::read(&blocker).unwrap(), b"preserve this file");
        assert_eq!(
            image::open(recovered).unwrap().to_rgb8().get_pixel(0, 0).0,
            [7, 8, 9]
        );
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn preview_encode_failures_surface_and_pngs_exist_after_join_under() {
        let dir = std::env::temp_dir().join(format!("ccp-preview-surface-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let blocker = dir.join("not-a-directory");
        std::fs::write(&blocker, b"x").unwrap();
        let encoders = PreviewPngEncoders::default();
        encoders.spawn(Rgb8::new(8, 8), blocker.join("corrected.png"));
        let error = encoders.join_under(&blocker).unwrap_err();
        assert!(error.to_string().contains("corrected.png"), "{error}");
        // The failure is reported once, then forgotten.
        encoders.join_under(&blocker).unwrap();
        let good = dir.join("good");
        encoders.spawn(
            Rgb8::from_pixel(8, 8, image::Rgb([9, 8, 7])),
            good.join("gc.png"),
        );
        encoders.join_under(&good).unwrap();
        assert_eq!(
            image::open(good.join("gc.png"))
                .unwrap()
                .to_rgb8()
                .get_pixel(0, 0)
                .0,
            [9, 8, 7]
        );
        // A failure noticed while reaping is still reported to a later join.
        encoders.spawn(Rgb8::new(8, 8), blocker.join("wb.png"));
        let deadline = Instant::now() + std::time::Duration::from_secs(5);
        while !encoders
            .pending
            .lock()
            .unwrap()
            .values()
            .all(|h| h.is_finished())
        {
            assert!(Instant::now() < deadline);
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        encoders.reap_finished();
        assert!(encoders.join(&blocker.join("wb.png")).is_err());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn preview_encoder_concurrency_is_capped() {
        let encoders = Arc::new(PreviewPngEncoders::default());
        let mut releases = Vec::new();
        for index in 0..MAX_PREVIEW_ENCODERS {
            let (release, wait) = std::sync::mpsc::channel::<()>();
            releases.push(release);
            encoders.pending.lock().unwrap().insert(
                PathBuf::from(format!("blocked-cap-{index}.png")),
                std::thread::spawn(move || {
                    let _ = wait.recv();
                    Ok(())
                }),
            );
        }
        let dir = std::env::temp_dir().join(format!("ccp-preview-cap-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let spawner = Arc::clone(&encoders);
        let path = dir.join("over-cap.png");
        let spawned_path = path.clone();
        let (submitted, submitted_rx) = std::sync::mpsc::channel();
        let thread = std::thread::spawn(move || {
            spawner.spawn(Rgb8::new(4, 4), spawned_path);
            submitted.send(()).unwrap();
        });
        assert!(
            submitted_rx
                .recv_timeout(std::time::Duration::from_millis(50))
                .is_err(),
            "spawn must wait while {MAX_PREVIEW_ENCODERS} encoders are running"
        );
        drop(releases);
        thread.join().unwrap();
        submitted_rx
            .recv_timeout(std::time::Duration::from_secs(5))
            .unwrap();
        encoders.flush().unwrap();
        assert!(path.is_file());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn preview_join_waits_for_concurrent_shutdown_flush() {
        let encoders = Arc::new(PreviewPngEncoders::default());
        let path = PathBuf::from("blocked-preview-for-flush-test.png");
        let (release, wait) = std::sync::mpsc::channel();
        encoders.pending.lock().unwrap().insert(
            path.clone(),
            std::thread::spawn(move || {
                wait.recv().unwrap();
                Ok(())
            }),
        );
        let flushing = Arc::clone(&encoders);
        let flush = std::thread::spawn(move || flushing.flush());
        let deadline = Instant::now() + std::time::Duration::from_secs(5);
        while encoders.pending.try_lock().is_ok() {
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        let reading = Arc::clone(&encoders);
        let (done, done_rx) = std::sync::mpsc::channel();
        let reader = std::thread::spawn(move || {
            let _ = reading.join(&path);
            done.send(()).unwrap();
        });
        assert!(done_rx
            .recv_timeout(std::time::Duration::from_millis(20))
            .is_err());
        release.send(()).unwrap();
        flush.join().unwrap().unwrap();
        reader.join().unwrap();
        done_rx
            .recv_timeout(std::time::Duration::from_secs(5))
            .unwrap();
    }

    #[test]
    fn preview_encoders_finish_pending_same_path_before_replacement() {
        let dir = std::env::temp_dir().join(format!("ccp-preview-rewrite-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("corrected.png");
        let encoders = Arc::new(PreviewPngEncoders::default());
        let (release, wait) = std::sync::mpsc::channel();
        let first_path = path.clone();
        encoders.pending.lock().unwrap().insert(
            path.clone(),
            std::thread::spawn(move || {
                wait.recv().unwrap();
                encode_preview_png(&Rgb8::from_pixel(8, 8, image::Rgb([1, 2, 3])), &first_path)
                    .unwrap();
                Ok(())
            }),
        );
        let second_encoders = Arc::clone(&encoders);
        let second_path = path.clone();
        let (submitted, submitted_rx) = std::sync::mpsc::channel();
        let replacement = std::thread::spawn(move || {
            second_encoders.spawn(Rgb8::from_pixel(8, 8, image::Rgb([4, 5, 6])), second_path);
            submitted.send(()).unwrap();
        });
        assert!(submitted_rx
            .recv_timeout(std::time::Duration::from_millis(20))
            .is_err());
        release.send(()).unwrap();
        replacement.join().unwrap();
        encoders.flush().unwrap();
        assert_eq!(
            image::open(&path).unwrap().to_rgb8().get_pixel(0, 0).0,
            [4, 5, 6]
        );
        assert!(encoders.pending.lock().unwrap().is_empty());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn preview_required_sidecar_failure_propagates_without_replacing_previous_png() {
        let dir = std::env::temp_dir().join(format!(
            "ccp-preview-required-errors-{}",
            std::process::id()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("corrected.png");
        let previous = Rgb8::from_pixel(8, 8, image::Rgb([21, 43, 65]));
        previous.save(&path).unwrap();
        let old_bytes = std::fs::read(&path).unwrap();
        std::fs::create_dir(path.with_extension("rgb")).unwrap();
        let error = write_preview(
            &Array3::from_elem((8, 8, 3), 0.8),
            &path,
            srgb_d65_space().1,
        )
        .unwrap_err();
        assert!(matches!(error, Error::Io(_)));
        assert_eq!(std::fs::read(&path).unwrap(), old_bytes);
        assert!(!pending_preview_pngs()
            .pending
            .lock()
            .unwrap()
            .contains_key(&path));
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn corrected_preview_converts_to_srgb_without_changing_export_pixels() {
        let dir = std::env::temp_dir().join(format!("ccp-color-preview-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let white = parse_white_point("d65").unwrap();
        let linear = parse_rgb_color_space("linear-srgb", &white).unwrap();
        let work = Array3::from_elem((2, 2, 3), 0.21404114f32);
        finish_image_writes(
            &work,
            &dir.join("linear.png"),
            Some(&dir),
            true,
            &PreviewMode::Corrected,
            &linear,
        )
        .unwrap();
        let exported = image::open(dir.join("linear.png")).unwrap().to_rgb8();
        assert_eq!(exported.get_pixel(0, 0).0, [55; 3]);
        let sidecar = std::fs::read(dir.join("corrected.rgb")).unwrap();
        assert_eq!(&sidecar[sidecar.len() - 3..], &[128; 3]);
        pending_preview_pngs()
            .join(&dir.join("corrected.png"))
            .unwrap();
        let preview = image::open(dir.join("corrected.png")).unwrap().to_rgb8();
        assert_eq!(preview.get_pixel(0, 0).0, [128; 3]);
        assert_eq!(work[[0, 0, 0]], 0.21404114);
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
