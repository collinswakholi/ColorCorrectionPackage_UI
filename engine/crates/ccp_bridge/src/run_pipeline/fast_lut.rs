//! Optional image-rendering approximation. Fitting and spatial FFC stay separate.
use super::*;

pub const FAST_LUT_SIZE: usize = 65;

fn effective_pipeline(saved: &SavedPipeline, apply: &ApplyOptions) -> Result<LutPipeline> {
    let (mut pipeline, _) = saved.lut_pipeline()?;
    if !apply.do_gc { pipeline.gc = None; }
    if !apply.do_wb { pipeline.wb = None; }
    if !apply.do_cc { pipeline.cc = None; }
    Ok(pipeline)
}

/// Match the f32 image-stage boundaries, including WB clipping.
fn evaluate(pipeline: &LutPipeline, mut rgb: [f64; 3]) -> ccp_core::Result<[f64; 3]> {
    if let Some(gc) = &pipeline.gc {
        rgb = apply_gc_pixel(rgb, gc)?.map(|x| (x as f32) as f64);
    }
    if let Some(wb) = &pipeline.wb {
        rgb = apply_wb_pixel(rgb, wb).map(|x| (x as f32) as f64);
    }
    if let Some((model, settings)) = &pipeline.cc {
        rgb = apply_correction_pixel(model, rgb, settings)?;
    }
    Ok(rgb)
}

/// Cache only active transforms. FFC is spatial and is never part of this table.
pub fn combined_lut_cached(saved: &SavedPipeline, apply: &ApplyOptions) -> Result<Arc<Vec<f32>>> {
    let p = effective_pipeline(saved, apply)?;
    let cc = p.cc.as_ref().map(|(m, s)| (m, &s.input_space, &s.target_space, &s.target_white));
    let key = serde_json::to_vec(&("combined-f32-v1", FAST_LUT_SIZE, &p.gc, p.wb.as_ref().map(|w| w.diag), cc))?;
    cached_lut(key, || {
        let table: Vec<[f32; 3]> = (0..FAST_LUT_SIZE.pow(3)).into_par_iter().map(|i| {
            let n = FAST_LUT_SIZE;
            let rgb = [(i % n) as f64 / 64., ((i / n) % n) as f64 / 64., (i / n / n) as f64 / 64.];
            evaluate(&p, rgb).map(|v| v.map(|x| x as f32))
        }).collect::<ccp_core::Result<_>>()?;
        if table.iter().flatten().any(|x| !x.is_finite()) {
            return Err(Error::Message("Combined LUT contains nonfinite model output".into()));
        }
        Ok(table.into_iter().flatten().collect())
    })
}

pub(super) fn apply_combined(work: &mut Array3<f32>, saved: &SavedPipeline, apply: &ApplyOptions) -> Result<()> {
    let p = effective_pipeline(saved, apply)?;
    if work.as_slice().is_none() {
        let dim = work.raw_dim();
        *work = Array3::from_shape_vec(dim, work.iter().copied().collect())
            .map_err(|e| Error::Message(e.to_string()))?;
    }
    // A single contiguous scan avoids ndarray's per-element traversal overhead
    // on 24 MP arrays. Do not clamp or modify values during validation.
    let (nonfinite, outside) = work.as_slice().expect("contiguous").par_chunks(65_536)
        .map(|chunk| chunk.iter().fold((false,false), |(invalid,outside), &x|
            (invalid || !x.is_finite(), outside || !(0.0..=1.0).contains(&x))))
        .reduce(||(false,false), |a,b| (a.0 || b.0, a.1 || b.1));
    if nonfinite {
        return Err(Error::Message("Fast mode requires finite image values".into()));
    }
    if p.gc.is_none() && p.wb.is_none() && p.cc.is_none() {
        remember_apply_backend("cpu");
        remember_apply_execution(json!({"backend":"cpu", "method":"identity", "accelerated_pixels":0, "cpu_pixels":work.len()/3}));
        return Ok(());
    }
    // File decoding and FFC produce [0,1]. The internal float API can also
    // receive extended values: preserve its normal semantics rather than clip.
    if outside {
        if let Some(gc) = &p.gc { apply_gc_inplace(work, gc)?; }
        if let Some(wb) = &p.wb { apply_wb_inplace(work, wb); }
        if let Some((m,s)) = &p.cc { apply_cc_accel(work, m, s)?; }
        let mut execution = last_apply_execution().unwrap_or_else(|| json!({"backend":last_apply_backend()}));
        execution["method"] = json!("standard");
        execution["fast_mode_skipped"] = json!("Input values exceed the combined LUT domain [0,1]");
        remember_apply_execution(execution);
        return Ok(());
    }
    let table = combined_lut_cached(saved, apply)?;
    apply_lut_accel(work, &table, FAST_LUT_SIZE, "combined-lut65")
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn auto_device_policy_keeps_small_images_on_cpu_and_large_images_on_gpu() {
        assert!(auto_uses_cpu(1));
        assert!(auto_uses_cpu(1_048_575));
        assert!(!auto_uses_cpu(1_048_576));
        assert!(!auto_uses_cpu(24_000_000));
        set_cpu_lut_request(false);
        set_auto_lut_request(true);
        let mut image = Array3::from_elem((2, 2, 3), 0.4f32);
        let saved = fixture();
        apply_combined(&mut image, &saved, &ApplyOptions::from_saved(&saved)).unwrap();
        let info = last_apply_execution().unwrap();
        assert_eq!(info["backend"], "cpu");
        assert_eq!(info["method"], "combined-lut65");
        assert_eq!(info["selection_reason"], "auto_small_image");
        assert!(info["fallback_reason"].is_null());
        set_auto_lut_request(false);
    }
    fn fixture() -> SavedPipeline {
        SavedPipeline {
            do_gc: true, do_wb: true, do_cc: true,
            gc_model: Some(GammaModel::legacy(vec![0.002, 0.8, 0.])),
            wb_diag: Some([1.12, 0.91, 1.03]),
            correction: Some(CorrectionModel {
                kind: ModelKind::Ccm3x3, feature_count: 3,
                coefficients: vec![0.7,0.1,0.2, 0.2,0.8,0.1, 0.1,0.1,0.7],
                ..CorrectionModel::default()
            }),
            ..SavedPipeline::default()
        }
    }
    #[test]
    fn nodes_match_stage_math_and_every_stage_mask_is_part_of_cache_identity() {
        let saved = fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        let mut tables = Vec::new();
        for mask in 0..8 {
            options.do_gc = mask & 1 != 0; options.do_wb = mask & 2 != 0; options.do_cc = mask & 4 != 0;
            let table = combined_lut_cached(&saved, &options).unwrap();
            assert!(Arc::ptr_eq(&table, &combined_lut_cached(&saved, &options).unwrap()));
            let pipeline = effective_pipeline(&saved, &options).unwrap();
            for i in (0..65usize.pow(3)).step_by(191) {
                let rgb = [(i%65) as f64/64., ((i/65)%65) as f64/64., (i/65/65) as f64/64.];
                let expected = evaluate(&pipeline, rgb).unwrap();
                for c in 0..3 { assert_eq!(table[i*3+c], expected[c] as f32); }
            }
            for old in &tables { assert!(!Arc::ptr_eq(old, &table)); }
            tables.push(table);
        }
        let old = combined_lut_cached(&saved, &options).unwrap();
        let mut changed = saved.clone(); changed.wb_diag = Some([0.9,0.8,0.7]);
        assert!(!Arc::ptr_eq(&old, &combined_lut_cached(&changed, &options).unwrap()));
        changed = saved.clone(); changed.target_white_id = "d50".into();
        assert!(!Arc::ptr_eq(&old, &combined_lut_cached(&changed, &options).unwrap()));
        changed = saved.clone(); changed.gc_model.as_mut().unwrap().coeffs[1] = 0.75;
        assert!(!Arc::ptr_eq(&old, &combined_lut_cached(&changed, &options).unwrap()));
        changed = saved.clone(); changed.correction.as_mut().unwrap().coefficients[0] = 0.8;
        assert!(!Arc::ptr_eq(&old, &combined_lut_cached(&changed, &options).unwrap()));
    }
    #[test]
    fn spatial_ffc_remains_separate_and_noncontiguous_images_work() {
        set_cpu_lut_request(true);
        let saved = fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        options.fast_mode = true; options.do_ffc = true;
        let mut input = Array3::from_shape_fn((40,32,3), |(y,x,c)| ((x*7+y*31+c)%256) as f32/255.);
        input.swap_axes(0,1);
        let gain = Array2::from_shape_fn((32,40), |(y,x)| 0.85+(x+y) as f32/180.);
        let mut expected = input.clone();
        apply_ffc_inplace(&mut expected, &gain);
        let table = combined_lut_cached(&saved,&options).unwrap();
        apply_encoded_lut_inplace(&mut expected, &table,65);
        apply_saved_to_work(&mut input,&saved,&options,Some(&gain)).unwrap();
        assert_eq!(input,expected);
        assert!(apply_saved_to_work(&mut input,&saved,&options,None).is_err());
        set_cpu_lut_request(false);
    }
    #[test]
    fn extended_values_preserve_standard_semantics_and_nonfinite_input_is_atomic() {
        set_cpu_lut_request(true);
        let saved = fixture();
        let mut options = ApplyOptions::from_saved(&saved);
        let source = Array3::from_shape_fn((32,40,3), |(y,x,c)| ((x*7+y*31+c)%300) as f32/255.-0.05);
        let mut expected = source.clone();
        apply_saved_to_work(&mut expected,&saved,&options,None).unwrap();
        options.fast_mode = true;
        let mut actual = source;
        apply_saved_to_work(&mut actual,&saved,&options,None).unwrap();
        assert_eq!(actual,expected);
        actual[[0,0,0]] = f32::INFINITY;
        let before = actual.clone();
        assert!(apply_saved_to_work(&mut actual,&saved,&options,None).is_err());
        assert_eq!(actual,before);
        set_cpu_lut_request(false);
    }
}
