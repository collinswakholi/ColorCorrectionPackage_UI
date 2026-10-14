//! sklearn-style NIPALS PLS, matching CCP_rs `fit_pls`.
//!
//! Center + scale (ddof=0), deflate, solve `PᵀW z = q` without ridge,
//! then unscale: `B[j,k] = B_scaled[j,k] * σY[k] / σX[j]`,
//! `β₀ = Ȳ − X̄ B`. Apply is `Y = X_raw B + β₀`.

// NIPALS updates several row-major matrices together; indices make those layouts explicit.
#![allow(clippy::needless_range_loop)]

use crate::{Error, Result};

pub const PLS_TOL: f64 = 1e-6;

/// Auto `ncomp` when `requested == 0`: `p − 1` (CCP_rs / sklearn), not a degree cap.
pub fn auto_ncomp(n: usize, p: usize, requested: i32) -> usize {
    if requested > 0 {
        (requested as usize).clamp(1, p.min(n).max(1))
    } else {
        p.saturating_sub(1).max(1).min(p).min(n.max(1))
    }
}

fn vec_norm(v: &[f64]) -> f64 {
    v.iter().map(|x| x * x).sum::<f64>().sqrt()
}

/// `x` is `n×p` row-major, `y` is `n×q` row-major.
/// Returns `(coefficients p×q row-major, intercept q)`.
pub fn fit_pls(
    x_in: &[f64],
    y_in: &[f64],
    n: usize,
    p: usize,
    q: usize,
    ncomp: usize,
    tol: f64,
) -> Result<(Vec<f64>, Vec<f64>)> {
    if n == 0 || p == 0 || q == 0 {
        return Err(Error::InvalidArgument(
            "PLS needs samples and features".into(),
        ));
    }
    if x_in.len() < n * p || y_in.len() < n * q {
        return Err(Error::InvalidArgument("PLS matrix size mismatch".into()));
    }

    let mut x = x_in[..n * p].to_vec();
    let mut y = y_in[..n * q].to_vec();
    let inv_n = 1.0 / n as f64;

    let mut x_mean = vec![0.0; p];
    let mut y_mean = vec![0.0; q];
    for row in 0..n {
        for j in 0..p {
            x_mean[j] += x[row * p + j];
        }
        for k in 0..q {
            y_mean[k] += y[row * q + k];
        }
    }
    for j in 0..p {
        x_mean[j] *= inv_n;
    }
    for k in 0..q {
        y_mean[k] *= inv_n;
    }
    for row in 0..n {
        for j in 0..p {
            x[row * p + j] -= x_mean[j];
        }
        for k in 0..q {
            y[row * q + k] -= y_mean[k];
        }
    }

    let mut x_std = vec![1.0; p];
    let mut y_std = vec![1.0; q];
    for j in 0..p {
        let ss: f64 = (0..n)
            .map(|row| {
                let v = x[row * p + j];
                v * v
            })
            .sum();
        let s = (ss * inv_n).sqrt();
        x_std[j] = if s < 1e-12 { 1.0 } else { s };
        for row in 0..n {
            x[row * p + j] /= x_std[j];
        }
    }
    for k in 0..q {
        let ss: f64 = (0..n)
            .map(|row| {
                let v = y[row * q + k];
                v * v
            })
            .sum();
        let s = (ss * inv_n).sqrt();
        y_std[k] = if s < 1e-12 { 1.0 } else { s };
        for row in 0..n {
            y[row * q + k] /= y_std[k];
        }
    }

    let ncomp = ncomp.min(p).min(n).max(1);
    let mut w_mat = vec![0.0; p * ncomp];
    let mut p_mat = vec![0.0; p * ncomp];
    let mut q_mat = vec![0.0; q * ncomp];
    let mut used = 0usize;
    let mut w = vec![0.0; p];
    let mut t = vec![0.0; n];
    let mut q_vec = vec![0.0; q];

    for h in 0..ncomp {
        let mut u: Vec<f64> = (0..n).map(|row| y[row * q]).collect();
        for _iter in 0..500 {
            let utu: f64 = u.iter().map(|v| v * v).sum();
            if utu < 1e-300 {
                break;
            }
            w.fill(0.0);
            for row in 0..n {
                let ur = u[row];
                let base = row * p;
                for j in 0..p {
                    w[j] += x[base + j] * ur;
                }
            }
            let w_norm = vec_norm(&w);
            if w_norm < 1e-300 {
                break;
            }
            for value in w.iter_mut() {
                *value /= w_norm;
            }
            for row in 0..n {
                let base = row * p;
                t[row] = (0..p).map(|j| x[base + j] * w[j]).sum();
            }
            let ttt: f64 = t.iter().map(|v| v * v).sum();
            if ttt < 1e-300 {
                break;
            }
            q_vec.fill(0.0);
            for row in 0..n {
                let tr = t[row];
                let base = row * q;
                for k in 0..q {
                    q_vec[k] += y[base + k] * tr;
                }
            }
            for k in 0..q {
                q_vec[k] /= ttt;
            }
            let qtq: f64 = q_vec.iter().map(|v| v * v).sum();
            if qtq < 1e-300 {
                break;
            }
            let mut diff = 0.0;
            for row in 0..n {
                let mut un = 0.0;
                let base = row * q;
                for k in 0..q {
                    un += y[base + k] * q_vec[k];
                }
                un /= qtq;
                let d = un - u[row];
                diff += d * d;
                u[row] = un;
            }
            if diff.sqrt() < tol {
                break;
            }
        }

        let utu: f64 = u.iter().map(|v| v * v).sum();
        if utu < 1e-300 {
            break;
        }
        w.fill(0.0);
        for row in 0..n {
            let ur = u[row];
            let base = row * p;
            for j in 0..p {
                w[j] += x[base + j] * ur;
            }
        }
        let w_norm = vec_norm(&w);
        if w_norm < 1e-300 {
            break;
        }
        for value in w.iter_mut() {
            *value /= w_norm;
        }
        for row in 0..n {
            let base = row * p;
            t[row] = (0..p).map(|j| x[base + j] * w[j]).sum();
        }
        let tt: f64 = t.iter().map(|v| v * v).sum();
        if tt < 1e-12 {
            break;
        }
        for j in 0..p {
            let loading: f64 = (0..n).map(|row| x[row * p + j] * t[row]).sum();
            p_mat[j * ncomp + h] = loading / tt;
            w_mat[j * ncomp + h] = w[j];
        }
        for k in 0..q {
            let loading: f64 = (0..n).map(|row| y[row * q + k] * t[row]).sum();
            q_mat[k * ncomp + h] = loading / tt;
        }
        for row in 0..n {
            let tr = t[row];
            let xb = row * p;
            let yb = row * q;
            for j in 0..p {
                x[xb + j] -= tr * p_mat[j * ncomp + h];
            }
            for k in 0..q {
                y[yb + k] -= tr * q_mat[k * ncomp + h];
            }
        }
        used = h + 1;
    }

    let k = used.max(1);
    let mut ptw = vec![0.0; k * k];
    for a in 0..k {
        for b in 0..k {
            let sum: f64 = (0..p)
                .map(|j| p_mat[j * ncomp + a] * w_mat[j * ncomp + b])
                .sum();
            ptw[a * k + b] = sum;
        }
    }
    let mut b_scaled = vec![0.0; p * q];
    for channel in 0..q {
        let rhs: Vec<f64> = (0..k).map(|h| q_mat[channel * ncomp + h]).collect();
        let z = match crate::maps::solve_linear_system(ptw.clone(), rhs.clone(), k) {
            Ok(z) => z,
            Err(_) => {
                let mut ridge = ptw.clone();
                for i in 0..k {
                    ridge[i * k + i] += 1e-12;
                }
                crate::maps::solve_linear_system(ridge, rhs, k)?
            }
        };
        for j in 0..p {
            b_scaled[j * q + channel] = (0..k).map(|h| w_mat[j * ncomp + h] * z[h]).sum();
        }
    }

    let mut coefficients = vec![0.0; p * q];
    for j in 0..p {
        for channel in 0..q {
            coefficients[j * q + channel] = b_scaled[j * q + channel] * y_std[channel] / x_std[j];
        }
    }
    let mut intercept = y_mean;
    for j in 0..p {
        for channel in 0..q {
            intercept[channel] -= x_mean[j] * coefficients[j * q + channel];
        }
    }
    Ok((coefficients, intercept))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn auto_ncomp_matches_ccp() {
        assert_eq!(auto_ncomp(24, 10, 0), 9);
        assert_eq!(auto_ncomp(24, 10, 6), 6);
        assert_eq!(auto_ncomp(3, 10, 0), 3);
        assert_eq!(auto_ncomp(24, 1, 0), 1);
    }

    #[test]
    fn pls_recovers_scaled_linear_map() {
        // y = 2x0 + 0.5 x1, 3x0 - x1  (plus tiny noise-free)
        let n = 20;
        let p = 2;
        let q = 2;
        let mut x = Vec::new();
        let mut y = Vec::new();
        for i in 0..n {
            let a = i as f64 * 0.05;
            let b = 1.0 - a;
            x.extend_from_slice(&[a, b]);
            y.extend_from_slice(&[2.0 * a + 0.5 * b, 3.0 * a - b]);
        }
        let (c, intercept) = fit_pls(&x, &y, n, p, q, 2, PLS_TOL).unwrap();
        for i in 0..n {
            let pred0 = intercept[0] + x[i * 2] * c[0] + x[i * 2 + 1] * c[2];
            let pred1 = intercept[1] + x[i * 2] * c[1] + x[i * 2 + 1] * c[3];
            assert!((pred0 - y[i * 2]).abs() < 1e-8, "{pred0} vs {}", y[i * 2]);
            assert!((pred1 - y[i * 2 + 1]).abs() < 1e-8);
        }
    }
}
