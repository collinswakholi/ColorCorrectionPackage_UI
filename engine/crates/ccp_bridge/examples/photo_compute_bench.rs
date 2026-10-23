//! Real-photo host benchmark. JSONL timings include UTC boundaries for telemetry.
//! No synthetic GPU load, global power settings, or inference CPU fallback.
use ccp_bridge::run_pipeline::*;
use ccp_core::{color::{delta_e_2000, srgb_to_lab_d65}, maps::*, stages::*};
use ndarray::Array3;
use rayon::prelude::*;
use serde_json::{json, Value};
use std::{path::PathBuf, time::{Instant, SystemTime, UNIX_EPOCH}};

fn now() -> f64 { SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs_f64() }
fn emit(v: Value) { println!("{v}"); }
fn stats(mut v: Vec<f64>) -> Value {
    v.sort_by(f64::total_cmp);
    json!({"n":v.len(),"median":v[v.len()/2],"p95":v[((v.len()-1) as f64*0.95) as usize],
        "p99":v[((v.len()-1) as f64*0.99) as usize],"max":v.last()})
}
fn checked_error(a: &Array3<f32>, b: &Array3<f32>) -> f32 {
    a.as_slice().unwrap().par_iter().zip(b.as_slice().unwrap()).map(|(a,b)| {
        assert!(a.is_finite() && b.is_finite()); (a-b).abs()
    }).reduce(||0., f32::max)
}
fn main() -> std::result::Result<(), Box<dyn std::error::Error>> {
    rayon::ThreadPoolBuilder::new().num_threads(8).build_global()?;
    let args: Vec<_> = std::env::args().collect();
    let config: Value = serde_json::from_slice(&std::fs::read(&args[1])?)?;
    let task = args.get(2).map(String::as_str).unwrap_or("kernel");
    let saved = load_saved(config["model"].as_str().unwrap())?;
    assert!(!saved.do_ffc, "This fixture must be no-FFC; spatial FFC is tested separately");
    let (pipeline, _) = saved.lut_pipeline()?;
    let (model, settings) = pipeline.cc.as_ref().unwrap();
    let lut = build_encoded_lut(model, settings, 33)?;
    let images: Vec<PathBuf> = config["images"].as_array().unwrap().iter()
        .map(|p| PathBuf::from(p.as_str().unwrap())).collect();
    let mut sources = Vec::new();
    let mut precs = Vec::new();
    let mut expected = Vec::new();
    for path in &images {
        let source = rgb_image_into_array3(ccp_core::sample::load_image_with_icc(path, true)?)?;
        let mut pre = source.clone();
        let mut options = ApplyOptions::from_saved(&saved);
        options.do_cc = false;
        apply_saved_to_work(&mut pre, &saved, &options, None)?;
        let mut reference = pre.clone();
        apply_encoded_lut_inplace(&mut reference, &lut, 33);
        emit(json!({"kind":"fixture","path":path,"width":source.shape()[1],"height":source.shape()[0],
            "pixels":source.len()/3,"model_kind":model.kind.name()}));
        sources.push(source); precs.push(pre); expected.push(reference);
    }
    if task == "kernel" {
        for warm in 0..4 {
            let mut work = precs[warm % precs.len()].clone();
            let result = ccp_detect::lut_wgpu::apply(work.as_slice_mut().unwrap(), &lut, 33)?;
            assert_eq!(result.chunks.accelerated_pixels, work.len()/3, "{result:?}");
            assert!(checked_error(&work, &expected[warm % precs.len()]) <= 1e-5);
        }
        for block in 0..5 {
            for rep in 0..20 {
                let i = rep % precs.len();
                let order = if (block+rep)%2 == 0 {["cpu","gpu"]} else {["gpu","cpu"]};
                for mode in order {
                    let mut work = precs[i].clone();
                    let utc = now(); let start = Instant::now();
                    let backend = if mode == "gpu" {
                        let r = ccp_detect::lut_wgpu::apply(work.as_slice_mut().unwrap(), &lut, 33)?;
                        assert_eq!(r.chunks.accelerated_pixels,work.len()/3, "{r:?}");
                        r.backend
                    } else {
                        apply_encoded_lut_inplace(&mut work,&lut,33); "cpu".into()
                    };
                    let elapsed = start.elapsed().as_secs_f64()*1000.;
                    let end = now();
                    let error = checked_error(&work,&expected[i]); assert!(error<=1e-5);
                    emit(json!({"kind":"kernel","block":block,"rep":rep,"image":images[i],"mode":mode,
                        "backend":backend,"ms":elapsed,"start":utc,"end":end,"error":error}));
                }
            }
        }
    } else if task == "workflow" {
        let out = PathBuf::from(config["output"].as_str().unwrap());
        std::fs::create_dir(&out)?;
        // Warm each route. All measured image runs include decode, full GC/WB/CC,
        // quantization, JPEG encoding, ICC generation and writing via app code.
        for block in 0..6 {
            let order = if block%2==0 {["cpu","gpu","fast"]} else {["fast","gpu","cpu"]};
            for mode in order {
                for (i,path) in images.iter().enumerate() {
                    let mut options = ApplyOptions::from_saved(&saved);
                    options.fast_mode = mode=="fast";
                    set_cpu_lut_request(mode!="gpu");
                    let dest = out.join(format!("{mode}-{i}.jpg"));
                    let utc=now(); let start=Instant::now();
                    apply_saved_to_image_ex(&saved,path,&dest,None,&options)?;
                    let ms=start.elapsed().as_secs_f64()*1000.; let end=now();
                    let backend=last_apply_backend();
                    assert!(if mode=="gpu" {backend.starts_with("wgpu:") && !backend.contains("+cpu")} else {backend=="cpu"});
                    emit(json!({"kind":"workflow","warmup":block==0,"block":block,"mode":mode,"image":path,
                        "backend":backend,"method":if mode=="fast" {"fast"} else {"cc-lut33"},
                        "ms":ms,"start":utc,"end":end,"output_bytes":std::fs::metadata(dest)?.len()}));
                }
            }
        }
    } else if task == "quality" {
        // LUT approximation is measured separately from GPU/CPU arithmetic parity.
        // Round between stages exactly as the production float image does.
        let full_color = |rgb| -> ccp_core::Result<[f64;3]> {
            let rgb = if let Some(gc)=&pipeline.gc {apply_gc_pixel(rgb,gc)?.map(|v| (v as f32) as f64)} else {rgb};
            let rgb = if let Some(wb)=&pipeline.wb {apply_wb_pixel(rgb,wb).map(|v| (v as f32) as f64)} else {rgb};
            apply_correction_pixel(model,rgb,settings)
        };
        for (label,gs) in [("cc",17),("cc",33),("cc",65),("combined",33),("combined",65)] {
            let start=Instant::now();
            let table = if label=="cc" {build_encoded_lut(model,settings,gs)?} else {
                (0..gs*gs*gs).into_par_iter().map(|i| {
                    let d=(gs-1) as f64;
                    full_color([(i%gs) as f64/d,((i/gs)%gs) as f64/d,(i/gs/gs) as f64/d])
                        .map(|v|v.map(|x|x as f32))
                }).collect::<ccp_core::Result<Vec<_>>>()?.into_iter().flatten().collect()
            };
            emit(json!({"kind":"lut_build","label":label,"grid":gs,"ms":start.elapsed().as_secs_f64()*1000.}));
            for i in 0..sources.len() {
                let src=if label=="cc" {&precs[i]} else {&sources[i]};
                let pixels=src.len()/3; let slice=src.as_slice().unwrap(); let mut de=Vec::new();let mut channel=Vec::new();
                for n in 0..20_000 {
                    let p=((n*104729+41)%pixels)*3;
                    let rgb=[slice[p] as f64,slice[p+1] as f64,slice[p+2] as f64];
                    let exact=if label=="cc" {apply_correction_pixel(model,rgb,settings)?} else {full_color(rgb)?};
                    let approx=sample_encoded_lut(&table,gs,rgb);
                    de.push(delta_e_2000(srgb_to_lab_d65(exact)?,srgb_to_lab_d65(approx)?));
                    channel.push((0..3).map(|c|(exact[c]-approx[c]).abs()).fold(0.,f64::max));
                }
                // Five math-only combined-LUT calls; experimental, no file IO.
                let mut times=Vec::new();
                if label=="combined" {
                    for rep in 0..6 {
                        let mut work=src.clone(); let start=Instant::now();
                        let r=ccp_detect::lut_wgpu::apply(work.as_slice_mut().unwrap(),&table,gs)?;
                        assert_eq!(r.chunks.accelerated_pixels,pixels);
                        let ms=start.elapsed().as_secs_f64()*1000.;
                        let mut cpu=src.clone(); apply_encoded_lut_inplace(&mut cpu,&table,gs);
                        assert!(checked_error(&work,&cpu)<=1e-5);
                        if rep>0 {times.push(ms);}
                    }
                }
                emit(json!({"kind":"quality","image":images[i],"label":label,"grid":gs,
                    "delta_e_2000_vs_direct":stats(de),"max_channel_error":stats(channel),
                    "combined_gpu_math_ms":times}));
            }
        }
        // Compare the standard pipeline with the combined 65-grid fast mode.
        for (i,src) in sources.iter().enumerate() {
            for mode in ["fast","cpu","gpu"] {
                let mut times=Vec::new(); let mut max_error=0f32;
                for rep in 0..4 {
                    let mut work=src.clone();
                    let mut options=ApplyOptions::from_saved(&saved); options.fast_mode=mode=="fast";
                    set_cpu_lut_request(mode!="gpu");
                    let utc=now(); let start=Instant::now();
                    apply_saved_to_work(&mut work,&saved,&options,None)?;
                    let ms=start.elapsed().as_secs_f64()*1000.;
                    if mode!="fast" {max_error=max_error.max(checked_error(&work,&expected[i]));assert!(max_error<=1e-5);}
                    else {assert!(work.par_iter().all(|v|v.is_finite()));}
                    if rep>0 {times.push(ms);}
                    emit(json!({"kind":"math_event","mode":mode,"image":images[i],"start":utc,"end":now()}));
                }
                emit(json!({"kind":"math","mode":mode,"image":images[i],"ms":times,"lut_parity_error":max_error}));
            }
        }
    } else if task == "combined" {
        // Experimental combined GC/WB/CC LUT. FFC is intentionally not folded in.
        let gs=65;
        let start=Instant::now();
        let table:Vec<f32>=(0..gs*gs*gs).into_par_iter().map(|i| {
            let d=(gs-1) as f64;
            let mut rgb=[(i%gs) as f64/d,((i/gs)%gs) as f64/d,(i/gs/gs) as f64/d];
            if let Some(gc)=&pipeline.gc {rgb=apply_gc_pixel(rgb,gc)?.map(|v|(v as f32) as f64);}
            if let Some(wb)=&pipeline.wb {rgb=apply_wb_pixel(rgb,wb).map(|v|(v as f32) as f64);}
            apply_correction_pixel(model,rgb,settings).map(|v|v.map(|x|x as f32))
        }).collect::<ccp_core::Result<Vec<_>>>()?.into_iter().flatten().collect();
        emit(json!({"kind":"lut_build","label":"combined","grid":gs,"ms":start.elapsed().as_secs_f64()*1000.}));
        let out=PathBuf::from(config["output"].as_str().unwrap()).with_file_name("combined-images");
        std::fs::create_dir(&out)?;
        for block in 0..6 {
            let order=if block%2==0 {["cpu","gpu"]} else {["gpu","cpu"]};
            for mode in order {
                for (i,path) in images.iter().enumerate() {
                    let utc=now(); let start=Instant::now();
                    let mut work=rgb_image_into_array3(ccp_core::sample::load_image_with_icc(path,true)?)?;
                    let backend=if mode=="gpu" {
                        let r=ccp_detect::lut_wgpu::apply(work.as_slice_mut().unwrap(),&table,gs)?;
                        assert_eq!(r.chunks.accelerated_pixels,work.len()/3);
                        r.backend
                    } else {apply_encoded_lut_inplace(&mut work,&table,gs); "cpu".into()};
                    let dest=out.join(format!("{mode}-{i}.jpg"));
                    write_corrected_image(&work,&dest,&settings.target_space)?;
                    let ms=start.elapsed().as_secs_f64()*1000.;let end=now();
                    let mut reference=sources[i].clone();apply_encoded_lut_inplace(&mut reference,&table,gs);
                    let error=checked_error(&work,&reference);assert!(error<=1e-5);
                    emit(json!({"kind":"workflow","warmup":block==0,"block":block,"mode":mode,"image":path,
                        "backend":backend,"method":"experimental-combined65","ms":ms,"start":utc,"end":end,
                        "error":error,"output_bytes":std::fs::metadata(dest)?.len()}));
                }
            }
        }
    } else {return Err("task must be kernel, workflow, quality or combined".into());}
    Ok(())
}
