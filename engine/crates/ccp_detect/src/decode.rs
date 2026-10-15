//! OBB decode, letterbox (pad 114), and ProbIoU — ported from Speedy `detect.cpp`.

use crate::geom::{
    order_visual_quad, quad_center, Det, Vec2, ANCHOR_NAMES, CHART_TYPES, LETTERBOX_PAD,
    NUM_CLASSES,
};

pub const OBB_ROW: usize = 29; // 4 + 24 classes + 1 angle
pub const DECODE_CAND_CAP: usize = 30000;
pub const PROBIOU_CLASS_OFFSET: f64 = 7680.0;

#[derive(Debug, Clone, Copy)]
pub struct Cov {
    pub a: f64,
    pub b: f64,
    pub c: f64,
}

#[derive(Debug, Clone, Copy)]
pub struct RawDet {
    pub x: f64,
    pub y: f64,
    pub w: f64,
    pub h: f64,
    pub r: f64,
    pub score: f64,
    pub cls: i32,
}

#[derive(Debug, Clone)]
pub struct Letterbox {
    pub canvas: Vec<u8>,
    pub imgsz: u32,
    pub gain: f64,
    pub pad_x: f64,
    pub pad_y: f64,
}

pub fn covariance(w: f64, h: f64, r: f64) -> Cov {
    let aa = (w * w) / 12.0;
    let bb = (h * h) / 12.0;
    let c = r.cos();
    let s = r.sin();
    let c2 = c * c;
    let s2 = s * s;
    Cov {
        a: aa * c2 + bb * s2,
        b: aa * s2 + bb * c2,
        c: (aa - bb) * c * s,
    }
}

#[allow(clippy::too_many_arguments)] // Mirrors the two oriented-box tuples used by the equation.
pub fn probiou(
    ax: f64,
    ay: f64,
    aw: f64,
    ah: f64,
    ar: f64,
    bx: f64,
    by: f64,
    bw: f64,
    bh: f64,
    br: f64,
) -> f64 {
    const EPS: f64 = 1e-7;
    let a = covariance(aw, ah, ar);
    let b = covariance(bw, bh, br);
    let dx = ax - bx;
    let dy = ay - by;
    let den = (a.a + b.a) * (a.b + b.b) - (a.c + b.c) * (a.c + b.c) + EPS;
    let t1 = (((a.a + b.a) * dy * dy + (a.b + b.b) * dx * dx) / den) * 0.25;
    let t2 = (((a.c + b.c) * (bx - ax) * dy) / den) * 0.5;
    let det1 = (a.a * a.b - a.c * a.c).max(0.0);
    let det2 = (b.a * b.b - b.c * b.c).max(0.0);
    let t3 = (((a.a + b.a) * (a.b + b.b) - (a.c + b.c) * (a.c + b.c))
        / (4.0 * (det1 * det2).sqrt() + EPS)
        + EPS)
        .ln()
        * 0.5;
    let bd = (t1 + t2 + t3).clamp(EPS, 100.0);
    let hd = (1.0 - (-bd).exp() + EPS).max(0.0).sqrt();
    1.0 - hd
}

fn clamp_i32(v: i32, lo: i32, hi: i32) -> i32 {
    v.max(lo).min(hi)
}

/// Letterbox an RGB float image in [0, 1] onto an `imgsz`×`imgsz` canvas filled with 114.
pub fn letterbox_rgb(rgb: &[f32], width: i32, height: i32, imgsz: i32) -> Letterbox {
    let canvas_len = (imgsz as usize) * (imgsz as usize) * 3;
    let mut canvas = vec![LETTERBOX_PAD; canvas_len];
    if width <= 0 || height <= 0 || imgsz <= 0 {
        return Letterbox {
            canvas,
            imgsz: imgsz.max(0) as u32,
            gain: 1.0,
            pad_x: 0.0,
            pad_y: 0.0,
        };
    }
    let r = (f64::from(imgsz) / f64::from(height)).min(f64::from(imgsz) / f64::from(width));
    let nh = (f64::from(height) * r).round() as i32;
    let nw = (f64::from(width) * r).round() as i32;
    let top = (imgsz - nh) / 2;
    let left = (imgsz - nw) / 2;
    let gain = r;
    let pad_x = f64::from(left);
    let pad_y = f64::from(top);
    for y in 0..nh {
        let sy = (f64::from(y) + 0.5) * f64::from(height) / f64::from(nh) - 0.5;
        let y0 = clamp_i32(sy.floor() as i32, 0, height - 1);
        let y1 = (y0 + 1).min(height - 1);
        let fy = sy - f64::from(y0);
        for x in 0..nw {
            let sx = (f64::from(x) + 0.5) * f64::from(width) / f64::from(nw) - 0.5;
            let x0 = clamp_i32(sx.floor() as i32, 0, width - 1);
            let x1 = (x0 + 1).min(width - 1);
            let fx = sx - f64::from(x0);
            let dst = ((top + y) as usize * imgsz as usize + (left + x) as usize) * 3;
            for c in 0..3 {
                let i00 = (y0 as usize * width as usize + x0 as usize) * 3 + c;
                let i10 = (y0 as usize * width as usize + x1 as usize) * 3 + c;
                let i01 = (y1 as usize * width as usize + x0 as usize) * 3 + c;
                let i11 = (y1 as usize * width as usize + x1 as usize) * 3 + c;
                let v00 = f64::from(*rgb.get(i00).unwrap_or(&0.0));
                let v10 = f64::from(*rgb.get(i10).unwrap_or(&0.0));
                let v01 = f64::from(*rgb.get(i01).unwrap_or(&0.0));
                let v11 = f64::from(*rgb.get(i11).unwrap_or(&0.0));
                let v =
                    (1.0 - fy) * ((1.0 - fx) * v00 + fx * v10) + fy * ((1.0 - fx) * v01 + fx * v11);
                let byte = (v * 255.0).round().clamp(0.0, 255.0) as u8;
                canvas[dst + c] = byte;
            }
        }
    }
    Letterbox {
        canvas,
        imgsz: imgsz as u32,
        gain,
        pad_x,
        pad_y,
    }
}

/// Convert a prediction tensor (row-major) into `[N, 29]` rows.
/// Layout matches C++: unsqueeze 2D, transpose if `dim1 == 4+C+1`.
pub fn preds_to_obb_rows(data: &[f32], dims: &[usize]) -> Vec<[f32; OBB_ROW]> {
    let (n, get) = match *dims {
        [n, 29] => (
            n,
            Box::new(move |i: usize, j: usize| data[i * 29 + j])
                as Box<dyn Fn(usize, usize) -> f32>,
        ),
        [29, n] => (
            n,
            Box::new(move |i: usize, j: usize| data[j * n + i]) as Box<dyn Fn(usize, usize) -> f32>,
        ),
        [1, a, b] if a == 4 + NUM_CLASSES as usize + 1 => {
            let n = b;
            (
                n,
                Box::new(move |i: usize, j: usize| data[j * n + i])
                    as Box<dyn Fn(usize, usize) -> f32>,
            )
        }
        [1, a, b] => {
            let n = a;
            (
                n,
                Box::new(move |i: usize, j: usize| data[i * b + j])
                    as Box<dyn Fn(usize, usize) -> f32>,
            )
        }
        [a, b] if b == 4 + NUM_CLASSES as usize + 1 => (
            a,
            Box::new(move |i: usize, j: usize| data[i * b + j]) as Box<dyn Fn(usize, usize) -> f32>,
        ),
        _ if data.len() >= OBB_ROW && data.len().is_multiple_of(OBB_ROW) => {
            let n = data.len() / OBB_ROW;
            (
                n,
                Box::new(move |i: usize, j: usize| data[i * OBB_ROW + j])
                    as Box<dyn Fn(usize, usize) -> f32>,
            )
        }
        _ => return Vec::new(),
    };
    let mut rows = Vec::with_capacity(n);
    for i in 0..n {
        let mut row = [0.0f32; OBB_ROW];
        for (j, value) in row.iter_mut().enumerate() {
            *value = get(i, j);
        }
        rows.push(row);
    }
    rows
}

/// Decode OBB rows `[N, 29]` = `[x, y, w, h, cls0..cls23, angle]`.
pub fn decode_obb(rows: &[[f32; OBB_ROW]], conf: f32, iou: f32, max_det: usize) -> Vec<RawDet> {
    let mut cands = Vec::new();
    for row in rows {
        let mut best = row[4];
        let mut cls = 0i32;
        for c in 1..NUM_CLASSES {
            let s = row[4 + c as usize];
            if s > best {
                best = s;
                cls = c;
            }
        }
        if best < conf {
            continue;
        }
        cands.push(RawDet {
            x: f64::from(row[0]),
            y: f64::from(row[1]),
            w: f64::from(row[2]),
            h: f64::from(row[3]),
            r: f64::from(row[4 + NUM_CLASSES as usize]),
            score: f64::from(best),
            cls,
        });
    }
    cands.sort_by(|a, b| {
        b.score
            .partial_cmp(&a.score)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    if cands.len() > DECODE_CAND_CAP {
        cands.truncate(DECODE_CAND_CAP);
    }
    let mut suppressed = vec![false; cands.len()];
    let mut kept = Vec::new();
    let max_det = max_det.max(1);
    for i in 0..cands.len() {
        if suppressed[i] {
            continue;
        }
        kept.push(cands[i]);
        if kept.len() >= max_det {
            break;
        }
        for j in (i + 1)..cands.len() {
            if suppressed[j] || cands[i].cls != cands[j].cls {
                continue;
            }
            let iou_ij = probiou(
                cands[i].x + f64::from(cands[i].cls) * PROBIOU_CLASS_OFFSET,
                cands[i].y,
                cands[i].w,
                cands[i].h,
                cands[i].r,
                cands[j].x + f64::from(cands[j].cls) * PROBIOU_CLASS_OFFSET,
                cands[j].y,
                cands[j].w,
                cands[j].h,
                cands[j].r,
            );
            if iou_ij > f64::from(iou) {
                suppressed[j] = true;
            }
        }
    }
    kept
}

/// Decode a flat f32 buffer of `[N, 29]` rows.
pub fn decode_obb_flat(
    data: &[f32],
    n_rows: usize,
    conf: f32,
    iou: f32,
    max_det: usize,
) -> Vec<RawDet> {
    let rows = preds_to_obb_rows(data, &[n_rows, OBB_ROW]);
    decode_obb(&rows, conf, iou, max_det)
}

pub fn dets_from_raw(raw: &[RawDet], _imgsz: i32, gain: f64, pad_x: f64, pad_y: f64) -> Vec<Det> {
    let mut dets = Vec::with_capacity(raw.len());
    for box_ in raw {
        if box_.cls < 0 || box_.cls >= NUM_CLASSES {
            continue;
        }
        let type_i = (box_.cls / 8) as usize;
        let anchor_i = (box_.cls % 8) as usize;
        if type_i >= CHART_TYPES.len() || anchor_i >= ANCHOR_NAMES.len() {
            continue;
        }
        let cx = (box_.x - pad_x) / gain;
        let cy = (box_.y - pad_y) / gain;
        let w = box_.w / gain;
        let h = box_.h / gain;
        let c = box_.r.cos();
        let s = box_.r.sin();
        let vx = (w / 2.0) * c;
        let vy = (w / 2.0) * s;
        let ux = (-h / 2.0) * s;
        let uy = (h / 2.0) * c;
        let pts = [
            Vec2::new(cx + vx + ux, cy + vy + uy),
            Vec2::new(cx + vx - ux, cy + vy - uy),
            Vec2::new(cx - vx - ux, cy - vy - uy),
            Vec2::new(cx - vx + ux, cy - vy + uy),
        ];
        dets.push(Det {
            chart_type: CHART_TYPES[type_i].to_string(),
            anchor_name: ANCHOR_NAMES[anchor_i].to_string(),
            score: box_.score,
            quad: order_visual_quad(pts),
        });
    }
    dets.sort_by(|a, b| {
        b.score
            .partial_cmp(&a.score)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    let mut deduped = Vec::new();
    for det in dets {
        let c = quad_center(det.quad);
        let dup = deduped.iter().any(|prev: &Det| {
            let p = quad_center(prev.quad);
            let dx = c.x - p.x;
            let dy = c.y - p.y;
            dx * dx + dy * dy < 9.0
        });
        if !dup {
            deduped.push(det);
        }
    }
    deduped
}

#[cfg(test)]
mod tests {
    use super::*;

    fn row(i: usize) -> [f32; OBB_ROW] {
        let mut r = [0.0f32; OBB_ROW];
        r[0] = i as f32;
        r[4] = 0.9;
        r
    }

    #[test]
    fn preds_to_obb_rows_n_by_29() {
        let mut data = Vec::new();
        for i in 0..4 {
            data.extend_from_slice(&row(i));
        }
        let rows = preds_to_obb_rows(&data, &[4, OBB_ROW]);
        assert_eq!(rows.len(), 4);
        assert!((rows[2][0] - 2.0).abs() < 1e-6);
    }

    #[test]
    fn preds_to_obb_rows_batch_1_29_n() {
        let n = 3;
        let mut data = vec![0.0f32; 29 * n];
        for j in 0..29 {
            for i in 0..n {
                data[j * n + i] = (i + 1) as f32 + j as f32 * 0.01;
            }
        }
        let rows = preds_to_obb_rows(&data, &[1, 29, n]);
        assert_eq!(rows.len(), n);
        assert!((rows[1][0] - 2.0).abs() < 1e-4);
    }
}
