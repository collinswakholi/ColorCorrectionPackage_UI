//! The engine's error type and the stable protocol-3 `code` each error maps to.
//!
//! The human-readable `error` string is unchanged from protocol 2; clients
//! should branch on `code` and display `error`.

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
    const ALL: [ErrorCode; 11] = [
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

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    Message(String),
    /// A message with an explicit protocol error code.
    #[error("{message}")]
    Coded { code: ErrorCode, message: String },
    #[error(transparent)]
    Core(#[from] ccp_core::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    #[error(transparent)]
    Image(#[from] image::ImageError),
}

pub type Result<T> = std::result::Result<T, Error>;

impl Error {
    pub fn coded(code: ErrorCode, message: impl Into<String>) -> Self {
        Self::Coded {
            code,
            message: message.into(),
        }
    }

    /// An image load failure, described by `message`: anything that is not an
    /// I/O failure or the pixel cap means the file could not be decoded.
    pub fn image_load(error: &ccp_core::Error, message: impl Into<String>) -> Self {
        let code = match core_code(error) {
            code @ (ErrorCode::IoError | ErrorCode::ImageTooLarge) => code,
            _ => ErrorCode::ImageDecodeFailed,
        };
        Self::coded(code, message)
    }

    /// Stable protocol-3 code for this error.
    pub fn code(&self) -> ErrorCode {
        match self {
            Self::Coded { code, .. } => *code,
            Self::Message(_) | Self::Json(_) => ErrorCode::InvalidRequest,
            Self::Core(core) => core_code(core),
            Self::Io(_) => ErrorCode::IoError,
            Self::Image(image) => image_code(image),
        }
    }
}

fn core_code(error: &ccp_core::Error) -> ErrorCode {
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
        Core::Image(image) => image_code(image),
        Core::Json(_) => ErrorCode::InvalidRequest,
    }
}

fn image_code(error: &image::ImageError) -> ErrorCode {
    use image::ImageError;
    match error {
        ImageError::Decoding(_) | ImageError::Unsupported(_) => ErrorCode::ImageDecodeFailed,
        ImageError::Limits(_) => ErrorCode::ImageTooLarge,
        ImageError::IoError(_) => ErrorCode::IoError,
        ImageError::Encoding(_) | ImageError::Parameter(_) => ErrorCode::ProcessingFailed,
    }
}
