//! Per-chart CIE Lab D50/2° tables in Opt2 row-major. Same-index law.

use crate::color::{lab_d50_to_srgb, Lab};
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct Vec2 {
    pub x: f64,
    pub y: f64,
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct Quad {
    pub top_left: Vec2,
    pub top_right: Vec2,
    pub bottom_right: Vec2,
    pub bottom_left: Vec2,
}

impl Quad {
    pub fn corners(self) -> [Vec2; 4] {
        [
            self.top_left,
            self.top_right,
            self.bottom_right,
            self.bottom_left,
        ]
    }

    pub fn bilerp(self, u: f64, v: f64) -> Vec2 {
        let w00 = (1.0 - u) * (1.0 - v);
        let w10 = u * (1.0 - v);
        let w11 = u * v;
        let w01 = (1.0 - u) * v;
        Vec2 {
            x: self.top_left.x * w00
                + self.top_right.x * w10
                + self.bottom_right.x * w11
                + self.bottom_left.x * w01,
            y: self.top_left.y * w00
                + self.top_right.y * w10
                + self.bottom_right.y * w11
                + self.bottom_left.y * w01,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ChartPatch {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provenance: Option<crate::reference_profile::PatchProvenance>,
    pub name: String,
    pub index: usize,
    pub reference_lab_d50: Lab,
    pub reference_srgb: [f64; 3],
    pub measured_srgb: [f64; 3],
    pub original_measured_srgb: [f64; 3],
    pub center: Vec2,
    pub saturated: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ChartKind {
    Classic,
    Passport,
    Spyder,
    DigitalSg,
}

impl ChartKind {
    pub fn id(self) -> &'static str {
        match self {
            Self::Classic => "classic",
            Self::Passport => "passport",
            Self::Spyder => "spyder",
            Self::DigitalSg => "digital-sg",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ChartSpec {
    pub kind: ChartKind,
    pub id: String,
    pub display_name: String,
    pub detector_name: String,
    pub columns: usize,
    pub rows: usize,
    pub names: Vec<String>,
    pub reference_lab_d50: Vec<Lab>,
    pub reference_srgb: Vec<[f64; 3]>,
}

impl ChartSpec {
    pub fn patch_count(&self) -> usize {
        self.columns * self.rows
    }

    pub fn index_of(&self, name: &str) -> Option<usize> {
        self.names.iter().position(|n| n.eq_ignore_ascii_case(name))
    }
}

/// BabelColor ColorChecker24 After Nov 2014, remapped column-major → Opt2 row-major.
const CLASSIC_LAB_D50: [Lab; 24] = [
    Lab {
        l: 37.54,
        a: 14.37,
        b: 14.92,
    },
    Lab {
        l: 64.66,
        a: 19.27,
        b: 17.50,
    },
    Lab {
        l: 49.32,
        a: -3.82,
        b: -22.54,
    },
    Lab {
        l: 43.46,
        a: -12.74,
        b: 22.72,
    },
    Lab {
        l: 54.94,
        a: 9.61,
        b: -24.79,
    },
    Lab {
        l: 70.48,
        a: -32.26,
        b: -0.37,
    },
    Lab {
        l: 62.73,
        a: 35.83,
        b: 56.50,
    },
    Lab {
        l: 39.43,
        a: 10.75,
        b: -45.17,
    },
    Lab {
        l: 50.57,
        a: 48.64,
        b: 16.67,
    },
    Lab {
        l: 30.10,
        a: 22.54,
        b: -20.87,
    },
    Lab {
        l: 71.77,
        a: -24.13,
        b: 58.19,
    },
    Lab {
        l: 71.51,
        a: 18.24,
        b: 67.37,
    },
    Lab {
        l: 28.37,
        a: 15.42,
        b: -49.80,
    },
    Lab {
        l: 54.38,
        a: -39.72,
        b: 32.27,
    },
    Lab {
        l: 42.43,
        a: 51.05,
        b: 28.62,
    },
    Lab {
        l: 81.80,
        a: 2.67,
        b: 80.41,
    },
    Lab {
        l: 50.63,
        a: 51.28,
        b: -14.12,
    },
    Lab {
        l: 49.57,
        a: -29.71,
        b: -28.32,
    },
    Lab {
        l: 95.19,
        a: -1.03,
        b: 2.93,
    },
    Lab {
        l: 81.29,
        a: -0.57,
        b: 0.44,
    },
    Lab {
        l: 66.89,
        a: -0.75,
        b: -0.06,
    },
    Lab {
        l: 50.76,
        a: -0.13,
        b: 0.14,
    },
    Lab {
        l: 35.63,
        a: -0.46,
        b: -0.48,
    },
    Lab {
        l: 20.64,
        a: 0.07,
        b: -0.46,
    },
];

const CLASSIC_NAMES: [&str; 24] = [
    "dark skin",
    "light skin",
    "blue sky",
    "foliage",
    "blue flower",
    "bluish green",
    "orange",
    "purplish blue",
    "moderate red",
    "purple",
    "yellow green",
    "orange yellow",
    "blue",
    "green",
    "red",
    "yellow",
    "magenta",
    "cyan",
    "white",
    "neutral 8",
    "neutral 6.5",
    "neutral 5",
    "neutral 3.5",
    "black",
];

/// SpyderCHECKR 24 after 2018 (Datacolor / darktable V2). Opt2 order D, C, B, A.
const SPYDER_LAB_D50: [Lab; 24] = [
    Lab {
        l: 70.19,
        a: -31.85,
        b: 1.98,
    },
    Lab {
        l: 54.38,
        a: 8.84,
        b: -25.71,
    },
    Lab {
        l: 42.03,
        a: -15.78,
        b: 22.93,
    },
    Lab {
        l: 48.82,
        a: -5.11,
        b: -23.08,
    },
    Lab {
        l: 65.10,
        a: 18.14,
        b: 18.68,
    },
    Lab {
        l: 36.13,
        a: 14.15,
        b: 15.78,
    },
    Lab {
        l: 60.94,
        a: 38.21,
        b: 61.31,
    },
    Lab {
        l: 37.80,
        a: 7.30,
        b: -43.04,
    },
    Lab {
        l: 49.81,
        a: 48.50,
        b: 15.76,
    },
    Lab {
        l: 28.88,
        a: 19.36,
        b: -24.48,
    },
    Lab {
        l: 72.45,
        a: -23.57,
        b: 60.47,
    },
    Lab {
        l: 71.65,
        a: 23.74,
        b: 72.28,
    },
    Lab {
        l: 47.12,
        a: -32.50,
        b: -28.75,
    },
    Lab {
        l: 50.49,
        a: 53.45,
        b: -13.55,
    },
    Lab {
        l: 83.61,
        a: 3.36,
        b: 87.02,
    },
    Lab {
        l: 41.05,
        a: 60.75,
        b: 31.17,
    },
    Lab {
        l: 54.14,
        a: -40.80,
        b: 34.75,
    },
    Lab {
        l: 24.75,
        a: 13.78,
        b: -49.48,
    },
    Lab {
        l: 96.04,
        a: 2.16,
        b: 2.60,
    },
    Lab {
        l: 80.44,
        a: 1.17,
        b: 2.05,
    },
    Lab {
        l: 65.52,
        a: 0.69,
        b: 1.86,
    },
    Lab {
        l: 49.62,
        a: 0.58,
        b: 1.56,
    },
    Lab {
        l: 33.55,
        a: 0.35,
        b: 1.40,
    },
    Lab {
        l: 16.91,
        a: 1.43,
        b: -0.81,
    },
];

const SPYDER_NAMES: [&str; 24] = [
    "D1", "D2", "D3", "D4", "D5", "D6", "C1", "C2", "C3", "C4", "C5", "C6", "B1", "B2", "B3", "B4",
    "B5", "B6", "A1", "A2", "A3", "A4", "A5", "A6",
];

/// ColorChecker Digital SG After-Nov-2014, Opt2 row-major A1…N1, A2…N10.
const DIGITAL_SG_LAB_D50: [Lab; 140] = [
    Lab {
        l: 96.7100,
        a: -0.6200,
        b: 2.0600,
    },
    Lab {
        l: 49.7000,
        a: 0.1200,
        b: 0.6800,
    },
    Lab {
        l: 8.1300,
        a: 0.1500,
        b: -0.7600,
    },
    Lab {
        l: 96.7900,
        a: -0.6600,
        b: 1.9900,
    },
    Lab {
        l: 49.7900,
        a: 0.1300,
        b: 0.6600,
    },
    Lab {
        l: 8.0900,
        a: 0.1900,
        b: -0.6900,
    },
    Lab {
        l: 96.7000,
        a: -0.6600,
        b: 1.9700,
    },
    Lab {
        l: 49.7400,
        a: 0.1400,
        b: 0.6800,
    },
    Lab {
        l: 8.1000,
        a: 0.1900,
        b: -0.9300,
    },
    Lab {
        l: 96.6900,
        a: -0.6700,
        b: 1.9500,
    },
    Lab {
        l: 49.7200,
        a: 0.1200,
        b: 0.6400,
    },
    Lab {
        l: 8.0800,
        a: 0.1300,
        b: -0.8100,
    },
    Lab {
        l: 49.7100,
        a: 0.1200,
        b: 0.6200,
    },
    Lab {
        l: 96.7900,
        a: -0.6700,
        b: 1.9700,
    },
    Lab {
        l: 8.0500,
        a: 0.1700,
        b: -0.6900,
    },
    Lab {
        l: 33.0200,
        a: 52.0000,
        b: -10.3000,
    },
    Lab {
        l: 19.6500,
        a: 20.4200,
        b: -18.8200,
    },
    Lab {
        l: 84.0000,
        a: -1.7000,
        b: -8.3700,
    },
    Lab {
        l: 32.7700,
        a: 19.9100,
        b: 22.3300,
    },
    Lab {
        l: 63.8800,
        a: 20.3400,
        b: 19.9300,
    },
    Lab {
        l: 45.8400,
        a: -3.7400,
        b: -25.3200,
    },
    Lab {
        l: 38.2900,
        a: -17.4400,
        b: 30.2200,
    },
    Lab {
        l: 51.3600,
        a: 9.5200,
        b: -26.9800,
    },
    Lab {
        l: 68.7100,
        a: -35.4100,
        b: -1.1100,
    },
    Lab {
        l: 85.6800,
        a: 10.7500,
        b: 18.3900,
    },
    Lab {
        l: 23.0300,
        a: 33.9500,
        b: 8.8800,
    },
    Lab {
        l: 42.5200,
        a: 63.5500,
        b: 11.4300,
    },
    Lab {
        l: 49.7800,
        a: 0.1200,
        b: 0.6500,
    },
    Lab {
        l: 49.7600,
        a: 0.1100,
        b: 0.7200,
    },
    Lab {
        l: 61.4000,
        a: 27.1400,
        b: -18.4200,
    },
    Lab {
        l: 41.7000,
        a: 18.9000,
        b: -37.4200,
    },
    Lab {
        l: 85.4800,
        a: 15.1500,
        b: 0.7900,
    },
    Lab {
        l: 62.2800,
        a: 37.5600,
        b: 68.8700,
    },
    Lab {
        l: 35.2800,
        a: 12.9300,
        b: -51.1700,
    },
    Lab {
        l: 47.6000,
        a: 53.6600,
        b: 22.1500,
    },
    Lab {
        l: 20.7600,
        a: 31.6600,
        b: -28.0400,
    },
    Lab {
        l: 71.6200,
        a: -24.7700,
        b: 64.1000,
    },
    Lab {
        l: 70.3900,
        a: 19.3700,
        b: 79.7300,
    },
    Lab {
        l: 89.3500,
        a: -16.3800,
        b: 6.4100,
    },
    Lab {
        l: 44.3500,
        a: 67.9400,
        b: 50.6200,
    },
    Lab {
        l: 18.0900,
        a: 32.6100,
        b: -5.9000,
    },
    Lab {
        l: 8.2300,
        a: 0.1800,
        b: -0.8200,
    },
    Lab {
        l: 96.7200,
        a: -0.6300,
        b: 2.0600,
    },
    Lab {
        l: 30.5400,
        a: 50.3900,
        b: -41.7900,
    },
    Lab {
        l: 20.2500,
        a: 0.2600,
        b: -36.4400,
    },
    Lab {
        l: 84.5600,
        a: -19.7400,
        b: -1.1300,
    },
    Lab {
        l: 19.9200,
        a: 25.0700,
        b: -61.0500,
    },
    Lab {
        l: 52.7500,
        a: -44.1200,
        b: 38.6800,
    },
    Lab {
        l: 36.8800,
        a: 65.7200,
        b: 41.6300,
    },
    Lab {
        l: 81.4300,
        a: 2.4100,
        b: 88.9800,
    },
    Lab {
        l: 48.7500,
        a: 57.2400,
        b: -14.4500,
    },
    Lab {
        l: 47.4200,
        a: -30.9100,
        b: -32.2700,
    },
    Lab {
        l: 84.5900,
        a: 5.2100,
        b: -5.8700,
    },
    Lab {
        l: 60.9100,
        a: 36.5500,
        b: 4.1500,
    },
    Lab {
        l: 40.6600,
        a: 65.5400,
        b: 31.9800,
    },
    Lab {
        l: 96.7300,
        a: -0.6700,
        b: 1.9900,
    },
    Lab {
        l: 8.1700,
        a: 0.1500,
        b: -0.6500,
    },
    Lab {
        l: 49.5600,
        a: -13.9000,
        b: -49.6500,
    },
    Lab {
        l: 60.1300,
        a: -17.8800,
        b: -32.0800,
    },
    Lab {
        l: 85.2600,
        a: 13.3700,
        b: 7.9500,
    },
    Lab {
        l: 96.7800,
        a: -0.6600,
        b: 2.0100,
    },
    Lab {
        l: 79.6500,
        a: -0.0800,
        b: 0.6200,
    },
    Lab {
        l: 65.2200,
        a: -0.2700,
        b: 0.1600,
    },
    Lab {
        l: 49.7100,
        a: 0.1200,
        b: 0.6900,
    },
    Lab {
        l: 34.8500,
        a: -0.2100,
        b: 0.7300,
    },
    Lab {
        l: 15.4300,
        a: -0.2400,
        b: -0.2500,
    },
    Lab {
        l: 83.6300,
        a: -12.4700,
        b: -8.8900,
    },
    Lab {
        l: 62.2000,
        a: 37.4500,
        b: 18.1800,
    },
    Lab {
        l: 53.1300,
        a: 68.4400,
        b: 49.5700,
    },
    Lab {
        l: 49.8000,
        a: 0.1100,
        b: 0.6700,
    },
    Lab {
        l: 49.6800,
        a: 0.1400,
        b: 0.7400,
    },
    Lab {
        l: 60.6200,
        a: -29.9100,
        b: -27.5400,
    },
    Lab {
        l: 19.7500,
        a: -17.7900,
        b: -22.3700,
    },
    Lab {
        l: 84.3800,
        a: -11.9700,
        b: 27.1600,
    },
    Lab {
        l: 8.0700,
        a: 0.1200,
        b: -0.9300,
    },
    Lab {
        l: 30.3200,
        a: -0.1000,
        b: 0.2200,
    },
    Lab {
        l: 39.5500,
        a: -0.3700,
        b: -0.0900,
    },
    Lab {
        l: 60.0400,
        a: 0.0900,
        b: 0.0500,
    },
    Lab {
        l: 75.3600,
        a: 0.3500,
        b: 0.2600,
    },
    Lab {
        l: 88.8500,
        a: -0.5900,
        b: 0.2500,
    },
    Lab {
        l: 70.6000,
        a: -0.2400,
        b: 0.0700,
    },
    Lab {
        l: 63.3300,
        a: 51.3000,
        b: 81.8800,
    },
    Lab {
        l: 82.0800,
        a: 23.3900,
        b: 87.2400,
    },
    Lab {
        l: 8.1800,
        a: 0.1500,
        b: -0.8400,
    },
    Lab {
        l: 96.6000,
        a: -0.6200,
        b: 2.1100,
    },
    Lab {
        l: 20.1300,
        a: -24.8100,
        b: -7.5000,
    },
    Lab {
        l: 60.4300,
        a: -5.1200,
        b: -32.7900,
    },
    Lab {
        l: 62.3500,
        a: 29.9400,
        b: 36.8900,
    },
    Lab {
        l: 77.3700,
        a: 20.2800,
        b: 24.2700,
    },
    Lab {
        l: 63.4600,
        a: 13.5300,
        b: 26.3700,
    },
    Lab {
        l: 44.4900,
        a: 16.0600,
        b: 26.7900,
    },
    Lab {
        l: 67.6000,
        a: 14.4700,
        b: 17.1200,
    },
    Lab {
        l: 45.1400,
        a: 26.3800,
        b: 41.2400,
    },
    Lab {
        l: 64.0000,
        a: 25.0900,
        b: 27.1400,
    },
    Lab {
        l: 45.1400,
        a: -0.0400,
        b: 0.8600,
    },
    Lab {
        l: 73.7400,
        a: -11.4500,
        b: 85.0700,
    },
    Lab {
        l: 82.5000,
        a: 5.2900,
        b: 96.6800,
    },
    Lab {
        l: 96.7300,
        a: -0.6500,
        b: 2.0100,
    },
    Lab {
        l: 7.9900,
        a: 0.2100,
        b: -0.7500,
    },
    Lab {
        l: 60.3200,
        a: -40.2900,
        b: -13.2500,
    },
    Lab {
        l: 50.4600,
        a: -47.9000,
        b: -11.5600,
    },
    Lab {
        l: 64.1700,
        a: 21.3400,
        b: 19.3600,
    },
    Lab {
        l: 74.0100,
        a: 29.0000,
        b: 25.8000,
    },
    Lab {
        l: 64.4400,
        a: 14.3100,
        b: 17.6300,
    },
    Lab {
        l: 64.9700,
        a: 15.8900,
        b: 16.7900,
    },
    Lab {
        l: 64.7500,
        a: 17.3000,
        b: 18.8800,
    },
    Lab {
        l: 36.2000,
        a: 16.7000,
        b: 27.0600,
    },
    Lab {
        l: 66.6500,
        a: 22.2100,
        b: 28.8100,
    },
    Lab {
        l: 20.3300,
        a: 0.4000,
        b: -0.2100,
    },
    Lab {
        l: 62.3500,
        a: 1.9600,
        b: 57.5200,
    },
    Lab {
        l: 71.9000,
        a: -17.3200,
        b: 77.7200,
    },
    Lab {
        l: 49.7500,
        a: 0.1300,
        b: 0.6700,
    },
    Lab {
        l: 49.6700,
        a: 0.1500,
        b: 0.7300,
    },
    Lab {
        l: 19.6200,
        a: 1.7700,
        b: 11.9900,
    },
    Lab {
        l: 60.5300,
        a: -40.7500,
        b: 19.3700,
    },
    Lab {
        l: 50.4800,
        a: -53.2100,
        b: 12.6500,
    },
    Lab {
        l: 20.3300,
        a: -23.9800,
        b: 7.2000,
    },
    Lab {
        l: 60.0500,
        a: -44.0000,
        b: 7.2700,
    },
    Lab {
        l: 60.7700,
        a: -30.1900,
        b: 40.7600,
    },
    Lab {
        l: 51.2600,
        a: -50.6500,
        b: 43.8000,
    },
    Lab {
        l: 61.6500,
        a: -54.3300,
        b: 46.1800,
    },
    Lab {
        l: 62.0500,
        a: 16.4500,
        b: 51.7400,
    },
    Lab {
        l: 62.3300,
        a: -14.5400,
        b: 54.5800,
    },
    Lab {
        l: 72.7700,
        a: -29.0900,
        b: 71.2600,
    },
    Lab {
        l: 21.9500,
        a: 13.4100,
        b: 16.3600,
    },
    Lab {
        l: 8.1100,
        a: 0.1500,
        b: -0.9000,
    },
    Lab {
        l: 96.5100,
        a: -0.6300,
        b: 2.1100,
    },
    Lab {
        l: 49.6800,
        a: 0.1500,
        b: 0.7800,
    },
    Lab {
        l: 8.0900,
        a: 0.1900,
        b: -0.6900,
    },
    Lab {
        l: 96.5700,
        a: -0.6400,
        b: 2.0000,
    },
    Lab {
        l: 49.7200,
        a: 0.1400,
        b: 0.7100,
    },
    Lab {
        l: 8.0800,
        a: 0.1800,
        b: -0.7800,
    },
    Lab {
        l: 96.7100,
        a: -0.6400,
        b: 2.0100,
    },
    Lab {
        l: 49.7600,
        a: 0.1400,
        b: 0.7100,
    },
    Lab {
        l: 7.9700,
        a: 0.1400,
        b: -0.8000,
    },
    Lab {
        l: 96.7100,
        a: -0.6400,
        b: 2.0200,
    },
    Lab {
        l: 49.7400,
        a: 0.1400,
        b: 0.6900,
    },
    Lab {
        l: 8.1300,
        a: 0.1500,
        b: -0.8600,
    },
    Lab {
        l: 49.7400,
        a: 0.1200,
        b: 0.6900,
    },
    Lab {
        l: 96.5500,
        a: -0.6400,
        b: 2.0200,
    },
];

const DEFAULT_CLASSIC_NEUTRALS: [&str; 6] = [
    "white",
    "neutral 8",
    "neutral 6.5",
    "neutral 5",
    "neutral 3.5",
    "black",
];
const DEFAULT_SPYDER_NEUTRALS: [&str; 6] = ["A1", "A2", "A3", "A4", "A5", "A6"];
const DEFAULT_SG_NEUTRALS: [&str; 12] = [
    "E5", "F5", "G5", "H5", "I5", "J5", "E6", "F6", "G6", "H6", "I6", "J6",
];

fn digital_sg_names() -> Vec<String> {
    let mut names = Vec::with_capacity(140);
    for row in 0..10 {
        for column in 0..14 {
            let letter = (b'A' + column as u8) as char;
            names.push(format!("{}{}", letter, row + 1));
        }
    }
    names
}

fn assign_srgb(labs: &[Lab]) -> Result<Vec<[f64; 3]>> {
    labs.iter().map(|lab| lab_d50_to_srgb(*lab)).collect()
}

pub fn parse_chart_kind(value: &str) -> Result<ChartKind> {
    let id: String = value
        .chars()
        .map(|ch| {
            if ch == '_' || ch == ' ' || ch == '.' {
                '-'
            } else {
                ch.to_ascii_lowercase()
            }
        })
        .collect();
    match id.as_str() {
        "classic" | "classic-4x6" | "colorchecker" | "colorchecker-classic" => {
            Ok(ChartKind::Classic)
        }
        "passport" | "colorchecker-passport" => Ok(ChartKind::Passport),
        "spyder" | "spyder-4x6" | "spydercheckr" | "spyder-checkr" => Ok(ChartKind::Spyder),
        "digital-sg" | "digital-sg-10x14" | "sg" | "colorchecker-digital-sg" => {
            Ok(ChartKind::DigitalSg)
        }
        _ => Err(Error::InvalidArgument(
            "supported chart types: classic, passport, spyder, digital-sg".into(),
        )),
    }
}

pub fn chart_spec(kind: ChartKind) -> Result<ChartSpec> {
    match kind {
        ChartKind::Spyder => Ok(ChartSpec {
            kind,
            id: kind.id().into(),
            display_name: "Datacolor SpyderCHECKR 24".into(),
            detector_name: "spyder_4x6".into(),
            columns: 6,
            rows: 4,
            names: SPYDER_NAMES.iter().map(|s| (*s).to_string()).collect(),
            reference_srgb: assign_srgb(&SPYDER_LAB_D50)?,
            reference_lab_d50: SPYDER_LAB_D50.to_vec(),
        }),
        ChartKind::DigitalSg => Ok(ChartSpec {
            kind,
            id: kind.id().into(),
            display_name: "ColorChecker Digital SG".into(),
            detector_name: "digital_sg_10x14".into(),
            columns: 14,
            rows: 10,
            names: digital_sg_names(),
            reference_srgb: assign_srgb(&DIGITAL_SG_LAB_D50)?,
            reference_lab_d50: DIGITAL_SG_LAB_D50.to_vec(),
        }),
        ChartKind::Classic | ChartKind::Passport => Ok(ChartSpec {
            kind,
            id: kind.id().into(),
            display_name: if kind == ChartKind::Passport {
                "ColorChecker Passport classic face".into()
            } else {
                "ColorChecker Classic".into()
            },
            detector_name: "classic_4x6".into(),
            columns: 6,
            rows: 4,
            names: CLASSIC_NAMES.iter().map(|s| (*s).to_string()).collect(),
            reference_srgb: assign_srgb(&CLASSIC_LAB_D50)?,
            reference_lab_d50: CLASSIC_LAB_D50.to_vec(),
        }),
    }
}

pub fn chart_spec_from_name(value: &str) -> Result<ChartSpec> {
    chart_spec(parse_chart_kind(value)?)
}

pub fn default_neutral_names(kind: ChartKind) -> Vec<String> {
    match kind {
        ChartKind::Classic | ChartKind::Passport => DEFAULT_CLASSIC_NEUTRALS
            .iter()
            .map(|s| (*s).to_string())
            .collect(),
        ChartKind::Spyder => DEFAULT_SPYDER_NEUTRALS
            .iter()
            .map(|s| (*s).to_string())
            .collect(),
        ChartKind::DigitalSg => DEFAULT_SG_NEUTRALS
            .iter()
            .map(|s| (*s).to_string())
            .collect(),
    }
}

/// Plan defaults. Used when `refs/neutrals.json` is missing.
pub fn embedded_neutrals_table() -> HashMap<String, Vec<String>> {
    let mut map = HashMap::new();
    map.insert(
        "classic".into(),
        DEFAULT_CLASSIC_NEUTRALS
            .iter()
            .map(|s| (*s).to_string())
            .collect(),
    );
    map.insert(
        "passport".into(),
        DEFAULT_CLASSIC_NEUTRALS
            .iter()
            .map(|s| (*s).to_string())
            .collect(),
    );
    map.insert(
        "spyder".into(),
        DEFAULT_SPYDER_NEUTRALS
            .iter()
            .map(|s| (*s).to_string())
            .collect(),
    );
    map.insert(
        "digital-sg".into(),
        DEFAULT_SG_NEUTRALS
            .iter()
            .map(|s| (*s).to_string())
            .collect(),
    );
    map
}

pub fn neutrals_json_path() -> PathBuf {
    if let Some(root) = std::env::var_os("CCP_NEW_UI_ROOT") {
        return PathBuf::from(root).join("refs/neutrals.json");
    }
    #[cfg(debug_assertions)]
    {
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../../refs/neutrals.json")
    }
    #[cfg(not(debug_assertions))]
    {
        PathBuf::from("refs/neutrals.json")
    }
}

pub fn load_neutrals_table(path: Option<&Path>) -> Result<HashMap<String, Vec<String>>> {
    let candidates: Vec<PathBuf> = match path {
        Some(p) => vec![p.to_path_buf()],
        None => vec![neutrals_json_path()],
    };
    for candidate in candidates {
        if candidate.is_file() {
            let text = std::fs::read_to_string(&candidate)?;
            let table: HashMap<String, Vec<String>> = serde_json::from_str(&text)?;
            return Ok(table);
        }
    }
    Ok(embedded_neutrals_table())
}

pub fn neutral_names_for(
    kind: ChartKind,
    table: Option<&HashMap<String, Vec<String>>>,
) -> Vec<String> {
    if let Some(table) = table {
        if let Some(names) = table.get(kind.id()) {
            return names.clone();
        }
    }
    default_neutral_names(kind)
}

/// Named neutrals only. Never last-6 of 140, never chroma threshold for SG.
pub fn lookup_neutrals(
    spec: &ChartSpec,
    table: Option<&HashMap<String, Vec<String>>>,
) -> Result<Vec<(usize, String, Lab)>> {
    let names = neutral_names_for(spec.kind, table);
    let mut out = Vec::with_capacity(names.len());
    for name in names {
        let index = spec.index_of(&name).ok_or_else(|| {
            Error::InvalidArgument(format!(
                "neutral '{}' is not in {} names (same-index law)",
                name, spec.id
            ))
        })?;
        out.push((index, name, spec.reference_lab_d50[index]));
    }
    Ok(out)
}

pub fn classic_lab_d50() -> &'static [Lab; 24] {
    &CLASSIC_LAB_D50
}

pub fn spyder_lab_d50() -> &'static [Lab; 24] {
    &SPYDER_LAB_D50
}

pub fn digital_sg_lab_d50() -> &'static [Lab; 140] {
    &DIGITAL_SG_LAB_D50
}
