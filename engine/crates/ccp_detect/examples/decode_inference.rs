//! Decode an isolated runtime's raw tensor through the production geometry code.
fn main()->Result<(),Box<dyn std::error::Error>> {
    let args:Vec<String>=std::env::args().collect();
    let mut value:serde_json::Value=serde_json::from_slice(&std::fs::read(&args[1])?)?;
    let shape:Vec<i64>=serde_json::from_value(value["shape"].clone())?;
    let output:Vec<f32>=serde_json::from_value(value["output"].clone())?;
    let size:i32=args[2].parse()?;
    value["decoded"]=if size==512 {
        serde_json::json!({"plane_box":ccp_detect::plane::plane_box_from_output(&output,&shape,size,size)})
    } else {
        let dims:Vec<usize>=shape.iter().map(|v|*v as usize).collect();
        let rows=ccp_detect::preds_to_obb_rows(&output,&dims);
        let raw=ccp_detect::decode_obb(&rows,ccp_detect::DEFAULT_CONF,ccp_detect::DECODE_IOU,ccp_detect::DEFAULT_MAX_DET as usize);
        let detections=ccp_detect::dets_from_raw(&raw,size,1.,0.,0.);
        serde_json::json!({"charts":ccp_detect::assemble_charts(&detections,"",ccp_detect::DEFAULT_CROP,size,size),"raw_detections":detections.len()})
    };
    std::fs::write(&args[1],serde_json::to_vec(&value)?)?;
    Ok(())
}
