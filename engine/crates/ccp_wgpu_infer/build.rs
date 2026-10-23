use sha2::{Digest, Sha256};
fn main() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let models = [
        "models/plane/plane_det_model_YOLO_512_n.onnx",
        "models/opt2/best_assemble_fp32_1280.onnx",
        "models/opt2/best_assemble_fp32_1536.onnx",
    ];
    let mut generator = burn_onnx::ModelGen::new();
    generator
        .out_dir("models")
        .load_strategy(burn_onnx::LoadStrategy::Embedded);
    for (index, relative) in models.iter().enumerate() {
        let path = root.join(relative);
        println!("cargo:rerun-if-changed={}", path.display());
        let bytes = std::fs::read(&path).expect("bundled ONNX model missing");
        println!(
            "cargo:rustc-env=CCP_MODEL_HASH_{index}={:x}",
            Sha256::digest(&bytes)
        );
        // These exports contain their weights. Reject external tensors rather
        // than silently producing a compiled model with incomplete provenance.
        assert!(
            !path.with_extension("onnx.data").exists(),
            "external model data needs an explicit provenance contract"
        );
        generator.input(path.to_str().expect("model path must be UTF-8"));
    }
    generator.run_from_script();
}
