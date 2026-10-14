//! Locate the CCP_New_UI root (`models/opt2` + `refs/neutrals.json`) and Opt2 deploy dir.

use std::env;
use std::path::{Path, PathBuf};

pub const PRIMARY_GRAPH: &str = "best_assemble_fp16_1280.torchscript";
pub const RECALL_GRAPH: &str = "best_assemble_fp16_1536.torchscript";
pub const PRIMARY_ONNX: &str = "best_assemble_fp32_1280.onnx";
pub const RECALL_ONNX: &str = "best_assemble_fp32_1536.onnx";
pub const PLANE_ONNX: &str = "plane_det_model_YOLO_512_n.onnx";
pub const LUT_ONNX: &str = "lut3d_trilinear.onnx";
pub const LIBTORCH_JSON: &str = "libtorch.json";
pub const REQUIRED_LIBTORCH_MAJOR_MINOR: (u32, u32) = (2, 9);

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

/// Find `CCP_New_UI` by walking cwd, the executable, and `CARGO_MANIFEST_DIR`.
pub fn find_ccp_new_ui_root() -> Option<PathBuf> {
    if let Ok(root) = env::var("CCP_NEW_UI_ROOT") {
        let p = PathBuf::from(root);
        if looks_like_ccp_root(&p) {
            return Some(p);
        }
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
        if dir.join(PRIMARY_GRAPH).is_file() {
            return dir;
        }
    }
    opt2_candidates(root)
        .into_iter()
        .next()
        .unwrap_or_else(|| root.join("models").join("opt2"))
}

pub fn default_deploy_dir() -> PathBuf {
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
            if cand.join(PRIMARY_GRAPH).is_file() {
                return cand;
            }
        }
    }
    PathBuf::from("models/opt2")
}

pub fn primary_graph_path(deploy_dir: &Path) -> PathBuf {
    deploy_dir.join(PRIMARY_GRAPH)
}

pub fn recall_graph_path(deploy_dir: &Path) -> PathBuf {
    deploy_dir.join(RECALL_GRAPH)
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

pub fn lut_onnx_path() -> Option<PathBuf> {
    if let Ok(p) = env::var("CCP_LUT_ONNX") {
        let path = PathBuf::from(p);
        if path.is_file() {
            return Some(path);
        }
    }
    let root = find_ccp_new_ui_root()?;
    [
        root.join("models").join("apply").join(LUT_ONNX),
        root.join("models").join(LUT_ONNX),
    ]
    .into_iter()
    .find(|candidate| candidate.is_file())
}

pub fn plane_onnx_path() -> Option<PathBuf> {
    if let Ok(p) = env::var("CCP_PLANE_ONNX") {
        let path = PathBuf::from(p);
        if path.is_file() {
            return Some(path);
        }
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

pub fn default_libtorch_root() -> PathBuf {
    if let Ok(p) = env::var("CCP_LIBTORCH_ROOT").or_else(|_| env::var("LIBTORCH_PATH")) {
        return PathBuf::from(p);
    }
    #[cfg(windows)]
    {
        if let Ok(base) = env::var("LOCALAPPDATA") {
            return PathBuf::from(base).join("CCP-New").join("libtorch");
        }
    }
    #[cfg(target_os = "macos")]
    {
        if let Ok(home) = env::var("HOME") {
            return PathBuf::from(home)
                .join("Library")
                .join("Application Support")
                .join("CCP-New")
                .join("libtorch");
        }
    }
    #[cfg(target_os = "linux")]
    {
        if let Ok(xdg) = env::var("XDG_DATA_HOME") {
            if !xdg.is_empty() {
                return PathBuf::from(xdg).join("CCP-New").join("libtorch");
            }
        }
        if let Ok(home) = env::var("HOME") {
            return PathBuf::from(home)
                .join(".local")
                .join("share")
                .join("CCP-New")
                .join("libtorch");
        }
    }
    PathBuf::from("libtorch")
}

#[derive(Debug, Clone)]
pub struct LibTorchJson {
    pub kind: String,
    pub version: String,
}

pub fn parse_libtorch_major_minor(version: &str) -> Option<(u32, u32)> {
    let mut nums = version
        .split(|c: char| !c.is_ascii_digit())
        .filter(|s| !s.is_empty());
    let major = nums.next()?.parse().ok()?;
    let minor = nums.next()?.parse().ok()?;
    Some((major, minor))
}

pub fn libtorch_version_matches_29(version: &str) -> bool {
    parse_libtorch_major_minor(version) == Some(REQUIRED_LIBTORCH_MAJOR_MINOR)
}

pub fn read_libtorch_json(root: &Path) -> Option<LibTorchJson> {
    let path = root.join(LIBTORCH_JSON);
    let text = std::fs::read_to_string(path).ok()?;
    let text = text.trim_start_matches('\u{feff}');
    let v: serde_json::Value = serde_json::from_str(text).ok()?;
    Some(LibTorchJson {
        kind: v.get("kind")?.as_str()?.to_string(),
        version: v.get("version")?.as_str()?.to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_libtorch_root_is_not_empty() {
        assert!(!default_libtorch_root().as_os_str().is_empty());
    }
}
