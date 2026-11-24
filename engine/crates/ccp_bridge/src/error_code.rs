//! Stable machine-readable `code` values for protocol-3 `ok:false` replies.
//!
//! The human-readable `error` string is unchanged from protocol 2; clients
//! should branch on `code` and display `error`.

use crate::run_pipeline::Error;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ErrorCode {
    /// Malformed, missing or out-of-range request fields (also invalid JSON/UTF-8).
    InvalidRequest,
    /// Unknown or removed command.
    UnsupportedCommand,
    /// The worker build or runtime lacks a requested capability.
    UnsupportedCapability,
    /// A saved model is unreadable as a model or fails validation.
    InvalidModel,
    /// An input image exceeds the decode pixel cap.
    ImageTooLarge,
    /// An input image cannot be decoded or uses an unsupported encoding.
    ImageDecodeFailed,
    /// Detection ran but found no usable chart.
    NoChartDetected,
    /// Chart, plane or warmup inference could not run.
    InferenceFailed,
    /// Fitting or correction failed on otherwise valid input.
    ProcessingFailed,
    /// A file could not be read, created or written.
    IoError,
    /// An unexpected internal failure (including a caught panic).
    InternalError,
}

impl ErrorCode {
    pub const ALL: [ErrorCode; 11] = [
        Self::InvalidRequest,
        Self::UnsupportedCommand,
        Self::UnsupportedCapability,
        Self::InvalidModel,
        Self::ImageTooLarge,
        Self::ImageDecodeFailed,
        Self::NoChartDetected,
        Self::InferenceFailed,
        Self::ProcessingFailed,
        Self::IoError,
        Self::InternalError,
    ];

    pub fn as_str(self) -> &'static str {
        match self {
            Self::InvalidRequest => "invalid_request",
            Self::UnsupportedCommand => "unsupported_command",
            Self::UnsupportedCapability => "unsupported_capability",
            Self::InvalidModel => "invalid_model",
            Self::ImageTooLarge => "image_too_large",
            Self::ImageDecodeFailed => "image_decode_failed",
            Self::NoChartDetected => "no_chart_detected",
            Self::InferenceFailed => "inference_failed",
            Self::ProcessingFailed => "processing_failed",
            Self::IoError => "io_error",
            Self::InternalError => "internal_error",
        }
    }

    /// Parse a code string (for example one set by `ccp_detect`).
    pub fn parse(value: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|code| code.as_str() == value)
    }
}

impl std::fmt::Display for ErrorCode {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

pub fn classify(error: &Error) -> ErrorCode {
    match error {
        Error::Coded { code, .. } => *code,
        Error::Message(_) => ErrorCode::InvalidRequest,
        Error::Core(core) => classify_core(core),
        Error::Io(_) => ErrorCode::IoError,
        Error::Json(_) => ErrorCode::InvalidRequest,
        Error::Image(image) => classify_image(image),
    }
}

pub fn classify_core(error: &ccp_core::Error) -> ErrorCode {
    use ccp_core::Error as Core;
    match error {
        Core::InvalidArgument(_) => ErrorCode::InvalidRequest,
        Core::InvalidModel(_) => ErrorCode::InvalidModel,
        Core::Runtime(_) => ErrorCode::ProcessingFailed,
        Core::UnsupportedRaw { .. }
        | Core::Unsupported16BitTiff { .. }
        | Core::Unsupported16Bit { .. } => ErrorCode::ImageDecodeFailed,
        Core::ImageTooLarge { .. } => ErrorCode::ImageTooLarge,
        Core::Io(_) => ErrorCode::IoError,
        Core::Image(image) => classify_image(image),
        Core::Json(_) => ErrorCode::InvalidRequest,
    }
}

pub fn classify_image(error: &image::ImageError) -> ErrorCode {
    use image::ImageError;
    match error {
        ImageError::Decoding(_) | ImageError::Unsupported(_) => ErrorCode::ImageDecodeFailed,
        ImageError::Limits(_) => ErrorCode::ImageTooLarge,
        ImageError::IoError(_) => ErrorCode::IoError,
        ImageError::Encoding(_) | ImageError::Parameter(_) => ErrorCode::ProcessingFailed,
    }
}

/// Classify an error from loading an input image: anything that is not an
/// I/O failure or the pixel cap means the file could not be decoded.
pub fn classify_image_load(error: &ccp_core::Error) -> ErrorCode {
    match classify_core(error) {
        code @ (ErrorCode::IoError | ErrorCode::ImageTooLarge) => code,
        _ => ErrorCode::ImageDecodeFailed,
    }
}
