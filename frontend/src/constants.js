/**
 * Shared constants for Color Correction Studio.
 */

export const DEFAULT_FFC_SETTINGS = {
  manual_crop: false,
  bins: 50,
  smooth_window: 5,
  degree: 3,
  fit_method: 'pls',
  interactions: true,
  max_iter: 1000,
  tol: 1e-8,
  verbose: false,
  random_seed: 0,
};

export const DEFAULT_GC_SETTINGS = {
  max_degree: 5,
};

export const DEFAULT_CC_SETTINGS = {
  cc_method: 'ours',
  method: 'Finlayson 2015',
  mtd: 'pls',
  degree: 2,
  max_iterations: 10000,
  random_state: 0,
  tol: 1e-8,
  verbose: false,
  n_samples: 50,
  ncomp: 1,
  hidden_layers: [64],
  learning_rate: 0.001,
  batch_size: 16,
  patience: 10,
  dropout_rate: 0.2,
  optim_type: 'adam',
  use_batch_norm: true,
};

export const CORRECTION_STEPS = ['FFC', 'GC', 'WB', 'CC'];

/** Lucide icon names for each pipeline step (imported in consuming components) */
export const STEP_ICON_NAMES = {
  FFC: 'Grid3x3',
  GC: 'Palette',
  WB: 'Scale',
  CC: 'Target',
};

/** Semantic quality tiers for Delta-E values */
export const DELTA_E_THRESHOLDS = [
  { max: 1.0,      quality: 'Excellent',          level: 'excellent' },
  { max: 2.0,      quality: 'Very Good',          level: 'good' },
  { max: 3.5,      quality: 'Good',               level: 'fair' },
  { max: 5.0,      quality: 'Fair',               level: 'warning' },
  { max: Infinity,  quality: 'Needs Improvement', level: 'poor' },
];

/** Map semantic quality levels to Tailwind classes */
export const QUALITY_STYLES = {
  excellent: { row: 'bg-emerald-50',  card: 'bg-emerald-50 border-emerald-400', text: 'text-emerald-700', dot: 'bg-emerald-500' },
  good:      { row: 'bg-emerald-50/60', card: 'bg-emerald-50/60 border-emerald-300', text: 'text-emerald-600', dot: 'bg-emerald-400' },
  fair:      { row: 'bg-amber-50',    card: 'bg-amber-50 border-amber-400', text: 'text-amber-700', dot: 'bg-amber-500' },
  warning:   { row: 'bg-orange-50',   card: 'bg-orange-50 border-orange-400', text: 'text-orange-700', dot: 'bg-orange-500' },
  poor:      { row: 'bg-red-50',      card: 'bg-red-50 border-red-400', text: 'text-red-700', dot: 'bg-red-500' },
};

/**
 * Return quality metadata for a given Delta-E value.
 */
export function getDeltaEQuality(value) {
  const num = typeof value === 'number' ? value : parseFloat(value);
  const tier = DELTA_E_THRESHOLDS.find(t => num < t.max) || DELTA_E_THRESHOLDS[DELTA_E_THRESHOLDS.length - 1];
  return { ...tier, styles: QUALITY_STYLES[tier.level] };
}

/**
 * Format a numeric value for display, defaulting to '-'.
 */
export function formatMetric(val, decimals = 2) {
  if (val === null || val === undefined) return '-';
  return typeof val === 'number' ? val.toFixed(decimals) : val;
}
