//! Handshake JSON with C++ field names from `make_handshake` in `detect.cpp`.

use std::path::Path;

use serde::Serialize;

use crate::geom::{cli_chart_from_detector, format_quad, format_xy, ChartDetection};
use crate::DetectResult;

#[derive(Serialize)]
struct CornersJson {
    tl: [f64; 2],
    tr: [f64; 2],
    br: [f64; 2],
    bl: [f64; 2],
}

#[derive(Serialize)]
struct FaceJson {
    id: i32,
    chart_type: String,
    cli_chart: String,
    score: f64,
    accepted: bool,
    rows: i32,
    cols: i32,
    quad: String,
    corners: CornersJson,
    patch_cells: Vec<[[f64; 2]; 4]>,
    patch_obbs: Vec<[[f64; 2]; 4]>,
}

#[derive(Serialize)]
struct HandshakeJson {
    ok: bool,
    backend: String,
    runtime: String,
    device: String,
    inference: serde_json::Value,
    chart_type: String,
    cli_chart: String,
    score: f64,
    accepted: bool,
    crop: f64,
    rows: i32,
    cols: i32,
    n_dets: i32,
    n_charts: i32,
    selected: i32,
    use_all: bool,
    recall_source: String,
    image: String,
    quad: String,
    corners: CornersJson,
    patch_cells: Vec<[[f64; 2]; 4]>,
    patch_obbs: Vec<[[f64; 2]; 4]>,
    charts: Vec<FaceJson>,
}

fn quad_points(q: crate::geom::Quad) -> [[f64; 2]; 4] {
    [
        format_xy(q.top_left),
        format_xy(q.top_right),
        format_xy(q.bottom_right),
        format_xy(q.bottom_left),
    ]
}

fn corners_of(q: crate::geom::Quad) -> CornersJson {
    CornersJson {
        tl: format_xy(q.top_left),
        tr: format_xy(q.top_right),
        br: format_xy(q.bottom_right),
        bl: format_xy(q.bottom_left),
    }
}

fn generic_path(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

fn cells_of(detection: &ChartDetection) -> Vec<[[f64; 2]; 4]> {
    let src = if detection.patch_cells.is_empty() {
        &detection.patch_obbs
    } else {
        &detection.patch_cells
    };
    src.iter().copied().map(quad_points).collect()
}

fn obbs_of(detection: &ChartDetection) -> Vec<[[f64; 2]; 4]> {
    detection
        .patch_obbs
        .iter()
        .copied()
        .map(quad_points)
        .collect()
}

fn face_json(detection: &ChartDetection, id: i32) -> FaceJson {
    let (rows, cols) = detection.kind.rows_cols();
    FaceJson {
        id,
        chart_type: detection.chart_type.clone(),
        cli_chart: cli_chart_from_detector(&detection.chart_type).to_string(),
        score: detection.score,
        accepted: true,
        rows,
        cols,
        quad: format_quad(detection.corners),
        corners: corners_of(detection.corners),
        patch_cells: cells_of(detection),
        patch_obbs: obbs_of(detection),
    }
}

/// Build handshake JSON matching C++ `make_handshake` field names.
pub fn make_handshake(
    result: &DetectResult,
    charts: &[ChartDetection],
    crop: f64,
    image_path: &Path,
) -> Result<String, serde_json::Error> {
    let detection = charts
        .first()
        .cloned()
        .unwrap_or_else(|| result.detection.clone());
    let (rows, cols) = detection.kind.rows_cols();
    let payload = HandshakeJson {
        ok: true,
        backend: result.backend.clone(),
        runtime: result.runtime.clone(),
        device: result.device.clone(),
        inference: result.inference.clone(),
        chart_type: detection.chart_type.clone(),
        cli_chart: if result.cli_chart.is_empty() {
            cli_chart_from_detector(&detection.chart_type).to_string()
        } else {
            result.cli_chart.clone()
        },
        score: detection.score,
        accepted: true,
        crop,
        rows,
        cols,
        n_dets: result.n_dets,
        n_charts: result.n_charts,
        selected: 0,
        use_all: false,
        recall_source: result.recall_source.clone(),
        image: generic_path(image_path),
        quad: if result.quad.is_empty() {
            format_quad(detection.corners)
        } else {
            result.quad.clone()
        },
        corners: corners_of(detection.corners),
        patch_cells: cells_of(&detection),
        patch_obbs: obbs_of(&detection),
        charts: charts
            .iter()
            .enumerate()
            .map(|(i, c)| face_json(c, i as i32))
            .collect(),
    };
    let mut text = serde_json::to_string_pretty(&payload)?;
    if !text.ends_with('\n') {
        text.push('\n');
    }
    Ok(text)
}

pub fn warmup_json(device: &str) -> String {
    serde_json::json!({"ok":true,"backend":"onnx","runtime":if device.contains(":wgpu:") {"burn-wgpu"} else {"ort"},"device":device,"warmed":true}).to_string()+"\n"
}
