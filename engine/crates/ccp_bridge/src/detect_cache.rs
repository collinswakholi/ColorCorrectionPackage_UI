//! The detection handshakes a worker keeps on disk: cached private copies keyed
//! by image and detector identity, and the default `detect` outputs it wrote
//! to the temp directory. Every one of these files is removed at shutdown.

use std::collections::{HashMap, VecDeque};
use std::path::{Path, PathBuf};

use ccp_detect::cache_key::{model_cache_identity, runtime_cache_identity};
use ccp_detect::paths::{primary_onnx_path, recall_onnx_path};
use ccp_detect::DetectSettings;
use serde_json::Value;

/// Detection cache entries (and their private temp copies) kept per worker.
const DETECT_CACHE_CAPACITY: usize = 64;
/// Worker-chosen default `detect` handshake files kept before the oldest is
/// deleted.
const TEMP_OUTPUT_CAPACITY: usize = 64;

#[derive(Debug, Default)]
pub(crate) struct DetectCache {
    entries: HashMap<String, PathBuf>,
    /// Least-recently-used order of `entries` keys (front = oldest).
    order: VecDeque<String>,
    /// Default detect handshakes this worker wrote to the temp directory.
    temp_outputs: VecDeque<PathBuf>,
}

impl DetectCache {
    /// Identity of a detection: the image file, the settings, the model files
    /// and the inference runtime.
    pub(crate) fn key(image: &Path, settings: &DetectSettings) -> String {
        let canonical = image.canonicalize().unwrap_or_else(|_| image.to_path_buf());
        let metadata = std::fs::metadata(image).ok();
        let deploy = if settings.deploy_dir.as_os_str().is_empty() {
            ccp_detect::default_deploy_dir()
        } else {
            settings.deploy_dir.clone()
        };
        let models = [primary_onnx_path(&deploy), recall_onnx_path(&deploy)]
            .iter()
            .map(|p| model_cache_identity(p))
            .collect::<Vec<_>>();
        format!(
            "{}|{:?}|{:?}|{}|{:?}|{}",
            canonical.display(),
            metadata.as_ref().map(|m| m.len()),
            metadata.and_then(|m| m.modified().ok()),
            serde_json::to_string(settings).unwrap_or_default(),
            models,
            runtime_cache_identity()
        )
    }

    /// The cached handshake file, its text and its JSON, when still readable.
    pub(crate) fn read(&self, key: &str) -> Option<(PathBuf, String, Value)> {
        let path = self.entries.get(key)?;
        if !path.is_file() {
            return None;
        }
        let text = std::fs::read_to_string(path).ok()?;
        if text.trim().is_empty() {
            return None;
        }
        let handshake = serde_json::from_str(&text).ok()?;
        Some((path.clone(), text, handshake))
    }

    /// Mark `key` most recently used.
    pub(crate) fn touch(&mut self, key: &str) {
        if let Some(index) = self.order.iter().position(|k| k == key) {
            if let Some(key) = self.order.remove(index) {
                self.order.push_back(key);
            }
        }
    }

    /// Cache the private copy at `path`, replacing any previous copy for `key`
    /// and evicting the least recently used entries over capacity.
    pub(crate) fn insert(&mut self, key: String, path: PathBuf) {
        if let Some(previous) = self.entries.insert(key.clone(), path.clone()) {
            if previous != path {
                let _ = std::fs::remove_file(previous);
            }
            self.order.retain(|k| k != &key);
        }
        self.order.push_back(key);
        while self.entries.len() > DETECT_CACHE_CAPACITY {
            let Some(oldest) = self.order.pop_front() else {
                break;
            };
            if let Some(evicted) = self.entries.remove(&oldest) {
                let _ = std::fs::remove_file(evicted);
            }
        }
    }

    /// Forget and delete every cached copy.
    pub(crate) fn clear(&mut self) {
        for (_, path) in self.entries.drain() {
            let _ = std::fs::remove_file(path);
        }
        self.order.clear();
    }

    /// Track a default handshake output, deleting the oldest over capacity.
    pub(crate) fn track_temp_output(&mut self, path: PathBuf) {
        self.temp_outputs.retain(|p| p != &path);
        self.temp_outputs.push_back(path);
        while self.temp_outputs.len() > TEMP_OUTPUT_CAPACITY {
            if let Some(oldest) = self.temp_outputs.pop_front() {
                let _ = std::fs::remove_file(oldest);
            }
        }
    }

    /// Remove every temp file this worker created. Caller-chosen
    /// `handshake_out` paths are kept.
    pub(crate) fn remove_files(&mut self) {
        self.clear();
        for path in self.temp_outputs.drain(..) {
            let _ = std::fs::remove_file(path);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detect_cache_and_temp_outputs_are_bounded_and_cleaned_up() {
        let dir = std::env::temp_dir().join(format!("ccp-serve-cache-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let mut cache = DetectCache::default();
        let mut cache_files = Vec::new();
        let mut outputs = Vec::new();
        for index in 0..(DETECT_CACHE_CAPACITY + 5) {
            let cached = dir.join(format!("cache-{index}.json"));
            std::fs::write(&cached, b"{}").unwrap();
            cache.insert(format!("key-{index}"), cached.clone());
            cache_files.push(cached);
            let output = dir.join(format!("detect-{index}.json"));
            std::fs::write(&output, b"{}").unwrap();
            cache.track_temp_output(output.clone());
            outputs.push(output);
        }
        assert_eq!(cache.entries.len(), DETECT_CACHE_CAPACITY);
        assert!(!cache.entries.contains_key("key-0"));
        assert!(!cache_files[4].exists() && cache_files[5].exists());
        assert!(!outputs[4].exists() && outputs[5].exists());
        // Re-inserting a key replaces (and removes) its previous private copy.
        let replacement = dir.join("replacement.json");
        std::fs::write(&replacement, b"{}").unwrap();
        cache.insert("key-10".into(), replacement.clone());
        assert!(!cache_files[10].exists());
        assert_eq!(cache.entries.len(), DETECT_CACHE_CAPACITY);
        cache.remove_files();
        assert!(cache.entries.is_empty());
        assert!(cache_files.iter().chain(&outputs).all(|p| !p.exists()));
        assert!(!replacement.exists());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn detect_cache_key_includes_mtime_crop_prefer() {
        let a = DetectCache::key(Path::new("x.jpg"), &DetectSettings::default());
        let b = DetectCache::key(
            Path::new("x.jpg"),
            &DetectSettings {
                crop: 0.5,
                prefer: "spyder".into(),
                ..DetectSettings::default()
            },
        );
        assert_ne!(a, b);
        for settings in [
            DetectSettings {
                conf: 0.91,
                ..DetectSettings::default()
            },
            DetectSettings {
                allow_recall: false,
                ..DetectSettings::default()
            },
            DetectSettings {
                device: "directml".into(),
                ..DetectSettings::default()
            },
        ] {
            assert_ne!(a, DetectCache::key(Path::new("x.jpg"), &settings));
        }
    }
}
