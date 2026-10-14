//! Sidecar `ffmpeg` only. This crate never links libav / FFmpeg libraries.
//!
//! Search order: `CCP_FFMPEG`, install-root / `installer` ffmpeg, sibling of
//! `ccp-engine`, then `PATH`.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use serde::{Deserialize, Serialize};

pub const LUT3D_NOTE: &str = "Video grading accepts 8-bit SDR Rec.709/sRGB-family sources; HDR, wide-gamut, high-bit-depth, and variable-rate per-frame FFC inputs are rejected with an actionable error. FFmpeg lut3d tetrahedral vs our trilinear may differ; the cube never includes FFC — when FFC is on, the clip is graded per-frame (white gain, then GC/WB/CC).";

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error(
        "ffmpeg sidecar not found (set CCP_FFMPEG, place ffmpeg next to the app or in installer/, or add it to PATH). This crate does not link libav."
    )]
    FfmpegNotFound,
    #[error("ffmpeg failed: {0}")]
    Ffmpeg(String),
    #[error("{0}")]
    InvalidArgument(String),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

pub type Result<T> = std::result::Result<T, Error>;

/// Shot report written after a keyframe fit + `lut3d` apply.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ShotReport {
    pub transform: String,
    pub keyframe: f64,
    pub chart_delta_e_exists: bool,
    pub note: String,
}

impl ShotReport {
    pub fn v1(transform: impl Into<String>, keyframe: f64, chart_delta_e_exists: bool) -> Self {
        Self {
            transform: transform.into(),
            keyframe,
            chart_delta_e_exists,
            note: LUT3D_NOTE.to_string(),
        }
    }

    pub fn write_json(&self, path: impl AsRef<Path>) -> Result<()> {
        let path = path.as_ref();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(path, serde_json::to_string_pretty(self)?)?;
        Ok(())
    }
}

fn ffmpeg_basename() -> &'static str {
    if cfg!(windows) {
        "ffmpeg.exe"
    } else {
        "ffmpeg"
    }
}

fn ffprobe_basename() -> &'static str {
    if cfg!(windows) {
        "ffprobe.exe"
    } else {
        "ffprobe"
    }
}

fn walk_installer_ffmpeg(start: &Path) -> Option<PathBuf> {
    let mut dir = Some(start);
    while let Some(cur) = dir {
        let cand = cur.join("installer").join(ffmpeg_basename());
        if cand.is_file() {
            return Some(cand);
        }
        dir = cur.parent();
    }
    None
}

fn on_path(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    for dir in std::env::split_paths(&path) {
        let cand = dir.join(name);
        if cand.is_file() {
            return Some(cand);
        }
    }
    None
}

/// Locate the bundled or PATH ffmpeg binary. Never a libav link path.
pub fn find_ffmpeg() -> Option<PathBuf> {
    if let Ok(p) = std::env::var("CCP_FFMPEG") {
        let path = PathBuf::from(p);
        if path.is_file() {
            return Some(path);
        }
    }
    if let Ok(root) = std::env::var("CCP_NEW_UI_ROOT") {
        let root = PathBuf::from(root);
        for cand in [
            root.join("installer").join(ffmpeg_basename()),
            root.join(ffmpeg_basename()),
        ] {
            if cand.is_file() {
                return Some(cand);
            }
        }
    }
    if let Ok(cwd) = std::env::current_dir() {
        if let Some(found) = walk_installer_ffmpeg(&cwd) {
            return Some(found);
        }
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            let sibling = parent.join(ffmpeg_basename());
            if sibling.is_file() {
                return Some(sibling);
            }
            if let Some(found) = walk_installer_ffmpeg(parent) {
                return Some(found);
            }
        }
    }
    // Release binaries must never retain or probe the build machine's checkout.
    #[cfg(debug_assertions)]
    {
        if let Some(found) = walk_installer_ffmpeg(Path::new(env!("CARGO_MANIFEST_DIR"))) {
            return Some(found);
        }
    }
    on_path(ffmpeg_basename()).or_else(|| on_path("ffmpeg"))
}

pub fn require_ffmpeg() -> Result<PathBuf> {
    find_ffmpeg().ok_or(Error::FfmpegNotFound)
}

fn sibling_ffprobe(ffmpeg: &Path) -> Option<PathBuf> {
    let dir = ffmpeg.parent()?;
    let cand = dir.join(ffprobe_basename());
    if cand.is_file() {
        Some(cand)
    } else {
        on_path(ffprobe_basename())
    }
}

fn run_ffmpeg(ffmpeg: &Path, args: &[&str]) -> Result<std::process::Output> {
    run_ffmpeg_in(ffmpeg, args, None)
}

fn run_ffmpeg_in(ffmpeg: &Path, args: &[&str], dir: Option<&Path>) -> Result<std::process::Output> {
    let mut cmd = Command::new(ffmpeg);
    cmd.args(args);
    if let Some(dir) = dir {
        cmd.current_dir(dir);
    }
    let output = cmd
        .output()
        .map_err(|e| Error::Ffmpeg(format!("spawn {}: {e}", ffmpeg.display())))?;
    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        return Err(Error::Ffmpeg(format!(
            "{} failed ({:?}): {} {}",
            ffmpeg.display(),
            output.status.code(),
            stderr.trim(),
            stdout.trim()
        )));
    }
    Ok(output)
}

fn ensure_parent(path: &Path) -> Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    Ok(())
}

/// Extract one JPEG at `timestamp_sec` via sidecar ffmpeg.
pub fn extract_frame(
    video: impl AsRef<Path>,
    timestamp_sec: f64,
    out_jpeg: impl AsRef<Path>,
) -> Result<PathBuf> {
    let video = video.as_ref();
    let out_jpeg = out_jpeg.as_ref();
    if !video.is_file() {
        return Err(Error::InvalidArgument(format!(
            "video not found: {}",
            video.display()
        )));
    }
    if !timestamp_sec.is_finite() || timestamp_sec < 0.0 {
        return Err(Error::InvalidArgument("timestamp_sec must be >= 0".into()));
    }
    ensure_parent(out_jpeg)?;
    let ffmpeg = require_ffmpeg()?;
    let ts = format!("{timestamp_sec:.3}");
    let video_s = video.to_string_lossy();
    let out_s = out_jpeg.to_string_lossy();
    run_ffmpeg(
        &ffmpeg,
        &[
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-ss",
            &ts,
            "-i",
            video_s.as_ref(),
            "-frames:v",
            "1",
            "-q:v",
            "2",
            out_s.as_ref(),
        ],
    )?;
    if !out_jpeg.is_file() {
        return Err(Error::Ffmpeg(format!(
            "extract_frame produced no file {}",
            out_jpeg.display()
        )));
    }
    Ok(out_jpeg.to_path_buf())
}

pub(crate) fn parse_duration_hms(text: &str) -> Option<f64> {
    let marker = "Duration: ";
    let i = text.find(marker)?;
    let rest = &text[i + marker.len()..];
    let token = rest
        .split(|c: char| c == ',' || c.is_whitespace())
        .find(|s| !s.is_empty())?;
    let parts: Vec<&str> = token.split(':').collect();
    if parts.len() != 3 {
        return None;
    }
    let hours: f64 = parts[0].parse().ok()?;
    let minutes: f64 = parts[1].parse().ok()?;
    let seconds: f64 = parts[2].parse().ok()?;
    Some(hours * 3600.0 + minutes * 60.0 + seconds)
}

fn probe_duration_seconds(ffmpeg: &Path, video: &Path) -> Option<f64> {
    if let Some(probe) = sibling_ffprobe(ffmpeg) {
        if let Ok(out) = Command::new(probe)
            .args([
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=nw=1:nk=1",
                &video.to_string_lossy(),
            ])
            .output()
        {
            if out.status.success() {
                if let Ok(s) = String::from_utf8(out.stdout) {
                    if let Ok(v) = s.trim().parse::<f64>() {
                        if v.is_finite() && v > 0.0 {
                            return Some(v);
                        }
                    }
                }
            }
        }
    }
    let out = Command::new(ffmpeg)
        .args(["-hide_banner", "-i", &video.to_string_lossy()])
        .output()
        .ok()?;
    let stderr = String::from_utf8_lossy(&out.stderr);
    parse_duration_hms(&stderr)
}

/// Write JPEG thumbs every `every_n_seconds`. Returns the written paths in time order.
pub fn timeline_thumbs(
    video: impl AsRef<Path>,
    every_n_seconds: f64,
    out_dir: impl AsRef<Path>,
) -> Result<Vec<PathBuf>> {
    let video = video.as_ref();
    let out_dir = out_dir.as_ref();
    if !video.is_file() {
        return Err(Error::InvalidArgument(format!(
            "video not found: {}",
            video.display()
        )));
    }
    if !every_n_seconds.is_finite() || every_n_seconds <= 0.0 {
        return Err(Error::InvalidArgument("every_n_seconds must be > 0".into()));
    }
    std::fs::create_dir_all(out_dir)?;
    let ffmpeg = require_ffmpeg()?;
    let duration = probe_duration_seconds(&ffmpeg, video).unwrap_or(0.0);
    let mut stamps = Vec::new();
    if duration <= 0.0 {
        stamps.push(0.0);
    } else {
        let mut t = 0.0;
        while t < duration {
            stamps.push(t);
            t += every_n_seconds;
        }
        if stamps.is_empty() {
            stamps.push(0.0);
        }
    }
    let mut paths = Vec::with_capacity(stamps.len());
    for (i, t) in stamps.iter().enumerate() {
        let out = out_dir.join(format!("thumb_{i:04}.jpg"));
        extract_frame(video, *t, &out)?;
        paths.push(out);
    }
    Ok(paths)
}

fn lut3d_filter_arg(cube_path: &Path) -> String {
    // Basename only. apply_lut3d sets cwd to the cube directory so Windows
    // drive colons never enter the -vf graph (`C:` is an option separator).
    let name = cube_path
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("lut.cube");
    format!("lut3d={name}")
}

/// Apply an IRIDAS cube with sidecar `ffmpeg -y -i in -vf lut3d=cube out`.
pub fn apply_lut3d(
    input_mp4: impl AsRef<Path>,
    cube_path: impl AsRef<Path>,
    output_mp4: impl AsRef<Path>,
) -> Result<PathBuf> {
    let input_mp4 = input_mp4.as_ref();
    let cube_path = cube_path.as_ref();
    let output_mp4 = output_mp4.as_ref();
    if !input_mp4.is_file() {
        return Err(Error::InvalidArgument(format!(
            "input video not found: {}",
            input_mp4.display()
        )));
    }
    if !cube_path.is_file() {
        return Err(Error::InvalidArgument(format!(
            "cube not found: {}",
            cube_path.display()
        )));
    }
    let info = probe_video(input_mp4)?;
    info.validate_for_grade(false)?;
    ensure_parent(output_mp4)?;
    let ffmpeg = require_ffmpeg()?;
    let vf = format!(
        "{},setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
        lut3d_filter_arg(cube_path)
    );
    let in_s = input_mp4.to_string_lossy();
    let out_s = output_mp4.to_string_lossy();
    let cube_dir = cube_path.parent();
    run_ffmpeg_in(
        &ffmpeg,
        &[
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            in_s.as_ref(),
            "-map",
            "0:v:0",
            "-map",
            "0:a?",
            "-map",
            "0:s?",
            "-map_metadata",
            "0",
            "-map_chapters",
            "0",
            "-vf",
            &vf,
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            "-crf",
            "18",
            "-color_range",
            "tv",
            "-colorspace",
            "bt709",
            "-color_primaries",
            "bt709",
            "-color_trc",
            "bt709",
            "-fps_mode",
            "passthrough",
            "-c:a",
            "copy",
            "-c:s",
            "copy",
            "-movflags",
            "+faststart",
            out_s.as_ref(),
        ],
        cube_dir,
    )?;
    if !output_mp4.is_file() {
        return Err(Error::Ffmpeg(format!(
            "apply_lut3d produced no file {}",
            output_mp4.display()
        )));
    }
    Ok(output_mp4.to_path_buf())
}

#[derive(Clone, Debug)]
pub struct VideoInfo {
    pub width: u32,
    pub height: u32,
    pub fps: f64,
    pub nominal_fps: f64,
    pub pixel_format: String,
    pub color_range: String,
    pub color_space: String,
    pub color_transfer: String,
    pub color_primaries: String,
}

impl VideoInfo {
    pub fn is_variable_frame_rate(&self) -> bool {
        self.nominal_fps > 0.0 && ((self.fps - self.nominal_fps).abs() / self.nominal_fps) > 0.001
    }

    pub fn validate_for_grade(&self, per_frame: bool) -> Result<()> {
        let transfer = self.color_transfer.to_ascii_lowercase();
        let primaries = self.color_primaries.to_ascii_lowercase();
        let space = self.color_space.to_ascii_lowercase();
        let pixel = self.pixel_format.to_ascii_lowercase();
        if matches!(transfer.as_str(), "smpte2084" | "arib-std-b67" | "smpte428")
            || primaries.contains("bt2020")
            || space.contains("bt2020")
        {
            return Err(Error::InvalidArgument(
                "HDR or wide-gamut video is not supported yet. Convert the clip to SDR Rec.709 before grading."
                    .into(),
            ));
        }
        if [
            "p010", "p016", "10le", "12le", "16le", "gbrp10", "gbrp12", "gbrp16",
        ]
        .iter()
        .any(|token| pixel.contains(token))
        {
            return Err(Error::InvalidArgument(
                "High-bit-depth video would be reduced to 8-bit by this pipeline. Convert to 8-bit SDR Rec.709 or use a future high-bit-depth export path."
                    .into(),
            ));
        }
        if per_frame && self.is_variable_frame_rate() {
            return Err(Error::InvalidArgument(
                "Per-frame FFC currently requires constant-frame-rate video. Convert to CFR or disable FFC; LUT grading preserves VFR timestamps."
                    .into(),
            ));
        }
        Ok(())
    }
}

fn probe_stream_number(probe: &Path, video: &Path, key: &str) -> Option<String> {
    let out = Command::new(probe)
        .args([
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_entries",
            &format!("stream={key}"),
            "-of",
            "default=nw=1:nk=1",
            &video.to_string_lossy(),
        ])
        .output()
        .ok()?;
    if !out.status.success() {
        return None;
    }
    let s = String::from_utf8(out.stdout).ok()?;
    let token = s.lines().next()?.trim();
    if token.is_empty() {
        None
    } else {
        Some(token.to_string())
    }
}

fn parse_fps(text: &str) -> Option<f64> {
    if let Some((n, d)) = text.split_once('/') {
        let num: f64 = n.parse().ok()?;
        let den: f64 = d.parse().ok()?;
        if den > 0.0 {
            return Some(num / den);
        }
    }
    text.parse().ok()
}

pub fn probe_video(video: impl AsRef<Path>) -> Result<VideoInfo> {
    let video = video.as_ref();
    if !video.is_file() {
        return Err(Error::InvalidArgument(format!(
            "video not found: {}",
            video.display()
        )));
    }
    let ffmpeg = require_ffmpeg()?;
    let probe = sibling_ffprobe(&ffmpeg).ok_or(Error::FfmpegNotFound)?;
    let width = probe_stream_number(&probe, video, "width")
        .and_then(|s| s.parse().ok())
        .ok_or_else(|| Error::Ffmpeg("ffprobe missing width".into()))?;
    let height = probe_stream_number(&probe, video, "height")
        .and_then(|s| s.parse().ok())
        .ok_or_else(|| Error::Ffmpeg("ffprobe missing height".into()))?;
    let fps = probe_stream_number(&probe, video, "avg_frame_rate")
        .and_then(|s| parse_fps(&s))
        .or_else(|| probe_stream_number(&probe, video, "r_frame_rate").and_then(|s| parse_fps(&s)))
        .filter(|v| v.is_finite() && *v > 0.0)
        .unwrap_or(24.0);
    let nominal_fps = probe_stream_number(&probe, video, "r_frame_rate")
        .and_then(|s| parse_fps(&s))
        .filter(|v| v.is_finite() && *v > 0.0)
        .unwrap_or(fps);
    if width == 0 || height == 0 {
        return Err(Error::Ffmpeg("ffprobe returned a 0-size frame".into()));
    }
    Ok(VideoInfo {
        width,
        height,
        fps,
        nominal_fps,
        pixel_format: probe_stream_number(&probe, video, "pix_fmt").unwrap_or_default(),
        color_range: probe_stream_number(&probe, video, "color_range").unwrap_or_default(),
        color_space: probe_stream_number(&probe, video, "color_space").unwrap_or_default(),
        color_transfer: probe_stream_number(&probe, video, "color_transfer").unwrap_or_default(),
        color_primaries: probe_stream_number(&probe, video, "color_primaries").unwrap_or_default(),
    })
}

/// Decode RGB24 frames, let `map` rewrite each packed buffer, encode H.264. Copies audio when present.
pub fn map_rgb_frames<F>(
    input_mp4: impl AsRef<Path>,
    output_mp4: impl AsRef<Path>,
    mut map: F,
) -> Result<PathBuf>
where
    F: FnMut(u32, u32, &mut [u8]) -> Result<()>,
{
    let input_mp4 = input_mp4.as_ref();
    let output_mp4 = output_mp4.as_ref();
    let info = probe_video(input_mp4)?;
    info.validate_for_grade(true)?;
    ensure_parent(output_mp4)?;
    let ffmpeg = require_ffmpeg()?;
    let in_s = input_mp4.to_string_lossy();
    let out_s = output_mp4.to_string_lossy();
    let size = format!("{}x{}", info.width, info.height);
    let fps = format!("{:.6}", info.fps);
    let frame_bytes = info.width as usize * info.height as usize * 3;

    let mut dec = Command::new(&ffmpeg)
        .args([
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            in_s.as_ref(),
            "-f",
            "rawvideo",
            "-pix_fmt",
            "rgb24",
            "-an",
            "pipe:1",
        ])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| Error::Ffmpeg(format!("spawn decode: {e}")))?;

    let mut enc = Command::new(&ffmpeg)
        .args([
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "rawvideo",
            "-pix_fmt",
            "rgb24",
            "-s",
            &size,
            "-r",
            &fps,
            "-i",
            "pipe:0",
            "-i",
            in_s.as_ref(),
            "-map",
            "0:v:0",
            "-map",
            "1:a?",
            "-map",
            "1:s?",
            "-map_metadata",
            "1",
            "-map_chapters",
            "1",
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            "-crf",
            "18",
            "-vf",
            "setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
            "-color_range",
            "tv",
            "-colorspace",
            "bt709",
            "-color_primaries",
            "bt709",
            "-color_trc",
            "bt709",
            "-c:a",
            "copy",
            "-c:s",
            "copy",
            "-movflags",
            "+faststart",
            "-shortest",
            "-y",
            out_s.as_ref(),
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| Error::Ffmpeg(format!("spawn encode: {e}")))?;

    let mut dec_stderr = dec
        .stderr
        .take()
        .ok_or_else(|| Error::Ffmpeg("decode stderr missing".into()))?;
    let mut enc_stderr = enc
        .stderr
        .take()
        .ok_or_else(|| Error::Ffmpeg("encode stderr missing".into()))?;
    let dec_stderr_task = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = dec_stderr.read_to_end(&mut bytes);
        bytes
    });
    let enc_stderr_task = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = enc_stderr.read_to_end(&mut bytes);
        bytes
    });
    let mut decoded = dec
        .stdout
        .take()
        .ok_or_else(|| Error::Ffmpeg("decode stdout missing".into()))?;
    let mut encoded = enc
        .stdin
        .take()
        .ok_or_else(|| Error::Ffmpeg("encode stdin missing".into()))?;
    let mut buf = vec![0u8; frame_bytes];
    let processing = (|| -> Result<()> {
        loop {
            match decoded.read_exact(&mut buf) {
                Ok(()) => {}
                Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => break,
                Err(e) => return Err(Error::Ffmpeg(format!("read frame: {e}"))),
            }
            map(info.width, info.height, &mut buf)?;
            encoded
                .write_all(&buf)
                .map_err(|e| Error::Ffmpeg(format!("write frame: {e}")))?;
        }
        Ok(())
    })();
    if processing.is_err() {
        let _ = dec.kill();
        let _ = enc.kill();
    }
    drop(decoded);
    drop(encoded);
    let dec_status = dec
        .wait()
        .map_err(|e| Error::Ffmpeg(format!("decode wait: {e}")))?;
    let enc_status = enc
        .wait()
        .map_err(|e| Error::Ffmpeg(format!("encode wait: {e}")))?;
    let dec_error = dec_stderr_task
        .join()
        .unwrap_or_else(|_| b"decode stderr reader failed".to_vec());
    let enc_error = enc_stderr_task
        .join()
        .unwrap_or_else(|_| b"encode stderr reader failed".to_vec());
    processing?;
    if !enc_status.success() {
        return Err(Error::Ffmpeg(format!(
            "encode failed: {}",
            String::from_utf8_lossy(&enc_error)
        )));
    }
    if !dec_status.success() && !String::from_utf8_lossy(&dec_error).trim().is_empty() {
        return Err(Error::Ffmpeg(format!(
            "decode failed: {}",
            String::from_utf8_lossy(&dec_error)
        )));
    }
    if !output_mp4.is_file() {
        return Err(Error::Ffmpeg(format!(
            "map_rgb_frames produced no file {}",
            output_mp4.display()
        )));
    }
    Ok(output_mp4.to_path_buf())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn shot_report_has_v1_note() {
        let report = ShotReport::v1("gc+wb+cc", 1.5, false);
        assert_eq!(report.transform, "gc+wb+cc");
        assert!((report.keyframe - 1.5).abs() < 1e-12);
        assert!(!report.chart_delta_e_exists);
        assert_eq!(report.note, LUT3D_NOTE);
        assert!(report.note.contains("Rec.709/sRGB"));
        assert!(report.note.contains("tetrahedral"));
        assert!(report.note.contains("the cube never includes FFC"));
        let json = serde_json::to_string(&report).unwrap();
        assert!(json.contains("chart_delta_e_exists"));
        assert!(json.contains("keyframe"));
    }

    #[test]
    fn parse_ffmpeg_duration() {
        let text = "Input #0, mov, from 'a.mp4':\n  Duration: 00:01:23.50, start: 0.000000\n";
        let secs = parse_duration_hms(text).unwrap();
        assert!((secs - 83.5).abs() < 1e-9);
    }

    #[test]
    fn find_ffmpeg_does_not_panic() {
        let _ = find_ffmpeg();
    }

    #[test]
    fn parse_fps_fraction() {
        assert!((parse_fps("30000/1001").unwrap() - 29.97002997).abs() < 1e-6);
        assert!((parse_fps("24").unwrap() - 24.0).abs() < 1e-12);
    }

    fn video_info() -> VideoInfo {
        VideoInfo {
            width: 1920,
            height: 1080,
            fps: 30.0,
            nominal_fps: 30.0,
            pixel_format: "yuv420p".into(),
            color_range: "tv".into(),
            color_space: "bt709".into(),
            color_transfer: "bt709".into(),
            color_primaries: "bt709".into(),
        }
    }

    #[test]
    fn grade_validation_rejects_hdr_and_vfr_per_frame() {
        let mut hdr = video_info();
        hdr.color_transfer = "smpte2084".into();
        assert!(hdr.validate_for_grade(false).is_err());

        let mut vfr = video_info();
        vfr.fps = 29.5;
        assert!(vfr.validate_for_grade(true).is_err());
        assert!(vfr.validate_for_grade(false).is_ok());
    }

    #[test]
    fn lut3d_filter_uses_forward_slashes() {
        let arg = lut3d_filter_arg(Path::new(r"C:\luts\shot.cube"));
        assert_eq!(arg, "lut3d=shot.cube");
        assert!(!arg.contains(':'));
        assert!(!arg.contains('\\'));
    }

    #[test]
    fn extract_frame_missing_video_errors() {
        let err = extract_frame("no_such_video.mp4", 0.0, "out.jpg").unwrap_err();
        match err {
            Error::InvalidArgument(msg) => assert!(msg.contains("not found")),
            Error::FfmpegNotFound => {}
            other => panic!("unexpected {other}"),
        }
    }

    #[test]
    fn real_ffmpeg_paths_preserve_audio_metadata_and_rec709_tags() {
        let Some(ffmpeg) = find_ffmpeg() else {
            eprintln!("skipping real FFmpeg smoke test: sidecar unavailable");
            return;
        };
        let Some(ffprobe) = sibling_ffprobe(&ffmpeg) else {
            eprintln!("skipping real FFmpeg smoke test: ffprobe sidecar unavailable");
            return;
        };
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock")
            .as_nanos();
        let root =
            std::env::temp_dir().join(format!("ccp-video-smoke-{}-{stamp}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let input = root.join("input.mp4");
        let lut_out = root.join("lut.mp4");
        let frame_out = root.join("frames.mp4");
        let cube = root.join("identity.cube");
        std::fs::write(
            &cube,
            "TITLE \"Identity\"\nLUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n",
        )
        .unwrap();

        let generated = Command::new(&ffmpeg)
            .args([
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=160x96:rate=24:duration=1",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=1000:sample_rate=48000:duration=1",
                "-metadata",
                "title=CCP-Smoke",
                "-c:v",
                "libx264",
                "-pix_fmt",
                "yuv420p",
                "-color_range",
                "tv",
                "-colorspace",
                "bt709",
                "-color_primaries",
                "bt709",
                "-color_trc",
                "bt709",
                "-c:a",
                "aac",
                "-shortest",
                &input.to_string_lossy(),
            ])
            .output()
            .unwrap();
        assert!(
            generated.status.success(),
            "{}",
            String::from_utf8_lossy(&generated.stderr)
        );

        apply_lut3d(&input, &cube, &lut_out).unwrap();
        let mut frames = 0usize;
        map_rgb_frames(&input, &frame_out, |_w, _h, _rgb| {
            frames += 1;
            Ok(())
        })
        .unwrap();
        assert!(
            frames >= 20,
            "expected a one-second 24fps stream, got {frames} frames"
        );

        for output in [&lut_out, &frame_out] {
            let inspected = Command::new(&ffprobe)
                .args([
                    "-v",
                    "error",
                    "-show_entries",
                    "stream=codec_type,color_space,color_transfer,color_primaries:format_tags=title",
                    "-of",
                    "default=nw=1",
                    &output.to_string_lossy(),
                ])
                .output()
                .unwrap();
            assert!(inspected.status.success());
            let report = String::from_utf8_lossy(&inspected.stdout);
            assert!(report.contains("codec_type=video"), "{report}");
            assert!(report.contains("codec_type=audio"), "{report}");
            assert!(report.contains("color_space=bt709"), "{report}");
            assert!(report.contains("color_transfer=bt709"), "{report}");
            assert!(report.contains("color_primaries=bt709"), "{report}");
            assert!(report.contains("TAG:title=CCP-Smoke"), "{report}");
        }

        std::fs::remove_dir_all(&root).unwrap();
    }
}
