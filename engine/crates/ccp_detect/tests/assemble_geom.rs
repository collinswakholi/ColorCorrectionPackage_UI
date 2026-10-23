use ccp_detect::{
    assemble_one, dets_from_raw, homography, lattice_plausible, letterbox_rgb, nms_charts,
    order_visual_quad, scale_quad, warp, ChartDetection, ChartKind, Det, Quad, RawDet, Vec2,
    LETTERBOX_PAD, NMS_CHARTS_IOU,
};

fn small_quad(cx: f64, cy: f64, half: f64) -> Quad {
    Quad::new(
        Vec2::new(cx - half, cy - half),
        Vec2::new(cx + half, cy - half),
        Vec2::new(cx + half, cy + half),
        Vec2::new(cx - half, cy + half),
    )
}

fn uv_to_xy(u: f64, v: f64) -> Vec2 {
    Vec2::new(100.0 + u * 600.0, 80.0 + v * 400.0)
}

fn synthetic_classic_anchors() -> Vec<Det> {
    let specs = ccp_detect::anchors_for(4, 6);
    specs
        .iter()
        .map(|spec| {
            let c = ccp_detect::canonical_center(spec, 4, 6);
            let p = uv_to_xy(c.x, c.y);
            Det {
                chart_type: "classic_4x6".into(),
                anchor_name: spec.name.to_string(),
                score: 0.92,
                quad: small_quad(p.x, p.y, 12.0),
            }
        })
        .collect()
}

fn chart_rect(score: f64, shift: f64) -> ChartDetection {
    ChartDetection {
        kind: ChartKind::Classic,
        chart_type: "classic_4x6".into(),
        score,
        corners: Quad::new(
            Vec2::new(shift, 0.0),
            Vec2::new(100.0 + shift, 0.0),
            Vec2::new(100.0 + shift, 80.0),
            Vec2::new(shift, 80.0),
        ),
        patch_cells: Vec::new(),
        patch_obbs: Vec::new(),
    }
}

#[test]
fn homography_identity() {
    let src = [
        Vec2::new(0.0, 0.0),
        Vec2::new(1.0, 0.0),
        Vec2::new(1.0, 1.0),
        Vec2::new(0.0, 1.0),
    ];
    let h = homography(&src, &src).expect("identity homography");
    let p = Vec2::new(0.3, 0.7);
    let q = warp(&h, p);
    assert!((q.x - p.x).abs() < 1e-9, "x {} vs {}", q.x, p.x);
    assert!((q.y - p.y).abs() < 1e-9, "y {} vs {}", q.y, p.y);
    let mid = warp(&h, Vec2::new(0.5, 0.5));
    assert!((mid.x - 0.5).abs() < 1e-9);
    assert!((mid.y - 0.5).abs() < 1e-9);
}

#[test]
fn nms_charts_iou_035() {
    let a = chart_rect(0.90, 0.0);
    let b = chart_rect(0.40, 5.0);
    let iou = ccp_detect::aabb_iou(a.corners, b.corners);
    assert!(iou >= NMS_CHARTS_IOU, "expected overlap >= 0.35, got {iou}");
    let kept = nms_charts(vec![b.clone(), a.clone()]);
    assert_eq!(kept.len(), 1);
    assert!((kept[0].score - 0.90).abs() < 1e-12);
}

#[test]
fn letterbox_pad_114() {
    let w = 4;
    let h = 8;
    let imgsz = 16;
    let rgb = vec![1.0f32; (w * h * 3) as usize];
    let lb = letterbox_rgb(&rgb, w, h, imgsz);
    assert_eq!(lb.canvas.len(), (imgsz * imgsz * 3) as usize);
    assert!((lb.gain - 2.0).abs() < 1e-9);
    assert!((lb.pad_x - 4.0).abs() < 1e-9);
    assert!((lb.pad_y - 0.0).abs() < 1e-9);
    for c in 0..3 {
        assert_eq!(lb.canvas[c], LETTERBOX_PAD);
        let last = ((imgsz * imgsz - 1) * 3) as usize + c;
        assert_eq!(lb.canvas[last], LETTERBOX_PAD);
    }
    let inner = 8usize * 3;
    assert_eq!(lb.canvas[inner], 255);
}

#[test]
fn classic_4x6_patch_count_24() {
    let dets = synthetic_classic_anchors();
    assert_eq!(dets.len(), 8);
    let (chart, score) = assemble_one(&dets, "classic_4x6", 0.75).expect("assemble_one");
    assert_eq!(chart.patch_cells.len(), 24);
    assert_eq!(chart.patch_obbs.len(), 24);
    assert!(score > 0.0);
    assert_eq!(chart.chart_type, "classic_4x6");
    assert!(lattice_plausible(&chart, 800, 600));
}

#[test]
fn assemble_one_eight_anchors_24_cells() {
    let dets = synthetic_classic_anchors();
    let (chart, _) = assemble_one(&dets, "classic_4x6", 0.75).expect("assemble_one");
    assert_eq!(chart.patch_cells.len(), 24);
    let inset = scale_quad(chart.patch_cells[0], 0.75);
    let c0 = ccp_detect::geom::quad_center(chart.patch_cells[0]);
    let ci = ccp_detect::geom::quad_center(inset);
    assert!((c0.x - ci.x).abs() < 1e-9);
    assert!((c0.y - ci.y).abs() < 1e-9);
}

#[test]
fn shuffle_overlapping_charts_nms_keeps_one() {
    let left = chart_rect(0.81, 0.0);
    let overlap = chart_rect(0.77, 8.0);
    assert!(ccp_detect::aabb_iou(left.corners, overlap.corners) >= 0.35);
    let kept = nms_charts(vec![left, overlap]);
    assert_eq!(kept.len(), 1);
}

#[test]
fn order_visual_quad_axis_aligned() {
    let q = order_visual_quad([
        Vec2::new(10.0, 0.0),
        Vec2::new(10.0, 8.0),
        Vec2::new(0.0, 8.0),
        Vec2::new(0.0, 0.0),
    ]);
    assert!((q.top_left.x - 0.0).abs() < 1e-9);
    assert!((q.top_left.y - 0.0).abs() < 1e-9);
    assert!((q.top_right.x - 10.0).abs() < 1e-9);
    assert!((q.bottom_right.y - 8.0).abs() < 1e-9);
}

#[test]
fn dets_from_raw_class_type_times_8() {
    let raw = [RawDet {
        x: 100.0,
        y: 80.0,
        w: 20.0,
        h: 20.0,
        r: 0.0,
        score: 0.5,
        cls: 8, // spyder tl
    }];
    let dets = dets_from_raw(&raw, 1280, 1.0, 0.0, 0.0);
    assert_eq!(dets.len(), 1);
    assert_eq!(dets[0].chart_type, "spyder_4x6");
    assert_eq!(dets[0].anchor_name, "tl");
}

#[test]
fn parse_crop_rect_xyxy() {
    let r = ccp_detect::parse_crop_rect("10,20,110,220").expect("rect");
    assert_eq!(r.x, 10);
    assert_eq!(r.y, 20);
    assert_eq!(r.width, 100);
    assert_eq!(r.height, 200);
    let full = ccp_detect::plane_or_full(640, 480, None, None);
    assert_eq!(full.width, 640);
    assert_eq!(full.height, 480);
}
