//! Global corrections rendered through cached 3D LUTs on the GPU or CPU.
//!
//! CC alone uses a 33³ encoded LUT (exact per-pixel math for small images);
//! the optional fast mode folds the active GC/WB/CC stages into one 65³ LUT.
//! Spatial FFC is never part of a LUT. The backend that actually ran is kept
//! per thread so the request that rendered can report it.

use std::cell::{Cell, RefCell};
use std::sync::{Arc, Mutex, OnceLock};

use ccp_core::lut::LutPipeline;
use ccp_core::maps::{
    apply_correction_pixel, build_encoded_lut, CorrectionModel, CorrectionSettings,
    APPLY_LUT_MIN_PIXELS, APPLY_LUT_SIZE,
};
use ccp_core::stages::{
    apply_cc_inplace, apply_encoded_lut_inplace, apply_gc_inplace, apply_gc_pixel,
    apply_wb_inplace, apply_wb_pixel,
};
use ccp_detect::{runtime_policy, DevicePolicy};
use ndarray::Array3;
use rayon::prelude::*;
use serde_json::{json, Value};

use crate::error::{Error, ErrorCode, Result};
use crate::work;

const FAST_LUT_SIZE: usize = 65;
const LUT_CACHE_CAPACITY: usize = 8;

#[derive(Clone, Copy, Default)]
struct LutRequest {
    force_cpu: bool,
    auto: bool,
}

// Request-local state must not be overwritten by concurrent callers of the
// library. The worker dispatches a request and reads its result on this thread.
thread_local! {
    static LUT_REQUEST: Cell<LutRequest> = const {
        Cell::new(LutRequest { force_cpu: false, auto: false })
    };
    static LAST_APPLY: RefCell<(String, Option<Value>)> =
        const { RefCell::new((String::new(), None)) };
}

/// The LUT device choice of the current request: `force_cpu` keeps LUTs on the
/// CPU; `auto` also does so for images below about one megapixel.
pub(crate) fn set_lut_request(force_cpu: bool, auto: bool) {
    LUT_REQUEST.set(LutRequest { force_cpu, auto });
}

/// Start a new execution record; it reads `cpu` until a LUT runs.
pub(crate) fn reset_execution_record() {
    remember_backend("cpu");
}

pub(crate) fn last_backend() -> String {
    LAST_APPLY.with_borrow(|v| v.0.clone())
}

pub(crate) fn last_execution() -> Option<Value> {
    LAST_APPLY.with_borrow(|v| v.1.clone())
}

fn remember_backend(label: &str) {
    LAST_APPLY.with_borrow_mut(|v| *v = (label.to_string(), None));
}

fn remember_execution(execution: Value) {
    LAST_APPLY.with_borrow_mut(|v| v.1 = Some(execution));
}

/// Apply CC: exact per-pixel math for small images, otherwise the 33³ LUT.
pub(crate) fn apply_cc(
    img: &mut Array3<f32>,
    model: &CorrectionModel,
    settings: &CorrectionSettings,
) -> Result<()> {
    if img.len() / 3 < APPLY_LUT_MIN_PIXELS {
        apply_cc_inplace(img, model, settings)?;
        remember_backend("cpu");
        return Ok(());
    }
    let key = serde_json::to_vec(&(
        model,
        &settings.input_space,
        &settings.target_space,
        &settings.target_white,
        APPLY_LUT_SIZE,
    ))?;
    let lut = cached_lut(key, || {
        Ok(build_encoded_lut(model, settings, APPLY_LUT_SIZE)?)
    })?;
    apply_lut(img, &lut, APPLY_LUT_SIZE, "cc-lut33")
}

/// Progress label for rendering `stages` as one combined LUT, if any is active.
pub(crate) fn combined_label(stages: &LutPipeline) -> Option<String> {
    let names: Vec<&str> = [
        (stages.gc.is_some(), "GC"),
        (stages.wb.is_some(), "WB"),
        (stages.cc.is_some(), "CC"),
    ]
    .into_iter()
    .filter_map(|(active, name)| active.then_some(name))
    .collect();
    (!names.is_empty()).then(|| format!("Combined LUT · {}", names.join(" + ")))
}

/// Render `stages` through the combined 65³ LUT. Values outside the LUT domain
/// [0,1] fall back to the separate stages; nonfinite input fails unchanged.
pub(crate) fn apply_combined(work: &mut Array3<f32>, stages: &LutPipeline) -> Result<()> {
    work::make_contiguous(work)?;
    // A single contiguous scan avoids ndarray's per-element traversal overhead
    // on 24 MP arrays. Do not clamp or modify values during validation.
    let (nonfinite, outside) = work
        .as_slice()
        .expect("contiguous")
        .par_chunks(65_536)
        .map(|chunk| {
            chunk.iter().fold((false, false), |(invalid, outside), &x| {
                (
                    invalid || !x.is_finite(),
                    outside || !(0.0..=1.0).contains(&x),
                )
            })
        })
        .reduce(|| (false, false), |a, b| (a.0 || b.0, a.1 || b.1));
    if nonfinite {
        return Err(Error::Message(
            "Fast mode requires finite image values".into(),
        ));
    }
    if stages.gc.is_none() && stages.wb.is_none() && stages.cc.is_none() {
        remember_backend("cpu");
        remember_execution(
            json!({"backend":"cpu", "method":"identity", "accelerated_pixels":0, "cpu_pixels":work.len()/3}),
        );
        return Ok(());
    }
    // File decoding and FFC produce [0,1]. The internal float API can also
    // receive extended values: preserve its normal semantics rather than clip.
    if outside {
        if let Some(gc) = &stages.gc {
            apply_gc_inplace(work, gc)?;
        }
        if let Some(wb) = &stages.wb {
            apply_wb_inplace(work, wb);
        }
        if let Some((model, settings)) = &stages.cc {
            apply_cc(work, model, settings)?;
        }
        let mut execution = last_execution().unwrap_or_else(|| json!({"backend":last_backend()}));
        execution["method"] = json!("standard");
        execution["fast_mode_skipped"] = json!("Input values exceed the combined LUT domain [0,1]");
        remember_execution(execution);
        return Ok(());
    }
    let table = combined_lut(stages)?;
    apply_lut(work, &table, FAST_LUT_SIZE, "combined-lut65")
}

/// Match the f32 image-stage boundaries, including WB clipping.
fn evaluate_combined(stages: &LutPipeline, mut rgb: [f64; 3]) -> ccp_core::Result<[f64; 3]> {
    if let Some(gc) = &stages.gc {
        rgb = apply_gc_pixel(rgb, gc)?.map(|x| (x as f32) as f64);
    }
    if let Some(wb) = &stages.wb {
        rgb = apply_wb_pixel(rgb, wb).map(|x| (x as f32) as f64);
    }
    if let Some((model, settings)) = &stages.cc {
        rgb = apply_correction_pixel(model, rgb, settings)?;
    }
    Ok(rgb)
}

/// The combined table of exactly the active stages.
fn combined_lut(stages: &LutPipeline) -> Result<Arc<Vec<f32>>> {
    let cc = stages
        .cc
        .as_ref()
        .map(|(m, s)| (m, &s.input_space, &s.target_space, &s.target_white));
    let key = serde_json::to_vec(&(
        "combined-f32-v1",
        FAST_LUT_SIZE,
        &stages.gc,
        stages.wb.as_ref().map(|w| w.diag),
        cc,
    ))?;
    cached_lut(key, || {
        let n = FAST_LUT_SIZE;
        let table: Vec<[f32; 3]> = (0..n.pow(3))
            .into_par_iter()
            .map(|i| {
                let rgb = [
                    (i % n) as f64 / 64.,
                    ((i / n) % n) as f64 / 64.,
                    (i / n / n) as f64 / 64.,
                ];
                evaluate_combined(stages, rgb).map(|v| v.map(|x| x as f32))
            })
            .collect::<ccp_core::Result<_>>()?;
        if table.iter().flatten().any(|x| !x.is_finite()) {
            return Err(Error::Message(
                "Combined LUT contains nonfinite model output".into(),
            ));
        }
        Ok(table.into_iter().flatten().collect())
    })
}

/// Least-recently-used cache of built LUTs, keyed by their serialized inputs.
fn cached_lut(key: Vec<u8>, build: impl FnOnce() -> Result<Vec<f32>>) -> Result<Arc<Vec<f32>>> {
    type LutCache = Mutex<Vec<(Vec<u8>, Arc<Vec<f32>>)>>;
    static CACHE: OnceLock<LutCache> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(Vec::new()));
    {
        let mut cache = cache.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(index) = cache.iter().position(|(candidate, _)| candidate == &key) {
            let hit = Arc::clone(&cache[index].1);
            if index + 1 != cache.len() {
                let entry = cache.remove(index);
                cache.push(entry);
            }
            return Ok(hit);
        }
    }
    let built = Arc::new(build()?);
    let mut cache = cache.lock().unwrap_or_else(|e| e.into_inner());
    if let Some((_, hit)) = cache.iter().find(|(candidate, _)| candidate == &key) {
        return Ok(Arc::clone(hit));
    }
    if cache.len() >= LUT_CACHE_CAPACITY {
        cache.remove(0);
    }
    cache.push((key, Arc::clone(&built)));
    Ok(built)
}

// Small images do not amortize GPU startup and transfer overhead. This is a
// conservative size policy, not a claim to benchmark the user's hardware.
fn auto_uses_cpu(pixels: usize) -> bool {
    pixels < 1_048_576
}

/// Run a LUT on the GPU unless the policy, the request or the image size keeps
/// it on the CPU, recording which backend ran and why.
fn apply_lut(img: &mut Array3<f32>, lut: &[f32], gs: usize, method: &str) -> Result<()> {
    let request = LUT_REQUEST.get();
    let small_auto = request.auto && auto_uses_cpu(img.len() / 3);
    if runtime_policy() != DevicePolicy::Cpu
        && ccp_detect::probe::gpu_correction_enabled()
        && !request.force_cpu
        && !small_auto
    {
        work::make_contiguous(img)?;
        let slice = img.as_slice_mut().expect("contiguous");
        let execution = ccp_detect::lut_wgpu::apply(slice, lut, gs)
            .map_err(|e| Error::coded(ErrorCode::ProcessingFailed, e))?;
        if let Some(reason) = &execution.chunks.fallback_reason {
            eprintln!(
                "LUT fallback: {reason}; accelerated_pixels={}, cpu_pixels={}",
                execution.chunks.accelerated_pixels, execution.chunks.cpu_pixels
            );
        }
        remember_backend(&execution.backend);
        remember_execution(json!({
            "backend": execution.backend, "method": method, "grid": gs,
            "accelerated_pixels": execution.chunks.accelerated_pixels,
            "cpu_pixels": execution.chunks.cpu_pixels,
            "fallback_reason": execution.chunks.fallback_reason,
        }));
        return Ok(());
    }
    apply_encoded_lut_inplace(img, lut, gs);
    remember_backend("cpu");
    remember_execution(json!({
        "backend":"cpu", "method":method, "grid":gs,
        "accelerated_pixels":0, "cpu_pixels":img.len()/3, "fallback_reason":null,
        "selection_reason": if small_auto { "auto_small_image" } else { "cpu_requested" }
    }));
    Ok(())
}

/// The combined table of `stages`, for comparing against the rendered image.
#[cfg(test)]
pub(crate) fn combined_lut_table(stages: &LutPipeline) -> Arc<Vec<f32>> {
    combined_lut(stages).unwrap()
}

#[cfg(test)]
mod tests {
    use super::*;
    use ccp_core::maps::ModelKind;
    use ccp_core::stages::{GammaModel, WhiteBalance};

    fn fixture() -> LutPipeline {
        LutPipeline {
            gc: Some(GammaModel::curve(
                vec![0.0, 50.0, 100.0],
                vec![0.0, 45.0, 100.0],
            )),
            wb: Some(WhiteBalance {
                diag: [1.12, 0.91, 1.03],
            }),
            cc: Some((
                CorrectionModel {
                    kind: ModelKind::Ccm3x3,
                    feature_count: 3,
                    coefficients: vec![0.7, 0.1, 0.2, 0.2, 0.8, 0.1, 0.1, 0.1, 0.7],
                    ..CorrectionModel::default()
                },
                CorrectionSettings::srgb_d65().unwrap(),
            )),
        }
    }

    #[test]
    fn auto_device_policy_keeps_small_images_on_cpu_and_large_images_on_gpu() {
        assert!(auto_uses_cpu(1));
        assert!(auto_uses_cpu(1_048_575));
        assert!(!auto_uses_cpu(1_048_576));
        assert!(!auto_uses_cpu(24_000_000));
        set_lut_request(false, true);
        let mut image = Array3::from_elem((2, 2, 3), 0.4f32);
        apply_combined(&mut image, &fixture()).unwrap();
        let info = last_execution().unwrap();
        assert_eq!(info["backend"], "cpu");
        assert_eq!(info["method"], "combined-lut65");
        assert_eq!(info["selection_reason"], "auto_small_image");
        assert!(info["fallback_reason"].is_null());
        set_lut_request(false, false);
    }

    #[test]
    fn nodes_match_stage_math_and_every_stage_mask_is_part_of_cache_identity() {
        let full = fixture();
        let mut tables = Vec::new();
        for mask in 0..8 {
            let stages = LutPipeline {
                gc: full.gc.clone().filter(|_| mask & 1 != 0),
                wb: full.wb.clone().filter(|_| mask & 2 != 0),
                cc: full.cc.clone().filter(|_| mask & 4 != 0),
            };
            let table = combined_lut(&stages).unwrap();
            assert!(Arc::ptr_eq(&table, &combined_lut(&stages).unwrap()));
            for i in (0..65usize.pow(3)).step_by(191) {
                let rgb = [
                    (i % 65) as f64 / 64.,
                    ((i / 65) % 65) as f64 / 64.,
                    (i / 65 / 65) as f64 / 64.,
                ];
                let expected = evaluate_combined(&stages, rgb).unwrap();
                for c in 0..3 {
                    assert_eq!(table[i * 3 + c], expected[c] as f32);
                }
            }
            for old in &tables {
                assert!(!Arc::ptr_eq(old, &table));
            }
            tables.push(table);
        }
        let old = combined_lut(&full).unwrap();
        let mut changed = fixture();
        changed.wb = Some(WhiteBalance {
            diag: [0.9, 0.8, 0.7],
        });
        assert!(!Arc::ptr_eq(&old, &combined_lut(&changed).unwrap()));
        changed = fixture();
        changed.cc.as_mut().unwrap().1 =
            crate::model::correction_settings("srgb", "d65", "srgb", "d50").unwrap();
        assert!(!Arc::ptr_eq(&old, &combined_lut(&changed).unwrap()));
        changed = fixture();
        changed.gc.as_mut().unwrap().values[1] = 40.0;
        assert!(!Arc::ptr_eq(&old, &combined_lut(&changed).unwrap()));
        changed = fixture();
        changed.cc.as_mut().unwrap().0.coefficients[0] = 0.8;
        assert!(!Arc::ptr_eq(&old, &combined_lut(&changed).unwrap()));
    }
}
