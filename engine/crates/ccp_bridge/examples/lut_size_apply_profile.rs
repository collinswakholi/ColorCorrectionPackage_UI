use ccp_bridge::run_pipeline::{load_saved, rgb_image_to_array3};
use ccp_core::maps::build_encoded_lut;
use ccp_core::sample::load_image;
use ccp_core::stages::apply_encoded_lut_inplace;
use serde_json::json;
use std::path::{Path, PathBuf};
use std::time::Instant;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("workspace root");
    let source = rgb_image_to_array3(&load_image(root.join("test_images/ColorCard_Combo.JPG"))?)?;
    let saved = load_saved(root.join("_ui_stress/matrix/rpcc2-full-ffc-linear/model.json"))?;
    let (pipeline, _) = saved.lut_pipeline()?;
    let (model, settings) = pipeline.cc.as_ref().expect("CC model");
    let mut rows = Vec::new();
    for size in [17usize, 33usize] {
        let lut = build_encoded_lut(model, settings, size)?;
        let mut times = Vec::new();
        for iteration in 0..6 {
            let mut work = source.clone();
            let started = Instant::now();
            apply_encoded_lut_inplace(&mut work, &lut, size);
            if iteration > 0 {
                times.push(started.elapsed().as_secs_f64() * 1000.0);
            }
        }
        times.sort_by(|a, b| a.total_cmp(b));
        rows.push(json!({
            "lut_size": size,
            "lut_bytes": lut.len() * std::mem::size_of::<f32>(),
            "p50_ms": times[times.len() / 2],
            "mean_ms": times.iter().sum::<f64>() / times.len() as f64,
            "min_ms": times.first(),
            "max_ms": times.last(),
            "times_ms": times,
        }));
    }
    let report = json!({
        "pixels": [source.shape()[1], source.shape()[0]],
        "rows": rows,
        "notes": "CPU trilinear sampling only; source clone is outside each timed interval."
    });
    let output: PathBuf = root.join("_ui_stress/lut-size-apply-profile.json");
    std::fs::write(&output, serde_json::to_string_pretty(&report)?)?;
    println!("{}", serde_json::to_string_pretty(&report)?);
    Ok(())
}
