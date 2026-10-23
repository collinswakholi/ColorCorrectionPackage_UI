//! Bradford CAT, CIE Lab, sRGB, and CIEDE2000 (Sharma) — Speedy `color.cpp`.

use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::sync::OnceLock;

pub type Vec3 = [f64; 3];
pub type Mat3 = [f64; 9];

const PI: f64 = std::f64::consts::PI;
const LAB_EPSILON: f64 = 216.0 / 24389.0;
const LAB_KAPPA: f64 = 24389.0 / 27.0;

const BRADFORD: Mat3 = [
    0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296,
];
const BRADFORD_INV: Mat3 = [
    0.9869929054667123,
    -0.14705425642099013,
    0.15996265166373125,
    0.4323052697233945,
    0.5183602715367776,
    0.049291228212855594,
    -0.008528664575177328,
    0.04004282165408486,
    0.9684866957875502,
];

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct Lab {
    pub l: f64,
    pub a: f64,
    pub b: f64,
}

impl Lab {
    pub const fn new(l: f64, a: f64, b: f64) -> Self {
        Self { l, a, b }
    }

    pub fn to_array(self) -> Vec3 {
        [self.l, self.a, self.b]
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct Chromaticity {
    pub x: f64,
    pub y: f64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct WhitePoint {
    pub id: String,
    pub xy: Chromaticity,
    pub xyz: Vec3,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum TransferFunction {
    Linear,
    Srgb,
    Gamma,
    Rec2020,
    ProPhoto,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct RgbColorSpace {
    pub id: String,
    pub name: String,
    pub red: Chromaticity,
    pub green: Chromaticity,
    pub blue: Chromaticity,
    pub white: WhitePoint,
    pub transfer: TransferFunction,
    pub gamma: f64,
    pub rgb_to_xyz: Mat3,
    pub xyz_to_rgb: Mat3,
}

fn degrees_to_radians(degrees: f64) -> f64 {
    degrees * PI / 180.0
}

fn radians_to_degrees(radians: f64) -> f64 {
    radians * 180.0 / PI
}

fn lab_f(value: f64) -> f64 {
    if value > LAB_EPSILON {
        value.cbrt()
    } else {
        (LAB_KAPPA * value + 16.0) / 116.0
    }
}

fn lab_f_inv(value: f64) -> f64 {
    const DELTA: f64 = 6.0 / 29.0;
    if value > DELTA {
        value * value * value
    } else {
        (116.0 * value - 16.0) / LAB_KAPPA
    }
}

fn normalize_id(value: &str) -> String {
    value
        .chars()
        .map(|ch| {
            if ch == '_' || ch == ' ' || ch == '.' {
                '-'
            } else {
                ch.to_ascii_lowercase()
            }
        })
        .collect()
}

fn xyz_from_xy(xy: Chromaticity) -> Result<Vec3> {
    if xy.y <= 0.0 {
        return Err(Error::InvalidArgument(
            "white point or primary has invalid y chromaticity".into(),
        ));
    }
    Ok([xy.x / xy.y, 1.0, (1.0 - xy.x - xy.y) / xy.y])
}

fn mat3_inverse(matrix: Mat3) -> Result<Mat3> {
    let [a, b, c, d, e, f, g, h, i] = matrix;
    let a_c = e * i - f * h;
    let b_c = -(d * i - f * g);
    let c_c = d * h - e * g;
    let d_c = -(b * i - c * h);
    let e_c = a * i - c * g;
    let f_c = -(a * h - b * g);
    let g_c = b * f - c * e;
    let h_c = -(a * f - c * d);
    let i_c = a * e - b * d;
    let det = a * a_c + b * b_c + c * c_c;
    if det.abs() < 1e-14 {
        return Err(Error::Runtime("cannot invert singular 3x3 matrix".into()));
    }
    let inv = 1.0 / det;
    Ok([
        a_c * inv,
        d_c * inv,
        g_c * inv,
        b_c * inv,
        e_c * inv,
        h_c * inv,
        c_c * inv,
        f_c * inv,
        i_c * inv,
    ])
}

fn make_rgb_to_xyz_matrix(
    red: Chromaticity,
    green: Chromaticity,
    blue: Chromaticity,
    white_xyz: Vec3,
) -> Result<Mat3> {
    let r = xyz_from_xy(red)?;
    let g = xyz_from_xy(green)?;
    let b = xyz_from_xy(blue)?;
    let primary = [r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2]];
    let scale = mat3_mul(mat3_inverse(primary)?, white_xyz);
    Ok([
        scale[0] * r[0],
        scale[1] * g[0],
        scale[2] * b[0],
        scale[0] * r[1],
        scale[1] * g[1],
        scale[2] * b[1],
        scale[0] * r[2],
        scale[1] * g[2],
        scale[2] * b[2],
    ])
}

fn make_white_point(id: &str, x: f64, y: f64) -> Result<WhitePoint> {
    let xy = Chromaticity { x, y };
    Ok(WhitePoint {
        id: id.to_string(),
        xy,
        xyz: xyz_from_xy(xy)?,
    })
}

#[allow(clippy::too_many_arguments)] // The arguments mirror the standard RGB-space definition tuple.
fn make_rgb_space(
    id: &str,
    name: &str,
    red: Chromaticity,
    green: Chromaticity,
    blue: Chromaticity,
    white: WhitePoint,
    transfer: TransferFunction,
    gamma: f64,
) -> Result<RgbColorSpace> {
    let rgb_to_xyz = make_rgb_to_xyz_matrix(red, green, blue, white.xyz)?;
    let xyz_to_rgb = mat3_inverse(rgb_to_xyz)?;
    Ok(RgbColorSpace {
        id: id.to_string(),
        name: name.to_string(),
        red,
        green,
        blue,
        white,
        transfer,
        gamma,
        rgb_to_xyz,
        xyz_to_rgb,
    })
}

fn decode_transfer_channel(mut value: f64, space: &RgbColorSpace) -> f64 {
    value = clamp01(value);
    match space.transfer {
        TransferFunction::Linear => value,
        TransferFunction::Srgb => srgb_channel_to_linear(value),
        TransferFunction::Gamma => value.powf(space.gamma),
        TransferFunction::Rec2020 => {
            const ALPHA: f64 = 1.09929682680944;
            const BETA: f64 = 0.018053968510807;
            if value < 4.5 * BETA {
                value / 4.5
            } else {
                ((value + ALPHA - 1.0) / ALPHA).powf(1.0 / 0.45)
            }
        }
        TransferFunction::ProPhoto => {
            if value <= 16.0 / 512.0 {
                value / 16.0
            } else {
                value.powf(1.8)
            }
        }
    }
}

fn encode_transfer_channel(mut value: f64, space: &RgbColorSpace) -> f64 {
    value = clamp01(value);
    match space.transfer {
        TransferFunction::Linear => value,
        TransferFunction::Srgb => linear_channel_to_srgb(value),
        TransferFunction::Gamma => value.powf(1.0 / space.gamma),
        TransferFunction::Rec2020 => {
            const ALPHA: f64 = 1.09929682680944;
            const BETA: f64 = 0.018053968510807;
            if value < BETA {
                4.5 * value
            } else {
                ALPHA * value.powf(0.45) - (ALPHA - 1.0)
            }
        }
        TransferFunction::ProPhoto => {
            if value <= 1.0 / 512.0 {
                16.0 * value
            } else {
                value.powf(1.0 / 1.8)
            }
        }
    }
}

pub fn clamp01(value: f64) -> f64 {
    value.clamp(0.0, 1.0)
}

pub fn clamp01_vec(value: Vec3) -> Vec3 {
    [clamp01(value[0]), clamp01(value[1]), clamp01(value[2])]
}

pub fn srgb_channel_to_linear(mut value: f64) -> f64 {
    value = clamp01(value);
    if value <= 0.04045 {
        value / 12.92
    } else {
        ((value + 0.055) / 1.055).powf(2.4)
    }
}

pub fn linear_channel_to_srgb(mut value: f64) -> f64 {
    value = clamp01(value);
    if value <= 0.0031308 {
        value * 12.92
    } else {
        1.055 * value.powf(1.0 / 2.4) - 0.055
    }
}

pub fn srgb_to_linear(value: Vec3) -> Vec3 {
    [
        srgb_channel_to_linear(value[0]),
        srgb_channel_to_linear(value[1]),
        srgb_channel_to_linear(value[2]),
    ]
}

pub fn linear_to_srgb(value: Vec3) -> Vec3 {
    [
        linear_channel_to_srgb(value[0]),
        linear_channel_to_srgb(value[1]),
        linear_channel_to_srgb(value[2]),
    ]
}

pub fn parse_white_point(value: &str) -> Result<WhitePoint> {
    match normalize_id(value).as_str() {
        "d50" => make_white_point("d50", 0.34567, 0.35850),
        "d55" => make_white_point("d55", 0.33242, 0.34743),
        "d60" | "aces" => make_white_point("d60", 0.32168, 0.33767),
        "d65" => make_white_point("d65", 0.31271, 0.32902),
        "d75" => make_white_point("d75", 0.29902, 0.31485),
        "a" | "illuminant-a" => make_white_point("a", 0.44757, 0.40745),
        "c" | "illuminant-c" => make_white_point("c", 0.31006, 0.31616),
        "e" | "equal-energy" => make_white_point("e", 1.0 / 3.0, 1.0 / 3.0),
        _ => Err(Error::InvalidArgument(format!(
            "unknown target white/illuminant: {value}"
        ))),
    }
}

pub fn supported_white_points() -> &'static [&'static str] {
    &["d50", "d55", "d60", "d65", "d75", "a", "c", "e"]
}

pub fn parse_rgb_color_space(value: &str, target_white: &WhitePoint) -> Result<RgbColorSpace> {
    let white = target_white.clone();
    match normalize_id(value).as_str() {
        "srgb" | "rgb" => make_rgb_space(
            "srgb",
            "sRGB",
            Chromaticity {
                x: 0.6400,
                y: 0.3300,
            },
            Chromaticity {
                x: 0.3000,
                y: 0.6000,
            },
            Chromaticity {
                x: 0.1500,
                y: 0.0600,
            },
            white,
            TransferFunction::Srgb,
            1.0,
        ),
        "linear-srgb" | "srgb-linear" => make_rgb_space(
            "linear-srgb",
            "Linear sRGB",
            Chromaticity {
                x: 0.6400,
                y: 0.3300,
            },
            Chromaticity {
                x: 0.3000,
                y: 0.6000,
            },
            Chromaticity {
                x: 0.1500,
                y: 0.0600,
            },
            white,
            TransferFunction::Linear,
            1.0,
        ),
        "display-p3" | "p3" => make_rgb_space(
            "display-p3",
            "Display P3",
            Chromaticity {
                x: 0.6800,
                y: 0.3200,
            },
            Chromaticity {
                x: 0.2650,
                y: 0.6900,
            },
            Chromaticity {
                x: 0.1500,
                y: 0.0600,
            },
            white,
            TransferFunction::Srgb,
            1.0,
        ),
        "adobe-rgb" | "adobe-rgb-1998" | "adobergb" | "adobe1998" => make_rgb_space(
            "adobe-rgb-1998",
            "Adobe RGB (1998)",
            Chromaticity {
                x: 0.6400,
                y: 0.3300,
            },
            Chromaticity {
                x: 0.2100,
                y: 0.7100,
            },
            Chromaticity {
                x: 0.1500,
                y: 0.0600,
            },
            white,
            TransferFunction::Gamma,
            563.0 / 256.0,
        ),
        "rec2020" | "bt2020" | "rec-2020" | "bt-2020" => make_rgb_space(
            "rec2020",
            "Rec. 2020",
            Chromaticity {
                x: 0.7080,
                y: 0.2920,
            },
            Chromaticity {
                x: 0.1700,
                y: 0.7970,
            },
            Chromaticity {
                x: 0.1310,
                y: 0.0460,
            },
            white,
            TransferFunction::Rec2020,
            1.0,
        ),
        "linear-rec2020" | "rec2020-linear" | "bt2020-linear" => make_rgb_space(
            "linear-rec2020",
            "Linear Rec. 2020",
            Chromaticity {
                x: 0.7080,
                y: 0.2920,
            },
            Chromaticity {
                x: 0.1700,
                y: 0.7970,
            },
            Chromaticity {
                x: 0.1310,
                y: 0.0460,
            },
            white,
            TransferFunction::Linear,
            1.0,
        ),
        "prophoto" | "prophoto-rgb" | "romm-rgb" => make_rgb_space(
            "prophoto-rgb",
            "ProPhoto RGB",
            Chromaticity {
                x: 0.7347,
                y: 0.2653,
            },
            Chromaticity {
                x: 0.1596,
                y: 0.8404,
            },
            Chromaticity {
                x: 0.0366,
                y: 0.0001,
            },
            white,
            TransferFunction::ProPhoto,
            1.8,
        ),
        "acescg" | "aces-cg" | "ap1" | "linear-acescg" => make_rgb_space(
            "acescg",
            "ACEScg",
            Chromaticity { x: 0.713, y: 0.293 },
            Chromaticity { x: 0.165, y: 0.830 },
            Chromaticity { x: 0.128, y: 0.044 },
            white,
            TransferFunction::Linear,
            1.0,
        ),
        _ => Err(Error::InvalidArgument(format!(
            "unknown target color space: {value}"
        ))),
    }
}

pub fn supported_rgb_color_spaces() -> &'static [&'static str] {
    &[
        "srgb",
        "linear-srgb",
        "display-p3",
        "adobe-rgb-1998",
        "rec2020",
        "linear-rec2020",
        "prophoto-rgb",
        "acescg",
    ]
}

pub fn mat3_mul(matrix: Mat3, vector: Vec3) -> Vec3 {
    [
        matrix[0] * vector[0] + matrix[1] * vector[1] + matrix[2] * vector[2],
        matrix[3] * vector[0] + matrix[4] * vector[1] + matrix[5] * vector[2],
        matrix[6] * vector[0] + matrix[7] * vector[1] + matrix[8] * vector[2],
    ]
}

pub fn adapt_xyz_white(
    xyz: Vec3,
    source_white: &WhitePoint,
    target_white: &WhitePoint,
) -> Result<Vec3> {
    if (source_white.xy.x - target_white.xy.x).abs() < 1e-10
        && (source_white.xy.y - target_white.xy.y).abs() < 1e-10
    {
        return Ok(xyz);
    }
    let source_cone = mat3_mul(BRADFORD, source_white.xyz);
    let target_cone = mat3_mul(BRADFORD, target_white.xyz);
    let mut cone = mat3_mul(BRADFORD, xyz);
    for channel in 0..3 {
        if source_cone[channel].abs() < 1e-14 {
            return Err(Error::Runtime(
                "invalid source white for Bradford adaptation".into(),
            ));
        }
        cone[channel] *= target_cone[channel] / source_cone[channel];
    }
    Ok(mat3_mul(BRADFORD_INV, cone))
}

pub fn encoded_to_linear_rgb(value: Vec3, space: &RgbColorSpace) -> Vec3 {
    [
        decode_transfer_channel(value[0], space),
        decode_transfer_channel(value[1], space),
        decode_transfer_channel(value[2], space),
    ]
}

pub fn linear_to_encoded_rgb(value: Vec3, space: &RgbColorSpace) -> Vec3 {
    [
        encode_transfer_channel(value[0], space),
        encode_transfer_channel(value[1], space),
        encode_transfer_channel(value[2], space),
    ]
}

pub fn linear_rgb_to_xyz(linear_rgb: Vec3, space: &RgbColorSpace) -> Vec3 {
    mat3_mul(space.rgb_to_xyz, linear_rgb)
}

pub fn xyz_to_linear_rgb(xyz: Vec3, space: &RgbColorSpace) -> Vec3 {
    mat3_mul(space.xyz_to_rgb, xyz)
}

pub fn xyz_to_lab(xyz: Vec3, white: &WhitePoint) -> Lab {
    let fx = lab_f(xyz[0] / white.xyz[0]);
    let fy = lab_f(xyz[1] / white.xyz[1]);
    let fz = lab_f(xyz[2] / white.xyz[2]);
    Lab {
        l: 116.0 * fy - 16.0,
        a: 500.0 * (fx - fy),
        b: 200.0 * (fy - fz),
    }
}

pub fn lab_to_xyz(lab: Lab, white: &WhitePoint) -> Vec3 {
    let fy = (lab.l + 16.0) / 116.0;
    let fx = lab.a / 500.0 + fy;
    let fz = fy - lab.b / 200.0;
    [
        white.xyz[0] * lab_f_inv(fx),
        white.xyz[1] * lab_f_inv(fy),
        white.xyz[2] * lab_f_inv(fz),
    ]
}

pub fn lab_d50_to_target_linear(
    lab: Lab,
    target_space: &RgbColorSpace,
    target_white: &WhitePoint,
) -> Result<Vec3> {
    let d50 = parse_white_point("d50")?;
    let xyz_target = adapt_xyz_white(lab_to_xyz(lab, &d50), &d50, target_white)?;
    Ok(xyz_to_linear_rgb(xyz_target, target_space))
}

pub fn lab_d50_to_target_lab(lab: Lab, target_white: &WhitePoint) -> Result<Lab> {
    let d50 = parse_white_point("d50")?;
    let xyz_target = adapt_xyz_white(lab_to_xyz(lab, &d50), &d50, target_white)?;
    Ok(xyz_to_lab(xyz_target, target_white))
}

pub fn lab_d50_to_srgb(lab: Lab) -> Result<Vec3> {
    let d65 = parse_white_point("d65")?;
    let srgb = parse_rgb_color_space("srgb", &d65)?;
    Ok(clamp01_vec(linear_to_srgb(lab_d50_to_target_linear(
        lab, &srgb, &d65,
    )?)))
}

/// Reference conversion for fitting. Display swatches use the clipped helper
/// above; clipping an out-of-gamut reference here would change its target.
pub fn lab_d50_to_srgb_unclipped(lab: Lab) -> Result<Vec3> {
    let d65 = parse_white_point("d65")?;
    let srgb = parse_rgb_color_space("srgb", &d65)?;
    Ok(lab_d50_to_target_linear(lab, &srgb, &d65)?.map(|v| {
        if v <= 0.0031308 {
            12.92 * v
        } else {
            1.055 * v.powf(1.0 / 2.4) - 0.055
        }
    }))
}

pub fn convert_encoded_rgb(
    value: Vec3,
    source_space: &RgbColorSpace,
    target_space: &RgbColorSpace,
) -> Result<Vec3> {
    let source_linear = encoded_to_linear_rgb(value, source_space);
    let source_xyz = linear_rgb_to_xyz(source_linear, source_space);
    let target_xyz = adapt_xyz_white(source_xyz, &source_space.white, &target_space.white)?;
    Ok(linear_to_encoded_rgb(
        clamp01_vec(xyz_to_linear_rgb(target_xyz, target_space)),
        target_space,
    ))
}

/// Cached sRGB / D65 pair. Building the space inverts a 3×3 every call — never
/// do that per pixel on a still.
pub fn srgb_d65_space() -> (&'static WhitePoint, &'static RgbColorSpace) {
    static CELL: OnceLock<(WhitePoint, RgbColorSpace)> = OnceLock::new();
    let (white, space) = CELL.get_or_init(|| {
        let d65 = parse_white_point("d65").expect("d65");
        let space = parse_rgb_color_space("srgb", &d65).expect("srgb");
        (d65, space)
    });
    (white, space)
}

pub fn srgb_to_xyz_d65(srgb: Vec3) -> Result<Vec3> {
    let (_, space) = srgb_d65_space();
    Ok(linear_rgb_to_xyz(srgb_to_linear(srgb), space))
}

pub fn srgb_to_lab_d65(srgb: Vec3) -> Result<Lab> {
    let (d65, space) = srgb_d65_space();
    Ok(xyz_to_lab(
        linear_rgb_to_xyz(srgb_to_linear(srgb), space),
        d65,
    ))
}

/// CIE L* for encoded sRGB under D65. This is the exact L* branch of
/// `srgb_to_lab_d65`, without computing the unused X/Z, a*, or b* values.
pub fn srgb_lstar_d65(srgb: Vec3) -> f64 {
    let (d65, space) = srgb_d65_space();
    let linear = srgb_to_linear(srgb);
    let y = space.rgb_to_xyz[3] * linear[0]
        + space.rgb_to_xyz[4] * linear[1]
        + space.rgb_to_xyz[5] * linear[2];
    116.0 * lab_f(y / d65.xyz[1]) - 16.0
}

/// CIEDE2000 (Sharma, Wu, Dalal). kl = kc = kh = 1.
pub fn delta_e_2000(c1: Lab, c2: Lab) -> f64 {
    const KL: f64 = 1.0;
    const KC: f64 = 1.0;
    const KH: f64 = 1.0;

    let c1_ab = (c1.a * c1.a + c1.b * c1.b).sqrt();
    let c2_ab = (c2.a * c2.a + c2.b * c2.b).sqrt();
    let c_bar_ab = (c1_ab + c2_ab) * 0.5;
    let c_bar_ab7 = c_bar_ab.powi(7);
    let g = 0.5 * (1.0 - (c_bar_ab7 / (c_bar_ab7 + 25.0_f64.powi(7))).sqrt());

    let a1_prime = (1.0 + g) * c1.a;
    let a2_prime = (1.0 + g) * c2.a;
    let c1_prime = (a1_prime * a1_prime + c1.b * c1.b).sqrt();
    let c2_prime = (a2_prime * a2_prime + c2.b * c2.b).sqrt();

    let hue_degrees = |b: f64, a: f64| {
        if a == 0.0 && b == 0.0 {
            return 0.0;
        }
        let mut hue = radians_to_degrees(b.atan2(a));
        if hue < 0.0 {
            hue += 360.0;
        }
        hue
    };

    let h1_prime = hue_degrees(c1.b, a1_prime);
    let h2_prime = hue_degrees(c2.b, a2_prime);
    let delta_l_prime = c2.l - c1.l;
    let delta_c_prime = c2_prime - c1_prime;

    let delta_h_prime = if c1_prime * c2_prime == 0.0 {
        0.0
    } else if (h2_prime - h1_prime).abs() <= 180.0 {
        h2_prime - h1_prime
    } else if h2_prime <= h1_prime {
        h2_prime - h1_prime + 360.0
    } else {
        h2_prime - h1_prime - 360.0
    };

    let delta_h_big_prime =
        2.0 * (c1_prime * c2_prime).sqrt() * degrees_to_radians(delta_h_prime * 0.5).sin();
    let l_bar_prime = (c1.l + c2.l) * 0.5;
    let c_bar_prime = (c1_prime + c2_prime) * 0.5;

    let h_bar_prime = if c1_prime * c2_prime == 0.0 {
        h1_prime + h2_prime
    } else if (h1_prime - h2_prime).abs() <= 180.0 {
        (h1_prime + h2_prime) * 0.5
    } else if (h1_prime + h2_prime) < 360.0 {
        (h1_prime + h2_prime + 360.0) * 0.5
    } else {
        (h1_prime + h2_prime - 360.0) * 0.5
    };

    let t = 1.0 - 0.17 * degrees_to_radians(h_bar_prime - 30.0).cos()
        + 0.24 * degrees_to_radians(2.0 * h_bar_prime).cos()
        + 0.32 * degrees_to_radians(3.0 * h_bar_prime + 6.0).cos()
        - 0.20 * degrees_to_radians(4.0 * h_bar_prime - 63.0).cos();

    let delta_theta = 30.0 * (-((h_bar_prime - 275.0) / 25.0).powi(2)).exp();
    let c_bar_prime7 = c_bar_prime.powi(7);
    let rc = 2.0 * (c_bar_prime7 / (c_bar_prime7 + 25.0_f64.powi(7))).sqrt();
    let sl =
        1.0 + (0.015 * (l_bar_prime - 50.0).powi(2)) / (20.0 + (l_bar_prime - 50.0).powi(2)).sqrt();
    let sc = 1.0 + 0.045 * c_bar_prime;
    let sh = 1.0 + 0.015 * c_bar_prime * t;
    let rt = -degrees_to_radians(2.0 * delta_theta).sin() * rc;

    let dl = delta_l_prime / (KL * sl);
    let dc = delta_c_prime / (KC * sc);
    let dh = delta_h_big_prime / (KH * sh);
    (dl * dl + dc * dc + dh * dh + rt * dc * dh).sqrt()
}

pub fn default_srgb_d65() -> Result<(WhitePoint, RgbColorSpace)> {
    let d65 = parse_white_point("d65")?;
    let srgb = parse_rgb_color_space("srgb", &d65)?;
    Ok((d65, srgb))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ciede2000_self_is_zero() {
        let lab = Lab::new(50.0, 10.0, -5.0);
        assert!(delta_e_2000(lab, lab).abs() < 1e-12);
    }

    #[test]
    fn lstar_fast_path_matches_full_lab_conversion() {
        let samples = [
            [0.0, 0.0, 0.0],
            [1.0, 1.0, 1.0],
            [0.18, 0.18, 0.18],
            [0.92, 0.15, 0.37],
            [0.01, 0.5, 0.99],
        ];
        for rgb in samples {
            let expected = srgb_to_lab_d65(rgb).expect("sRGB Lab").l;
            let actual = srgb_lstar_d65(rgb);
            assert!(
                (actual - expected).abs() < 1e-12,
                "{rgb:?}: {actual} != {expected}"
            );
        }
    }
}
