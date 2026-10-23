//! Sequential LUT replacement, grid change and backend selection recovery.
fn main() -> Result<(), String> {
    let mut observations = vec![];
    for (backend, adapter) in [
        ("dx12", "nvidia"),
        ("dx12", "nonexistent-adapter"),
        ("vulkan", "intel"),
        ("dx12", "nvidia"),
    ] {
        std::env::set_var("CCP_LUT_BACKEND", backend);
        std::env::set_var("CCP_LUT_ADAPTER", adapter);
        for (gs, scale) in [(33usize, 0.8f32), (33, 0.4), (17, 0.7), (2, 0.5), (33, 0.8)] {
            let mut lut = vec![];
            for b in 0..gs {
                for g in 0..gs {
                    for r in 0..gs {
                        lut.extend([r, g, b].map(|v| (v as f32 / (gs - 1) as f32).powi(2) * scale));
                    }
                }
            }
            let mut image: Vec<f32> = (0..771).map(|v| v as f32 / 770.).collect();
            let expected: Vec<f32> = image
                .chunks_exact(3)
                .flat_map(|p| {
                    ccp_core::maps::sample_encoded_lut(
                        &lut,
                        gs,
                        [p[0] as f64, p[1] as f64, p[2] as f64],
                    )
                    .map(|v| v as f32)
                })
                .collect();
            let execution = ccp_detect::lut_wgpu::apply(&mut image, &lut, gs)?;
            if adapter == "nonexistent-adapter" {
                assert_eq!(execution.backend, "cpu");
            } else {
                assert!(execution.backend.starts_with("wgpu:"), "{:?}", execution);
            }
            let error = image
                .iter()
                .zip(&expected)
                .map(|(a, b)| (a - b).abs())
                .fold(0f32, f32::max);
            assert!(error <= 1e-5);
            observations.push(serde_json::json!({"backend":execution.backend,"grid":gs,"scale":scale,"error":error}));
        }
    }
    println!(
        "{}",
        serde_json::json!({"checks":observations,"status":"passed"})
    );
    Ok(())
}
