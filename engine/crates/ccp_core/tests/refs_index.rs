use ccp_core::honest::mean_delta_e00;
use ccp_core::refs::{
    chart_spec, chart_spec_from_name, load_neutrals_table, lookup_neutrals, neutrals_json_path,
    ChartKind,
};
use ccp_core::{delta_e_2000, Lab};

#[test]
fn spyder_names_and_white() {
    let spec = chart_spec(ChartKind::Spyder).unwrap();
    assert_eq!(spec.names[0], "D1");
    assert_eq!(spec.names[18], "A1");
    assert!((spec.reference_lab_d50[18].l - 96.04).abs() < 1e-6);
}

#[test]
fn classic_names_white() {
    let spec = chart_spec(ChartKind::Classic).unwrap();
    assert_eq!(spec.names[0], "dark skin");
    assert_eq!(spec.names[18], "white");
    assert!((spec.reference_lab_d50[18].l - 95.19).abs() < 1e-6);
}

#[test]
fn digital_sg_e5_e6() {
    let spec = chart_spec_from_name("digital-sg").unwrap();
    assert_eq!(spec.names.len(), 140);
    assert_eq!(spec.names[60], "E5");
    assert_eq!(spec.names[74], "E6");
    assert!((spec.reference_lab_d50[60].l - 96.78).abs() < 1e-6);
    assert!((spec.reference_lab_d50[74].l - 8.07).abs() < 1e-6);
}

#[test]
fn neutrals_json_lookup() {
    let table = load_neutrals_table(Some(&neutrals_json_path())).unwrap();
    let spyder = chart_spec(ChartKind::Spyder).unwrap();
    let classic = chart_spec(ChartKind::Classic).unwrap();
    let spyder_n = lookup_neutrals(&spyder, Some(&table)).unwrap();
    let classic_n = lookup_neutrals(&classic, Some(&table)).unwrap();
    assert_eq!(spyder_n[0].1, "A1");
    assert_eq!(classic_n[0].1, "white");
    assert!((spyder_n[0].2.l - classic_n[0].2.l).abs() > 0.5);
    assert_eq!(
        table.get("digital-sg").unwrap(),
        &vec!["E5", "F5", "G5", "H5", "I5", "J5", "E6", "F6", "G6", "H6", "I6", "J6"]
    );
}

#[test]
fn shuffle_classic_vs_spyder_changes_mean_de() {
    let classic = chart_spec(ChartKind::Classic).unwrap();
    let spyder = chart_spec(ChartKind::Spyder).unwrap();
    let same = mean_delta_e00(&classic.reference_srgb, &classic.reference_lab_d50).unwrap();
    let shuffled = mean_delta_e00(&classic.reference_srgb, &spyder.reference_lab_d50).unwrap();
    assert!(
        shuffled > same + 2.0,
        "same-table ΔE00 {same:.4} vs shuffled {shuffled:.4}"
    );
}

#[test]
fn ciede2000_self_zero() {
    let lab = Lab::new(50.0, 2.0, -3.0);
    assert!(delta_e_2000(lab, lab).abs() < 1e-12);
}
