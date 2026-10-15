//! Chart lattice assemble — faithful port of Speedy `detect.cpp`.

use std::collections::BTreeMap;

use crate::geom::{
    aabb_iou, anchors_for, canonical_center, chart_index, dist2, edge_len, homography_4,
    lattice_plausible, order_visual_quad, parse_chart_kind, patch_diag, quad_center, scale_quad,
    warp, ChartDetection, Det, CHART_COLS, CHART_ROWS, CHART_TYPES, NMS_CHARTS_IOU,
};

fn median_nth(mut values: Vec<f64>) -> Option<f64> {
    if values.is_empty() {
        return None;
    }
    let mid = values.len() / 2;
    values.select_nth_unstable_by(mid, |a, b| {
        a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal)
    });
    Some(values[mid])
}

/// Assemble one face from per-anchor detections. Requires at least four named anchors.
pub fn assemble_one(dets: &[Det], chart_type: &str, crop: f64) -> Option<(ChartDetection, f64)> {
    let type_i = chart_index(chart_type)?;
    let rows = CHART_ROWS[type_i];
    let cols = CHART_COLS[type_i];
    let specs = anchors_for(rows, cols);
    let mut best: BTreeMap<String, Det> = BTreeMap::new();
    for det in dets {
        if det.chart_type != chart_type {
            continue;
        }
        match best.get(&det.anchor_name) {
            Some(prev) if det.score <= prev.score => {}
            _ => {
                best.insert(det.anchor_name.clone(), det.clone());
            }
        }
    }
    if best.len() < 4 {
        return None;
    }

    let mut src = [crate::geom::Vec2::default(); 4];
    let mut dst = [crate::geom::Vec2::default(); 4];
    let mut n = 0usize;
    const CORNER_NAMES: [&str; 4] = ["tl", "tr", "br", "bl"];
    for name in CORNER_NAMES {
        let Some(det) = best.get(name) else {
            continue;
        };
        for spec in &specs {
            if spec.name == name {
                src[n] = canonical_center(spec, rows, cols);
                dst[n] = quad_center(det.quad);
                n += 1;
                break;
            }
        }
    }
    if n < 4 {
        n = 0;
        for (name, det) in &best {
            if n == 4 {
                break;
            }
            for spec in &specs {
                if spec.name == name {
                    src[n] = canonical_center(spec, rows, cols);
                    dst[n] = quad_center(det.quad);
                    n += 1;
                    break;
                }
            }
        }
    }
    if n < 4 {
        return None;
    }

    let h = homography_4(&src, &dst)?;
    let tl = warp(&h, crate::geom::Vec2::new(0.0, 0.0));
    let tr = warp(&h, crate::geom::Vec2::new(1.0, 0.0));
    let br = warp(&h, crate::geom::Vec2::new(1.0, 1.0));
    let bl = warp(&h, crate::geom::Vec2::new(0.0, 1.0));
    if !tl.finite() || !tr.finite() || !br.finite() || !bl.finite() {
        return None;
    }

    let mut out = ChartDetection {
        kind: parse_chart_kind(chart_type)?,
        chart_type: chart_type.to_string(),
        score: 0.0,
        corners: order_visual_quad([tl, tr, br, bl]),
        patch_cells: Vec::new(),
        patch_obbs: Vec::new(),
    };
    if !out.corners.finite() {
        return None;
    }
    out.patch_cells.reserve((rows * cols) as usize);
    out.patch_obbs.reserve((rows * cols) as usize);
    for row in 0..rows {
        for col in 0..cols {
            let x0 = f64::from(col) / f64::from(cols);
            let x1 = f64::from(col + 1) / f64::from(cols);
            let y0 = f64::from(row) / f64::from(rows);
            let y1 = f64::from(row + 1) / f64::from(rows);
            let cell = crate::geom::Quad::new(
                warp(&h, crate::geom::Vec2::new(x0, y0)),
                warp(&h, crate::geom::Vec2::new(x1, y0)),
                warp(&h, crate::geom::Vec2::new(x1, y1)),
                warp(&h, crate::geom::Vec2::new(x0, y1)),
            );
            if !cell.finite() {
                return None;
            }
            let ordered = order_visual_quad([
                cell.top_left,
                cell.top_right,
                cell.bottom_right,
                cell.bottom_left,
            ]);
            out.patch_cells.push(ordered);
            out.patch_obbs.push(scale_quad(ordered, crop));
        }
    }

    let cell = 0.5
        * (0.5
            * (edge_len(out.corners.top_left, out.corners.top_right)
                + edge_len(out.corners.bottom_left, out.corners.bottom_right))
            / f64::from(cols)
            + 0.5
                * (edge_len(out.corners.top_left, out.corners.bottom_left)
                    + edge_len(out.corners.top_right, out.corners.bottom_right))
                / f64::from(rows));
    if cell > 1e-6 {
        let mut named_err = Vec::with_capacity(best.len());
        for (name, det) in &best {
            for spec in &specs {
                if spec.name != name.as_str() {
                    continue;
                }
                let pred = warp(&h, canonical_center(spec, rows, cols));
                let act = quad_center(det.quad);
                named_err.push(edge_len(pred, act) / cell);
                break;
            }
        }
        if let Some(med) = median_nth(named_err) {
            if med > 0.45 {
                return None;
            }
        }
    }

    let score = best.values().map(|d| d.score).sum::<f64>() / best.len() as f64;
    out.score = score;
    Some((out, score))
}

pub fn assemble_type(
    dets: &[Det],
    chart_type: &str,
    crop: f64,
    width: i32,
    height: i32,
    charts: &mut Vec<ChartDetection>,
) {
    let Some(type_i) = chart_index(chart_type) else {
        return;
    };
    let mut remaining: Vec<usize> = dets
        .iter()
        .enumerate()
        .filter(|(_, det)| det.chart_type == chart_type)
        .map(|(i, _)| i)
        .collect();
    if remaining.len() < 4 {
        return;
    }

    let mut name_counts: BTreeMap<String, i32> = BTreeMap::new();
    let mut diags = Vec::with_capacity(remaining.len());
    for &idx in &remaining {
        *name_counts
            .entry(dets[idx].anchor_name.clone())
            .or_insert(0) += 1;
        diags.push(patch_diag(dets[idx].quad).max(1.0));
    }
    let mut by_name: BTreeMap<String, Vec<crate::geom::Vec2>> = BTreeMap::new();
    for &idx in &remaining {
        by_name
            .entry(dets[idx].anchor_name.clone())
            .or_default()
            .push(quad_center(dets[idx].quad));
    }
    let mut dup_names = 0;
    let mut seps = Vec::new();
    for pts in by_name.values() {
        if pts.len() < 2 {
            continue;
        }
        dup_names += 1;
        for i in 0..pts.len() {
            for j in (i + 1)..pts.len() {
                seps.push(dist2(pts[i], pts[j]).sqrt());
            }
        }
    }
    let pair_sep = median_nth(seps).unwrap_or(-1.0);
    let med_diag = median_nth(diags).unwrap_or(1.0);
    let two_face = dup_names >= 3 && pair_sep > 2.0 * med_diag;
    let cell_span = f64::from(CHART_ROWS[type_i]).hypot(f64::from(CHART_COLS[type_i]));

    let is_mid = |name: &str| {
        name == "center" || name == "top_mid" || name == "bottom_mid" || name == "left_mid"
    };
    let is_corner = |name: &str| name == "tl" || name == "tr" || name == "br" || name == "bl";
    let rank_anchor = |det: &Det, center: crate::geom::Vec2, radius: f64| -> f64 {
        let dist = dist2(quad_center(det.quad), center).sqrt();
        let soft = (radius * 0.35).max(1.0);
        let score = det.score.max(1e-3);
        let inv_r = 1.0 / radius.max(1.0);
        if two_face && name_counts.get(&det.anchor_name).copied().unwrap_or(0) >= 2 {
            return score / (dist + soft) - 0.08 * dist * inv_r;
        }
        if is_corner(&det.anchor_name) {
            return (score * score) / (dist + soft) + 0.15 * score;
        }
        score / (dist + soft) - 0.05 * dist * inv_r
    };

    while remaining.len() >= 4 {
        let mut seed = remaining[0];
        let mut have_mid = false;
        for &idx in &remaining {
            let det = &dets[idx];
            if is_mid(&det.anchor_name) {
                if !have_mid || det.score > dets[seed].score {
                    seed = idx;
                    have_mid = true;
                }
            } else if !have_mid && det.score > dets[seed].score {
                seed = idx;
            }
        }
        let seed_det = &dets[seed];
        let diag = patch_diag(seed_det.quad).max(1.0);
        let mut radius = 0.55 * diag * cell_span / 0.7;
        if two_face {
            radius = radius.min(0.82 * pair_sep);
        }
        let radius2 = radius * radius;
        let seed_c = quad_center(seed_det.quad);

        let mut bucket: BTreeMap<String, usize> = BTreeMap::new();
        let mut rank: BTreeMap<String, f64> = BTreeMap::new();
        for &idx in &remaining {
            let det = &dets[idx];
            let d2 = dist2(quad_center(det.quad), seed_c);
            if d2 > radius2 {
                continue;
            }
            let value = rank_anchor(det, seed_c, radius);
            if rank.get(&det.anchor_name).is_none_or(|prev| value > *prev) {
                bucket.insert(det.anchor_name.clone(), idx);
                rank.insert(det.anchor_name.clone(), value);
            }
        }

        if bucket.len() >= 3 {
            let mut recentre = crate::geom::Vec2::default();
            let mut n_c = 0i32;
            const CORNER_NAMES: [&str; 4] = ["tl", "tr", "br", "bl"];
            for name in CORNER_NAMES {
                if let Some(&idx) = bucket.get(name) {
                    let c = quad_center(dets[idx].quad);
                    recentre.x += c.x;
                    recentre.y += c.y;
                    n_c += 1;
                }
            }
            if n_c < 2 {
                n_c = 0;
                recentre = crate::geom::Vec2::default();
                for &idx in bucket.values() {
                    let c = quad_center(dets[idx].quad);
                    recentre.x += c.x;
                    recentre.y += c.y;
                    n_c += 1;
                }
            }
            if n_c > 0 {
                recentre.x /= f64::from(n_c);
                recentre.y /= f64::from(n_c);
                let mut refined: BTreeMap<String, usize> = BTreeMap::new();
                let mut refined_rank: BTreeMap<String, f64> = BTreeMap::new();
                for &idx in &remaining {
                    let det = &dets[idx];
                    let d2 = dist2(quad_center(det.quad), recentre);
                    if d2 > radius2 {
                        continue;
                    }
                    let value = rank_anchor(det, recentre, radius);
                    if refined_rank
                        .get(&det.anchor_name)
                        .is_none_or(|prev| value > *prev)
                    {
                        refined.insert(det.anchor_name.clone(), idx);
                        refined_rank.insert(det.anchor_name.clone(), value);
                    }
                }
                for (name, idx) in &bucket {
                    match refined.get(name) {
                        None => {
                            refined.insert(name.clone(), *idx);
                        }
                        Some(&new_idx) => {
                            let old_s = dets[*idx].score;
                            let new_s = dets[new_idx].score;
                            if old_s >= 0.85 && old_s > new_s + 0.12 {
                                refined.insert(name.clone(), *idx);
                            }
                        }
                    }
                }
                bucket = refined;
            }
        }

        if bucket.len() < 4 {
            remaining.retain(|&idx| idx != seed);
            continue;
        }

        let mut cluster = Vec::with_capacity(bucket.len());
        let mut used = Vec::with_capacity(bucket.len());
        for &idx in bucket.values() {
            cluster.push(dets[idx].clone());
            used.push(idx);
        }
        let mut accepted = false;
        if let Some((cand, _)) = assemble_one(&cluster, chart_type, crop) {
            if lattice_plausible(&cand, width, height) {
                let mut cluster_diag = 0.0;
                for det in &cluster {
                    cluster_diag += patch_diag(det.quad).max(1.0);
                }
                cluster_diag /= cluster.len() as f64;
                let face_span = patch_diag(cand.corners);
                let expected = cluster_diag * cell_span / 0.7;
                if face_span <= 2.2 * expected && face_span >= 0.2 * expected {
                    charts.push(cand);
                    accepted = true;
                }
            }
        }
        if accepted {
            remaining.retain(|idx| !used.contains(idx));
        } else {
            remaining.retain(|&idx| idx != seed);
        }
    }
}

pub fn nms_charts(mut charts: Vec<ChartDetection>) -> Vec<ChartDetection> {
    charts.sort_by(|a, b| {
        b.score
            .partial_cmp(&a.score)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    let mut kept = Vec::new();
    for chart in charts {
        let overlap = kept
            .iter()
            .any(|prev: &ChartDetection| aabb_iou(chart.corners, prev.corners) >= NMS_CHARTS_IOU);
        if !overlap {
            kept.push(chart);
        }
    }
    kept
}

/// Prefer-order + remap-if-empty, then NMS at 0.35. Matches C++ `assemble_charts`.
pub fn assemble_charts(
    dets: &[Det],
    prefer: &str,
    crop: f64,
    width: i32,
    height: i32,
) -> Vec<ChartDetection> {
    let mut order = vec![
        CHART_TYPES[0].to_string(),
        CHART_TYPES[1].to_string(),
        CHART_TYPES[2].to_string(),
    ];
    if prefer == "spyder" || prefer == "spyder_4x6" {
        order = vec![
            "spyder_4x6".into(),
            "classic_4x6".into(),
            "digital_sg_10x14".into(),
        ];
    } else if prefer == "digital-sg" || prefer == "digital_sg_10x14" {
        order = vec![
            "digital_sg_10x14".into(),
            "classic_4x6".into(),
            "spyder_4x6".into(),
        ];
    } else if prefer == "classic" || prefer == "classic_4x6" {
        order = vec![
            "classic_4x6".into(),
            "spyder_4x6".into(),
            "digital_sg_10x14".into(),
        ];
    }

    let mut charts = Vec::new();
    for ty in &order {
        assemble_type(dets, ty, crop, width, height, &mut charts);
    }
    if charts.is_empty() {
        for ty in &order {
            let remapped: Vec<Det> = dets
                .iter()
                .map(|det| Det {
                    chart_type: ty.clone(),
                    ..det.clone()
                })
                .collect();
            assemble_type(&remapped, ty, crop, width, height, &mut charts);
        }
    }
    charts = nms_charts(charts);
    if !prefer.is_empty() && !charts.is_empty() {
        let preferred = order[0].as_str();
        // C++ std::stable_partition: preferred first, relative order preserved.
        let (mut pref, rest): (Vec<_>, Vec<_>) =
            charts.into_iter().partition(|c| c.chart_type == preferred);
        pref.extend(rest);
        charts = pref;
    }
    charts
}
