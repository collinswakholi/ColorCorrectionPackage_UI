//! Locate the CCP_New_UI root (`models/opt2` + `refs/neutrals.json`) and Opt2 deploy dir.

use std::env;
use std::path::{Path, PathBuf};

pub const PRIMARY_ONNX: &str = "best_assemble_fp32_1280.onnx";
pub const RECALL_ONNX: &str = "best_assemble_fp32_1536.onnx";
pub const PLANE_ONNX: &str = "plane_det_model_YOLO_512_n.onnx";

pub fn looks_like_ccp_root(path: &Path) -> bool {
    path.join("models").join("opt2").is_dir() && path.join("refs").join("neutrals.json").is_file()
}

fn walk_parents(start: &Path) -> Option<PathBuf> {
    let mut dir = Some(start);
    while let Some(cur) = dir {
        if looks_like_ccp_root(cur) {
            return Some(cur.to_path_buf());
        }
        dir = cur.parent();
    }
    None
}

/// An explicit resource root is authoritative, including when incomplete.
/// Otherwise prefer the executable's resources before development discovery.
pub fn find_ccp_new_ui_root() -> Option<PathBuf> {
    if let Ok(root) = env::var("CCP_NEW_UI_ROOT") {
        let p = PathBuf::from(root);
        return looks_like_ccp_root(&p).then_some(p);
    }
    if let Ok(cwd) = env::current_dir() {
        if let Some(found) = walk_parents(&cwd) {
            return Some(found);
        }
    }
    if let Ok(exe) = env::current_exe() {
        if let Some(parent) = exe.parent() {
            if let Some(found) = walk_parents(parent) {
                return Some(found);
            }
        }
    }
    if let Ok(manifest) = env::var("CARGO_MANIFEST_DIR") {
        if let Some(found) = walk_parents(Path::new(&manifest)) {
            return Some(found);
        }
    }
    None
}

pub fn opt2_candidates(root: &Path) -> Vec<PathBuf> {
    vec![
        root.join("models").join("opt2").join("deploy_exports"),
        root.join("models").join("opt2"),
    ]
}

pub fn deploy_dir_from_root(root: &Path) -> PathBuf {
    for dir in opt2_candidates(root) {
        if dir.join(PRIMARY_ONNX).is_file() {
            return dir;
        }
    }
    opt2_candidates(root)
        .into_iter()
        .next()
        .unwrap_or_else(|| root.join("models").join("opt2"))
}

pub fn default_deploy_dir() -> PathBuf {
    if let Ok(root) = env::var("CCP_NEW_UI_ROOT") {
        return deploy_dir_from_root(Path::new(&root));
    }
    if let Some(root) = find_ccp_new_ui_root() {
        return deploy_dir_from_root(&root);
    }
    if let Ok(cwd) = env::current_dir() {
        for rel in [
            PathBuf::from("models").join("opt2").join("deploy_exports"),
            PathBuf::from("models").join("opt2"),
            PathBuf::from("chart_detection")
                .join("models")
                .join("opt2")
                .join("deploy_exports"),
        ] {
            let cand = cwd.join(&rel);
            if cand.join(PRIMARY_ONNX).is_file() {
                return cand;
            }
        }
    }
    PathBuf::from("models/opt2")
}

pub fn primary_onnx_path(deploy_dir: &Path) -> PathBuf {
    if deploy_dir.join(PRIMARY_ONNX).is_file() {
        return deploy_dir.join(PRIMARY_ONNX);
    }
    deploy_dir.join(PRIMARY_ONNX)
}

pub fn recall_onnx_path(deploy_dir: &Path) -> PathBuf {
    deploy_dir.join(RECALL_ONNX)
}

pub fn opt2_onnx_candidates(root: &Path) -> Vec<PathBuf> {
    vec![
        root.join("models").join("opt2"),
        root.join("models").join("opt2").join("deploy_exports"),
    ]
}

pub fn resolve_opt2_onnx_dir() -> Option<PathBuf> {
    if let Some(root) = find_ccp_new_ui_root() {
        for dir in opt2_onnx_candidates(&root) {
            if dir.join(PRIMARY_ONNX).is_file() {
                return Some(dir);
            }
        }
    }
    let deploy = default_deploy_dir();
    if deploy.join(PRIMARY_ONNX).is_file() {
        return Some(deploy);
    }
    None
}

pub fn plane_onnx_path() -> Option<PathBuf> {
    if let Ok(p) = env::var("CCP_PLANE_ONNX") {
        return Some(PathBuf::from(p));
    }
    let root = find_ccp_new_ui_root()?;
    [
        root.join("models").join(PLANE_ONNX),
        root.join("models").join("opt2").join(PLANE_ONNX),
        root.join("models").join("plane").join(PLANE_ONNX),
    ]
    .into_iter()
    .find(|candidate| candidate.is_file())
}
