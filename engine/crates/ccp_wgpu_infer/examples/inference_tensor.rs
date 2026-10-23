use std::time::Instant;
fn main() -> Result<(),Box<dyn std::error::Error>> {
    let args:Vec<String>=std::env::args().collect();
    let size:usize=args[2].parse()?;
    let bytes=std::fs::read(&args[3])?;
    if bytes.len()!=size*size*3*4 {return Err("input dimensions mismatch".into());}
    let input:Vec<f32>=bytes.chunks_exact(4).map(|v|f32::from_le_bytes(v.try_into().unwrap())).collect();
    let start=Instant::now();
    let session=ccp_wgpu_infer::Session::new(std::path::Path::new(&args[1]))?;
    let initialization_ms=start.elapsed().as_secs_f64()*1000.;
    let mut times=Vec::new(); let mut cold=0.; let mut shape=Vec::new(); let mut output=Vec::new();
    let reps:usize=args.get(5).map(|v|v.parse()).transpose()?.unwrap_or(3);
    for i in 0..reps+3 {
        let start=Instant::now();
        (shape,output)=session.forward(input.clone(),size)?;
        let ms=start.elapsed().as_secs_f64()*1000.;
        if i==0 {cold=ms;}
        if i>=3 {times.push(ms);}
    }
    std::fs::write(&args[4],serde_json::to_vec(&serde_json::json!({"backend":session.backend(),"initialization_ms":initialization_ms,"first_inference_ms":cold,"times_ms":times,"shape":shape,"output":output,"cpu_inference":false}))?)?;
    Ok(())
}
