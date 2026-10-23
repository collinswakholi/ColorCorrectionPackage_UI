//! Independent published vectors + invariants; see docs/color-science-validation.md.
use ccp_core::color::*;
use ccp_core::honest::{delta_e00_in_space, delta_e00_same_index};
use ccp_core::refs::{chart_spec, ChartKind};

fn close(a: f64, b: f64, tol: f64) {
    assert!((a - b).abs() <= tol, "{a} != {b} (tolerance {tol})");
}
fn close3(a: Vec3, b: Vec3, tol: f64) {
    for i in 0..3 {
        close(a[i], b[i], tol);
    }
}

#[test]
fn all_34_sharma_vectors_both_directions() {
    // Sharma, Wu, Dalal (2005), author's supplementary dataset. Four-decimal truth.
    let mut count = 0;
    for row in include_str!("fixtures/sharma_ciede2000.txt").lines() {
        let v: Vec<f64> = row.split_whitespace().map(|s| s.parse().unwrap()).collect();
        let a = Lab::new(v[0], v[1], v[2]);
        let b = Lab::new(v[3], v[4], v[5]);
        close(delta_e_2000(a, b), v[6], 0.00005);
        close(delta_e_2000(b, a), v[6], 0.00005);
        close(delta_e_2000(a, a), 0.0, 1e-12);
        count += 1;
    }
    assert_eq!(count, 34);
}

#[test]
fn classic_and_passport_match_independent_cgats_same_cells() {
    for kind in [ChartKind::Classic, ChartKind::Passport] {
        let spec = chart_spec(kind).unwrap();
        let mut count = 0;
        for line in include_str!("fixtures/classic_after2014.txt").lines() {
            let v: Vec<_> = line.split_whitespace().collect();
            if v.len() != 4 || v[0].len() != 2 || !v[0].as_bytes()[1].is_ascii_digit() {
                continue;
            }
            let col = (v[0].as_bytes()[0] - b'A') as usize;
            let row = (v[0].as_bytes()[1] - b'1') as usize;
            let expected = [v[1], v[2], v[3]].map(|x| x.replace(',', ".").parse::<f64>().unwrap());
            close3(
                spec.reference_lab_d50[row * 6 + col].to_array(),
                expected,
                1e-12,
            );
            count += 1;
        }
        assert_eq!(count, 24);
    }
}

#[test]
fn sg_and_spyder_match_independent_named_cell_tables() {
    for (kind, data, expected_count) in [
        (
            ChartKind::DigitalSg,
            include_str!("fixtures/sg_after2014.txt"),
            140,
        ),
        (
            ChartKind::Spyder,
            include_str!("fixtures/spyder24_v2.txt"),
            24,
        ),
    ] {
        let spec = chart_spec(kind).unwrap();
        let mut count = 0;
        for row in data.lines() {
            let v: Vec<_> = row.split_whitespace().collect();
            if v.len() != 4 {
                continue;
            }
            if let Some(index) = spec.index_of(v[0]) {
                let expected = [v[1], v[2], v[3]].map(|s| s.replace(',', ".").parse().unwrap());
                close3(spec.reference_lab_d50[index].to_array(), expected, 1e-12);
                count += 1;
            }
        }
        assert_eq!(count, expected_count);
    }
}

#[test]
fn published_transfer_vectors_and_breakpoints() {
    let d65 = parse_white_point("d65").unwrap();
    for (name, encoded, linear) in [
        ("srgb", 0.04045, 0.0031308049535603713),
        ("srgb", 0.5, 0.21404114048223255),
        ("display-p3", 0.5, 0.21404114048223255),
        ("adobe-rgb-1998", 0.5, 0.21775552814439456),
        ("rec2020", 0.5, 0.2597194371011775),
        ("prophoto-rgb", 0.03125, 0.001953125),
        ("prophoto-rgb", 0.5, 0.2871745887492587),
        ("linear-srgb", 0.5, 0.5),
        ("linear-rec2020", 0.5, 0.5),
        ("acescg", 0.5, 0.5),
    ] {
        let space = parse_rgb_color_space(name, &d65).unwrap();
        close(
            encoded_to_linear_rgb([encoded; 3], &space)[0],
            linear,
            1e-12,
        );
    }
    for name in supported_rgb_color_spaces() {
        let space = parse_rgb_color_space(name, &d65).unwrap();
        for value in [
            0.0,
            0.0001,
            0.0031308,
            0.018053968510807,
            0.03125,
            0.04045,
            0.0812428582986315,
            0.5,
            1.0,
        ] {
            close3(
                linear_to_encoded_rgb(encoded_to_linear_rgb([value; 3], &space), &space),
                [value; 3],
                3e-8,
            );
        }
    }
}

#[test]
fn primary_matrices_match_published_standard_rounding() {
    // ICC/W3C matrices rounded to 7 decimals. App retains CIE D65 xy
    // 0.31271/0.32902 for saved-fit compatibility; normative RGB D65 is 0.3127/0.3290.
    for (name, white, matrix) in [
        (
            "srgb",
            "d65",
            [
                0.4123908, 0.3575843, 0.1804808, 0.2126390, 0.7151687, 0.0721923, 0.0193308,
                0.1191948, 0.9505322,
            ],
        ),
        (
            "display-p3",
            "d65",
            [
                0.4865709, 0.2656677, 0.1982173, 0.2289746, 0.6917385, 0.0792869, 0.0, 0.0451134,
                1.0439444,
            ],
        ),
        (
            "adobe-rgb-1998",
            "d65",
            [
                0.5766690, 0.1855582, 0.1882286, 0.2973450, 0.6273636, 0.0752915, 0.0270314,
                0.0706889, 0.9913375,
            ],
        ),
        (
            "rec2020",
            "d65",
            [
                0.6369580, 0.1446169, 0.1688810, 0.2627002, 0.6779981, 0.0593017, 0.0, 0.0280727,
                1.0609851,
            ],
        ),
        (
            "prophoto-rgb",
            "d50",
            [
                0.7977605, 0.1351858, 0.0313493, 0.2880711, 0.7118432, 0.0000857, 0.0, 0.0,
                0.8251046,
            ],
        ),
        (
            "acescg",
            "d60",
            [
                0.6624542, 0.1340042, 0.1561877, 0.2722287, 0.6740818, 0.0536895, -0.0055746,
                0.0040607, 1.0103391,
            ],
        ),
    ] {
        let space = parse_rgb_color_space(name, &parse_white_point(white).unwrap()).unwrap();
        for (actual, expected) in space.rgb_to_xyz.iter().zip(matrix) {
            close(*actual, expected, 0.0002);
        }
    }
}

#[test]
fn all_64_space_white_combinations_roundtrip_and_compare_same_color() {
    for white in supported_white_points() {
        let white = parse_white_point(white).unwrap();
        for name in supported_rgb_color_spaces() {
            let space = parse_rgb_color_space(name, &white).unwrap();
            close3(linear_rgb_to_xyz([1.0; 3], &space), white.xyz, 1e-12);
            for xyz in [[0.0; 3], [0.2, 0.3, 0.4], white.xyz] {
                close3(lab_to_xyz(xyz_to_lab(xyz, &white), &white), xyz, 1e-12);
                close3(
                    linear_rgb_to_xyz(xyz_to_linear_rgb(xyz, &space), &space),
                    xyz,
                    1e-12,
                );
                let d50 = parse_white_point("d50").unwrap();
                close3(
                    adapt_xyz_white(adapt_xyz_white(xyz, &white, &d50).unwrap(), &d50, &white)
                        .unwrap(),
                    xyz,
                    1e-12,
                );
            }
            // Modest chroma safely in gamut even for custom white variants.
            let reference = Lab::new(60.0, 5.0, -5.0);
            let rgb = linear_to_encoded_rgb(
                lab_d50_to_target_linear(reference, &space, &white).unwrap(),
                &space,
            );
            close(
                delta_e00_in_space(rgb, reference, &space).unwrap(),
                0.0,
                1e-10,
            );
        }
    }
}

#[test]
fn wrong_srgb_interpretation_is_detectably_wrong_for_linear_output() {
    let white = parse_white_point("d65").unwrap();
    let space = parse_rgb_color_space("linear-srgb", &white).unwrap();
    let reference = Lab::new(60.0, 5.0, -5.0);
    let rgb = lab_d50_to_target_linear(reference, &space, &white).unwrap();
    let old = delta_e00_same_index(rgb, reference).unwrap();
    let fixed = delta_e00_in_space(rgb, reference, &space).unwrap();
    println!("Identical color linear-sRGB: old false error {old:.8}, fixed {fixed:.12}");
    assert!(old > 15.0);
    assert!(fixed < 1e-10);
}

#[test]
fn held_out_linear_fits_are_scored_in_every_output_space() {
    use ccp_core::honest::cross_validate_correction;
    use ccp_core::maps::{CorrectionSettings, MappingOptions, ModelKind};
    use ccp_core::refs::{ChartPatch, Vec2};
    let patches: Vec<_> = (0..30)
        .map(|i| {
            // Interior-gamut independent colors avoid clipping as a confounder.
            let lab = Lab::new(
                30.0 + (i % 5) as f64 * 10.0,
                (i % 3) as f64 * 8.0 - 8.0,
                (i % 7) as f64 * 3.0 - 9.0,
            );
            let rgb = lab_d50_to_srgb(lab).unwrap();
            ChartPatch {
                provenance: None,
                name: format!("test{i}"),
                index: i,
                reference_lab_d50: lab,
                reference_srgb: rgb,
                measured_srgb: rgb,
                original_measured_srgb: rgb,
                center: Vec2 { x: 0.0, y: 0.0 },
                saturated: false,
            }
        })
        .collect();
    for name in supported_rgb_color_spaces() {
        let mut settings = CorrectionSettings::srgb_d65().unwrap();
        let white = parse_white_point(match *name {
            "prophoto-rgb" => "d50",
            "acescg" => "d60",
            _ => "d65",
        })
        .unwrap();
        settings.target_space = parse_rgb_color_space(name, &white).unwrap();
        settings.target_white = white;
        let report = cross_validate_correction(
            &patches,
            ModelKind::Linear,
            &settings,
            1e-10,
            &MappingOptions::default(),
        )
        .unwrap();
        assert_eq!(report.samples, 30);
        assert!(report.max < 0.001, "{name}: max ΔE {}", report.max);
    }
}
