use ccp_bridge::serve::prepare_libtorch_env;
use ccp_detect::apply_encoded_lut_gpu;

fn main() {
    prepare_libtorch_env();
    let gs = 17usize;
    let mut lut = vec![0.0f32; gs * gs * gs * 3];
    for b in 0..gs {
        for g in 0..gs {
            for r in 0..gs {
                let index = ((b * gs + g) * gs + r) * 3;
                lut[index] = r as f32 / (gs - 1) as f32;
                lut[index + 1] = g as f32 / (gs - 1) as f32;
                lut[index + 2] = b as f32 / (gs - 1) as f32;
            }
        }
    }
    let mut rgb = vec![0.25f32, 0.5, 0.75, 0.9, 0.1, 0.4];
    match apply_encoded_lut_gpu(&mut rgb, 1, 2, &lut, gs, "cuda") {
        Ok(device) => println!("ok device={device} rgb={rgb:?}"),
        Err(error) => println!("error: {error}"),
    }
}
