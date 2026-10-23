//! Transactional accelerated chunks: after an error only untouched pixels fall back.
use rayon::prelude::*;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChunkExecution {
    pub accelerated_pixels: usize,
    pub cpu_pixels: usize,
    pub fallback_reason: Option<String>,
}

pub fn apply_chunks<F>(
    rgb: &mut [f32],
    lut: &[f32],
    gs: usize,
    chunk_pixels: usize,
    mut accelerated: F,
) -> Result<ChunkExecution, String>
where
    F: FnMut(&[f32]) -> Result<Vec<f32>, String>,
{
    apply_chunks_into(rgb, lut, gs, chunk_pixels, |input, output| {
        *output = accelerated(input)?;
        Ok(())
    })
}

/// Reuse a bounded scratch allocation while preserving commit-after-validation.
pub fn apply_chunks_into<F>(
    rgb: &mut [f32],
    lut: &[f32],
    gs: usize,
    chunk_pixels: usize,
    mut accelerated: F,
) -> Result<ChunkExecution, String>
where
    F: FnMut(&[f32], &mut Vec<f32>) -> Result<(), String>,
{
    let expected = gs.checked_pow(3).and_then(|n| n.checked_mul(3));
    if gs < 2 || expected != Some(lut.len()) || rgb.len() % 3 != 0 || chunk_pixels == 0 {
        return Err("invalid LUT, RGB length, or chunk size".into());
    }
    if !lut.iter().all(|v| v.is_finite())
        || !rgb.par_iter().with_min_len(65_536).all(|v| v.is_finite())
    {
        return Err("LUT and image values must be finite".into());
    }
    let mut done = 0;
    let mut scratch = Vec::new();
    while done < rgb.len() {
        let end = done
            .saturating_add(chunk_pixels.saturating_mul(3))
            .min(rgb.len());
        // Clear the length without freeing capacity. A failed/short writer can
        // never reuse stale output from a previously completed chunk.
        scratch.clear();
        let output = accelerated(&rgb[done..end], &mut scratch).and_then(|()| {
            if scratch.len() != end - done
                || !scratch
                    .par_iter()
                    .with_min_len(65_536)
                    .all(|v| v.is_finite())
            {
                Err("accelerator returned malformed or nonfinite LUT output".into())
            } else {
                Ok(())
            }
        });
        match output {
            Ok(()) => {
                rgb[done..end]
                    .par_chunks_mut(262_144)
                    .zip(scratch.par_chunks(262_144))
                    .for_each(|(dst, src)| dst.copy_from_slice(src));
                done = end;
            }
            Err(error) => {
                rgb[done..].par_chunks_exact_mut(3).for_each(|pixel| {
                    let out = ccp_core::maps::sample_encoded_lut(
                        lut,
                        gs,
                        [pixel[0] as f64, pixel[1] as f64, pixel[2] as f64],
                    );
                    pixel.copy_from_slice(&[out[0] as f32, out[1] as f32, out[2] as f32]);
                });
                return Ok(ChunkExecution {
                    accelerated_pixels: done / 3,
                    cpu_pixels: (rgb.len() - done) / 3,
                    fallback_reason: Some(error),
                });
            }
        }
    }
    Ok(ChunkExecution {
        accelerated_pixels: done / 3,
        cpu_pixels: 0,
        fallback_reason: None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn lut() -> Vec<f32> {
        let mut out = Vec::new();
        for b in 0..3 {
            for g in 0..3 {
                for r in 0..3 {
                    out.extend([
                        (r as f32 / 2.0).powi(2),
                        (g as f32 / 2.0).powi(2),
                        (b as f32 / 2.0).powi(2),
                    ]);
                }
            }
        }
        out
    }
    #[test]
    fn failure_after_completed_chunk_never_reapplies_it() {
        let lut = lut();
        let original = vec![0.75f32; 15];
        let mut expected = original.clone();
        apply_chunks(&mut expected, &lut, 3, 2, |_| Err("cpu".into())).unwrap();
        let mut actual = original;
        let mut calls = 0;
        let result = apply_chunks(&mut actual, &lut, 3, 2, |input| {
            calls += 1;
            if calls == 2 {
                return Err("injected device loss".into());
            }
            let mut output = input.to_vec();
            apply_chunks(&mut output, &lut, 3, 2, |_| Err("cpu".into())).unwrap();
            Ok(output)
        })
        .unwrap();
        assert_eq!(actual, expected);
        assert_eq!(calls, 2);
        assert_eq!(result.accelerated_pixels, 2);
        assert_eq!(result.cpu_pixels, 3);
    }
    #[test]
    fn malformed_output_is_recovered_before_commit() {
        for malformed in [vec![], vec![f32::NAN; 6], vec![0.; 3]] {
            let mut pixels = vec![0.75; 6];
            let result =
                apply_chunks(&mut pixels, &lut(), 3, 2, |_| Ok(malformed.clone())).unwrap();
            assert_eq!(result.accelerated_pixels, 0);
            assert_eq!(pixels, vec![0.625; 6]);
        }
    }

    #[test]
    fn invalid_inputs_do_not_launch_or_mutate() {
        for (mut input, table, grid) in [
            (vec![0.5; 4], lut(), 3),
            (vec![0.5; 6], lut(), 2),
            (vec![f32::INFINITY; 6], lut(), 3),
            (vec![0.5; 6], vec![f32::NAN; 81], 3),
        ] {
            let before = input.clone();
            assert!(apply_chunks(&mut input, &table, grid, 2, |_| panic!(
                "invalid input reached GPU"
            ))
            .is_err());
            assert_eq!(input, before);
        }
    }

    #[test]
    fn malformed_second_chunk_preserves_first_and_recovers_tail() {
        for malformed in [vec![], vec![f32::INFINITY; 6], vec![0.; 3]] {
            let mut pixels = vec![0.75; 15];
            let mut calls = 0;
            let result = apply_chunks(&mut pixels, &lut(), 3, 2, |_| {
                calls += 1;
                if calls == 1 {
                    Ok(vec![0.625; 6])
                } else {
                    Ok(malformed.clone())
                }
            })
            .unwrap();
            assert_eq!(pixels, vec![0.625; 15]);
            assert_eq!(
                (result.accelerated_pixels, result.cpu_pixels, calls),
                (2, 3, 2)
            );
        }
    }

    #[test]
    fn reused_scratch_never_commits_stale_or_partial_output() {
        for failure in 0..3 {
            let mut pixels = vec![0.75; 15];
            let mut calls = 0;
            let result = apply_chunks_into(&mut pixels, &lut(), 3, 2, |input, scratch| {
                assert!(scratch.is_empty());
                calls += 1;
                if calls == 1 {
                    scratch.extend(std::iter::repeat_n(0.625, input.len()));
                    return Ok(());
                }
                match failure {
                    0 => Ok(()),
                    1 => {
                        scratch.extend([f32::NAN; 6]);
                        Ok(())
                    }
                    _ => {
                        scratch.push(0.);
                        Err("device lost during readback".into())
                    }
                }
            })
            .unwrap();
            assert_eq!(pixels, vec![0.625; 15]);
            assert_eq!(
                (result.accelerated_pixels, result.cpu_pixels, calls),
                (2, 3, 2)
            );
        }
    }
}
