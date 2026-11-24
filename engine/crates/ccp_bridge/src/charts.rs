//! The chart faces a fit uses: handshake geometry, reference values and the
//! patch samples read from the working image.

use ccp_core::ffc::{apply_ffc_pixel, sample_gain_at};
use ccp_core::reference_profile::{
    annotate_patches, ChartReferenceAssignment, ReferenceProfile, ResolvedReferenceContext,
};
use ccp_core::refs::{chart_spec_from_name, ChartPatch, ChartSpec, Quad, Vec2};
use ccp_core::sample::{
    sample_patch_obbs_view, sample_patch_obbs_view_transform, RgbView, SamplingOptions,
};
use ndarray::{Array2, Array3};
use serde_json::Value;

use crate::error::{Error, Result};
use crate::request::{json_bool, json_f64, json_i64, json_str};
use crate::work;

/// The handshake of a `run` request: an inline object, a JSON string, or a path.
pub(crate) fn load_handshake(req: &Value) -> Result<Value> {
    if let Some(obj) = req.get("handshake").filter(|v| v.is_object()) {
        return Ok(obj.clone());
    }
    for key in ["handshake", "chart_json", "handshake_path", "chart_path"] {
        if let Some(path) = req.get(key).and_then(Value::as_str) {
            if path.is_empty() || path.starts_with('{') {
                continue;
            }
            let text = std::fs::read_to_string(path)?;
            return Ok(serde_json::from_str(&text)?);
        }
    }
    if let Some(s) = req.get("handshake").and_then(Value::as_str) {
        if s.trim_start().starts_with('{') {
            return Ok(serde_json::from_str(s)?);
        }
    }
    Err(Error::Message(
        "run requires handshake or chart_json (path or object)".into(),
    ))
}

/// One detected or manual chart face.
#[derive(Clone, Debug)]
struct Face {
    snapshot_id: String,
    chart_id: String,
    /// Bundled reference chart name.
    cli_chart: String,
    patch_obbs: Vec<Quad>,
}

/// The selected faces of one handshake with their resolved custom references.
pub(crate) struct Charts {
    faces: Vec<Face>,
    references: Option<ResolvedReferenceContext>,
}

/// The charts sampled from the input image.
pub(crate) struct Sampled {
    /// Reference of the first face; the GC/WB neutral patches come from it.
    pub(crate) spec: ChartSpec,
    pub(crate) patches: Vec<ChartPatch>,
    pub(crate) chart_type: String,
}

impl Charts {
    /// Select faces with `face`/`use_all`, shrink patch cells by `crop`, and
    /// resolve `reference_profiles`/`reference_assignments`.
    pub(crate) fn from_request(req: &Value, handshake: &Value) -> Result<Self> {
        let crop = json_f64(req, &["crop"], 0.75);
        let face = json_i64(req, &["face"], 0).max(0) as usize;
        let use_all = json_bool(req, &["use_all"], false);
        let faces = parse_handshake(handshake, face, use_all, crop)?;
        let references = resolve_references(req, &faces)?;
        Ok(Self { faces, references })
    }

    pub(crate) fn references(&self) -> Option<&ResolvedReferenceContext> {
        self.references.as_ref()
    }

    pub(crate) fn sample(&self, image: RgbView<'_>, options: SamplingOptions) -> Result<Sampled> {
        self.sample_with(|spec, obbs| sample_patch_obbs_view(&image, spec, obbs, options))
    }

    /// Patches of the working image, seen through the FFC `gain` when it has
    /// not been applied to `work` yet.
    pub(crate) fn resample(
        &self,
        work: &Array3<f32>,
        options: SamplingOptions,
        gain: Option<&Array2<f32>>,
    ) -> Result<Vec<ChartPatch>> {
        let pixels = work::samples(work);
        let width = work.shape()[1] as u32;
        let height = work.shape()[0] as u32;
        let view = RgbView::from_rgb(width, height, pixels.as_ref())?;
        let sampled = match gain {
            None => {
                self.sample_with(|spec, obbs| sample_patch_obbs_view(&view, spec, obbs, options))
            }
            Some(gain) => {
                let transform =
                    |rgb, x, y| apply_ffc_pixel(rgb, sample_gain_at(gain, x, y, width, height));
                self.sample_with(|spec, obbs| {
                    sample_patch_obbs_view_transform(&view, spec, obbs, options, &transform)
                })
            }
        };
        Ok(sampled?.patches)
    }

    fn sample_with(
        &self,
        sample: impl Fn(&ChartSpec, &[Quad]) -> ccp_core::Result<Vec<ChartPatch>>,
    ) -> Result<Sampled> {
        let mut patches = Vec::new();
        let mut first_spec = None;
        let mut chart_type = String::new();
        for face in &self.faces {
            let resolved = self
                .references
                .as_ref()
                .map(|r| r.chart(&face.chart_id))
                .transpose()?;
            let spec = match resolved {
                Some(r) => r.spec.clone(),
                None => chart_spec_from_name(&face.cli_chart)?,
            };
            if chart_type.is_empty() {
                chart_type = spec.id.clone();
            }
            let mut sampled = sample(&spec, &face.patch_obbs)?;
            annotate_patches(
                &mut sampled,
                &face.chart_id,
                &spec,
                resolved.map(|r| (r.profile_id.as_str(), r.profile_revision)),
            );
            patches.extend(sampled);
            first_spec.get_or_insert(spec);
        }
        let spec = first_spec.ok_or_else(|| Error::Message("no handshake faces".into()))?;
        Ok(Sampled {
            spec,
            patches,
            chart_type,
        })
    }
}

/// `[x, y]` or `{x, y}`.
fn parse_point(v: &Value) -> Option<Vec2> {
    match v.as_array() {
        Some(xy) if xy.len() >= 2 => Some(Vec2 {
            x: xy[0].as_f64()?,
            y: xy[1].as_f64()?,
        }),
        Some(_) => None,
        None => Some(Vec2 {
            x: v.get("x")?.as_f64()?,
            y: v.get("y")?.as_f64()?,
        }),
    }
}

/// `[tl, tr, br, bl]` or `{top_left, top_right, bottom_right, bottom_left}`.
fn parse_quad(v: &Value) -> Option<Quad> {
    let corners = match v.as_array() {
        Some(points) if points.len() >= 4 => [&points[0], &points[1], &points[2], &points[3]],
        Some(_) => return None,
        None => {
            let obj = v.as_object()?;
            [
                obj.get("top_left")?,
                obj.get("top_right")?,
                obj.get("bottom_right")?,
                obj.get("bottom_left")?,
            ]
        }
    };
    Some(Quad {
        top_left: parse_point(corners[0])?,
        top_right: parse_point(corners[1])?,
        bottom_right: parse_point(corners[2])?,
        bottom_left: parse_point(corners[3])?,
    })
}

fn parse_quads(face: &Value, name: &str) -> Vec<Quad> {
    face.get(name)
        .and_then(Value::as_array)
        .map(|arr| arr.iter().filter_map(parse_quad).collect())
        .unwrap_or_default()
}

/// Shrink a patch cell about its centre to the sampled fraction `crop`.
fn scale_quad(q: Quad, crop: f64) -> Quad {
    let k = crop.max(0.02);
    let c = Vec2 {
        x: 0.25 * (q.top_left.x + q.top_right.x + q.bottom_right.x + q.bottom_left.x),
        y: 0.25 * (q.top_left.y + q.top_right.y + q.bottom_right.y + q.bottom_left.y),
    };
    let s = |p: Vec2| Vec2 {
        x: c.x + (p.x - c.x) * k,
        y: c.y + (p.y - c.y) * k,
    };
    Quad {
        top_left: s(q.top_left),
        top_right: s(q.top_right),
        bottom_right: s(q.bottom_right),
        bottom_left: s(q.bottom_left),
    }
}

/// `patch_obbs`, or `patch_cells` shrunk by `crop`; `None` without either.
fn face_from_value(face: &Value, crop: f64) -> Option<Face> {
    let mut patch_obbs = parse_quads(face, "patch_obbs");
    if patch_obbs.is_empty() {
        patch_obbs = parse_quads(face, "patch_cells")
            .into_iter()
            .map(|q| scale_quad(q, crop))
            .collect();
    }
    if patch_obbs.is_empty() {
        return None;
    }
    Some(Face {
        snapshot_id: json_str(face, &["snapshot_id"]).unwrap_or("").into(),
        chart_id: json_str(face, &["chart_id"]).unwrap_or("").into(),
        cli_chart: json_str(face, &["cli_chart", "chart_type"])
            .unwrap_or("classic")
            .into(),
        patch_obbs,
    })
}

/// Opt2/manual handshake JSON. `face` selects `charts[i]`; `use_all` keeps every face.
fn parse_handshake(root: &Value, face: usize, use_all: bool, crop: f64) -> Result<Vec<Face>> {
    let snapshot = json_str(root, &["snapshot_id"]).unwrap_or("");
    let identify = |mut parsed: Face| {
        if parsed.snapshot_id.is_empty() {
            parsed.snapshot_id = snapshot.into();
        }
        parsed
    };
    if let Some(charts) = root
        .get("charts")
        .and_then(Value::as_array)
        .filter(|c| !c.is_empty())
    {
        if use_all {
            return charts
                .iter()
                .enumerate()
                .map(|(index, c)| {
                    face_from_value(c, crop).map(&identify).ok_or_else(|| {
                        Error::Message(format!("handshake chart {index} has no valid patch_obbs"))
                    })
                })
                .collect();
        }
        let selected = charts.get(face).ok_or_else(|| {
            Error::Message(format!(
                "handshake face {face} is out of range for {} charts",
                charts.len()
            ))
        })?;
        return face_from_value(selected, crop)
            .map(|f| vec![identify(f)])
            .ok_or_else(|| Error::Message("handshake face has no patch_obbs".into()));
    }
    face_from_value(root, crop)
        .map(|f| vec![identify(f)])
        .ok_or_else(|| Error::Message("handshake has no patch_obbs / patch_cells".into()))
}

fn resolve_references(req: &Value, faces: &[Face]) -> Result<Option<ResolvedReferenceContext>> {
    if req.get("reference_profiles").is_none() && req.get("reference_assignments").is_none() {
        return Ok(None);
    }
    let field = |name: &str| req.get(name).cloned().unwrap_or(Value::Null);
    let profiles: Vec<ReferenceProfile> = serde_json::from_value(field("reference_profiles"))?;
    let assignments: Vec<ChartReferenceAssignment> =
        serde_json::from_value(field("reference_assignments"))?;
    let snapshot = faces.first().map(|f| f.snapshot_id.as_str()).unwrap_or("");
    if faces.iter().any(|f| f.snapshot_id != snapshot) {
        return Err(Error::Message(
            "chart assignments span different detection snapshots".into(),
        ));
    }
    let charts = faces
        .iter()
        .map(|f| Ok((f.chart_id.clone(), chart_spec_from_name(&f.cli_chart)?.kind)))
        .collect::<Result<Vec<_>>>()?;
    Ok(Some(ResolvedReferenceContext::resolve(
        snapshot,
        &charts,
        profiles,
        assignments,
    )?))
}

#[cfg(test)]
impl Charts {
    /// `(snapshot_id, chart_id)` of every selected face.
    pub(crate) fn identities(&self) -> Vec<(&str, &str)> {
        self.faces
            .iter()
            .map(|f| (f.snapshot_id.as_str(), f.chart_id.as_str()))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_handshake_reads_obbs() {
        let hs = crate::test_support::classic_handshake(120, 80);
        let faces = parse_handshake(&hs, 0, false, 0.75).unwrap();
        assert_eq!(faces.len(), 1);
        assert_eq!(faces[0].patch_obbs.len(), 24);
        assert_eq!(faces[0].cli_chart, "classic");
    }
}
