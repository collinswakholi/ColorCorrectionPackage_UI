//! Color science, chart references, correction maps, and CCP-style stages.
//!
//! Chart Labs are CIE Lab D50/2° in Opt2 **row-major**. The same index `i`
//! is used for `names[i]`, `reference_lab_d50[i]`, and `patch_cells[i]`.

pub mod color;
pub mod ffc;
pub mod honest;
pub mod lut;
pub mod maps;
pub mod mlp;
pub mod phase2;
pub mod pls;
pub mod refs;
pub mod sample;
pub mod stages;

pub use color::{
    adapt_xyz_white, delta_e_2000, lab_d50_to_srgb, lab_d50_to_target_lab,
    lab_d50_to_target_linear, parse_rgb_color_space, parse_white_point, srgb_to_lab_d65, Lab,
    RgbColorSpace, TransferFunction, WhitePoint,
};
pub use maps::{fit_correction_model, CorrectionModel, MappingOptions, ModelKind};
pub use refs::{chart_spec, chart_spec_from_name, ChartKind, ChartPatch, ChartSpec, Quad, Vec2};

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    InvalidArgument(String),
    #[error("{0}")]
    Runtime(String),
    #[error(
        "RAW files are not supported ({ext}): {path}. Export an 8-bit JPEG, PNG, or TIFF first."
    )]
    UnsupportedRaw { path: String, ext: String },
    #[error("16-bit TIFF is not supported: {path}. Export an 8-bit JPEG, PNG, or TIFF first.")]
    Unsupported16BitTiff { path: String },
    #[error("apply-only / no chart: ΔE metrics are hidden")]
    MetricsHidden,
    #[error("Phase 2 reconciler is disabled until a .ts path exists")]
    ReconcilerDisabled,
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Image(#[from] image::ImageError),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

pub type Result<T> = std::result::Result<T, Error>;

/// Ridge used by Speedy OLS maps (CCM, RPCC, linear).
pub const RIDGE_LAMBDA: f64 = 1e-5;

/// Saturation flag: any encoded channel at or above this value.
pub const SATURATION_THRESHOLD: f64 = 0.99;

/// CCP `compute_diag` scale applied to the median per-channel ratio.
pub const WB_DIAG_SCALE: f64 = 0.95;

/// CCP_rs sat-check working floor (`clamp(-0.2, 1.3)` before the 0–1 stretch).
pub const RGB_CLIP_LO: f64 = -0.2;

/// CCP_rs sat-check working ceiling (`clamp(-0.2, 1.3)` before the 0–1 stretch).
pub const RGB_CLIP_HI: f64 = 1.3;

/// CCP_rs FFC `transform_extremity` — tanh soft-cap starts here.
pub const FFC_EXTREMITY_CUT_OFF: f32 = 1.3;

/// CCP_rs FFC `transform_extremity` — asymptotic gain ceiling.
pub const FFC_EXTREMITY_MAX: f32 = 1.8;
