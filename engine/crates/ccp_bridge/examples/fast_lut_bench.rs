//! Production fast-mode benchmark and approximation audit on real photographs.
use ccp_bridge::run_pipeline::{*, fast_lut::combined_lut_cached};
use ccp_core::{color::{delta_e_2000, srgb_to_lab_d65}, maps::*, stages::*};
use rayon::prelude::*;
use serde_json::{json,Value};
use std::{path::PathBuf, time::{Instant,SystemTime,UNIX_EPOCH}};
fn now()->f64 { SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs_f64() }
fn stats(mut v:Vec<f64>)->Value {
    v.sort_by(f64::total_cmp);
    json!({"n":v.len(),"median":v[v.len()/2],"p95":v[(v.len()-1)*95/100],"max":v.last()})
}
fn main()->std::result::Result<(),Box<dyn std::error::Error>> {
    rayon::ThreadPoolBuilder::new().num_threads(8).build_global()?;
    let args:Vec<_>=std::env::args().collect();
    let config:Value=serde_json::from_slice(&std::fs::read(&args[1])?)?;
    let saved=load_saved(config["model"].as_str().unwrap())?;
    let photos:Vec<PathBuf>=config["images"].as_array().unwrap().iter().map(|v|PathBuf::from(v.as_str().unwrap())).collect();
    if args[2]=="workflow" {
        let dir=PathBuf::from(config["output"].as_str().unwrap()); std::fs::create_dir(&dir)?;
        for block in 0..6 {
            let modes=if block%2==0 {["standard-cpu","fast-gpu","fast-cpu","standard-gpu"]} else {["standard-gpu","fast-cpu","fast-gpu","standard-cpu"]};
            for mode in modes { for (i,path) in photos.iter().enumerate() {
                let mut options=ApplyOptions::from_saved(&saved); options.fast_mode=mode.starts_with("fast");
                set_cpu_lut_request(mode.ends_with("cpu"));
                let start_utc=now(); let start=Instant::now();
                apply_saved_to_image_ex(&saved,path,&dir.join(format!("{mode}-{i}.jpg")),None,&options)?;
                let ms=start.elapsed().as_secs_f64()*1000.; let end=now();
                let execution=last_apply_execution().unwrap();
                assert_eq!(execution["method"],if options.fast_mode {"combined-lut65"} else {"cc-lut33"});
                if mode.ends_with("gpu") { assert_eq!(execution["accelerated_pixels"],24000000); assert_eq!(execution["cpu_pixels"],0); }
                else { assert_eq!(execution["cpu_pixels"],24000000); }
                println!("{}",json!({"kind":"workflow","block":block,"warmup":block==0,"mode":mode,"image":path,"ms":ms,"start":start_utc,"end":end,"execution":execution}));
            }}
        }
    } else {
        let mut options=ApplyOptions::from_saved(&saved); options.fast_mode=true;
        let start=Instant::now(); let table=combined_lut_cached(&saved,&options)?;
        println!("{}",json!({"kind":"build","grid":65,"ms":start.elapsed().as_secs_f64()*1000.}));
        let (pipeline,_)=saved.lut_pipeline()?;
        let exact=|mut rgb:[f64;3]| -> ccp_core::Result<[f64;3]> {
            if let Some(g)=&pipeline.gc {rgb=apply_gc_pixel(rgb,g)?.map(|x|(x as f32) as f64);}
            if let Some(w)=&pipeline.wb {rgb=apply_wb_pixel(rgb,w).map(|x|(x as f32) as f64);}
            if let Some((m,s))=&pipeline.cc {rgb=apply_correction_pixel(m,rgb,s)?;}
            Ok(rgb)
        };
        for path in &photos {
            let input=rgb_image_into_array3(ccp_core::sample::load_image_with_icc(path,true)?)?;
            let mut cpu=input.clone(); set_cpu_lut_request(true); apply_saved_to_work(&mut cpu,&saved,&options,None)?;
            let mut gpu=input.clone(); set_cpu_lut_request(false); apply_saved_to_work(&mut gpu,&saved,&options,None)?;
            let parity=cpu.as_slice().unwrap().par_iter().zip(gpu.as_slice().unwrap().par_iter()).map(|(a,b)|{assert!(a.is_finite()&&b.is_finite());(a-b).abs()}).reduce(||0f32,f32::max);
            assert!(parity<=1e-5); assert_eq!(last_apply_execution().unwrap()["accelerated_pixels"],input.len()/3);
            let src=input.as_slice().unwrap(); let mut de=Vec::new(); let mut channel=Vec::new();
            for n in 0..100_000 {
                let p=((n*104729+41)%(src.len()/3))*3;
                let rgb=[src[p] as f64,src[p+1] as f64,src[p+2] as f64];
                let a=exact(rgb)?; let b=sample_encoded_lut(&table,65,rgb);
                de.push(delta_e_2000(srgb_to_lab_d65(a)?,srgb_to_lab_d65(b)?));
                channel.push((0..3).map(|c|(a[c]-b[c]).abs()).fold(0.,f64::max));
            }
            println!("{}",json!({"kind":"quality","photo":path,"pixels":src.len()/3,"parity_max":parity,"delta_e00_vs_model":stats(de),"channel_error":stats(channel)}));
        }
        // Dense neutral/shadow ramps, saturated faces and cube corners supplement
        // photographs. This is approximation to the fitted model, not color truth.
        let mut de=Vec::new(); let mut channel=Vec::new();
        for n in 0..4097 {
            let v=n as f64/4096.;
            for rgb in [[v,v,v],[v,0.,0.],[0.,v,0.],[0.,0.,v],[v,1.,0.],[1.,v,1.]] {
                let a=exact(rgb)?; let b=sample_encoded_lut(&table,65,rgb);
                de.push(delta_e_2000(srgb_to_lab_d65(a)?,srgb_to_lab_d65(b)?));
                channel.push((0..3).map(|c|(a[c]-b[c]).abs()).fold(0.,f64::max));
            }
        }
        println!("{}",json!({"kind":"boundary_quality","delta_e00_vs_model":stats(de),"channel_error":stats(channel)}));
    }
    Ok(())
}
