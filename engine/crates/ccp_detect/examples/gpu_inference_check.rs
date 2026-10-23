//! GPU-only correctness/profiling harness; no CPU execution provider is registered.
use std::{path::PathBuf, time::Instant};
use ccp_detect::gpu_inference::{commit_session, GpuEp};
use ort::value::Tensor;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let result = run();
    // Failed provider initialization can still register a native plugin.
    // Release it before process teardown on both success and error paths.
    let cleanup = ccp_detect::gpu_inference::shutdown();
    result?;
    cleanup?;
    Ok(())
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = std::env::args().collect();
    let model = PathBuf::from(&args[1]);
    let size: usize = args[2].parse()?;
    let ep = match args[3].as_str() { "directml" => GpuEp::DirectMl, "vulkan" => GpuEp::Vulkan, "webgpu" => GpuEp::WebGpu, _ => return Err("unsupported GPU provider".into()) };
    let image = image::open(&args[4])?.to_rgb8();
    let input: Vec<f32> = if size == 512 {
        ccp_detect::plane::resize_rgb_to_nchw(image.as_raw(), image.width(), image.height(), size as i32)
    } else {
        let floats: Vec<f32> = image.as_raw().iter().map(|x| *x as f32 / 255.).collect();
        let lb = ccp_detect::letterbox_rgb(&floats, image.width() as i32, image.height() as i32, size as i32);
        (0..3).flat_map(|c| { let canvas=&lb.canvas; (0..size*size).map(move |i| canvas[i*3+c] as f32 /255.) }).collect()
    };
    let start=Instant::now();
    let mut session=commit_session(&model,ep)?;
    let initialization_ms=start.elapsed().as_secs_f64()*1000.;
    let count: usize=args.get(6).map(|v| v.parse()).transpose()?.unwrap_or(20);
    let mut times=Vec::new(); let mut first=Vec::new(); let mut shape=Vec::new(); let mut max_repeat_error=0f32;
    for i in 0..count+3 {
        let tensor=Tensor::from_array(([1usize,3,size,size],input.clone()))?;
        let start=Instant::now();
        let outputs=session.run(ort::inputs![tensor])?;
        let (_, value)=outputs.iter().next().ok_or("no output")?;
        let (dims, values)=value.try_extract_tensor::<f32>()?;
        let elapsed=start.elapsed().as_secs_f64()*1000.;
        if !values.iter().all(|v|v.is_finite()) { return Err("nonfinite inference output".into()); }
        if i==0 { first=values.to_vec(); shape=dims.iter().copied().collect::<Vec<_>>(); }
        if values.len()!=first.len() {return Err("output shape changed".into());}
        max_repeat_error=values.iter().zip(&first).fold(max_repeat_error,|m,(a,b)|m.max((a-b).abs()));
        if i>=3 {times.push(elapsed);}
    }
    let profile=if std::env::var_os("CCP_INFERENCE_PROFILE").is_some() {Some(session.end_profiling()?)} else {None};
    std::fs::write(&args[5],serde_json::to_vec(&serde_json::json!({"provider":ep.key(),"model":model,"size":size,"initialization_ms":initialization_ms,"times_ms":times,"shape":shape,"output":first,"max_repeat_error":max_repeat_error,"profile":profile,"cpu_fallback_disabled":true}))?)?;
    drop(session);
    Ok(())
}
