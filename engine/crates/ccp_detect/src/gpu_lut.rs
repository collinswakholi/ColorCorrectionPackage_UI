//! GPU 17³ encoded-RGB LUT apply (LibTorch CUDA / MPS). CPU path stays in ccp_core.

/// Apply a packed `gs³×3` encoded LUT on GPU. Caller falls back to CPU on error.
/// On success, returns the device that ran (`cuda` or `mps`).
pub fn apply_encoded_lut_gpu(
    rgb: &mut [f32],
    height: usize,
    width: usize,
    lut: &[f32],
    gs: usize,
    prefer: &str,
) -> Result<&'static str, String> {
    #[cfg(feature = "tch")]
    {
        apply_encoded_lut_tch(rgb, height, width, lut, gs, prefer)
    }
    #[cfg(not(feature = "tch"))]
    {
        let _ = (rgb, height, width, lut, gs, prefer);
        Err("libtorch disabled".into())
    }
}

#[cfg(feature = "tch")]
fn pick_device(prefer: &str) -> Result<tch::Device, String> {
    use tch::Device;
    let p = prefer.trim().to_ascii_lowercase();
    let cuda = tch::Cuda::is_available();
    match p.as_str() {
        "cpu" => Err("cpu apply".into()),
        "cuda" if cuda => Ok(Device::Cuda(0)),
        "cuda" => Err("cuda not available".into()),
        "mps" if cfg!(target_os = "macos") => Ok(Device::Mps),
        "mps" => Err("mps is macOS-only".into()),
        _ if cuda => Ok(Device::Cuda(0)),
        _ if cfg!(target_os = "macos") => Ok(Device::Mps),
        _ => Err("no CUDA/MPS device".into()),
    }
}

#[cfg(feature = "tch")]
fn apply_encoded_lut_tch(
    rgb: &mut [f32],
    height: usize,
    width: usize,
    lut: &[f32],
    gs: usize,
    prefer: &str,
) -> Result<&'static str, String> {
    use tch::{Device, Kind, Tensor};

    let gs = gs.max(2);
    let expected = gs * gs * gs * 3;
    if lut.len() != expected {
        return Err(format!("lut len {} != {expected}", lut.len()));
    }
    if rgb.len() != height * width * 3 {
        return Err("image len mismatch".into());
    }
    let device = pick_device(prefer)?;

    let lut_t = Tensor::from_slice(lut)
        .to_kind(Kind::Float)
        .view([gs as i64, gs as i64, gs as i64, 3])
        .permute([3, 0, 1, 2])
        .unsqueeze(0)
        .to(device);
    let grid = (Tensor::from_slice(rgb)
        .to_kind(Kind::Float)
        .view([height as i64, width as i64, 3])
        .to(device)
        * 2.0
        - 1.0)
        .view([1, 1, height as i64, width as i64, 3]);
    let out = lut_t
        .grid_sampler_3d(&grid, 0, 0, true)
        .squeeze_dim(0)
        .squeeze_dim(1)
        .permute([1, 2, 0])
        .to(Device::Cpu)
        .contiguous()
        .view([-1]);
    let vec = Vec::<f32>::try_from(out).map_err(|e| e.to_string())?;
    if vec.len() != rgb.len() {
        return Err("gpu lut size mismatch".into());
    }
    rgb.copy_from_slice(&vec);
    let label = match device {
        Device::Cuda(_) => "cuda",
        Device::Mps => "mps",
        _ => "cpu",
    };
    Ok(label)
}
