//! Isolated CPU/GPU benchmark. CPU inference is never enabled in the shipped worker.
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
    let ep = match args[3].as_str() { "cpu" => None, "directml" => Some(GpuEp::DirectMl), "vulkan" => Some(GpuEp::Vulkan), "webgpu" => Some(GpuEp::WebGpu), _ => return Err("unsupported GPU provider".into()) };
    let image = image::open(&args[4])?.to_rgb8();
    let input: Vec<f32> = if size == 512 {
        ccp_detect::plane::resize_rgb_to_nchw(image.as_raw(), image.width(), image.height(), size as i32)
    } else {
        let floats: Vec<f32> = image.as_raw().iter().map(|x| *x as f32 / 255.).collect();
        let lb = ccp_detect::letterbox_rgb(&floats, image.width() as i32, image.height() as i32, size as i32);
        (0..3).flat_map(|c| { let canvas=&lb.canvas; (0..size*size).map(move |i| canvas[i*3+c] as f32 /255.) }).collect()
    };
    if let Some(path) = std::env::var_os("CCP_DUMP_INPUT") {
        let bytes: Vec<u8> = input.iter().flat_map(|value| value.to_le_bytes()).collect();
        std::fs::write(path, bytes)?;
    }
    let start=Instant::now();
    let mut session=if let Some(ep)=ep { commit_session(&model,ep)? } else { let mut builder=ort::session::Session::builder()?.with_intra_threads(8)?.with_parallel_execution(false)?; if let Some(path)=std::env::var_os("CCP_INFERENCE_PROFILE") { builder=builder.with_profiling(PathBuf::from(path))?; } builder.commit_from_file(&model)? };
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
    let decoded = if size == 512 {
        serde_json::json!({"plane_box":ccp_detect::plane::plane_box_from_output(&first,&shape,size as i32,size as i32)})
    } else {
        let dims:Vec<usize>=shape.iter().map(|v|*v as usize).collect();
        let rows=ccp_detect::preds_to_obb_rows(&first,&dims);
        let raw=ccp_detect::decode_obb(&rows,ccp_detect::DEFAULT_CONF,ccp_detect::DECODE_IOU,ccp_detect::DEFAULT_MAX_DET as usize);
        let detections=ccp_detect::dets_from_raw(&raw,size as i32,1.,0.,0.);
        serde_json::json!({"charts":ccp_detect::assemble_charts(&detections,"",ccp_detect::DEFAULT_CROP,size as i32,size as i32),"raw_detections":detections.len()})
    };
    let profile=if std::env::var_os("CCP_INFERENCE_PROFILE").is_some() {Some(session.end_profiling()?)} else {None};
    std::fs::write(&args[5],serde_json::to_vec(&serde_json::json!({"provider":args[3],"model":model,"size":size,"initialization_ms":initialization_ms,"times_ms":times,"shape":shape,"output":first,"decoded":decoded,"max_repeat_error":max_repeat_error,"profile":profile,"cpu_fallback_disabled":ep.is_some(),"cpu_threads":8}))?)?;
    drop(session);
    Ok(())
}
