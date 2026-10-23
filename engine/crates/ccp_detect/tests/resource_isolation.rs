//! Explicit package resources must never borrow models from a development cwd.
use ccp_detect::paths::{
    default_deploy_dir, find_ccp_new_ui_root, plane_onnx_path, resolve_opt2_onnx_dir,
};
use std::{path::PathBuf, process::Command};

#[test]
fn explicit_missing_resources_are_authoritative() {
    const CHILD: &str = "CCP_RESOURCE_ISOLATION_CHILD";
    if std::env::var_os(CHILD).is_some() {
        let root = PathBuf::from(std::env::var_os("CCP_NEW_UI_ROOT").unwrap());
        assert!(find_ccp_new_ui_root().is_none());
        assert!(default_deploy_dir().starts_with(&root));
        assert!(resolve_opt2_onnx_dir().is_none());
        assert_eq!(plane_onnx_path(), Some(root.join("missing-plane.onnx")));
        return;
    }
    let root = std::env::temp_dir().join(format!("ccp-missing-package-{}", std::process::id()));
    assert!(!root.exists());
    let status = Command::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "explicit_missing_resources_are_authoritative",
            "--nocapture",
        ])
        .env(CHILD, "1")
        .env("CCP_NEW_UI_ROOT", &root)
        .env("CCP_PLANE_ONNX", root.join("missing-plane.onnx"))
        .current_dir(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.."))
        .status()
        .unwrap();
    assert!(status.success());
}
