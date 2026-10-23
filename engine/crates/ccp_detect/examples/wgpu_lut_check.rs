//! Production-path parity and timing. Host tests use --require-gpu; VM acceptance
//! permits an explicitly reported CPU fallback. No experimental shader is used.
use std::{path::Path, time::Instant};
fn main() -> Result<(), String> {
    rayon::ThreadPoolBuilder::new()
        .num_threads(8)
        .build_global()
        .map_err(|e| e.to_string())?;
    let args: Vec<_> = std::env::args().collect();
    let n: usize = args
        .get(1)
        .and_then(|s| s.parse().ok())
        .unwrap_or(1_048_833);
    let gs: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(33);
    let repetitions: usize = args.get(3).and_then(|s| s.parse().ok()).unwrap_or(1);
    let mut lut = Vec::new();
    for b in 0..gs {
        for g in 0..gs {
            for r in 0..gs {
                let [r, g, b] = [r, g, b].map(|v| v as f32 / (gs - 1) as f32);
                lut.extend([
                    0.8 * r * r + 0.1 * g + 0.03 * b,
                    0.7 * g * g + 0.15 * b + 0.05 * r,
                    0.85 * b * b + 0.1 * r,
                ]);
            }
        }
    }
    let source: Vec<f32> = if let Ok(file) = std::env::var("CCP_BENCH_FILE") {
        let bytes = std::fs::read(Path::new(&file)).map_err(|e| e.to_string())?;
        if bytes.len() != n * 12 {
            return Err("fixture size mismatch".into());
        }
        bytes
            .chunks_exact(4)
            .map(|v| f32::from_le_bytes(v.try_into().unwrap()))
            .collect()
    } else {
        (0..n * 3)
            .map(|i| (i * 17 % 1009) as f32 / 1008. * 1.2 - 0.1)
            .collect()
    };
    let expected: Vec<f32> = source
        .chunks_exact(3)
        .flat_map(|p| {
            ccp_core::maps::sample_encoded_lut(&lut, gs, [p[0] as f64, p[1] as f64, p[2] as f64])
                .map(|v| v as f32)
        })
        .collect();
    let mut runs = vec![];
    let mut error = 0f32;
    let mut backend = String::new();
    let mut fallback = None;
    let mut cold_ms = 0.;
    let mut profiles = vec![];
    for run in 0..repetitions + 4 {
        let mut output = source.clone();
        let start = Instant::now();
        let execution = if args.iter().any(|a| a == "--cpu-direct") {
            let mut image = ndarray::Array3::from_shape_vec((1, n, 3), std::mem::take(&mut output))
                .map_err(|e| e.to_string())?;
            ccp_core::stages::apply_encoded_lut_inplace(&mut image, &lut, gs);
            output = image.into_raw_vec_and_offset().0;
            ccp_detect::lut_wgpu::LutExecution {
                backend: "cpu-direct".into(),
                chunks: ccp_detect::lut_chunks::ChunkExecution {
                    accelerated_pixels: 0,
                    cpu_pixels: n,
                    fallback_reason: None,
                },
                profile: None,
            }
        } else {
            ccp_detect::lut_wgpu::apply(&mut output, &lut, gs)?
        };
        let ms = start.elapsed().as_secs_f64() * 1000.;
        if run == 0 {
            cold_ms = ms;
        }
        backend = execution.backend;
        fallback = execution.chunks.fallback_reason;
        if args.iter().any(|a| a == "--require-gpu")
            && (execution.chunks.accelerated_pixels != n || execution.chunks.cpu_pixels != 0)
        {
            return Err(format!("hardware GPU required: {backend}, {fallback:?}"));
        }
        for (a, b) in output.iter().zip(&expected) {
            error = error.max((a - b).abs());
        }
        if error > 1e-5 {
            return Err(format!("parity failed: {error}"));
        }
        if run >= 4 {
            runs.push(ms);
            profiles.push(execution.profile);
        }
    }
    println!(
        "{}",
        serde_json::json!({"backend":backend,"fallback":fallback,"grid":gs,"pixels":n,
        "cold_ms":cold_ms,"max_abs_rgb_error":error,"runs_ms":runs,"profiles":profiles,"validation":"every output, every call"})
    );
    Ok(())
}
