//! Untimed production chart assembly for external runtimes' saved float32 tensors.
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 4 {
        return Err(
            "usage: decode_detect_tensor OUTPUT.f32 CANVAS_SIZE '[1,29,N]' [confidence]".into(),
        );
    }
    let size: i32 = args[2].parse()?;
    let shape: Vec<usize> = serde_json::from_str(&args[3])?;
    let confidence: f32 = args
        .get(4)
        .filter(|v| v.as_str() != "plane")
        .map(|v| v.parse())
        .transpose()?
        .unwrap_or(ccp_detect::DEFAULT_CONF);
    if size <= 0 || !confidence.is_finite() || !(0.0..=1.0).contains(&confidence) {
        return Err("positive canvas size and confidence in [0,1] required".into());
    }
    let bytes = std::fs::read(&args[1])?;
    if bytes.len() % 4 != 0 {
        return Err("float32 byte length mismatch".into());
    }
    let values: Vec<f32> = bytes
        .chunks_exact(4)
        .map(|v| f32::from_le_bytes(v.try_into().unwrap()))
        .collect();
    if shape.iter().try_fold(1usize, |a, &v| a.checked_mul(v)) != Some(values.len())
        || !values.iter().all(|v| v.is_finite())
    {
        return Err("tensor shape/nonfinite output mismatch".into());
    }
    if args.get(4).map(String::as_str) == Some("plane") {
        #[cfg(feature = "ort-plane")]
        let plane = ccp_detect::plane::plane_box_from_output(
            &values,
            &shape.iter().map(|v| *v as i64).collect::<Vec<_>>(),
            size,
            size,
        );
        #[cfg(not(feature = "ort-plane"))]
        return Err("plane decoding requires the ort-plane feature".into());
        #[cfg(feature = "ort-plane")]
        println!(
            "{}",
            serde_json::json!({"size":size,"output_dims":shape,"plane_box":plane,
            "coordinate_space":"model-input canvas","production_confidence":0.7,"production_shrink":0.95})
        );
        return Ok(());
    }
    let rows = ccp_detect::preds_to_obb_rows(&values, &shape);
    if rows.is_empty() {
        return Err("unsupported detector output shape".into());
    }
    let raw = ccp_detect::decode_obb(
        &rows,
        confidence,
        ccp_detect::DECODE_IOU,
        ccp_detect::DEFAULT_MAX_DET as usize,
    );
    let detections = ccp_detect::dets_from_raw(&raw, size, 1.0, 0.0, 0.0);
    let charts = ccp_detect::assemble_charts(&detections, "", ccp_detect::DEFAULT_CROP, size, size);
    println!(
        "{}",
        serde_json::json!({"size":size,"output_dims":shape,"confidence":confidence,
        "coordinate_space":"letterbox-canvas","charts":charts,"raw_detections":detections.len()})
    );
    Ok(())
}
