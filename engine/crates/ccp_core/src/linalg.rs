//! Dense linear least squares shared by the correction maps, PLS and FFC.

use crate::error::{Error, Result};

/// Pivot threshold relative to the largest entry, floored at the historical
/// absolute `1e-12` for systems whose entries reach 1 or more. Uniformly
/// down-scaled well-conditioned systems therefore solve, while the result for
/// every system the absolute threshold accepted is unchanged.
const PIVOT_RELATIVE_TOLERANCE: f64 = 1e-12;

pub(crate) fn solve_linear_system(
    mut matrix: Vec<f64>,
    mut rhs: Vec<f64>,
    n: usize,
) -> Result<Vec<f64>> {
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

pub(crate) fn fit_least_squares(
    x: &[f64],
    y: &[f64],
    n: usize,
    p: usize,
    ridge: f64,
) -> Result<Vec<f64>> {
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

#[cfg(test)]
mod tests {
    use super::*;

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
}
