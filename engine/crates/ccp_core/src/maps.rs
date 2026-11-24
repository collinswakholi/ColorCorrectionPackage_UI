//! Speedy correction maps: CCM, RPCC2/3, linear, PLS, NN. Ridge 1e-5.

// These hot paths index multiple parallel coefficient buffers; explicit indices keep the
// matrix layout visible and avoid layered zip/enumerate adapters in per-pixel code.
#![allow(clippy::needless_range_loop)]

use crate::color::{
    encoded_to_linear_rgb, lab_d50_to_target_linear, linear_to_encoded_rgb, parse_rgb_color_space,
    parse_white_point, RgbColorSpace, Vec3, WhitePoint,
};
use crate::refs::ChartPatch;
use crate::{Error, Result, RIDGE_LAMBDA};
use serde::{Deserialize, Serialize};

const MIN_POLY_DEGREE: i32 = 1;
const MAX_POLY_DEGREE: i32 = 7;
/// Degree-7 RGB polynomial with bias and combinations: C(7 + 3, 3) = 120 terms.
const MAX_FEATURES: usize = 120;
const MIN_CHANNEL: f64 = 1e-12;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ModelKind {
    Ccm3x3,
    #[serde(rename = "rpcc2")]
    RootPolynomial2,
    #[serde(rename = "rpcc3")]
    RootPolynomial3,
    Linear,
    Pls,
    #[serde(rename = "nn")]
    NeuralNet,
}

impl ModelKind {
    pub fn name(self) -> &'static str {
        match self {
            Self::Ccm3x3 => "ccm",
            Self::RootPolynomial2 => "rpcc2",
            Self::RootPolynomial3 => "rpcc3",
            Self::Linear => "linear",
            Self::Pls => "pls",
            Self::NeuralNet => "nn",
        }
    }
}

pub fn parse_model_kind(value: &str) -> Result<ModelKind> {
    match value {
        "ccm" | "3x3" => Ok(ModelKind::Ccm3x3),
        "rpcc" | "rpcc2" | "root-polynomial-2" => Ok(ModelKind::RootPolynomial2),
        "rpcc3" | "root-polynomial-3" | "finlayson" | "conv" => Ok(ModelKind::RootPolynomial3),
        "linear" | "poly2" | "polynomial-2" => Ok(ModelKind::Linear),
        "pls" => Ok(ModelKind::Pls),
        "nn" | "mlp" => Ok(ModelKind::NeuralNet),
        _ => Err(Error::InvalidArgument(format!(
            "unknown model kind: {value}"
        ))),
    }
}

fn uses_polynomial_features(kind: ModelKind) -> bool {
    matches!(
        kind,
        ModelKind::Linear | ModelKind::Pls | ModelKind::NeuralNet
    )
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct MappingOptions {
    pub extrapolate_saturated: bool,
    pub saturation_threshold: f64,
    pub poly_degree: i32,
    pub include_combinations: bool,
    pub pls_components: i32,
    pub hidden_size: i32,
    #[serde(default)]
    pub hidden_layers: Vec<i32>,
    #[serde(default = "default_nn_optimizer")]
    pub nn_optimizer: String,
    #[serde(default)]
    pub nn_batch_norm: bool,
    #[serde(default)]
    pub nn_dropout: f64,
    pub nn_epochs: i32,
    pub nn_patience: i32,
    pub learning_rate: f64,
    /// CCP_rs `cc_kwargs.tol` — NN early-stop and PLS NIPALS.
    pub tol: f64,
    /// CCP_rs sat-check working floor for recovered RGB (`-0.2`).
    #[serde(default = "default_clip_lo")]
    pub clip_lo: f64,
    /// CCP_rs sat-check working ceiling for recovered RGB (`1.3`).
    #[serde(default = "default_clip_hi")]
    pub clip_hi: f64,
}

fn default_clip_lo() -> f64 {
    crate::RGB_CLIP_LO
}

fn default_clip_hi() -> f64 {
    crate::RGB_CLIP_HI
}

fn default_nn_optimizer() -> String {
    "adam".into()
}

impl Default for MappingOptions {
    fn default() -> Self {
        Self {
            extrapolate_saturated: true,
            saturation_threshold: 0.99,
            poly_degree: 2,
            include_combinations: true,
            pls_components: 0,
            hidden_size: 100,
            hidden_layers: Vec::new(),
            nn_optimizer: default_nn_optimizer(),
            nn_batch_norm: false,
            nn_dropout: 0.0,
            nn_epochs: 10000,
            nn_patience: 1500,
            learning_rate: 0.001,
            tol: 1e-6,
            clip_lo: crate::RGB_CLIP_LO,
            clip_hi: crate::RGB_CLIP_HI,
        }
    }
}

#[derive(Clone, Debug)]
pub struct CorrectionSettings {
    pub input_space: RgbColorSpace,
    pub target_space: RgbColorSpace,
    pub target_white: WhitePoint,
}

impl CorrectionSettings {
    pub fn srgb_d65() -> Result<Self> {
        let white = parse_white_point("d65")?;
        let space = parse_rgb_color_space("srgb", &white)?;
        Ok(Self {
            input_space: space.clone(),
            target_space: space,
            target_white: white,
        })
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CorrectionModel {
    pub kind: ModelKind,
    pub feature_count: i32,
    pub poly_degree: i32,
    pub include_combinations: bool,
    pub pls_components: i32,
    pub hidden_size: i32,
    #[serde(default)]
    pub hidden_layers: Vec<i32>,
    #[serde(default = "default_nn_optimizer")]
    pub nn_optimizer: String,
    #[serde(default)]
    pub nn_batch_norm: bool,
    #[serde(default)]
    pub nn_dropout: f64,
    /// NN models predict a correction residual and therefore start from an
    /// identity-safe baseline. Every Studio release writes `true`; absolute-
    /// output networks from older tools are rejected by `validate`.
    #[serde(default)]
    pub nn_residual: bool,
    #[serde(default)]
    pub nn_epochs_trained: i32,
    #[serde(default)]
    pub nn_best_validation_score: f64,
    #[serde(default)]
    pub nn_best_validation_loss: f64,
    pub input_space_id: String,
    pub target_space_id: String,
    pub target_white_id: String,
    pub saturated_patches: i32,
    pub saturated_names: Vec<String>,
    pub x_mean: Vec<f64>,
    pub x_std: Vec<f64>,
    pub y_mean: Vec3,
    pub y_std: Vec3,
    pub intercept: Vec3,
    pub coefficients: Vec<f64>,
    pub w1: Vec<f64>,
    pub b1: Vec<f64>,
    pub w2: Vec<f64>,
    pub b2: Vec<f64>,
    #[serde(default)]
    pub layer_weights: Vec<Vec<f64>>,
    #[serde(default)]
    pub layer_biases: Vec<Vec<f64>>,
}

impl Default for CorrectionModel {
    fn default() -> Self {
        Self {
            kind: ModelKind::RootPolynomial2,
            feature_count: 0,
            poly_degree: 2,
            include_combinations: true,
            pls_components: 0,
            hidden_size: 0,
            hidden_layers: Vec::new(),
            nn_optimizer: default_nn_optimizer(),
            nn_batch_norm: false,
            nn_dropout: 0.0,
            nn_residual: false,
            nn_epochs_trained: 0,
            nn_best_validation_score: 0.0,
            nn_best_validation_loss: 0.0,
            input_space_id: "srgb".into(),
            target_space_id: "srgb".into(),
            target_white_id: "d65".into(),
            saturated_patches: 0,
            saturated_names: Vec::new(),
            x_mean: Vec::new(),
            x_std: Vec::new(),
            y_mean: [0.0; 3],
            y_std: [1.0, 1.0, 1.0],
            intercept: [0.0; 3],
            coefficients: Vec::new(),
            w1: Vec::new(),
            b1: Vec::new(),
            w2: Vec::new(),
            b2: Vec::new(),
            layer_weights: Vec::new(),
            layer_biases: Vec::new(),
        }
    }
}

fn invalid_model(detail: impl std::fmt::Display) -> Error {
    Error::InvalidModel(format!("invalid correction model: {detail}"))
}

fn check_len(name: &str, actual: usize, expected: usize) -> Result<()> {
    if actual == expected {
        Ok(())
    } else {
        Err(invalid_model(format!(
            "{name} has {actual} values, expected {expected}"
        )))
    }
}

fn check_finite<'a>(name: &str, values: impl IntoIterator<Item = &'a f64>) -> Result<()> {
    if values.into_iter().all(|v| v.is_finite()) {
        Ok(())
    } else {
        Err(invalid_model(format!("{name} contains non-finite values")))
    }
}

impl CorrectionModel {
    /// Check that every coefficient buffer matches the model's declared
    /// shape so [`apply_model`] can never index out of bounds. Saved models
    /// are untrusted input; call this before applying a deserialized model.
    pub fn validate(&self) -> Result<()> {
        if uses_polynomial_features(self.kind)
            && !(MIN_POLY_DEGREE..=MAX_POLY_DEGREE).contains(&self.poly_degree)
        {
            return Err(invalid_model(format!(
                "poly_degree {} is outside {MIN_POLY_DEGREE}..={MAX_POLY_DEGREE}",
                self.poly_degree
            )));
        }
        let mut scratch = [0.0; MAX_FEATURES];
        let probe = [0.25, 0.25, 0.25];
        let expected = write_features_for(
            probe,
            self.kind,
            self.poly_degree,
            self.include_combinations,
            &mut scratch,
        );
        // Linear models historically also accept the bias-augmented basis.
        let alternate = (self.kind == ModelKind::Linear).then(|| {
            write_polynomial_features(
                probe,
                self.poly_degree,
                true,
                self.include_combinations,
                &mut scratch,
            )
        });
        let p = usize::try_from(self.feature_count).unwrap_or(usize::MAX);
        if p != expected && Some(p) != alternate {
            return Err(invalid_model(format!(
                "feature_count {} does not match {} (expected {expected})",
                self.feature_count,
                self.kind.name()
            )));
        }
        check_finite("intercept", &self.intercept)?;
        check_finite("y_mean", &self.y_mean)?;
        check_finite("y_std", &self.y_std)?;
        match self.kind {
            ModelKind::NeuralNet => self.validate_mlp(p),
            ModelKind::Linear | ModelKind::Pls => {
                check_len("coefficients", self.coefficients.len(), p * 3)?;
                check_finite("coefficients", &self.coefficients)?;
                if !self.x_mean.is_empty() || !self.x_std.is_empty() {
                    check_len("x_mean", self.x_mean.len(), p)?;
                    check_len("x_std", self.x_std.len(), p)?;
                    check_finite("x_mean", &self.x_mean)?;
                    check_finite("x_std", &self.x_std)?;
                    if self.x_std.contains(&0.0) {
                        return Err(invalid_model("x_std contains zero"));
                    }
                }
                Ok(())
            }
            _ => {
                check_len("coefficients", self.coefficients.len(), p * 3)?;
                check_finite("coefficients", &self.coefficients)
            }
        }
    }

    fn validate_mlp(&self, p: usize) -> Result<()> {
        let max = crate::mlp::MAX_HIDDEN;
        if p > max {
            return Err(invalid_model(format!("NN input width {p} exceeds {max}")));
        }
        if self.layer_weights.is_empty() || !self.nn_residual {
            return Err(invalid_model(
                "this single-hidden-layer network was written by a release older than ColorCorrect Studio 0.1 and is no longer supported; fit a new calibration",
            ));
        }
        if self.layer_weights.len() != self.layer_biases.len() {
            return Err(invalid_model(
                "layer_weights and layer_biases have different layer counts",
            ));
        }
        let mut input = p;
        for (layer, (weights, biases)) in self
            .layer_weights
            .iter()
            .zip(&self.layer_biases)
            .enumerate()
        {
            let output = biases.len();
            if output == 0 || output > max {
                return Err(invalid_model(format!(
                    "NN layer {layer} width {output} is outside 1..={max}"
                )));
            }
            check_len(
                &format!("layer_weights[{layer}]"),
                weights.len(),
                input * output,
            )?;
            check_finite("layer_weights", weights)?;
            check_finite("layer_biases", biases)?;
            input = output;
        }
        if input != 3 {
            return Err(invalid_model(format!(
                "NN output layer has width {input}, expected 3"
            )));
        }
        Ok(())
    }

    pub fn apply_linear(&self, linear_rgb: Vec3) -> Result<Vec3> {
        apply_model(self, linear_rgb)
    }
}

pub fn write_polynomial_features(
    rgb: Vec3,
    degree: i32,
    include_bias: bool,
    include_combinations: bool,
    out: &mut [f64],
) -> usize {
    let degree = degree.clamp(MIN_POLY_DEGREE, MAX_POLY_DEGREE);
    let r = rgb[0].max(0.0);
    let g = rgb[1].max(0.0);
    let b = rgb[2].max(0.0);
    let mut rp = [0.0; 8];
    let mut gp = [0.0; 8];
    let mut bp = [0.0; 8];
    rp[0] = 1.0;
    gp[0] = 1.0;
    bp[0] = 1.0;
    for n in 1..=degree as usize {
        rp[n] = rp[n - 1] * r;
        gp[n] = gp[n - 1] * g;
        bp[n] = bp[n - 1] * b;
    }
    let mut count = 0;
    let start = if include_bias { 0 } else { 1 };
    for total in start..=degree {
        for i in (0..=total).rev() {
            for j in (0..=(total - i)).rev() {
                let k = total - i - j;
                let used = (i > 0) as i32 + (j > 0) as i32 + (k > 0) as i32;
                if !include_combinations && used > 1 {
                    continue;
                }
                out[count] = rp[i as usize] * gp[j as usize] * bp[k as usize];
                count += 1;
            }
        }
    }
    count
}

pub fn write_features_for(
    linear_rgb: Vec3,
    kind: ModelKind,
    poly_degree: i32,
    include_combinations: bool,
    out: &mut [f64],
) -> usize {
    match kind {
        ModelKind::Linear => {
            write_polynomial_features(linear_rgb, poly_degree, false, include_combinations, out)
        }
        ModelKind::Pls | ModelKind::NeuralNet => {
            write_polynomial_features(linear_rgb, poly_degree, true, true, out)
        }
        ModelKind::Ccm3x3 => {
            let r = linear_rgb[0].max(0.0);
            let g = linear_rgb[1].max(0.0);
            let b = linear_rgb[2].max(0.0);
            out[0] = r;
            out[1] = g;
            out[2] = b;
            3
        }
        ModelKind::RootPolynomial3 => {
            let r = linear_rgb[0].max(0.0);
            let g = linear_rgb[1].max(0.0);
            let b = linear_rgb[2].max(0.0);
            out[0] = r;
            out[1] = g;
            out[2] = b;
            out[3] = (r * g).sqrt();
            out[4] = (r * b).sqrt();
            out[5] = (g * b).sqrt();
            out[6] = (r * r * g).cbrt();
            out[7] = (g * g * b).cbrt();
            out[8] = (b * b * r).cbrt();
            out[9] = (r * g * g).cbrt();
            out[10] = (g * b * b).cbrt();
            out[11] = (b * r * r).cbrt();
            out[12] = (r * g * b).cbrt();
            13
        }
        ModelKind::RootPolynomial2 => {
            let r = linear_rgb[0].max(0.0);
            let g = linear_rgb[1].max(0.0);
            let b = linear_rgb[2].max(0.0);
            out[0] = r;
            out[1] = g;
            out[2] = b;
            out[3] = (r * g).sqrt();
            out[4] = (r * b).sqrt();
            out[5] = (g * b).sqrt();
            6
        }
    }
}

pub fn features_for(
    linear_rgb: Vec3,
    kind: ModelKind,
    poly_degree: i32,
    include_combinations: bool,
) -> Vec<f64> {
    let mut scratch = [0.0; MAX_FEATURES];
    let n = write_features_for(
        linear_rgb,
        kind,
        poly_degree,
        include_combinations,
        &mut scratch,
    );
    scratch[..n].to_vec()
}

/// Pivot threshold relative to the largest entry, floored at the historical
/// absolute `1e-12` for systems whose entries reach 1 or more. Uniformly
/// down-scaled well-conditioned systems therefore solve, while the result for
/// every system the absolute threshold accepted is unchanged.
const PIVOT_RELATIVE_TOLERANCE: f64 = 1e-12;

pub fn solve_linear_system(mut matrix: Vec<f64>, mut rhs: Vec<f64>, n: usize) -> Result<Vec<f64>> {
    if matrix.len() != n * n || rhs.len() != n {
        return Err(Error::InvalidArgument(format!(
            "linear system shape mismatch: {} matrix values and {} rhs values for n={n}",
            matrix.len(),
            rhs.len()
        )));
    }
    if !matrix.iter().chain(&rhs).all(|v| v.is_finite()) {
        return Err(Error::Runtime(
            "correction fit has non-finite values; check chart samples".into(),
        ));
    }
    let scale = matrix.iter().fold(0.0_f64, |m, v| m.max(v.abs()));
    let tolerance = PIVOT_RELATIVE_TOLERANCE * scale.min(1.0);
    for pivot in 0..n {
        let mut best_row = pivot;
        let mut best_value = matrix[pivot * n + pivot].abs();
        for row in (pivot + 1)..n {
            let value = matrix[row * n + pivot].abs();
            if value > best_value {
                best_value = value;
                best_row = row;
            }
        }
        if best_value.is_nan() || best_value == 0.0 || best_value < tolerance {
            return Err(Error::Runtime(
                "correction fit is singular; check chart samples".into(),
            ));
        }
        if best_row != pivot {
            for column in 0..n {
                matrix.swap(pivot * n + column, best_row * n + column);
            }
            rhs.swap(pivot, best_row);
        }
        let divisor = matrix[pivot * n + pivot];
        for column in pivot..n {
            matrix[pivot * n + column] /= divisor;
        }
        rhs[pivot] /= divisor;
        for row in 0..n {
            if row == pivot {
                continue;
            }
            let factor = matrix[row * n + pivot];
            if factor == 0.0 {
                continue;
            }
            for column in pivot..n {
                matrix[row * n + column] -= factor * matrix[pivot * n + column];
            }
            rhs[row] -= factor * rhs[pivot];
        }
    }
    if !rhs.iter().all(|v| v.is_finite()) {
        return Err(Error::Runtime(
            "correction fit produced non-finite coefficients; check chart samples".into(),
        ));
    }
    Ok(rhs)
}

pub fn fit_least_squares(x: &[f64], y: &[f64], n: usize, p: usize, ridge: f64) -> Result<Vec<f64>> {
    let mut xtx = vec![0.0; p * p];
    let mut xty = vec![0.0; p * 3];
    for row in 0..n {
        for i in 0..p {
            let xi = x[row * p + i];
            for j in 0..p {
                xtx[i * p + j] += xi * x[row * p + j];
            }
            for channel in 0..3 {
                xty[i * 3 + channel] += xi * y[row * 3 + channel];
            }
        }
    }
    for i in 0..p {
        xtx[i * p + i] += ridge;
    }
    let mut coefficients = vec![0.0; p * 3];
    for channel in 0..3 {
        let mut rhs = vec![0.0; p];
        for i in 0..p {
            rhs[i] = xty[i * 3 + channel];
        }
        let solution = solve_linear_system(xtx.clone(), rhs, p)?;
        for i in 0..p {
            coefficients[i * 3 + channel] = solution[i];
        }
    }
    Ok(coefficients)
}

fn fit_least_squares_intercept(
    x: &[f64],
    y: &[f64],
    n: usize,
    p: usize,
    ridge: f64,
) -> Result<(Vec<f64>, Vec3)> {
    let p_aug = p + 1;
    let mut x_aug = vec![0.0; n * p_aug];
    for row in 0..n {
        for j in 0..p {
            x_aug[row * p_aug + j] = x[row * p + j];
        }
        x_aug[row * p_aug + p] = 1.0;
    }
    // The intercept column stays ridge-penalized: exempting it moved fitted
    // outputs on the reference fixtures by 2.6e-5 (degree 1) to 0.26 (degree 2).
    let coeffs_aug = fit_least_squares(&x_aug, y, n, p_aug, ridge)?;
    let mut coefficients = vec![0.0; p * 3];
    for j in 0..p {
        for channel in 0..3 {
            coefficients[j * 3 + channel] = coeffs_aug[j * 3 + channel];
        }
    }
    let intercept = [
        coeffs_aug[p * 3],
        coeffs_aug[p * 3 + 1],
        coeffs_aug[p * 3 + 2],
    ];
    Ok((coefficients, intercept))
}

fn pack_pls_into_model(
    coefficients: Vec<f64>,
    intercept: Vec<f64>,
    p: usize,
    model: &mut CorrectionModel,
) {
    model.coefficients = if intercept.len() == 3 {
        coefficients
    } else {
        let mut packed = vec![0.0; p * 3];
        let q = intercept.len().max(1);
        for j in 0..p {
            for k in 0..q.min(3) {
                packed[j * 3 + k] = coefficients[j * q + k];
            }
        }
        packed
    };
    model.intercept = [
        *intercept.first().unwrap_or(&0.0),
        *intercept.get(1).unwrap_or(&0.0),
        *intercept.get(2).unwrap_or(&0.0),
    ];
    model.x_mean.clear();
    model.x_std.clear();
    model.y_mean = [0.0; 3];
    model.y_std = [1.0, 1.0, 1.0];
}

fn fit_pls_model(
    x: Vec<f64>,
    y: Vec<f64>,
    n: usize,
    p: usize,
    ncomp: i32,
    tol: f64,
    model: &mut CorrectionModel,
) -> Result<()> {
    let k = crate::pls::auto_ncomp(n, p, ncomp);
    model.pls_components = k as i32;
    let pls_tol = if tol.is_finite() && tol > 0.0 {
        tol
    } else {
        crate::pls::PLS_TOL
    };
    let (coefficients, intercept) = crate::pls::fit_pls(&x, &y, n, p, 3, k, pls_tol)?;
    pack_pls_into_model(coefficients, intercept, p, model);
    Ok(())
}

fn fit_nn_model(
    x: Vec<f64>,
    y: &[f64],
    n: usize,
    p: usize,
    mapping: &MappingOptions,
    model: &mut CorrectionModel,
) {
    let params = crate::mlp::MlpParams::from_cc(mapping);
    let residual_targets: Vec<f64> = (0..n)
        .flat_map(|row| {
            (0..3).map({
                let x = &x;
                move |channel| y[row * 3 + channel] - x[row * p + channel + 1]
            })
        })
        .collect();
    let fitted = crate::mlp::fit_mlp(&x, &residual_targets, n, p, &params);
    model.hidden_size = fitted.hidden as i32;
    model.hidden_layers = fitted
        .hidden_layers
        .iter()
        .map(|&width| width as i32)
        .collect();
    model.nn_optimizer = mapping.nn_optimizer.clone();
    model.nn_batch_norm = mapping.nn_batch_norm;
    model.nn_dropout = mapping.nn_dropout;
    model.nn_residual = true;
    model.nn_epochs_trained = fitted.epochs_trained as i32;
    model.nn_best_validation_score = fitted.best_validation_score;
    model.nn_best_validation_loss = fitted.best_validation_loss;
    model.layer_weights = fitted.weights.clone();
    model.layer_biases = fitted.biases.clone();
    model.w1 = fitted.w1;
    model.b1 = fitted.b1;
    model.w2 = fitted.w2;
    model.b2 = fitted.b2;
    // CCP_rs feeds raw poly features — never persist input standardization.
    model.x_mean.clear();
    model.x_std.clear();
    model.y_mean = [0.0; 3];
    model.y_std = [1.0, 1.0, 1.0];
}

fn apply_linear_map(model: &CorrectionModel, features: &[f64]) -> Vec3 {
    let mut output = model.intercept;
    let has_x = !model.x_mean.is_empty();
    for feature in 0..model.feature_count as usize {
        let mut x = features[feature];
        if has_x {
            x = (x - model.x_mean[feature]) / model.x_std[feature];
        }
        let base = feature * 3;
        output[0] += x * model.coefficients[base];
        output[1] += x * model.coefficients[base + 1];
        output[2] += x * model.coefficients[base + 2];
    }
    if model.y_std != [1.0, 1.0, 1.0] || model.y_mean != [0.0; 3] {
        for channel in 0..3 {
            output[channel] = output[channel] * model.y_std[channel] + model.y_mean[channel];
        }
    }
    output
}

fn apply_mlp(model: &CorrectionModel, features: &[f64]) -> Vec3 {
    if model.layer_weights.is_empty() || model.layer_weights.len() != model.layer_biases.len() {
        return [f64::NAN; 3];
    }
    let mut a = [0.0; crate::mlp::MAX_HIDDEN];
    let mut b = [0.0; crate::mlp::MAX_HIDDEN];
    let mut input_width = features.len();
    a[..input_width].copy_from_slice(features);
    let last = model.layer_weights.len() - 1;
    for layer in 0..model.layer_weights.len() {
        let output_width = model.layer_biases[layer].len();
        if output_width > crate::mlp::MAX_HIDDEN
            || model.layer_weights[layer].len() != input_width * output_width
        {
            return [f64::NAN; 3];
        }
        for out in 0..output_width {
            let mut value = model.layer_biases[layer][out];
            for input in 0..input_width {
                value += a[input] * model.layer_weights[layer][input * output_width + out];
            }
            b[out] = if layer == last { value } else { value.max(0.0) };
        }
        a[..output_width].copy_from_slice(&b[..output_width]);
        input_width = output_width;
    }
    if input_width == 3 {
        return [a[0], a[1], a[2]];
    }
    [f64::NAN; 3]
}

pub fn apply_model(model: &CorrectionModel, linear_rgb: Vec3) -> Result<Vec3> {
    let mut features = [0.0; MAX_FEATURES];
    let mut n = write_features_for(
        linear_rgb,
        model.kind,
        model.poly_degree,
        model.include_combinations,
        &mut features,
    );
    if model.kind == ModelKind::Linear && n != model.feature_count as usize {
        n = write_polynomial_features(
            linear_rgb,
            model.poly_degree,
            true,
            model.include_combinations,
            &mut features,
        );
    }
    if n != model.feature_count as usize {
        return Err(Error::Runtime("model feature count mismatch".into()));
    }
    Ok(match model.kind {
        ModelKind::NeuralNet => {
            let mut output = apply_mlp(model, &features[..n]);
            if model.nn_residual {
                for channel in 0..3 {
                    output[channel] += linear_rgb[channel];
                }
            }
            output
        }
        ModelKind::Pls | ModelKind::Linear => apply_linear_map(model, &features[..n]),
        _ => {
            let mut output = [0.0; 3];
            for feature in 0..model.feature_count as usize {
                let x = features[feature];
                let base = feature * 3;
                output[0] += x * model.coefficients[base];
                output[1] += x * model.coefficients[base + 1];
                output[2] += x * model.coefficients[base + 2];
            }
            output
        }
    })
}

pub fn collect_xy(
    patches: &[ChartPatch],
    kind: ModelKind,
    poly_degree: i32,
    include_combinations: bool,
    settings: &CorrectionSettings,
) -> Result<(Vec<f64>, Vec<f64>, usize, usize)> {
    let p = features_for([0.25, 0.25, 0.25], kind, poly_degree, include_combinations).len();
    let mut x = Vec::with_capacity(patches.len() * p);
    let mut y = Vec::with_capacity(patches.len() * 3);
    for patch in patches {
        let measured = encoded_to_linear_rgb(patch.measured_srgb, &settings.input_space);
        let reference = lab_d50_to_target_linear(
            patch.reference_lab_d50,
            &settings.target_space,
            &settings.target_white,
        )?;
        let feats = features_for(measured, kind, poly_degree, include_combinations);
        // A non-finite sample would poison every coefficient (or, through the
        // feature clamp, silently read as black); leave it out.
        let values = patch
            .measured_srgb
            .iter()
            .chain(&measured)
            .chain(&reference);
        if !values.chain(&feats).all(|v| v.is_finite()) {
            continue;
        }
        x.extend_from_slice(&feats[..p]);
        y.extend_from_slice(&reference);
    }
    let n = y.len() / 3;
    if n == 0 {
        return Err(Error::InvalidArgument(
            "cannot fit correction model: no chart patch has finite measured and reference values"
                .into(),
        ));
    }
    Ok((x, y, n, p))
}

pub fn fit_correction_model(
    patches: &[ChartPatch],
    kind: ModelKind,
    settings: &CorrectionSettings,
    ridge_lambda: f64,
    mapping: &MappingOptions,
) -> Result<CorrectionModel> {
    if patches.is_empty() {
        return Err(Error::InvalidArgument(
            "cannot fit correction model without chart patches".into(),
        ));
    }
    let mut model = CorrectionModel {
        kind,
        poly_degree: if uses_polynomial_features(kind) {
            mapping.poly_degree.clamp(MIN_POLY_DEGREE, MAX_POLY_DEGREE)
        } else {
            mapping.poly_degree
        },
        include_combinations: match kind {
            ModelKind::Pls | ModelKind::NeuralNet => true,
            _ => !uses_polynomial_features(kind) || mapping.include_combinations,
        },
        input_space_id: settings.input_space.id.clone(),
        target_space_id: settings.target_space.id.clone(),
        target_white_id: settings.target_white.id.clone(),
        ..CorrectionModel::default()
    };
    model.kind = kind;
    model.saturated_names = patches
        .iter()
        .filter(|p| {
            p.saturated
                || p.original_measured_srgb
                    .iter()
                    .any(|&c| c >= mapping.saturation_threshold)
        })
        .map(|p| p.name.clone())
        .collect();
    model.saturated_patches = model.saturated_names.len() as i32;

    let (x, y, n, p) = collect_xy(
        patches,
        kind,
        model.poly_degree,
        model.include_combinations,
        settings,
    )?;
    model.feature_count = p as i32;
    let ridge = if ridge_lambda > 0.0 {
        ridge_lambda
    } else {
        RIDGE_LAMBDA
    };
    match kind {
        ModelKind::Pls => {
            fit_pls_model(x, y, n, p, mapping.pls_components, mapping.tol, &mut model)?;
        }
        ModelKind::NeuralNet => fit_nn_model(x, &y, n, p, mapping, &mut model),
        ModelKind::Linear => {
            let (coeffs, intercept) = fit_least_squares_intercept(&x, &y, n, p, ridge)?;
            model.coefficients = coeffs;
            model.intercept = intercept;
        }
        _ => {
            model.coefficients = fit_least_squares(&x, &y, n, p, ridge)?;
        }
    }
    let _ = MIN_CHANNEL;
    // PLS and NN fits do not go through the solver; check every family.
    model.validate()?;
    Ok(model)
}

pub fn apply_correction_pixel(
    model: &CorrectionModel,
    encoded: Vec3,
    settings: &CorrectionSettings,
) -> Result<Vec3> {
    let linear = encoded_to_linear_rgb(encoded, &settings.input_space);
    let corrected = crate::color::clamp01_vec(apply_model(model, linear)?);
    Ok(linear_to_encoded_rgb(corrected, &settings.target_space))
}

/// High-quality encoded-RGB LUT for still-image apply. A 33³ grid reduces
/// interpolation error substantially while adding negligible sampling time.
pub const APPLY_LUT_SIZE: usize = 33;
pub const APPLY_LUT_MIN_PIXELS: usize = 1024;

pub fn build_encoded_lut(
    model: &CorrectionModel,
    settings: &CorrectionSettings,
    size: usize,
) -> Result<Vec<f32>> {
    model.validate()?;
    let gs = size.max(2);
    let mut lut = vec![0f32; gs * gs * gs * 3];
    let denom = (gs - 1) as f64;
    for bi in 0..gs {
        for gi in 0..gs {
            for ri in 0..gs {
                let rgb = [ri as f64 / denom, gi as f64 / denom, bi as f64 / denom];
                let out = apply_correction_pixel(model, rgb, settings)?;
                let idx = ((bi * gs + gi) * gs + ri) * 3;
                lut[idx] = out[0] as f32;
                lut[idx + 1] = out[1] as f32;
                lut[idx + 2] = out[2] as f32;
            }
        }
    }
    Ok(lut)
}

#[inline]
pub fn sample_encoded_lut(lut: &[f32], gs: usize, rgb: Vec3) -> Vec3 {
    let max = (gs - 1) as f64;
    let r = rgb[0].clamp(0.0, 1.0) * max;
    let g = rgb[1].clamp(0.0, 1.0) * max;
    let b = rgb[2].clamp(0.0, 1.0) * max;
    let r0 = r.floor() as usize;
    let g0 = g.floor() as usize;
    let b0 = b.floor() as usize;
    let r1 = (r0 + 1).min(gs - 1);
    let g1 = (g0 + 1).min(gs - 1);
    let b1 = (b0 + 1).min(gs - 1);
    let fr = r - r0 as f64;
    let fg = g - g0 as f64;
    let fb = b - b0 as f64;
    let at = |ri: usize, gi: usize, bi: usize| {
        let i = ((bi * gs + gi) * gs + ri) * 3;
        [lut[i] as f64, lut[i + 1] as f64, lut[i + 2] as f64]
    };
    let lerp = |a: Vec3, bb: Vec3, t: f64| -> Vec3 {
        [
            a[0] + (bb[0] - a[0]) * t,
            a[1] + (bb[1] - a[1]) * t,
            a[2] + (bb[2] - a[2]) * t,
        ]
    };
    let c00 = lerp(at(r0, g0, b0), at(r1, g0, b0), fr);
    let c10 = lerp(at(r0, g1, b0), at(r1, g1, b0), fr);
    let c01 = lerp(at(r0, g0, b1), at(r1, g0, b1), fr);
    let c11 = lerp(at(r0, g1, b1), at(r1, g1, b1), fr);
    lerp(lerp(c00, c10, fg), lerp(c01, c11, fg), fb)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::color::Lab;
    use crate::refs::Vec2;

    fn patch(name: &str, index: usize, lab: Lab, measured: Vec3) -> ChartPatch {
        ChartPatch {
            provenance: None,
            name: name.into(),
            index,
            reference_lab_d50: lab,
            reference_srgb: measured,
            measured_srgb: measured,
            original_measured_srgb: measured,
            center: Vec2 { x: 0.0, y: 0.0 },
            saturated: false,
        }
    }

    fn classic_ish_patches() -> Vec<ChartPatch> {
        let labs = [
            Lab::new(38.0, 13.0, 14.0),
            Lab::new(65.0, 18.0, 18.0),
            Lab::new(50.0, -5.0, -22.0),
            Lab::new(43.0, -13.0, 22.0),
            Lab::new(55.0, 9.0, -25.0),
            Lab::new(71.0, -33.0, 0.0),
            Lab::new(62.0, 36.0, 57.0),
            Lab::new(40.0, 10.0, -46.0),
            Lab::new(51.0, 48.0, 16.0),
            Lab::new(30.0, 22.0, -22.0),
            Lab::new(72.0, -24.0, 57.0),
            Lab::new(72.0, 19.0, 68.0),
            Lab::new(29.0, 14.0, -50.0),
            Lab::new(55.0, -38.0, 31.0),
            Lab::new(42.0, 51.0, 28.0),
            Lab::new(82.0, 4.0, 80.0),
            Lab::new(52.0, 50.0, -15.0),
            Lab::new(51.0, -28.0, -28.0),
            Lab::new(96.0, 0.0, 0.0),
            Lab::new(81.0, 0.0, 0.0),
            Lab::new(66.0, 0.0, 0.0),
            Lab::new(51.0, 0.0, 0.0),
            Lab::new(35.0, 0.0, 0.0),
            Lab::new(20.0, 0.0, 0.0),
        ];
        labs.iter()
            .enumerate()
            .map(|(i, lab)| {
                let t = i as f64 / 23.0;
                let measured = [
                    (0.15 + 0.7 * t).min(0.95),
                    (0.12 + 0.55 * (1.0 - t)).min(0.95),
                    (0.20 + 0.4 * ((i % 5) as f64 / 4.0)).min(0.95),
                ];
                patch(&format!("P{i}"), i, *lab, measured)
            })
            .collect()
    }

    #[test]
    fn pls_auto_ncomp_is_p_minus_one() {
        let settings = CorrectionSettings::srgb_d65().unwrap();
        let mapping = MappingOptions {
            pls_components: 0,
            poly_degree: 2,
            include_combinations: true,
            ..MappingOptions::default()
        };
        let model = fit_correction_model(
            &classic_ish_patches(),
            ModelKind::Pls,
            &settings,
            0.0,
            &mapping,
        )
        .unwrap();
        // degree 2 RGB + bias = 10 features → auto k = 9
        assert_eq!(model.feature_count, 10);
        assert_eq!(model.pls_components, 9);
        assert!(model.x_mean.is_empty());
        let y = apply_model(&model, [0.4, 0.3, 0.2]).unwrap();
        assert!(y.iter().all(|v| v.is_finite()));
    }

    fn fitted(kind: ModelKind, degree: i32) -> CorrectionModel {
        let settings = CorrectionSettings::srgb_d65().unwrap();
        let mapping = MappingOptions {
            poly_degree: degree,
            hidden_size: 8,
            hidden_layers: vec![8],
            nn_epochs: 40,
            nn_patience: 10,
            ..MappingOptions::default()
        };
        fit_correction_model(&classic_ish_patches(), kind, &settings, 0.0, &mapping).unwrap()
    }

    #[test]
    fn solver_is_scale_aware_and_still_rejects_singular_systems() {
        let system = vec![4.0, 1.0, 0.5, 1.0, 3.0, 0.25, 0.5, 0.25, 2.0];
        let rhs = vec![1.0, 2.0, 3.0];
        let unit = solve_linear_system(system.clone(), rhs.clone(), 3).unwrap();
        for scale in [1e-14, 1e-9, 1e6] {
            let scaled = system.iter().map(|v| v * scale).collect();
            let rhs_scaled = rhs.iter().map(|v| v * scale).collect();
            let solution = solve_linear_system(scaled, rhs_scaled, 3)
                .unwrap_or_else(|e| panic!("scale {scale}: {e}"));
            for (a, b) in solution.iter().zip(&unit) {
                assert!((a - b).abs() <= 1e-9 * b.abs().max(1.0), "scale {scale}");
            }
        }
        assert!(solve_linear_system(vec![0.0; 4], vec![1.0, 1.0], 2).is_err());
        assert!(solve_linear_system(vec![1.0, 1.0, 1.0, 1.0], vec![1.0, 2.0], 2).is_err());
        assert!(solve_linear_system(vec![1.0, f64::NAN, 0.0, 1.0], vec![1.0, 2.0], 2).is_err());
        assert!(
            solve_linear_system(vec![1.0, 0.0, 0.0, 1.0], vec![f64::INFINITY, 2.0], 2).is_err()
        );
    }

    #[test]
    fn non_finite_samples_are_dropped_before_fitting() {
        let settings = CorrectionSettings::srgb_d65().unwrap();
        let mut patches = classic_ish_patches();
        let clean = fit_correction_model(
            &patches,
            ModelKind::RootPolynomial2,
            &settings,
            0.0,
            &MappingOptions::default(),
        )
        .unwrap();
        let mut bad = patches[0].clone();
        bad.measured_srgb = [f64::NAN, 0.2, 0.3];
        patches.push(bad);
        let mut infinite = patches[1].clone();
        infinite.reference_lab_d50.l = f64::INFINITY;
        patches.push(infinite);
        let (_, _, n, _) =
            collect_xy(&patches, ModelKind::RootPolynomial2, 2, true, &settings).unwrap();
        assert_eq!(n, patches.len() - 2);
        let model = fit_correction_model(
            &patches,
            ModelKind::RootPolynomial2,
            &settings,
            0.0,
            &MappingOptions::default(),
        )
        .unwrap();
        assert_eq!(model.coefficients, clean.coefficients);
        let all_bad = vec![patches[patches.len() - 2].clone()];
        assert!(fit_correction_model(
            &all_bad,
            ModelKind::RootPolynomial2,
            &settings,
            0.0,
            &MappingOptions::default()
        )
        .is_err());
    }

    #[test]
    fn degree_seven_polynomial_models_fit_and_apply_without_panicking() {
        for kind in [ModelKind::Linear, ModelKind::Pls, ModelKind::NeuralNet] {
            let model = fitted(kind, 7);
            assert_eq!(model.poly_degree, 7);
            model.validate().unwrap();
            let y = apply_model(&model, [0.4, 0.3, 0.2]).unwrap();
            assert!(y.iter().all(|v| v.is_finite()), "{kind:?}");
        }
    }

    #[test]
    fn validate_accepts_fitted_models_and_rejects_crafted_ones() {
        for kind in [
            ModelKind::Ccm3x3,
            ModelKind::RootPolynomial2,
            ModelKind::RootPolynomial3,
            ModelKind::Linear,
            ModelKind::Pls,
            ModelKind::NeuralNet,
        ] {
            let model = fitted(kind, 2);
            model.validate().unwrap_or_else(|e| panic!("{kind:?}: {e}"));

            let mut short = model.clone();
            if kind == ModelKind::NeuralNet {
                short.layer_weights[0].pop();
            } else {
                short.coefficients.pop();
            }
            assert!(short.validate().is_err(), "{kind:?} truncated");

            let mut count = model.clone();
            count.feature_count += 1;
            assert!(count.validate().is_err(), "{kind:?} feature_count");

            let mut huge = model.clone();
            huge.feature_count = 10_000;
            assert!(huge.validate().is_err(), "{kind:?} huge feature_count");

            let mut nan = model.clone();
            nan.intercept[0] = f64::NAN;
            if kind == ModelKind::NeuralNet {
                nan.layer_biases[0][0] = f64::INFINITY;
            } else {
                nan.coefficients[0] = f64::INFINITY;
            }
            assert!(nan.validate().is_err(), "{kind:?} non-finite");
        }

        let mut standardized = fitted(ModelKind::Linear, 2);
        standardized.x_mean = vec![0.0; 2];
        standardized.x_std = vec![1.0; 2];
        assert!(standardized.validate().is_err(), "short standardization");

        let mut legacy = CorrectionModel {
            kind: ModelKind::NeuralNet,
            poly_degree: 1,
            feature_count: 4,
            hidden_size: 4,
            w1: vec![0.2; 16],
            b1: vec![0.0; 4],
            w2: vec![0.1; 12],
            b2: vec![0.0; 3],
            ..CorrectionModel::default()
        };
        let error = legacy.validate().expect_err("pre-Studio single-layer NN");
        assert!(error.to_string().contains("fit a new calibration"), "{error}");
        legacy.nn_residual = true;
        assert!(legacy.validate().is_err(), "w1..b2 alone are not applied");
    }

    #[test]
    fn nn_does_not_standardize_inputs() {
        let settings = CorrectionSettings::srgb_d65().unwrap();
        let mapping = MappingOptions {
            hidden_size: 16,
            nn_epochs: 120,
            nn_patience: 30,
            poly_degree: 2,
            ..MappingOptions::default()
        };
        let model = fit_correction_model(
            &classic_ish_patches(),
            ModelKind::NeuralNet,
            &settings,
            0.0,
            &mapping,
        )
        .unwrap();
        assert!(model.x_mean.is_empty(), "CCP_rs NN uses raw poly features");
        assert_eq!(model.hidden_size, 16);
        assert_eq!(model.w1.len(), 10 * 16);
        assert_eq!(model.b2.len(), 3);
        let y = apply_model(&model, [0.5, 0.4, 0.3]).unwrap();
        assert!(y.iter().all(|v| v.is_finite()));
    }
}
