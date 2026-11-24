//! The error type shared by every ccp_core module.

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    InvalidArgument(String),
    #[error("{0}")]
    Runtime(String),
    /// A saved correction model failed validation.
    #[error("{0}")]
    InvalidModel(String),
    #[error(
        "RAW files are not supported ({ext}): {path}. Export an 8-bit JPEG, PNG, or TIFF first."
    )]
    UnsupportedRaw { path: String, ext: String },
    #[error("16-bit TIFF is not supported: {path}. Export an 8-bit JPEG, PNG, or TIFF first.")]
    Unsupported16BitTiff { path: String },
    #[error("16-bit {format} is not supported: {path}. Export an 8-bit JPEG, PNG, or TIFF first.")]
    Unsupported16Bit { path: String, format: String },
    #[error(
        "Image is too large: {path} is {width}x{height} ({:.1} MP); the limit is {:.1} MP. \
         Downscale the image or raise CCP_MAX_IMAGE_MEGAPIXELS.",
        f64::from(*width) * f64::from(*height) / 1e6,
        *max_pixels as f64 / 1e6
    )]
    ImageTooLarge {
        path: String,
        width: u32,
        height: u32,
        max_pixels: u64,
    },
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Image(#[from] image::ImageError),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

pub type Result<T> = std::result::Result<T, Error>;
