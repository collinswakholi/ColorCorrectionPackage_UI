//! Versioned user reference values. Conversion is performed by the engine once,
//! before sampling, and canonical Lab values are never display-gamut clipped.

use crate::color::{
    adapt_xyz_white, encoded_to_linear_rgb, lab_to_xyz, linear_rgb_to_xyz, parse_rgb_color_space,
    parse_white_point, xyz_to_lab, Lab,
};
use crate::refs::{chart_spec, ChartKind, ChartPatch, ChartSpec};
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ReferenceProfile {
    pub version: u32,
    pub id: String,
    pub name: String,
    pub revision: u32,
    pub chart: ChartKind,
    pub color_space: String,
    pub white_point: String,
    pub scale: f64,
    pub order: String,
    pub values: Vec<[f64; 3]>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ChartReferenceAssignment {
    pub chart_id: String,
    pub profile_id: String,
    pub profile_revision: u32,
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct PatchProvenance {
    #[serde(default)]
    pub custom_reference: bool,
    pub chart_id: String,
    pub template_id: String,
    pub cell_id: String,
    pub profile_id: String,
    pub profile_revision: u32,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ResolvedChartReference {
    pub chart_id: String,
    pub profile_id: String,
    pub profile_revision: u32,
    pub spec: ChartSpec,
}

/// Immutable snapshot persisted with the fitted model. It records both input
/// interpretation and the derived canonical values used in this calibration.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ResolvedReferenceContext {
    pub version: u32,
    pub snapshot_id: String,
    pub reference_profiles: Vec<ReferenceProfile>,
    pub reference_assignments: Vec<ChartReferenceAssignment>,
    pub charts: Vec<ResolvedChartReference>,
}

fn invalid(message: impl Into<String>) -> Error {
    Error::InvalidArgument(message.into())
}

impl ReferenceProfile {
    pub fn resolve(&self) -> Result<ChartSpec> {
        if self.version != 1 {
            return Err(invalid("unsupported reference profile version; expected 1"));
        }
        if self.id.trim().is_empty() || self.name.trim().is_empty() || self.revision == 0 {
            return Err(invalid(
                "reference profile needs a nonempty id/name and positive revision",
            ));
        }
        let mut spec = chart_spec(self.chart)?;
        if self.values.len() != spec.patch_count() {
            return Err(invalid(format!(
                "{} needs exactly {} reference cells, received {}",
                spec.id,
                spec.patch_count(),
                self.values.len()
            )));
        }
        if self.values.iter().flatten().any(|v| !v.is_finite()) {
            return Err(invalid("reference values must be finite"));
        }
        if !matches!(self.order.as_str(), "row-major" | "column-major") {
            return Err(invalid("reference order must be row-major or column-major"));
        }
        let white = parse_white_point(&self.white_point)?;
        let d50 = parse_white_point("d50")?;
        let space = self.color_space.to_ascii_lowercase();
        let rgb_space = match space.as_str() {
            "lab" => {
                if self.scale != 1.0 {
                    return Err(invalid("Lab scale must be 1 (L* uses 0–100)"));
                }
                None
            }
            "xyz" => {
                if !matches!(self.scale, 1.0 | 100.0) {
                    return Err(invalid("XYZ scale must be 1 or 100"));
                }
                None
            }
            _ => {
                if !matches!(self.scale, 1.0 | 255.0 | 65535.0) {
                    return Err(invalid("RGB scale must be 1, 255 or 65535"));
                }
                Some(parse_rgb_color_space(&space, &white)?)
            }
        };
        let mut labs = Vec::with_capacity(spec.patch_count());
        for index in 0..spec.patch_count() {
            let source = if self.order == "column-major" {
                (index % spec.columns) * spec.rows + index / spec.columns
            } else {
                index
            };
            let value = self.values[source];
            let xyz = match space.as_str() {
                "lab" => {
                    if !(0.0..=100.0).contains(&value[0]) {
                        return Err(invalid("Lab L* must be in 0–100"));
                    }
                    lab_to_xyz(Lab::new(value[0], value[1], value[2]), &white)
                }
                "xyz" => {
                    if value.iter().any(|v| *v < 0.0) {
                        return Err(invalid("XYZ values must be nonnegative"));
                    }
                    value.map(|v| v / self.scale)
                }
                _ => {
                    if value.iter().any(|v| *v < 0.0 || *v > self.scale) {
                        return Err(invalid("RGB values are outside the declared scale"));
                    }
                    let rgb = rgb_space.as_ref().expect("validated RGB space");
                    linear_rgb_to_xyz(
                        encoded_to_linear_rgb(value.map(|v| v / self.scale), rgb),
                        rgb,
                    )
                }
            };
            let lab = xyz_to_lab(adapt_xyz_white(xyz, &white, &d50)?, &d50);
            if ![lab.l, lab.a, lab.b].iter().all(|v| v.is_finite()) {
                return Err(invalid("reference conversion produced nonfinite Lab"));
            }
            labs.push(lab);
        }
        spec.reference_srgb = labs
            .iter()
            .copied()
            .map(crate::lab_d50_to_srgb)
            .collect::<Result<_>>()?;
        spec.reference_lab_d50 = labs;
        Ok(spec)
    }
}

impl ResolvedReferenceContext {
    pub fn resolve(
        snapshot_id: &str,
        charts: &[(String, ChartKind)],
        profiles: Vec<ReferenceProfile>,
        assignments: Vec<ChartReferenceAssignment>,
    ) -> Result<Self> {
        if snapshot_id.trim().is_empty() {
            return Err(invalid(
                "custom references require an identified detection snapshot",
            ));
        }
        if profiles.is_empty() || assignments.is_empty() {
            return Err(invalid("custom references require profiles and an explicit assignment for every selected chart"));
        }
        let mut profile_ids = HashSet::new();
        let mut specs = Vec::new();
        for p in &profiles {
            if !profile_ids.insert((&p.id, p.revision)) {
                return Err(invalid("duplicate reference profile id/revision"));
            }
            specs.push(p.resolve()?);
        }
        let mut assigned_ids = HashSet::new();
        for a in &assignments {
            if a.chart_id.trim().is_empty() || !assigned_ids.insert(&a.chart_id) {
                return Err(invalid("duplicate or empty chart reference assignment"));
            }
            if !profiles
                .iter()
                .any(|p| p.id == a.profile_id && p.revision == a.profile_revision)
            {
                return Err(invalid("assignment references an absent profile revision"));
            }
        }
        let mut chart_ids = HashSet::new();
        let mut resolved = Vec::new();
        for (chart_id, kind) in charts {
            if chart_id.trim().is_empty() || !chart_ids.insert(chart_id) {
                return Err(invalid("custom references require unique chart identities"));
            }
            let a = assignments
                .iter()
                .find(|a| a.chart_id == *chart_id)
                .ok_or_else(|| {
                    invalid(format!(
                        "selected chart {chart_id} has no reference assignment"
                    ))
                })?;
            let i = profiles
                .iter()
                .position(|p| p.id == a.profile_id && p.revision == a.profile_revision)
                .expect("validated assignment");
            let profile_kind = profiles[i].chart;
            if profile_kind != *kind
                && !matches!(
                    (profile_kind, kind),
                    (ChartKind::Passport, ChartKind::Classic)
                        | (ChartKind::Classic, ChartKind::Passport)
                )
            {
                return Err(invalid(format!(
                    "profile {} is incompatible with chart {}",
                    profiles[i].id, chart_id
                )));
            }
            resolved.push(ResolvedChartReference {
                chart_id: chart_id.clone(),
                profile_id: a.profile_id.clone(),
                profile_revision: a.profile_revision,
                spec: specs[i].clone(),
            });
        }
        Ok(Self {
            version: 1,
            snapshot_id: snapshot_id.into(),
            reference_profiles: profiles,
            reference_assignments: assignments,
            charts: resolved,
        })
    }

    pub fn chart(&self, chart_id: &str) -> Result<&ResolvedChartReference> {
        self.charts
            .iter()
            .find(|c| c.chart_id == chart_id)
            .ok_or_else(|| {
                invalid(format!(
                    "chart {chart_id} is not in the resolved reference snapshot"
                ))
            })
    }
}

pub fn annotate_patches(
    patches: &mut [ChartPatch],
    chart_id: &str,
    spec: &ChartSpec,
    profile: Option<(&str, u32)>,
) {
    let (profile_id, revision) = profile
        .map(|(id, r)| (id.to_owned(), r))
        .unwrap_or_else(|| (format!("builtin:{}", spec.id), 1));
    for patch in patches {
        patch.provenance = Some(PatchProvenance {
            custom_reference: profile.is_some(),
            chart_id: chart_id.into(),
            template_id: spec.id.clone(),
            cell_id: format!(
                "r{}c{}",
                patch.index / spec.columns + 1,
                patch.index % spec.columns + 1
            ),
            profile_id: profile_id.clone(),
            profile_revision: revision,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn profile() -> ReferenceProfile {
        ReferenceProfile {
            version: 1,
            id: "measured-card".into(),
            name: "Measured card".into(),
            revision: 1,
            chart: ChartKind::Classic,
            color_space: "lab".into(),
            white_point: "d50".into(),
            scale: 1.0,
            order: "row-major".into(),
            values: (0..24)
                .map(|i| [20.0 + i as f64 * 2.0, 3.0, -2.0])
                .collect(),
        }
    }

    #[test]
    fn row_and_column_major_have_identical_canonical_cells() {
        for kind in [
            ChartKind::Classic,
            ChartKind::Passport,
            ChartKind::Spyder,
            ChartKind::DigitalSg,
        ] {
            let spec = chart_spec(kind).unwrap();
            let mut row = profile();
            row.chart = kind;
            row.values = (0..spec.patch_count())
                .map(|i| [10.0 + i as f64 * 0.5, i as f64 * 0.1, -2.0])
                .collect();
            let mut column = row.clone();
            column.order = "column-major".into();
            column.values = (0..spec.columns)
                .flat_map(|c| (0..spec.rows).map(move |r| r * spec.columns + c))
                .map(|i| row.values[i])
                .collect();
            assert_eq!(row.resolve().unwrap(), column.resolve().unwrap());
        }
    }

    #[test]
    fn lab_xyz_and_rgb_produce_equivalent_lab_without_display_clipping() {
        let white = parse_white_point("d65").unwrap();
        let srgb = parse_rgb_color_space("srgb", &white).unwrap();
        let rgb = [0.22, 0.47, 0.68];
        let xyz = linear_rgb_to_xyz(encoded_to_linear_rgb(rgb, &srgb), &srgb);
        let lab = xyz_to_lab(xyz, &white);
        let mut lab_profile = profile();
        lab_profile.white_point = "d65".into();
        lab_profile.values = vec![[lab.l, lab.a, lab.b]; 24];
        let expected = lab_profile.resolve().unwrap();
        for (space, scale, value) in [
            ("xyz", 1.0, xyz),
            ("xyz", 100.0, xyz.map(|v| v * 100.0)),
            ("srgb", 1.0, rgb),
            ("srgb", 255.0, rgb.map(|v| v * 255.0)),
            ("srgb", 65535.0, rgb.map(|v| v * 65535.0)),
        ] {
            let mut other = lab_profile.clone();
            other.color_space = space.into();
            other.scale = scale;
            other.values = vec![value; 24];
            let actual = other.resolve().unwrap().reference_lab_d50[0];
            let wanted = expected.reference_lab_d50[0];
            assert!(
                (actual.l - wanted.l).abs() < 1e-9
                    && (actual.a - wanted.a).abs() < 1e-9
                    && (actual.b - wanted.b).abs() < 1e-9,
                "{space}/{scale}"
            );
        }
        let mut out_of_gamut = profile();
        out_of_gamut.values[0] = [50.0, 180.0, -140.0];
        let resolved = out_of_gamut.resolve().unwrap();
        assert!((resolved.reference_lab_d50[0].a - 180.0).abs() < 1e-8);
        assert!((resolved.reference_lab_d50[0].b + 140.0).abs() < 1e-8);
    }

    #[test]
    fn context_preserves_different_same_type_profiles_and_passport_compatibility() {
        let first = profile();
        let mut second = first.clone();
        second.id = "second".into();
        second.chart = ChartKind::Passport;
        second.values[0][0] += 5.0;
        let assignments = vec![
            ChartReferenceAssignment {
                chart_id: "snapshot:a".into(),
                profile_id: first.id.clone(),
                profile_revision: 1,
            },
            ChartReferenceAssignment {
                chart_id: "snapshot:b".into(),
                profile_id: second.id.clone(),
                profile_revision: 1,
            },
        ];
        let ctx = ResolvedReferenceContext::resolve(
            "snapshot",
            &[
                ("snapshot:a".into(), ChartKind::Classic),
                ("snapshot:b".into(), ChartKind::Classic),
            ],
            vec![first, second],
            assignments,
        )
        .unwrap();
        assert_eq!(ctx.charts[1].spec.kind, ChartKind::Passport);
        assert!(
            (ctx.charts[1].spec.reference_lab_d50[0].l
                - ctx.charts[0].spec.reference_lab_d50[0].l
                - 5.0)
                .abs()
                < 1e-9
        );
        let roundtrip: ResolvedReferenceContext =
            serde_json::from_str(&serde_json::to_string(&ctx).unwrap()).unwrap();
        assert_eq!(roundtrip.reference_profiles, ctx.reference_profiles);
        assert_eq!(roundtrip.reference_assignments, ctx.reference_assignments);
        for (a, b) in roundtrip.charts.iter().zip(&ctx.charts) {
            assert_eq!(a.chart_id, b.chart_id);
            for (la, lb) in a
                .spec
                .reference_lab_d50
                .iter()
                .zip(&b.spec.reference_lab_d50)
            {
                assert!(
                    (la.l - lb.l).abs() < 1e-12
                        && (la.a - lb.a).abs() < 1e-12
                        && (la.b - lb.b).abs() < 1e-12
                );
            }
        }
    }

    #[test]
    fn invalid_and_stale_profiles_fail_before_sampling() {
        let good = profile();
        let mut unknown = serde_json::to_value(&good).unwrap();
        unknown["observer"] = serde_json::json!(10);
        assert!(serde_json::from_value::<ReferenceProfile>(unknown).is_err());
        for bad in [
            ReferenceProfile {
                version: 2,
                ..good.clone()
            },
            ReferenceProfile {
                scale: 100.0,
                ..good.clone()
            },
            ReferenceProfile {
                values: vec![[0.0; 3]; 23],
                ..good.clone()
            },
            ReferenceProfile {
                order: "guess".into(),
                ..good.clone()
            },
            ReferenceProfile {
                values: vec![[f64::NAN; 3]; 24],
                ..good.clone()
            },
        ] {
            assert!(bad.resolve().is_err());
        }
        let assignment = ChartReferenceAssignment {
            chart_id: "s:a".into(),
            profile_id: good.id.clone(),
            profile_revision: 1,
        };
        assert!(ResolvedReferenceContext::resolve(
            "",
            &[("s:a".into(), ChartKind::Classic)],
            vec![good.clone()],
            vec![assignment.clone()]
        )
        .is_err());
        assert!(ResolvedReferenceContext::resolve(
            "s",
            &[("new:a".into(), ChartKind::Classic)],
            vec![good.clone()],
            vec![assignment.clone()]
        )
        .is_err());
        assert!(ResolvedReferenceContext::resolve(
            "s",
            &[("s:a".into(), ChartKind::Spyder)],
            vec![good.clone()],
            vec![assignment.clone()]
        )
        .is_err());
        assert!(ResolvedReferenceContext::resolve(
            "s",
            &[("s:a".into(), ChartKind::Classic)],
            vec![good],
            vec![assignment.clone(), assignment]
        )
        .is_err());
    }
}
