//! `ccp-engine`, the ColorCorrect Studio worker: chart fitting and model
//! application behind the NDJSON protocol in `docs/api/protocol.md`.
//!
//! Layers, lowest first; each module uses only modules above it in this list:
//! `error`, `request`, `work`, `output`, `render`, `ffc`, `model`, `charts`,
//! `report`, then the commands `fit` and `apply`, then `detect_cache` and the
//! `serve` loop.

mod apply;
mod charts;
mod detect_cache;
mod error;
mod ffc;
mod fit;
mod model;
mod output;
mod render;
mod report;
mod request;
mod serve;
#[cfg(test)]
mod test_support;
mod work;

pub use serve::run;
