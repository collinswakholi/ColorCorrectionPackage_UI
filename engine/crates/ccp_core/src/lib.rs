//! Color science, chart references, correction maps, and CCP-style stages.
//!
//! Chart Labs are CIE Lab D50/2° in Opt2 **row-major**. The same index `i`
//! is used for `names[i]`, `reference_lab_d50[i]`, and `patch_cells[i]`.

pub mod color;
mod error;
pub mod ffc;
pub mod honest;
pub mod lut;
pub mod maps;
mod mlp;
pub mod phase2;
mod pls;
pub mod reference_profile;
pub mod refs;
pub mod sample;
pub mod stages;

pub use error::{Error, Result};
