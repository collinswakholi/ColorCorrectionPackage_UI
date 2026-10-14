//! Geometry types and helpers ported from Speedy `detect.cpp`.

// The Gaussian-elimination helper indexes a fixed augmented matrix by pivot column.
#![allow(clippy::needless_range_loop)]

use serde::{Deserialize, Serialize};

pub const ANCHOR_NAMES: [&str; 8] = [
    "tl",
    "tr",
    "br",
    "bl",
    "top_mid",
    "bottom_mid",
    "left_mid",
    "center",
];

pub const CHART_TYPES: [&str; 3] = ["classic_4x6", "spyder_4x6", "digital_sg_10x14"];
pub const CHART_ROWS: [i32; 3] = [4, 4, 10];
pub const CHART_COLS: [i32; 3] = [6, 6, 14];
pub const NUM_CLASSES: i32 = 24;
pub const PRIMARY_SIZE: i32 = 1280;
pub const RECALL_SIZE: i32 = 1536;
pub const DEFAULT_CONF: f32 = 0.15;
pub const DECODE_IOU: f32 = 0.7;
pub const NMS_CHARTS_IOU: f64 = 0.35;
pub const DEFAULT_CROP: f64 = 0.75;
pub const DEFAULT_MAX_DET: i32 = 300;
pub const LETTERBOX_PAD: u8 = 114;

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct Vec2 {
    pub x: f64,
    pub y: f64,
}

impl Vec2 {
    pub fn new(x: f64, y: f64) -> Self {
        Self { x, y }
    }

    pub fn finite(self) -> bool {
        self.x.is_finite() && self.y.is_finite()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize, Default)]
pub struct Quad {
    pub top_left: Vec2,
    pub top_right: Vec2,
    pub bottom_right: Vec2,
    pub bottom_left: Vec2,
}

impl Default for Vec2 {
    fn default() -> Self {
        Self { x: 0.0, y: 0.0 }
    }
}

impl Quad {
    pub fn new(top_left: Vec2, top_right: Vec2, bottom_right: Vec2, bottom_left: Vec2) -> Self {
        Self {
            top_left,
            top_right,
            bottom_right,
            bottom_left,
        }
    }

    pub fn points(self) -> [Vec2; 4] {
        [
            self.top_left,
            self.top_right,
            self.bottom_right,
            self.bottom_left,
        ]
    }

    pub fn finite(self) -> bool {
        self.top_left.finite()
            && self.top_right.finite()
            && self.bottom_right.finite()
            && self.bottom_left.finite()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum ChartKind {
    #[default]
    Classic,
    Passport,
    Spyder,
    DigitalSg,
}

impl ChartKind {
    pub fn id(self) -> &'static str {
        match self {
            Self::Passport => "passport",
            Self::Spyder => "spyder",
            Self::DigitalSg => "digital-sg",
            Self::Classic => "classic",
        }
    }

    pub fn detector_name(self) -> &'static str {
        match self {
            Self::Spyder => "spyder_4x6",
            Self::DigitalSg => "digital_sg_10x14",
            Self::Classic | Self::Passport => "classic_4x6",
        }
    }

    pub fn rows_cols(self) -> (i32, i32) {
        match self {
            Self::DigitalSg => (10, 14),
            Self::Classic | Self::Passport | Self::Spyder => (4, 6),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Det {
    pub chart_type: String,
    pub anchor_name: String,
    pub score: f64,
    pub quad: Quad,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
pub struct ChartDetection {
    pub kind: ChartKind,
    pub chart_type: String,
    pub score: f64,
    pub corners: Quad,
    pub patch_cells: Vec<Quad>,
    pub patch_obbs: Vec<Quad>,
}

#[derive(Debug, Clone, Copy)]
pub struct AnchorGeom {
    pub name: &'static str,
    pub row: i32,
    pub col: i32,
}

pub fn chart_index(name: &str) -> Option<usize> {
    CHART_TYPES.iter().position(|t| *t == name)
}

pub fn chart_rows_cols(chart_type: &str) -> Option<(i32, i32)> {
    let i = chart_index(chart_type)?;
    Some((CHART_ROWS[i], CHART_COLS[i]))
}

pub fn cli_chart_from_detector(chart_type: &str) -> &'static str {
    match chart_type {
        "spyder_4x6" => "spyder",
        "digital_sg_10x14" => "digital-sg",
        _ => "classic",
    }
}

pub fn class_id(type_i: i32, anchor_i: i32) -> i32 {
    type_i * 8 + anchor_i
}

pub fn normalize_chart_id(value: &str) -> String {
    value
        .chars()
        .map(|ch| match ch {
            '_' | ' ' | '.' => '-',
            c => c.to_ascii_lowercase(),
        })
        .collect()
}

pub fn parse_chart_kind(value: &str) -> Option<ChartKind> {
    let id = normalize_chart_id(value);
    match id.as_str() {
        "classic" | "classic-4x6" | "colorchecker" | "colorchecker-classic" => {
            Some(ChartKind::Classic)
        }
        "passport" | "colorchecker-passport" => Some(ChartKind::Passport),
        "spyder" | "spyder-4x6" | "spydercheckr" | "spyder-checkr" => Some(ChartKind::Spyder),
        "digital-sg" | "digital-sg-10x14" | "sg" | "colorchecker-digital-sg" => {
            Some(ChartKind::DigitalSg)
        }
        _ => None,
    }
}

pub fn quad_center(q: Quad) -> Vec2 {
    Vec2 {
        x: 0.25 * (q.top_left.x + q.top_right.x + q.bottom_right.x + q.bottom_left.x),
        y: 0.25 * (q.top_left.y + q.top_right.y + q.bottom_right.y + q.bottom_left.y),
    }
}

pub fn order_visual_quad(pts_in: [Vec2; 4]) -> Quad {
    let mut pts = pts_in;
    let mut center = Vec2::default();
    for p in &pts {
        center.x += p.x;
        center.y += p.y;
    }
    center.x *= 0.25;
    center.y *= 0.25;
    pts.sort_by(|a, b| {
        let aa = (a.y - center.y).atan2(a.x - center.x);
        let bb = (b.y - center.y).atan2(b.x - center.x);
        aa.partial_cmp(&bb).unwrap_or(std::cmp::Ordering::Equal)
    });
    let mut area = 0.0;
    for i in 0..4 {
        let a = pts[i];
        let b = pts[(i + 1) % 4];
        area += a.x * b.y - a.y * b.x;
    }
    if area < 0.0 {
        pts.reverse();
    }
    let mut start = 0usize;
    let mut best = pts[0].x + pts[0].y;
    for (i, p) in pts.iter().enumerate().skip(1) {
        let s = p.x + p.y;
        if s < best {
            best = s;
            start = i;
        }
    }
    let ordered = [
        pts[start],
        pts[(start + 1) % 4],
        pts[(start + 2) % 4],
        pts[(start + 3) % 4],
    ];
    Quad::new(ordered[0], ordered[1], ordered[2], ordered[3])
}

pub fn scale_quad(q: Quad, crop: f64) -> Quad {
    let c = quad_center(q);
    let k = crop.max(0.02);
    let scale = |p: Vec2| Vec2 {
        x: c.x + (p.x - c.x) * k,
        y: c.y + (p.y - c.y) * k,
    };
    Quad::new(
        scale(q.top_left),
        scale(q.top_right),
        scale(q.bottom_right),
        scale(q.bottom_left),
    )
}

pub fn solve_8x8(a: &[[f64; 8]; 8], b: &[f64; 8]) -> Option<[f64; 8]> {
    let mut m = [[0.0f64; 9]; 8];
    for r in 0..8 {
        for c in 0..8 {
            m[r][c] = a[r][c];
        }
        m[r][8] = b[r];
    }
    for col in 0..8 {
        let mut pivot = col;
        for r in (col + 1)..8 {
            if m[r][col].abs() > m[pivot][col].abs() {
                pivot = r;
            }
        }
        if m[pivot][col].abs() < 1e-12 {
            return None;
        }
        if pivot != col {
            for c in col..9 {
                let tmp = m[col][c];
                m[col][c] = m[pivot][c];
                m[pivot][c] = tmp;
            }
        }
        let diag = m[col][col];
        for c in col..9 {
            m[col][c] /= diag;
        }
        for r in 0..8 {
            if r == col {
                continue;
            }
            let f = m[r][col];
            for c in col..9 {
                m[r][c] -= f * m[col][c];
            }
        }
    }
    let mut x = [0.0f64; 8];
    for r in 0..8 {
        x[r] = m[r][8];
    }
    Some(x)
}

pub fn homography_4(src: &[Vec2; 4], dst: &[Vec2; 4]) -> Option<[f64; 9]> {
    let mut a = [[0.0f64; 8]; 8];
    let mut b = [0.0f64; 8];
    for i in 0..4 {
        let x = src[i].x;
        let y = src[i].y;
        let u = dst[i].x;
        let v = dst[i].y;
        let r0 = i * 2;
        let r1 = r0 + 1;
        a[r0][0] = x;
        a[r0][1] = y;
        a[r0][2] = 1.0;
        a[r0][6] = -u * x;
        a[r0][7] = -u * y;
        b[r0] = u;
        a[r1][3] = x;
        a[r1][4] = y;
        a[r1][5] = 1.0;
        a[r1][6] = -v * x;
        a[r1][7] = -v * y;
        b[r1] = v;
    }
    let x = solve_8x8(&a, &b)?;
    let mut h = [0.0f64; 9];
    h[..8].copy_from_slice(&x);
    h[8] = 1.0;
    Some(h)
}

pub fn homography(src: &[Vec2; 4], dst: &[Vec2; 4]) -> Option<[f64; 9]> {
    homography_4(src, dst)
}

pub fn warp(h: &[f64; 9], p: Vec2) -> Vec2 {
    let z = h[6] * p.x + h[7] * p.y + h[8];
    if z.abs() < 1e-12 {
        return p;
    }
    Vec2 {
        x: (h[0] * p.x + h[1] * p.y + h[2]) / z,
        y: (h[3] * p.x + h[4] * p.y + h[5]) / z,
    }
}

pub fn anchors_for(rows: i32, cols: i32) -> [AnchorGeom; 8] {
    let r_bot = rows - 1;
    let c_right = cols - 1;
    let r_center = rows / 2;
    let c_center = cols / 2;
    [
        AnchorGeom {
            name: "tl",
            row: 0,
            col: 0,
        },
        AnchorGeom {
            name: "tr",
            row: 0,
            col: c_right,
        },
        AnchorGeom {
            name: "br",
            row: r_bot,
            col: c_right,
        },
        AnchorGeom {
            name: "bl",
            row: r_bot,
            col: 0,
        },
        AnchorGeom {
            name: "top_mid",
            row: 0,
            col: c_center,
        },
        AnchorGeom {
            name: "bottom_mid",
            row: r_bot,
            col: c_center,
        },
        AnchorGeom {
            name: "left_mid",
            row: r_center,
            col: 0,
        },
        AnchorGeom {
            name: "center",
            row: r_center,
            col: c_center,
        },
    ]
}

pub fn canonical_center(spec: &AnchorGeom, rows: i32, cols: i32) -> Vec2 {
    Vec2 {
        x: (f64::from(spec.col) + 0.5) / f64::from(cols),
        y: (f64::from(spec.row) + 0.5) / f64::from(rows),
    }
}

pub fn dist2(a: Vec2, b: Vec2) -> f64 {
    let dx = a.x - b.x;
    let dy = a.y - b.y;
    dx * dx + dy * dy
}

pub fn patch_diag(q: Quad) -> f64 {
    (q.top_left.x - q.bottom_right.x).hypot(q.top_left.y - q.bottom_right.y)
}

pub fn quad_area(q: Quad) -> f64 {
    let pts = q.points();
    let mut area = 0.0;
    for i in 0..4 {
        let a = pts[i];
        let b = pts[(i + 1) % 4];
        area += a.x * b.y - a.y * b.x;
    }
    0.5 * area
}

pub fn edge_len(a: Vec2, b: Vec2) -> f64 {
    (a.x - b.x).hypot(a.y - b.y)
}

pub fn face_edge_imbalance(q: Quad) -> f64 {
    let edges = [
        edge_len(q.top_left, q.top_right),
        edge_len(q.top_right, q.bottom_right),
        edge_len(q.bottom_right, q.bottom_left),
        edge_len(q.bottom_left, q.top_left),
    ];
    let min_e = edges[0].min(edges[1]).min(edges[2]).min(edges[3]);
    if min_e <= 1e-6 {
        return 1.0;
    }
    let ratio_a = edges[0].min(edges[2]) / edges[0].max(edges[2]);
    let ratio_b = edges[1].min(edges[3]) / edges[1].max(edges[3]);
    1.0 - ratio_a.min(ratio_b)
}

pub fn lattice_plausible(chart: &ChartDetection, width: i32, height: i32) -> bool {
    if !chart.corners.finite() {
        return false;
    }
    for patch in &chart.patch_obbs {
        if !patch.finite() {
            return false;
        }
    }
    let area = quad_area(chart.corners).abs();
    if area < 64.0 {
        return false;
    }
    if face_edge_imbalance(chart.corners) > 0.38 {
        return false;
    }
    let c = quad_center(chart.corners);
    let pad_x = 0.25 * f64::from(width.max(1));
    let pad_y = 0.25 * f64::from(height.max(1));
    if c.x < -pad_x
        || c.y < -pad_y
        || c.x > f64::from(width) + pad_x
        || c.y > f64::from(height) + pad_y
    {
        return false;
    }
    let span = (chart.corners.top_left.x - chart.corners.bottom_right.x)
        .hypot(chart.corners.top_left.y - chart.corners.bottom_right.y);
    let limit = 4.0 * f64::from(width.max(height));
    span < limit
}

pub fn aabb_iou(a: Quad, b: Quad) -> f64 {
    let box_of = |q: Quad| -> [f64; 4] {
        let xs = [
            q.top_left.x,
            q.top_right.x,
            q.bottom_right.x,
            q.bottom_left.x,
        ];
        let ys = [
            q.top_left.y,
            q.top_right.y,
            q.bottom_right.y,
            q.bottom_left.y,
        ];
        let x0 = xs[0].min(xs[1]).min(xs[2]).min(xs[3]);
        let y0 = ys[0].min(ys[1]).min(ys[2]).min(ys[3]);
        let x1 = xs[0].max(xs[1]).max(xs[2]).max(xs[3]);
        let y1 = ys[0].max(ys[1]).max(ys[2]).max(ys[3]);
        [x0, y0, x1, y1]
    };
    let aa = box_of(a);
    let bb = box_of(b);
    let ix0 = aa[0].max(bb[0]);
    let iy0 = aa[1].max(bb[1]);
    let ix1 = aa[2].min(bb[2]);
    let iy1 = aa[3].min(bb[3]);
    let iw = (ix1 - ix0).max(0.0);
    let ih = (iy1 - iy0).max(0.0);
    let inter = iw * ih;
    let area_a = (aa[2] - aa[0]).max(0.0) * (aa[3] - aa[1]).max(0.0);
    let area_b = (bb[2] - bb[0]).max(0.0) * (bb[3] - bb[1]).max(0.0);
    let denom = area_a + area_b - inter;
    if denom <= 1e-9 {
        0.0
    } else {
        inter / denom
    }
}

pub fn format_quad(quad: Quad) -> String {
    let one = |p: Vec2| format!("{:.3},{:.3}", p.x, p.y);
    format!(
        "{};{};{};{}",
        one(quad.top_left),
        one(quad.top_right),
        one(quad.bottom_right),
        one(quad.bottom_left)
    )
}

pub fn format_xy(p: Vec2) -> [f64; 2] {
    [
        if p.x.is_finite() { p.x } else { 0.0 },
        if p.y.is_finite() { p.y } else { 0.0 },
    ]
}
