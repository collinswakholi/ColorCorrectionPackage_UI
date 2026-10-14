//! LUT ORT vs CPU `sample_encoded_lut` parity.

use ccp_core::color::Vec3;
use ccp_core::maps::sample_encoded_lut;

#[cfg(feature = "ort-plane")]
use ccp_detect::apply_encoded_lut_ort;
#[cfg(feature = "ort-plane")]
use ccp_detect::paths::lut_onnx_path;

fn identity_lut(gs: usize) -> Vec<f32> {
    let mut lut = vec![0.0f32; gs * gs * gs * 3];
    for bi in 0..gs {
        for gi in 0..gs {
            for ri in 0..gs {
                let idx = ((bi * gs + gi) * gs + ri) * 3;
                let v = ri as f32 / (gs - 1) as f32;
                lut[idx] = v;
                lut[idx + 1] = gi as f32 / (gs - 1) as f32;
                lut[idx + 2] = bi as f32 / (gs - 1) as f32;
            }
        }
    }
    lut
}

#[test]
fn lut_cpu_reference_edges() {
    let gs = 17;
    let lut = identity_lut(gs);
    let black = sample_encoded_lut(&lut, gs, Vec3::from([0.0, 0.0, 0.0]));
    assert!((black[0]).abs() < 1e-6);
    let white = sample_encoded_lut(&lut, gs, Vec3::from([1.0, 1.0, 1.0]));
    assert!((white[0] - 1.0).abs() < 1e-5);
}

#[cfg(feature = "ort-plane")]
#[test]
fn lut_ort_matches_cpu() {
    if lut_onnx_path().is_none() {
        eprintln!("skip lut_ort_matches_cpu: missing lut3d_trilinear.onnx");
        return;
    }
    for gs in [17, 33] {
        let mut lut = identity_lut(gs);
        for color in lut.chunks_exact_mut(3) {
            let (r, g, b) = (color[0], color[1], color[2]);
            color[0] = 0.85 * r * r + 0.1 * g + 0.05 * b;
            color[1] = 0.8 * g.sqrt() + 0.15 * b + 0.05 * r;
            color[2] = 0.9 * b + 0.1 * g * r;
        }
        let probes: Vec<[f32; 3]> = vec![
            [0.0, 0.0, 0.0],
            [1.0, 1.0, 1.0],
            [0.5, 0.25, 0.75],
            [0.1, 0.9, 0.3],
        ];
        let mut pixels: Vec<f32> = probes.iter().flat_map(|p| p.iter().copied()).collect();
        let n = probes.len();
        apply_encoded_lut_ort(&mut pixels, 1, n, &lut, gs).expect("ort lut apply");
        for (i, rgb) in probes.iter().enumerate() {
            let cpu = sample_encoded_lut(&lut, gs, [rgb[0] as f64, rgb[1] as f64, rgb[2] as f64]);
            for c in 0..3 {
                let got = pixels[i * 3 + c];
                let want = cpu[c] as f32;
                assert!(
                    (got - want).abs() <= 1e-5,
                    "pixel {i} ch {c}: ort {got} cpu {want}"
                );
            }
        }
    }
}

#[cfg(feature = "ort-plane")]
#[test]
fn lut_ort_chunked_ramp() {
    if lut_onnx_path().is_none() {
        eprintln!("skip lut_ort_chunked_ramp: missing lut3d_trilinear.onnx");
        return;
    }
    let gs = 33;
    let lut = identity_lut(gs);
    let n = ccp_detect::lut_ort::LUT_CHUNK + 1;
    let mut pixels: Vec<f32> = (0..n)
        .flat_map(|i| {
            let t = i as f32 / (n - 1) as f32;
            [t, 1.0 - t, t * 0.5]
        })
        .collect();
    let expect: Vec<f32> = pixels
        .chunks(3)
        .map(|rgb| {
            sample_encoded_lut(
                &lut,
                gs,
                Vec3::from([rgb[0] as f64, rgb[1] as f64, rgb[2] as f64]),
            )
        })
        .flat_map(|v| [v[0] as f32, v[1] as f32, v[2] as f32])
        .collect();
    apply_encoded_lut_ort(&mut pixels, 1, n, &lut, gs).expect("ort lut chunked");
    for (got, want) in pixels.iter().zip(expect.iter()) {
        assert!((got - want).abs() <= 1e-4, "ort {got} cpu {want}");
    }
}
