//! Compact dense MLP used by chart correction and FFC.
//!
//! CC (`from_cc`): raw polynomial features (no input standardization),
//! NumPy MT19937 seed 42, `shuffle=false` after the train/val split,
//! early stop on validation **R²**, `n_iter_no_change = 15% × max_iter`,
//! Adam, L2 `alpha=1e-4`, batch `min(200, N)`.
//!
//! FFC (`for_ffc`): hidden 100, shuffle on, 3 LR cuts, MSE floor `5e-4`.

// Training kernels operate on several row-major buffers in lockstep.
#![allow(clippy::needless_range_loop)]

use crate::maps::MappingOptions;

pub const MAX_HIDDEN: usize = 512;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MlpOptimizer {
    Adam,
    Sgd,
    RmsProp,
}

impl MlpOptimizer {
    fn parse(value: &str) -> Self {
        match value.trim().to_ascii_lowercase().as_str() {
            "sgd" | "momentum" => Self::Sgd,
            "rmsprop" | "rms" => Self::RmsProp,
            _ => Self::Adam,
        }
    }
}

#[derive(Clone, Debug)]
pub struct MlpParams {
    pub hidden: usize,
    pub hidden_layers: Vec<usize>,
    pub optimizer: MlpOptimizer,
    pub batch_normalization: bool,
    pub dropout: f64,
    pub n_out: usize,
    pub max_iter: usize,
    pub learning_rate: f64,
    pub tol: f64,
    pub random_state: u64,
    pub shuffle: bool,
    pub early_stopping: bool,
    pub validation_fraction: f64,
    pub n_iter_no_change: usize,
    pub alpha: f64,
    pub lr_decay_factor: f64,
    pub max_lr_reductions: usize,
    pub convergence_floor: f64,
    pub use_numpy_rng: bool,
    /// Train a zero-initialized residual head. CC uses this so the untrained
    /// network is the identity correction instead of an arbitrary colour map.
    pub residual_output: bool,
}

impl MlpParams {
    /// CCP_rs `MlpParams::from_cc_regressor` (UI: hidden 100, 10000 iter, patience 15%).
    pub fn from_cc(mapping: &MappingOptions) -> Self {
        let max_iter = mapping.nn_epochs.max(1) as usize;
        let patience = if mapping.nn_patience > 0 {
            mapping.nn_patience as usize
        } else {
            ((max_iter as f64) * 0.15).max(10.0) as usize
        };
        Self {
            hidden: mapping.hidden_size.max(1) as usize,
            hidden_layers: if mapping.hidden_layers.is_empty() {
                vec![mapping.hidden_size.max(1) as usize]
            } else {
                mapping
                    .hidden_layers
                    .iter()
                    .take(4)
                    .map(|&width| width.clamp(4, MAX_HIDDEN as i32) as usize)
                    .collect()
            },
            optimizer: MlpOptimizer::parse(&mapping.nn_optimizer),
            batch_normalization: mapping.nn_batch_norm,
            dropout: mapping.nn_dropout.clamp(0.0, 0.8),
            n_out: 3,
            max_iter,
            learning_rate: mapping.learning_rate,
            tol: if mapping.tol.is_finite() && mapping.tol > 0.0 {
                mapping.tol
            } else {
                1e-6
            },
            random_state: 42,
            shuffle: false,
            early_stopping: true,
            validation_fraction: 0.15,
            n_iter_no_change: patience.max(1),
            alpha: 1e-4,
            lr_decay_factor: 0.2,
            max_lr_reductions: 0,
            convergence_floor: 0.0,
            use_numpy_rng: true,
            residual_output: true,
        }
    }

    /// CCP_rs `MlpParams::for_ffc`.
    pub fn for_ffc() -> Self {
        Self::for_ffc_iters(10_000)
    }

    pub fn for_ffc_iters(max_iter: usize) -> Self {
        let max_iter = max_iter.max(1);
        Self {
            hidden: 100,
            hidden_layers: vec![100],
            optimizer: MlpOptimizer::Adam,
            batch_normalization: false,
            dropout: 0.0,
            n_out: 1,
            max_iter,
            learning_rate: 0.001,
            tol: 1e-8,
            random_state: 0,
            shuffle: true,
            early_stopping: true,
            validation_fraction: 0.15,
            n_iter_no_change: ((max_iter as f64) * 0.1) as usize,
            alpha: 1e-4,
            lr_decay_factor: 0.2,
            max_lr_reductions: 3,
            convergence_floor: 5e-4,
            use_numpy_rng: false,
            residual_output: false,
        }
    }
}

#[derive(Clone, Debug)]
pub struct MlpWeights {
    pub n_in: usize,
    pub hidden: usize,
    pub n_out: usize,
    pub w1: Vec<f64>,
    pub b1: Vec<f64>,
    pub w2: Vec<f64>,
    pub b2: Vec<f64>,
    pub hidden_layers: Vec<usize>,
    pub weights: Vec<Vec<f64>>,
    pub biases: Vec<Vec<f64>>,
    pub epochs_trained: usize,
    pub best_validation_score: f64,
    pub best_validation_loss: f64,
}

impl MlpWeights {
    #[inline]
    pub fn predict_into(&self, x: &[f64], hidden_scratch: &mut [f64], out: &mut [f64]) {
        if !self.weights.is_empty() && self.weights.len() == self.biases.len() {
            let mut a = [0.0; MAX_HIDDEN];
            let mut b = [0.0; MAX_HIDDEN];
            let mut input_width = x.len();
            a[..input_width].copy_from_slice(x);
            let last = self.weights.len() - 1;
            for layer in 0..self.weights.len() {
                let output_width = self.biases[layer].len();
                for output in 0..output_width {
                    let mut value = self.biases[layer][output];
                    for input in 0..input_width {
                        value += a[input] * self.weights[layer][input * output_width + output];
                    }
                    b[output] = if layer == last { value } else { value.max(0.0) };
                }
                a[..output_width].copy_from_slice(&b[..output_width]);
                input_width = output_width;
            }
            out[..self.n_out].copy_from_slice(&a[..self.n_out]);
            return;
        }

        let h = self.hidden;
        let p = self.n_in;
        debug_assert!(hidden_scratch.len() >= h);
        hidden_scratch[..h].copy_from_slice(&self.b1[..h]);
        for j in 0..p {
            let xj = x[j];
            let row = j * h;
            for hh in 0..h {
                hidden_scratch[hh] += xj * self.w1[row + hh];
            }
        }
        for hh in 0..h {
            hidden_scratch[hh] = hidden_scratch[hh].max(0.0);
        }
        for k in 0..self.n_out {
            let mut y = self.b2[k];
            for hh in 0..h {
                y += hidden_scratch[hh] * self.w2[hh * self.n_out + k];
            }
            out[k] = y;
        }
    }

    pub fn predict3(&self, x: &[f64]) -> [f64; 3] {
        let mut hidden = [0.0; MAX_HIDDEN];
        let mut out = [0.0; 3];
        self.predict_into(x, &mut hidden, &mut out);
        out
    }

    pub fn predict1(&self, x: &[f64]) -> f64 {
        let mut hidden = [0.0; MAX_HIDDEN];
        let mut out = [0.0; 1];
        self.predict_into(x, &mut hidden, &mut out);
        out[0]
    }
}

/// NumPy `RandomState` MT19937: `init_genrand` + `genrand_res53` + `rk_interval`.
struct NumpyRng {
    mt: [u32; 624],
    idx: usize,
}

impl NumpyRng {
    fn new(seed: u32) -> Self {
        let mut mt = [0u32; 624];
        mt[0] = seed;
        for i in 1..624 {
            mt[i] = 1_812_433_253u32
                .wrapping_mul(mt[i - 1] ^ (mt[i - 1] >> 30))
                .wrapping_add(i as u32);
        }
        Self { mt, idx: 624 }
    }

    fn twist(&mut self) {
        for i in 0..624 {
            let x = (self.mt[i] & 0x8000_0000) | (self.mt[(i + 1) % 624] & 0x7fff_ffff);
            let mut xa = x >> 1;
            if x & 1 != 0 {
                xa ^= 0x9908_b0df;
            }
            self.mt[i] = self.mt[(i + 397) % 624] ^ xa;
        }
        self.idx = 0;
    }

    fn next_u32(&mut self) -> u32 {
        if self.idx >= 624 {
            self.twist();
        }
        let mut y = self.mt[self.idx];
        self.idx += 1;
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c_5680;
        y ^= (y << 15) & 0xefc6_0000;
        y ^= y >> 18;
        y
    }

    fn next_double(&mut self) -> f64 {
        let a = (self.next_u32() >> 5) as f64;
        let b = (self.next_u32() >> 6) as f64;
        (a * 67_108_864.0 + b) * (1.0 / 9_007_199_254_740_992.0)
    }

    fn uniform(&mut self, lo: f64, hi: f64) -> f64 {
        lo + (hi - lo) * self.next_double()
    }

    fn randint(&mut self, max: usize) -> usize {
        if max == 0 {
            return 0;
        }
        let max_u32 = max as u32;
        let mut mask = max_u32;
        mask |= mask >> 1;
        mask |= mask >> 2;
        mask |= mask >> 4;
        mask |= mask >> 8;
        mask |= mask >> 16;
        loop {
            let value = self.next_u32() & mask;
            if value <= max_u32 {
                return value as usize;
            }
        }
    }

    fn shuffle(&mut self, arr: &mut [usize]) {
        for i in (1..arr.len()).rev() {
            let j = self.randint(i);
            arr.swap(i, j);
        }
    }
}

struct SplitMix64(u64);

impl SplitMix64 {
    fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    fn uniform(&mut self, lo: f64, hi: f64) -> f64 {
        let t = (self.next_u64() >> 11) as f64 / ((1u64 << 53) as f64);
        lo + (hi - lo) * t
    }

    fn shuffle(&mut self, arr: &mut [usize]) {
        for i in (1..arr.len()).rev() {
            let j = (self.next_u64() as usize) % (i + 1);
            arr.swap(i, j);
        }
    }
}

enum TrainRng {
    Numpy(Box<NumpyRng>),
    Fast(SplitMix64),
}

impl TrainRng {
    fn new(params: &MlpParams) -> Self {
        if params.use_numpy_rng {
            Self::Numpy(Box::new(NumpyRng::new(params.random_state as u32)))
        } else {
            Self::Fast(SplitMix64(params.random_state.wrapping_add(1)))
        }
    }

    fn uniform(&mut self, lo: f64, hi: f64) -> f64 {
        match self {
            Self::Numpy(r) => r.uniform(lo, hi),
            Self::Fast(r) => r.uniform(lo, hi),
        }
    }

    fn shuffle(&mut self, arr: &mut [usize]) {
        match self {
            Self::Numpy(r) => r.shuffle(arr),
            Self::Fast(r) => r.shuffle(arr),
        }
    }
}

#[derive(Clone)]
struct DenseLayer {
    input: usize,
    output: usize,
    weights: Vec<f64>,
    biases: Vec<f64>,
}

struct HiddenCache {
    normalized: Vec<f64>,
    inv_std: Vec<f64>,
    dropout_mask: Vec<f64>,
}

fn forward_batch(
    layers: &[DenseLayer],
    input: &[f64],
    rows: usize,
    params: &MlpParams,
    training: bool,
    rng: &mut TrainRng,
) -> (Vec<Vec<f64>>, Vec<HiddenCache>) {
    let mut activations = vec![input.to_vec()];
    let mut caches = Vec::with_capacity(layers.len().saturating_sub(1));
    let keep = 1.0 - params.dropout.clamp(0.0, 0.8);
    for (layer_index, layer) in layers.iter().enumerate() {
        let previous = activations.last().expect("input activation");
        let mut z = vec![0.0; rows * layer.output];
        for row in 0..rows {
            for output in 0..layer.output {
                let mut value = layer.biases[output];
                for input_index in 0..layer.input {
                    value += previous[row * layer.input + input_index]
                        * layer.weights[input_index * layer.output + output];
                }
                z[row * layer.output + output] = value;
            }
        }

        if layer_index + 1 == layers.len() {
            activations.push(z);
            break;
        }

        let mut inv_std = vec![1.0; layer.output];
        if params.batch_normalization && rows > 1 {
            for output in 0..layer.output {
                let mean = (0..rows)
                    .map(|row| z[row * layer.output + output])
                    .sum::<f64>()
                    / rows as f64;
                let variance = (0..rows)
                    .map(|row| {
                        let delta = z[row * layer.output + output] - mean;
                        delta * delta
                    })
                    .sum::<f64>()
                    / rows as f64;
                let inverse = 1.0 / (variance + 1e-5).sqrt();
                inv_std[output] = inverse;
                for row in 0..rows {
                    z[row * layer.output + output] =
                        (z[row * layer.output + output] - mean) * inverse;
                }
            }
        }

        let normalized = z.clone();
        let mut dropout_mask = vec![1.0; z.len()];
        for index in 0..z.len() {
            z[index] = z[index].max(0.0);
            if training && params.dropout > 0.0 {
                dropout_mask[index] = if rng.uniform(0.0, 1.0) < keep {
                    1.0 / keep
                } else {
                    0.0
                };
                z[index] *= dropout_mask[index];
            }
        }
        caches.push(HiddenCache {
            normalized,
            inv_std,
            dropout_mask,
        });
        activations.push(z);
    }
    (activations, caches)
}

struct EvalSet<'a> {
    x: &'a [f64],
    y: &'a [f64],
    rows: &'a [usize],
    input_width: usize,
    output_width: usize,
}

fn validation_score(
    layers: &[DenseLayer],
    eval: EvalSet<'_>,
    params: &MlpParams,
    rng: &mut TrainRng,
) -> (f64, f64) {
    let mut eval_x = Vec::with_capacity(eval.rows.len() * eval.input_width);
    for &row in eval.rows {
        eval_x.extend_from_slice(&eval.x[row * eval.input_width..(row + 1) * eval.input_width]);
    }
    let (activations, _) = forward_batch(layers, &eval_x, eval.rows.len(), params, false, rng);
    let predictions = activations.last().expect("output activation");
    let mut score = 0.0;
    let mut loss = 0.0;
    for output in 0..eval.output_width {
        let mean = eval
            .rows
            .iter()
            .map(|&row| eval.y[row * eval.output_width + output])
            .sum::<f64>()
            / eval.rows.len() as f64;
        let mut total = 0.0;
        let mut residual = 0.0;
        for (local, &row) in eval.rows.iter().enumerate() {
            let expected = eval.y[row * eval.output_width + output];
            total += (expected - mean).powi(2);
            residual += (predictions[local * eval.output_width + output] - expected).powi(2);
        }
        loss += residual;
        score += if total > 0.0 {
            1.0 - residual / total
        } else {
            0.0
        };
    }
    (
        score / eval.output_width as f64,
        loss / eval.rows.len().max(1) as f64,
    )
}

fn fold_batch_normalization(layers: &mut [DenseLayer], x: &[f64], rows: usize, p: usize) {
    if rows < 2 || layers.len() < 2 {
        return;
    }
    let mut previous = x.to_vec();
    let mut input_width = p;
    let hidden_count = layers.len() - 1;
    for layer in layers.iter_mut().take(hidden_count) {
        let mut z = vec![0.0; rows * layer.output];
        for row in 0..rows {
            for output in 0..layer.output {
                let mut value = layer.biases[output];
                for input in 0..input_width {
                    value += previous[row * input_width + input]
                        * layer.weights[input * layer.output + output];
                }
                z[row * layer.output + output] = value;
            }
        }
        for output in 0..layer.output {
            let mean = (0..rows)
                .map(|row| z[row * layer.output + output])
                .sum::<f64>()
                / rows as f64;
            let variance = (0..rows)
                .map(|row| (z[row * layer.output + output] - mean).powi(2))
                .sum::<f64>()
                / rows as f64;
            let std = (variance + 1e-5).sqrt();
            for input in 0..input_width {
                layer.weights[input * layer.output + output] /= std;
            }
            layer.biases[output] = (layer.biases[output] - mean) / std;
        }
        for row in 0..rows {
            for output in 0..layer.output {
                let mut value = layer.biases[output];
                for input in 0..input_width {
                    value += previous[row * input_width + input]
                        * layer.weights[input * layer.output + output];
                }
                z[row * layer.output + output] = value.max(0.0);
            }
        }
        previous = z;
        input_width = layer.output;
    }
}

pub fn fit_mlp(x: &[f64], y: &[f64], n: usize, p: usize, params: &MlpParams) -> MlpWeights {
    let mut hidden_layers: Vec<usize> = if params.hidden_layers.is_empty() {
        vec![params.hidden]
    } else {
        params.hidden_layers.clone()
    };
    hidden_layers.truncate(4);
    for width in &mut hidden_layers {
        *width = (*width).clamp(1, MAX_HIDDEN);
    }
    let q = params.n_out.max(1);
    let mut dimensions = Vec::with_capacity(hidden_layers.len() + 2);
    dimensions.push(p);
    dimensions.extend(hidden_layers.iter().copied());
    dimensions.push(q);

    let mut rng = TrainRng::new(params);
    let mut layers = Vec::with_capacity(dimensions.len() - 1);
    for pair in dimensions.windows(2) {
        let input = pair[0];
        let output = pair[1];
        let bound = (6.0 / (input + output) as f64).sqrt();
        let mut weights = vec![0.0; input * output];
        let mut biases = vec![0.0; output];
        for value in &mut weights {
            *value = rng.uniform(-bound, bound);
        }
        for value in &mut biases {
            *value = rng.uniform(-bound, bound);
        }
        layers.push(DenseLayer {
            input,
            output,
            weights,
            biases,
        });
    }
    if params.residual_output {
        if let Some(output) = layers.last_mut() {
            output.weights.fill(0.0);
            output.biases.fill(0.0);
        }
    }

    // Tiny chart sets (Classic 24, Digital SG subsets) cannot spare a stable
    // holdout. Batch-normalized models also need evaluation statistics that
    // match the statistics folded into the inference weights. In both cases,
    // score the full chart and let regularization/dropout control overfitting.
    let use_holdout = params.early_stopping && n >= 64 && !params.batch_normalization;
    let (train_idx, val_idx) = if use_holdout {
        let val_n = (((n as f64) * params.validation_fraction).ceil() as usize)
            .clamp(1, n.saturating_sub(1));
        let mut split: Vec<usize> = (0..n).collect();
        rng.shuffle(&mut split);
        (split[val_n..].to_vec(), split[..val_n].to_vec())
    } else {
        ((0..n).collect(), Vec::new())
    };
    let eval_idx = if val_idx.is_empty() {
        &train_idx
    } else {
        &val_idx
    };
    let train_n = train_idx.len().max(1);
    let batch_size = 200.min(train_n);
    let mut order = train_idx.clone();

    let mut moment_w: Vec<Vec<f64>> = layers
        .iter()
        .map(|layer| vec![0.0; layer.weights.len()])
        .collect();
    let mut variance_w = moment_w.clone();
    let mut moment_b: Vec<Vec<f64>> = layers
        .iter()
        .map(|layer| vec![0.0; layer.biases.len()])
        .collect();
    let mut variance_b = moment_b.clone();
    let mut best_layers = layers.clone();
    let (mut best_val_score, mut best_val_loss) = validation_score(
        &layers,
        EvalSet {
            x,
            y,
            rows: eval_idx,
            input_width: p,
            output_width: q,
        },
        params,
        &mut rng,
    );
    let mut no_improvement = 0usize;
    let mut lr = params.learning_rate.max(1e-8);
    let mut lr_cuts = 0usize;
    let mut step = 0i32;
    let mut epochs_trained = 0usize;

    for epoch in 0..params.max_iter {
        epochs_trained = epoch + 1;
        if params.shuffle {
            rng.shuffle(&mut order);
        }
        for batch_start in (0..train_n).step_by(batch_size) {
            let batch_end = (batch_start + batch_size).min(train_n);
            let rows = &order[batch_start..batch_end];
            let bs = rows.len();
            let mut batch_x = Vec::with_capacity(bs * p);
            let mut batch_y = Vec::with_capacity(bs * q);
            for &row in rows {
                batch_x.extend_from_slice(&x[row * p..(row + 1) * p]);
                batch_y.extend_from_slice(&y[row * q..(row + 1) * q]);
            }
            let (activations, caches) =
                forward_batch(&layers, &batch_x, bs, params, true, &mut rng);
            let predictions = activations.last().expect("output activation");
            let mut delta: Vec<f64> = predictions
                .iter()
                .zip(batch_y.iter())
                .map(|(predicted, expected)| (predicted - expected) / bs as f64)
                .collect();
            let mut grad_w: Vec<Vec<f64>> = layers
                .iter()
                .map(|layer| vec![0.0; layer.weights.len()])
                .collect();
            let mut grad_b: Vec<Vec<f64>> = layers
                .iter()
                .map(|layer| vec![0.0; layer.biases.len()])
                .collect();

            for layer_index in (0..layers.len()).rev() {
                let layer = &layers[layer_index];
                let previous = &activations[layer_index];
                for input in 0..layer.input {
                    for output in 0..layer.output {
                        let mut gradient = 0.0;
                        for row in 0..bs {
                            gradient += previous[row * layer.input + input]
                                * delta[row * layer.output + output];
                        }
                        grad_w[layer_index][input * layer.output + output] = gradient
                            + params.alpha * layer.weights[input * layer.output + output]
                                / train_n as f64;
                    }
                }
                for output in 0..layer.output {
                    grad_b[layer_index][output] =
                        (0..bs).map(|row| delta[row * layer.output + output]).sum();
                }

                if layer_index > 0 {
                    let previous_width = layer.input;
                    let mut propagated = vec![0.0; bs * previous_width];
                    for row in 0..bs {
                        for input in 0..previous_width {
                            for output in 0..layer.output {
                                propagated[row * previous_width + input] += delta
                                    [row * layer.output + output]
                                    * layer.weights[input * layer.output + output];
                            }
                        }
                    }
                    let cache = &caches[layer_index - 1];
                    for index in 0..propagated.len() {
                        propagated[index] *= cache.dropout_mask[index];
                        if cache.normalized[index] <= 0.0 {
                            propagated[index] = 0.0;
                        }
                    }
                    if params.batch_normalization && bs > 1 {
                        let mut normalized_gradient = vec![0.0; propagated.len()];
                        for output in 0..previous_width {
                            let sum_gradient = (0..bs)
                                .map(|row| propagated[row * previous_width + output])
                                .sum::<f64>();
                            let sum_gradient_x = (0..bs)
                                .map(|row| {
                                    propagated[row * previous_width + output]
                                        * cache.normalized[row * previous_width + output]
                                })
                                .sum::<f64>();
                            for row in 0..bs {
                                let index = row * previous_width + output;
                                normalized_gradient[index] = cache.inv_std[output] / bs as f64
                                    * (bs as f64 * propagated[index]
                                        - sum_gradient
                                        - cache.normalized[index] * sum_gradient_x);
                            }
                        }
                        propagated = normalized_gradient;
                    }
                    delta = propagated;
                }
            }

            step += 1;
            for layer_index in 0..layers.len() {
                update_values(
                    &mut layers[layer_index].weights,
                    &grad_w[layer_index],
                    &mut moment_w[layer_index],
                    &mut variance_w[layer_index],
                    params.optimizer,
                    lr,
                    step,
                );
                update_values(
                    &mut layers[layer_index].biases,
                    &grad_b[layer_index],
                    &mut moment_b[layer_index],
                    &mut variance_b[layer_index],
                    params.optimizer,
                    lr,
                    step,
                );
            }
        }

        let (val_score, val_loss) = validation_score(
            &layers,
            EvalSet {
                x,
                y,
                rows: eval_idx,
                input_width: p,
                output_width: q,
            },
            params,
            &mut rng,
        );
        if val_score > best_val_score + params.tol {
            no_improvement = 0;
        } else {
            no_improvement += 1;
        }
        if val_score > best_val_score {
            best_val_score = val_score;
            best_val_loss = val_loss;
            best_layers.clone_from(&layers);
        }
        if params.convergence_floor > 0.0 && best_val_loss < params.convergence_floor {
            break;
        }
        if no_improvement > params.n_iter_no_change {
            let proposed = lr * params.lr_decay_factor;
            if proposed <= 1e-6
                || (params.max_lr_reductions > 0 && lr_cuts >= params.max_lr_reductions)
            {
                break;
            }
            lr = proposed;
            lr_cuts += 1;
            no_improvement = 0;
        }
    }

    if params.batch_normalization {
        let mut train_x = Vec::with_capacity(train_idx.len() * p);
        for &row in &train_idx {
            train_x.extend_from_slice(&x[row * p..(row + 1) * p]);
        }
        fold_batch_normalization(&mut best_layers, &train_x, train_idx.len(), p);
    }

    let weights: Vec<Vec<f64>> = best_layers
        .iter()
        .map(|layer| layer.weights.clone())
        .collect();
    let biases: Vec<Vec<f64>> = best_layers
        .iter()
        .map(|layer| layer.biases.clone())
        .collect();
    let single_hidden = hidden_layers.len() == 1;
    MlpWeights {
        n_in: p,
        hidden: hidden_layers[0],
        n_out: q,
        w1: if single_hidden {
            weights[0].clone()
        } else {
            Vec::new()
        },
        b1: if single_hidden {
            biases[0].clone()
        } else {
            Vec::new()
        },
        w2: if single_hidden {
            weights[1].clone()
        } else {
            Vec::new()
        },
        b2: if single_hidden {
            biases[1].clone()
        } else {
            Vec::new()
        },
        hidden_layers,
        weights,
        biases,
        epochs_trained,
        best_validation_score: best_val_score,
        best_validation_loss: best_val_loss,
    }
}

fn update_values(
    values: &mut [f64],
    gradients: &[f64],
    moment: &mut [f64],
    variance: &mut [f64],
    optimizer: MlpOptimizer,
    learning_rate: f64,
    step: i32,
) {
    const BETA1: f64 = 0.9;
    const BETA2: f64 = 0.999;
    const EPS: f64 = 1e-8;
    for index in 0..values.len() {
        match optimizer {
            MlpOptimizer::Adam => {
                moment[index] = BETA1 * moment[index] + (1.0 - BETA1) * gradients[index];
                variance[index] =
                    BETA2 * variance[index] + (1.0 - BETA2) * gradients[index] * gradients[index];
                let corrected_moment = moment[index] / (1.0 - BETA1.powi(step));
                let corrected_variance = variance[index] / (1.0 - BETA2.powi(step));
                values[index] -=
                    learning_rate * corrected_moment / (corrected_variance.sqrt() + EPS);
            }
            MlpOptimizer::Sgd => {
                moment[index] = 0.9 * moment[index] + gradients[index];
                values[index] -= learning_rate * moment[index];
            }
            MlpOptimizer::RmsProp => {
                variance[index] = 0.9 * variance[index] + 0.1 * gradients[index] * gradients[index];
                values[index] -= learning_rate * gradients[index] / (variance[index].sqrt() + EPS);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn numpy_mt_first_uniform_matches_known() {
        // numpy.random.RandomState(42).uniform(-1, 1) first draw
        let mut rng = NumpyRng::new(42);
        let u = rng.uniform(-1.0, 1.0);
        // RandomState(42).uniform(-1, 1) == -1 + 2 * random()
        let expected = -1.0 + 2.0 * 0.3745401188473625;
        assert!((u - expected).abs() < 1e-12, "got {u} expected {expected}");
    }

    #[test]
    fn mlp_has_no_feature_standardization_in_weights() {
        let n = 16;
        let p = 4;
        let mut x = Vec::new();
        let mut y = Vec::new();
        for i in 0..n {
            let t = i as f64 / n as f64;
            x.extend_from_slice(&[1.0, t, t * t, 0.2]);
            y.extend_from_slice(&[t, 1.0 - t, 0.5]);
        }
        let params = MlpParams {
            hidden: 8,
            hidden_layers: vec![8],
            optimizer: MlpOptimizer::Adam,
            batch_normalization: false,
            dropout: 0.0,
            n_out: 3,
            max_iter: 200,
            learning_rate: 0.01,
            tol: 1e-6,
            random_state: 42,
            shuffle: false,
            early_stopping: true,
            validation_fraction: 0.15,
            n_iter_no_change: 40,
            alpha: 1e-4,
            lr_decay_factor: 0.2,
            max_lr_reductions: 0,
            convergence_floor: 0.0,
            use_numpy_rng: true,
            residual_output: false,
        };
        let w = fit_mlp(&x, &y, n, p, &params);
        assert_eq!(w.n_in, p);
        assert_eq!(w.n_out, 3);
        let a = w.predict3(&x[..p]);
        let b = w.predict3(&x[p..2 * p]);
        assert!(a.iter().all(|v| v.is_finite()));
        assert!(b.iter().all(|v| v.is_finite()));
    }

    #[test]
    fn from_cc_uses_mapping_tol() {
        let mapping = MappingOptions {
            tol: 2.5e-5,
            ..MappingOptions::default()
        };
        let params = MlpParams::from_cc(&mapping);
        assert!((params.tol - 2.5e-5).abs() < 1e-18);
    }

    #[test]
    fn deep_mlp_supports_all_optimizers_batch_norm_and_dropout() {
        let n = 24;
        let p = 4;
        let mut x = Vec::new();
        let mut y = Vec::new();
        for i in 0..n {
            let t = i as f64 / (n - 1) as f64;
            x.extend_from_slice(&[1.0, t, t * t, 1.0 - t]);
            y.extend_from_slice(&[0.1 + 0.7 * t, 0.8 - 0.5 * t, 0.2 + 0.4 * t]);
        }
        for optimizer in [MlpOptimizer::Adam, MlpOptimizer::Sgd, MlpOptimizer::RmsProp] {
            let mapping = MappingOptions {
                hidden_layers: vec![16, 8],
                nn_optimizer: match optimizer {
                    MlpOptimizer::Adam => "adam",
                    MlpOptimizer::Sgd => "sgd",
                    MlpOptimizer::RmsProp => "rmsprop",
                }
                .into(),
                nn_batch_norm: true,
                nn_dropout: 0.2,
                nn_epochs: 80,
                nn_patience: 20,
                learning_rate: 0.005,
                ..MappingOptions::default()
            };
            let params = MlpParams::from_cc(&mapping);
            let baseline_mse = y
                .chunks_exact(3)
                .zip(x.chunks_exact(p))
                .map(|(target, source)| {
                    (0..3)
                        .map(|channel| (target[channel] - source[channel + 1]).powi(2))
                        .sum::<f64>()
                })
                .sum::<f64>()
                / (n * 3) as f64;
            let residuals: Vec<f64> = y
                .chunks_exact(3)
                .zip(x.chunks_exact(p))
                .flat_map(|(target, source)| {
                    (0..3).map(move |channel| target[channel] - source[channel + 1])
                })
                .collect();
            let fitted = fit_mlp(&x, &residuals, n, p, &params);
            assert_eq!(fitted.hidden_layers, vec![16, 8]);
            assert_eq!(fitted.weights.len(), 3);
            assert_eq!(fitted.biases.len(), 3);
            let prediction = fitted.predict3(&x[..p]);
            assert!(prediction.iter().all(|value| value.is_finite()));
            let fitted_mse = x
                .chunks_exact(p)
                .zip(y.chunks_exact(3))
                .map(|(source, target)| {
                    let correction = fitted.predict3(source);
                    (0..3)
                        .map(|channel| {
                            (source[channel + 1] + correction[channel] - target[channel]).powi(2)
                        })
                        .sum::<f64>()
                })
                .sum::<f64>()
                / (n * 3) as f64;
            assert!(
                fitted_mse <= baseline_mse,
                "{optimizer:?} regressed {baseline_mse} -> {fitted_mse}"
            );
        }
    }
}
