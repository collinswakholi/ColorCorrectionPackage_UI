//! `ccp-engine` — long-lived NDJSON worker for ColorCorrect Studio.

fn main() {
    let result = ccp_bridge::serve::run();
    let cleanup = ccp_detect::shutdown_inference();
    if let Err(e) = result.and(cleanup) {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
