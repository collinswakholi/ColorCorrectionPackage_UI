//! `ccp-engine` — long-lived NDJSON worker for ColorCorrect Studio.

fn main() {
    // PATH / LIBTORCH / LIBTORCH_PATH must be set before any tch load.
    ccp_bridge::serve::prepare_libtorch_env();
    if let Err(e) = ccp_bridge::serve::run() {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
