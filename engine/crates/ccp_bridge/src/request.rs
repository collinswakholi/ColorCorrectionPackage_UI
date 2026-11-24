//! Typed reading of NDJSON request fields: aliases, defaults and range checks.

use std::ops::RangeInclusive;

use ccp_core::ffc::{
    FfcFitMethod, FfcFitParams, Upsample, FFC_BINS_RANGE, FFC_DEGREE_RANGE, FFC_NN_MAX_ITER_RANGE,
    FFC_SMOOTH_RANGE,
};
use ccp_core::maps::MappingOptions;
use ccp_core::sample::{SamplingOptions, MAX_GRID_SAMPLES_PER_AXIS, MAX_RANDOM_SAMPLES_PER_PATCH};
use serde_json::Value;

use crate::error::{Error, Result};

/// Aliases of the input image path on `detect`, `run` and `preview_stage`.
pub(crate) const IMAGE_KEYS: [&str; 3] = ["image", "input", "image_path"];

/// The first nonempty string under any of `keys`.
pub(crate) fn json_str<'a>(v: &'a Value, keys: &[&str]) -> Option<&'a str> {
    keys.iter()
        .filter_map(|key| v.get(*key).and_then(Value::as_str))
        .find(|s| !s.is_empty())
}

pub(crate) fn json_bool(v: &Value, keys: &[&str], default: bool) -> bool {
    keys.iter()
        .find_map(|key| v.get(*key).and_then(Value::as_bool))
        .unwrap_or(default)
}

pub(crate) fn json_f64(v: &Value, keys: &[&str], default: f64) -> f64 {
    keys.iter()
        .find_map(|key| v.get(*key).and_then(Value::as_f64))
        .unwrap_or(default)
}

/// Integers, or numbers truncated toward zero.
pub(crate) fn json_i64(v: &Value, keys: &[&str], default: i64) -> i64 {
    keys.iter()
        .find_map(|key| {
            let value = v.get(*key)?;
            value.as_i64().or_else(|| value.as_f64().map(|n| n as i64))
        })
        .unwrap_or(default)
}

/// `ffc.method`, polynomial when absent.
pub(crate) fn ffc_method(req: &Value) -> Result<FfcFitMethod> {
    let fields = match req.get("ffc") {
        None | Some(Value::Null) => None,
        Some(Value::Object(map)) => Some(map),
        _ => return Err(Error::Message("ffc must be an object".into())),
    };
    match fields.and_then(|m| m.get("method")) {
        None => Ok(FfcFitMethod::Poly),
        Some(value) => match value.as_str().map(str::to_ascii_lowercase).as_deref() {
            Some("linear") => Ok(FfcFitMethod::Linear),
            Some("poly" | "polynomial") => Ok(FfcFitMethod::Poly),
            Some("pls") => Ok(FfcFitMethod::Pls),
            Some("nn") => Ok(FfcFitMethod::Nn),
            _ => Err(Error::Message(
                "unknown FFC method: choose linear, poly, pls or nn".into(),
            )),
        },
    }
}

pub(crate) fn ffc_params(req: &Value, method: FfcFitMethod) -> Result<FfcFitParams> {
    let ffc = req.get("ffc").cloned().unwrap_or(Value::Null);
    let ranged = |keys: &[&str], name: &str, default: i64, range: RangeInclusive<i64>| {
        let value = json_i64(&ffc, keys, default);
        if range.contains(&value) {
            Ok(value)
        } else {
            Err(Error::Message(format!(
                "ffc.{name} must be an integer from {} to {} (got {value})",
                range.start(),
                range.end()
            )))
        }
    };
    let mut smooth = ranged(
        &["smooth", "smooth_window"],
        "smooth_window",
        5,
        FFC_SMOOTH_RANGE,
    )?;
    if smooth % 2 == 0 {
        smooth += 1;
    }
    let bins = ranged(&["bins"], "bins", 50, FFC_BINS_RANGE)?;
    let degree = ranged(&["degree"], "degree", 5, FFC_DEGREE_RANGE)?;
    let nn_max_iter = ranged(
        &["nn_max_iter", "max_iter"],
        "nn_max_iter",
        10_000,
        FFC_NN_MAX_ITER_RANGE,
    )?;
    let tol = json_f64(&ffc, &["tol"], 1e-8);
    Ok(FfcFitParams {
        method,
        bins: bins as usize,
        smooth_window: smooth as i32,
        degree: degree as usize,
        upsample: Upsample::Cubic,
        nn_max_iter: nn_max_iter as usize,
        tol: if tol.is_finite() && tol > 0.0 {
            tol
        } else {
            1e-8
        },
        cut_off: json_f64(&ffc, &["cut_off"], 1.3) as f32,
        max_val: json_f64(&ffc, &["max_val"], 1.8) as f32,
    })
}

/// CC model options. Nested `pls`/`nn` fields win over their flat aliases.
pub(crate) fn mapping(req: &Value) -> MappingOptions {
    let defaults = MappingOptions::default();
    let pls = req.get("pls").cloned().unwrap_or(Value::Null);
    let nn = req.get("nn").cloned().unwrap_or(Value::Null);
    let nested_i64 = |keys: &[&str], flat: &str, default: i64| {
        json_i64(&nn, keys, json_i64(req, &[flat], default))
    };
    let nested_f64 = |keys: &[&str], flat: &str, default: f64| {
        json_f64(&nn, keys, json_f64(req, &[flat], default))
    };
    let hidden_size = nested_i64(&["hidden_size"], "hidden_size", 100) as i32;
    let mut hidden_layers: Vec<i32> = nn
        .get("hidden_layers")
        .or_else(|| req.get("hidden_layers"))
        .and_then(Value::as_array)
        .map(|values| {
            values
                .iter()
                .filter_map(Value::as_i64)
                .take(4)
                .map(|width| width.clamp(4, 512) as i32)
                .collect()
        })
        .unwrap_or_default();
    if hidden_layers.is_empty() {
        hidden_layers.push(hidden_size.clamp(4, 512));
    }
    let tol = nested_f64(&["tol"], "tol", 1e-6);
    MappingOptions {
        extrapolate_saturated: defaults.extrapolate_saturated,
        saturation_threshold: defaults.saturation_threshold,
        poly_degree: json_i64(req, &["degree"], 2) as i32,
        include_combinations: json_bool(req, &["include_combinations"], true),
        pls_components: json_i64(
            &pls,
            &["components", "pls_components"],
            json_i64(req, &["pls_components"], 0),
        ) as i32,
        hidden_size: hidden_layers[0],
        hidden_layers,
        nn_optimizer: json_str(&nn, &["optimizer", "nn_optimizer"])
            .or_else(|| json_str(req, &["nn_optimizer"]))
            .unwrap_or("adam")
            .to_ascii_lowercase(),
        nn_batch_norm: json_bool(
            &nn,
            &["batch_norm"],
            json_bool(req, &["nn_batch_norm"], false),
        ),
        nn_dropout: nested_f64(&["dropout"], "nn_dropout", 0.0).clamp(0.0, 0.8),
        nn_epochs: nested_i64(&["epochs", "nn_epochs"], "nn_epochs", 10000) as i32,
        nn_patience: nested_i64(&["patience", "nn_patience"], "nn_patience", 1500) as i32,
        learning_rate: nested_f64(&["lr", "learning_rate"], "nn_lr", 0.001),
        tol: if tol.is_finite() && tol > 0.0 {
            tol
        } else {
            1e-6
        },
        clip_lo: json_f64(req, &["clip_lo"], -0.2),
        clip_hi: json_f64(req, &["clip_hi"], 1.3),
    }
}

/// Grid (`fit_samples`) or seeded random (`samples_per_patch`) patch sampling.
pub(crate) fn sampling(req: &Value) -> Result<SamplingOptions> {
    for alias in ["n_samples", "random_seed"] {
        if req.get(alias).is_some() {
            return Err(Error::Message(format!(
                "{alias} is not supported; use samples_per_patch and sampling_seed for random sampling, or fit_samples for grid points per axis"
            )));
        }
    }
    let random = if let Some(value) = req.get("samples_per_patch") {
        if req.get("fit_samples").is_some() {
            return Err(Error::Message(
                "choose fit_samples or samples_per_patch, not both".into(),
            ));
        }
        let count = value
            .as_u64()
            .filter(|n| (1..=u64::from(MAX_RANDOM_SAMPLES_PER_PATCH)).contains(n))
            .ok_or_else(|| {
                Error::Message(format!(
                    "samples_per_patch must be an integer from 1 to {MAX_RANDOM_SAMPLES_PER_PATCH}"
                ))
            })?;
        let seed = match req.get("sampling_seed") {
            None => 0,
            Some(s) => s
                .as_u64()
                .and_then(|v| u32::try_from(v).ok())
                .ok_or_else(|| {
                    Error::Message("sampling_seed must be an unsigned 32-bit integer".into())
                })?,
        };
        Some((count as u32, seed))
    } else {
        if req.get("sampling_seed").is_some() {
            return Err(Error::Message(
                "sampling_seed requires samples_per_patch".into(),
            ));
        }
        None
    };
    let samples_per_axis = match req.get("fit_samples") {
        None => SamplingOptions::default().samples_per_axis,
        Some(value) => value.as_i64()
            .filter(|n| (1..=i64::from(MAX_GRID_SAMPLES_PER_AXIS)).contains(n))
            .ok_or_else(|| Error::Message(format!(
                "fit_samples must be an integer from 1 to {MAX_GRID_SAMPLES_PER_AXIS} (grid points per axis)"
            )))? as i32,
    };
    Ok(SamplingOptions {
        samples_per_axis,
        random,
        ..SamplingOptions::default()
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn ffc_default_smooth_is_odd_five() {
        assert_eq!(FfcFitParams::default().smooth_window, 5);
        assert_eq!(FfcFitParams::default().smooth_window % 2, 1);
    }

    #[test]
    fn sampling_request_rejects_coercion_overflow_and_aliases() {
        assert_eq!(sampling(&json!({})).unwrap().samples_per_axis, 9);
        for value in [
            json!(true),
            json!(1.5),
            json!("9"),
            Value::Null,
            json!(0),
            json!(-1),
            json!(201),
            json!(4_294_967_305u64),
        ] {
            let error = sampling(&json!({"fit_samples":value})).unwrap_err();
            assert!(error.to_string().contains("fit_samples"));
        }
        for request in [
            json!({"random_seed":42}),
            json!({"n_samples":10}),
            json!({"sampling_seed":42}),
            json!({"fit_samples":9,"samples_per_patch":10}),
            json!({"samples_per_patch":1.5}),
            json!({"samples_per_patch":true}),
            json!({"samples_per_patch":4097}),
            json!({"samples_per_patch":10,"sampling_seed":4_294_967_296u64}),
        ] {
            assert!(sampling(&request).is_err(), "accepted {request}");
        }
        for count in [1, 10, 20, 50] {
            let options = sampling(&json!({"samples_per_patch":count,"sampling_seed":42})).unwrap();
            assert_eq!(options.random, Some((count, 42)));
        }
    }
}
