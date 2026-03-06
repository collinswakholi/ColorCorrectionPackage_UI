import React from 'react';
import Modal from '../Modal';
import { CORRECTION_STEPS, getDeltaEQuality, formatMetric } from '../../constants';
import { Grid3x3, Palette, Scale, Target } from 'lucide-react';

/* Map step names to Lucide components */
const STEP_ICON_MAP = { FFC: Grid3x3, GC: Palette, WB: Scale, CC: Target };

/* Skeleton loader for lazy-fetched images */
function Skeleton({ className = '' }) {
  return <div className={`bg-slate-200 animate-pulse rounded-lg ${className}`} />;
}

/**
 * Delta E color accuracy metrics table + summary cards.
 */
export function DeltaEModal({ isOpen, onClose, deltaEValues }) {
  const steps = deltaEValues
    ? CORRECTION_STEPS.filter((s) => s !== '_method' && deltaEValues[s])
    : [];

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Delta E Metrics — Color Accuracy" maxWidth="max-w-7xl">
      {/* Threshold legend */}
      <div className="bg-slate-50 p-4 rounded-lg border border-slate-200 mb-6">
        <h3 className="font-medium text-slate-800 mb-2 text-sm">Understanding Delta E Thresholds</h3>
        <ul className="text-sm text-slate-600 space-y-1">
          <li><span className="inline-block w-2 h-2 rounded-full bg-emerald-500 mr-2" /><strong>&lt; 1.0</strong> — Excellent: not perceptible by human eyes</li>
          <li><span className="inline-block w-2 h-2 rounded-full bg-emerald-400 mr-2" /><strong>1 – 2</strong> — Very Good: perceptible through close observation</li>
          <li><span className="inline-block w-2 h-2 rounded-full bg-amber-400 mr-2" /><strong>2 – 3.5</strong> — Good: perceptible at a glance</li>
          <li><span className="inline-block w-2 h-2 rounded-full bg-orange-500 mr-2" /><strong>3.5 – 5</strong> — Fair: significant difference</li>
          <li><span className="inline-block w-2 h-2 rounded-full bg-red-500 mr-2" /><strong>&gt; 5</strong> — Needs Improvement: very obvious difference</li>
        </ul>
      </div>

      {steps.length > 0 ? (
        <>
          {/* Table */}
          <div className="overflow-x-auto mb-6">
            <table className="w-full border-collapse text-sm">
              <thead>
                <tr className="bg-slate-100 text-slate-600">
                  <th className="border border-slate-200 px-4 py-2.5 text-left font-medium">Step</th>
                  <th className="border border-slate-200 px-4 py-2.5 text-right font-medium">DE_mean</th>
                  <th className="border border-slate-200 px-4 py-2.5 text-right font-medium">DE_min</th>
                  <th className="border border-slate-200 px-4 py-2.5 text-right font-medium">DE_max</th>
                  <th className="border border-slate-200 px-4 py-2.5 text-right font-medium">DE_std</th>
                  <th className="border border-slate-200 px-4 py-2.5 text-center font-medium">Quality</th>
                </tr>
              </thead>
              <tbody>
                {steps.map((step) => {
                  const metrics = deltaEValues[step];
                  const ccMethod = deltaEValues._method || '';
                  const quality = getDeltaEQuality(metrics.DE_mean);
                  const Icon = STEP_ICON_MAP[step];
                  return (
                    <tr key={step} className={quality.styles.row}>
                      <td className="border border-slate-200 px-4 py-2 font-medium text-slate-800">
                        <span className="inline-flex items-center gap-1.5">
                          {Icon && <Icon className="w-3.5 h-3.5 text-indigo-500" />}
                          {step}
                          {step === 'CC' && ccMethod ? ` (${ccMethod})` : ''}
                        </span>
                      </td>
                      <td className="border border-slate-200 px-4 py-2 text-right font-bold text-base">
                        {formatMetric(metrics.DE_mean)}
                      </td>
                      <td className="border border-slate-200 px-4 py-2 text-right">
                        {formatMetric(metrics.DE_min)}
                      </td>
                      <td className="border border-slate-200 px-4 py-2 text-right">
                        {formatMetric(metrics.DE_max)}
                      </td>
                      <td className="border border-slate-200 px-4 py-2 text-right">
                        {formatMetric(metrics.DE_std)}
                      </td>
                      <td className="border border-slate-200 px-4 py-2 text-center">
                        <span className="inline-flex items-center gap-1.5">
                          <span className={`w-2 h-2 rounded-full ${quality.styles.dot}`} />
                          <span className={quality.styles.text}>{quality.quality}</span>
                        </span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* Summary cards */}
          <div className="grid grid-cols-2 gap-3 mb-6">
            {steps.map((step) => {
              const metrics = deltaEValues[step];
              const quality = getDeltaEQuality(metrics.DE_mean);
              const Icon = STEP_ICON_MAP[step];
              return (
                <div key={step} className={`p-4 rounded-lg border ${quality.styles.card}`}>
                  <div className="text-sm font-medium text-slate-600 mb-1 flex items-center gap-1.5">
                    {Icon && <Icon className="w-3.5 h-3.5 text-indigo-500" />}
                    {step}
                  </div>
                  <div className="text-xl font-bold flex items-center gap-2">
                    <span className={`w-2.5 h-2.5 rounded-full ${quality.styles.dot}`} />
                    {formatMetric(metrics.DE_mean)}
                  </div>
                </div>
              );
            })}
          </div>
        </>
      ) : (
        <div className="text-center text-slate-400 py-8">No Delta E metrics available.</div>
      )}

      <button
        onClick={onClose}
        className="w-full px-4 py-2.5 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700 transition-colors"
      >
        Close
      </button>
    </Modal>
  );
}

/**
 * Difference image (JET colormap).
 */
export function DifferenceDialog({ isOpen, onClose }) {
  const [diffImage, setDiffImage] = React.useState(null);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState(null);

  React.useEffect(() => {
    if (!isOpen) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetch('/api/get-diff-image')
      .then((r) => r.json())
      .then((data) => {
        if (cancelled) return;
        if (data.success) setDiffImage(data.diff_image);
        else setError(data.error || 'Failed to generate difference image');
      })
      .catch((e) => { if (!cancelled) setError(e.message); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [isOpen]);

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Difference Image (JET Colormap)" maxWidth="max-w-6xl">
      <p className="text-sm text-slate-600 mb-4">
        Pixel-wise differences between original and corrected images.{' '}
        <span className="text-blue-600 font-medium">Blue = minimal</span>,{' '}
        <span className="text-red-600 font-medium">Red = maximum</span>.
      </p>

      {loading ? (
        <Skeleton className="h-64 mb-6" />
      ) : error ? (
        <div className="text-center text-red-500 py-8 text-sm">{error}</div>
      ) : diffImage ? (
        <div className="border border-slate-200 rounded-lg overflow-hidden mb-6">
          <img src={diffImage} alt="Difference (JET colormap)" className="w-full h-auto" />
        </div>
      ) : (
        <div className="text-center text-slate-400 py-8">No difference image available.</div>
      )}

      <button onClick={onClose} className="w-full px-4 py-2.5 bg-slate-100 text-slate-700 text-sm font-medium rounded-lg hover:bg-slate-200 transition-colors">
        Close
      </button>
    </Modal>
  );
}

/**
 * Side-by-side before/after comparison.
 */
export function BeforeAfterDialog({ isOpen, onClose, original, corrected }) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Before & After Comparison" maxWidth="max-w-6xl">
      <p className="text-sm text-slate-600 mb-4">Side-by-side comparison of the original and corrected images.</p>

      {original && corrected ? (
        <div className="grid grid-cols-2 gap-4 mb-6">
          <div className="border border-slate-200 rounded-lg overflow-hidden">
            <div className="bg-slate-50 px-3 py-2 text-xs font-medium text-slate-600 uppercase tracking-wide">Before (Original)</div>
            <img src={original} alt="Original" className="w-full h-auto" />
          </div>
          <div className="border border-emerald-200 rounded-lg overflow-hidden">
            <div className="bg-emerald-50 px-3 py-2 text-xs font-medium text-emerald-700 uppercase tracking-wide">After (Corrected)</div>
            <img src={corrected} alt="Corrected" className="w-full h-auto" />
          </div>
        </div>
      ) : (
        <div className="text-center text-slate-400 py-8">No images available for comparison.</div>
      )}

      <button onClick={onClose} className="w-full px-4 py-2.5 bg-slate-100 text-slate-700 text-sm font-medium rounded-lg hover:bg-slate-200 transition-colors">
        Close
      </button>
    </Modal>
  );
}

/**
 * RGB scatter plot comparison.
 */
export function ScatterPlotDialog({ isOpen, onClose }) {
  const [plotData, setPlotData] = React.useState(null);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState(null);

  React.useEffect(() => {
    if (!isOpen) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetch('/api/get-scatter-plot')
      .then((r) => r.json())
      .then((data) => {
        if (cancelled) return;
        if (data.success) setPlotData(data.scatter_plot);
        else setError(data.error || 'Failed to generate scatter plot');
      })
      .catch((e) => { if (!cancelled) setError(e.message); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [isOpen]);

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="RGB Scatter Plot" maxWidth="max-w-6xl">
      <p className="text-sm text-slate-600 mb-4">
        RGB distribution of pixel values comparing the original and corrected images.
      </p>

      {loading ? (
        <Skeleton className="h-64 mb-6" />
      ) : error ? (
        <div className="text-center text-red-500 py-8 text-sm">{error}</div>
      ) : plotData ? (
        <div className="border border-slate-200 rounded-lg overflow-hidden mb-6">
          <img src={plotData} alt="RGB Scatter Plot" className="w-full h-auto" />
        </div>
      ) : (
        <div className="text-center text-slate-400 py-8">No scatter plot data available.</div>
      )}

      <button onClick={onClose} className="w-full px-4 py-2.5 bg-slate-100 text-slate-700 text-sm font-medium rounded-lg hover:bg-slate-200 transition-colors">
        Close
      </button>
    </Modal>
  );
}
