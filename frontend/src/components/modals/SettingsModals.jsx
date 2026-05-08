import React from 'react';
import Modal from '../Modal';
import { Grid3x3, Palette, Scale, Target, Info } from 'lucide-react';

/* ── Shared classes ──────────────────────────────────────────────────────── */
const inputCls =
  'w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white transition-colors ' +
  'focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none';

const labelCls = 'block text-sm font-medium text-slate-700 mb-1';
const checkCls = 'w-4 h-4 rounded accent-indigo-600 cursor-pointer';
const infoCls = 'bg-slate-50 rounded-lg p-3 border border-slate-200 text-xs text-slate-600 leading-relaxed';
const doneBtnCls =
  'w-full py-2.5 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 ' +
  'transition-colors focus:outline-none focus:ring-2 focus:ring-indigo-500/30';

// ── Flat Field Correction Settings ──────────────────────────────────────────
export function FFCSettingsModal({ isOpen, onClose, settings, setSettings }) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="FFC Settings">
      <div className="space-y-4">
        <div className="flex items-center gap-2 text-indigo-600 mb-1">
          <Grid3x3 className="w-4 h-4" />
          <span className="text-xs font-medium uppercase tracking-wide">Flat Field Correction</span>
        </div>

        <div>
          <label className={labelCls}>Fit Method</label>
          <select className={inputCls} value={settings.fit_method} onChange={(e) => setSettings({ ...settings, fit_method: e.target.value })}>
            <option value="linear">linear</option>
            <option value="pls">pls</option>
            <option value="nn">nn</option>
            <option value="svm">svm</option>
          </select>
        </div>

        <div className="grid grid-cols-2 gap-3">
          {[
            { label: 'Bins', key: 'bins', min: 10, max: 200, step: 1, parse: parseInt },
            { label: 'Smooth Window', key: 'smooth_window', min: 3, max: 21, step: 2, parse: parseInt },
            { label: 'Poly Degree', key: 'degree', min: 1, max: 10, step: 1, parse: parseInt },
            { label: 'Max Iterations', key: 'max_iter', min: 100, max: 10000, step: 100, parse: parseInt },
            { label: 'Tolerance', key: 'tol', min: 1e-10, max: 1e-3, step: 1e-9, parse: parseFloat },
            { label: 'Random Seed', key: 'random_seed', min: 0, max: 9999, step: 1, parse: parseInt },
          ].map(({ label, key, min, max, step, parse }) => (
            <div key={key}>
              <label className={labelCls}>{label}</label>
              <input type="number" min={min} max={max} step={step} className={inputCls} value={settings[key]}
                     onChange={(e) => setSettings({ ...settings, [key]: parse(e.target.value) })} />
            </div>
          ))}
        </div>

        <div className="space-y-2">
          {['manual_crop', 'interactions', 'verbose'].map((key) => (
            <label key={key} className="flex items-center gap-2 text-sm text-slate-700 cursor-pointer">
              <input type="checkbox" className={checkCls} checked={settings[key]}
                     onChange={(e) => setSettings({ ...settings, [key]: e.target.checked })} />
              {key.replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase())}
            </label>
          ))}
        </div>

        <div className={infoCls}>
          <Info className="w-3.5 h-3.5 inline mr-1 -mt-0.5 text-slate-400" />
          Compensates for uneven illumination across the sensor caused by lens vignetting, non-uniform light sources, or sensor response variation.
        </div>

        <button className={doneBtnCls} onClick={onClose}>Done</button>
      </div>
    </Modal>
  );
}

// ── Gamma Correction Settings ───────────────────────────────────────────────
export function GCSettingsModal({ isOpen, onClose, settings, setSettings }) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="GC Settings">
      <div className="space-y-4">
        <div className="flex items-center gap-2 text-indigo-600 mb-1">
          <Palette className="w-4 h-4" />
          <span className="text-xs font-medium uppercase tracking-wide">Gamma Correction</span>
        </div>

        <div>
          <label className={labelCls}>Maximum Polynomial Degree</label>
          <input type="number" min={1} max={10} className={inputCls} value={settings.max_degree}
                 onChange={(e) => setSettings({ ...settings, max_degree: parseInt(e.target.value) })} />
        </div>

        <div className={infoCls}>
          <Info className="w-3.5 h-3.5 inline mr-1 -mt-0.5 text-slate-400" />
          Adjusts the brightness/contrast curve to linearize camera response. A polynomial of the specified degree maps captured intensity values to linear-light equivalents.
        </div>

        <button className={doneBtnCls} onClick={onClose}>Done</button>
      </div>
    </Modal>
  );
}

// ── White Balance Settings ──────────────────────────────────────────────────
export function WBSettingsModal({ isOpen, onClose }) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="WB Settings">
      <div className="space-y-4">
        <div className="flex items-center gap-2 text-indigo-600 mb-1">
          <Scale className="w-4 h-4" />
          <span className="text-xs font-medium uppercase tracking-wide">White Balance</span>
        </div>

        <div className={infoCls}>
          <Info className="w-3.5 h-3.5 inline mr-1 -mt-0.5 text-slate-400" />
          Corrects color casts caused by the scene illuminant so neutral objects appear neutral. Scales color channels to compensate for light source color temperature.
        </div>

        <div className="bg-emerald-50 rounded-lg p-3 border border-emerald-200 text-xs text-emerald-700">
          White Balance uses default settings. No additional configuration required.
        </div>

        <button className={doneBtnCls} onClick={onClose}>Done</button>
      </div>
    </Modal>
  );
}

// ── Color Correction Settings ───────────────────────────────────────────────
export function CCSettingsModal({ isOpen, onClose, settings, setSettings, saveCcModel, setSaveCcModel }) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="CC Settings">
      <div className="space-y-4">
        <div className="flex items-center gap-2 text-indigo-600 mb-1">
          <Target className="w-4 h-4" />
          <span className="text-xs font-medium uppercase tracking-wide">Color Correction</span>
        </div>

        <div>
          <label className={labelCls}>CC Method</label>
          <select className={inputCls} value={settings.cc_method} onChange={(e) => setSettings({ ...settings, cc_method: e.target.value })}>
            <option value="ours">Custom (ML-based)</option>
            <option value="conv">Conventional (Finlayson 2015)</option>
          </select>
        </div>

        {settings.cc_method === 'ours' && (
          <div>
            <label className={labelCls}>ML Method</label>
            <select className={inputCls} value={settings.mtd} onChange={(e) => setSettings({ ...settings, mtd: e.target.value })}>
              <option value="linear">linear</option>
              <option value="pls">pls</option>
              <option value="nn">nn</option>
            </select>
          </div>
        )}

        {settings.cc_method === 'conv' && (
          <div>
            <label className={labelCls}>Conventional Method</label>
            <select className={inputCls} value={settings.method} onChange={(e) => setSettings({ ...settings, method: e.target.value })}>
              <option value="Finlayson 2015">Finlayson 2015</option>
            </select>
          </div>
        )}

        <div className="grid grid-cols-2 gap-3">
          {[
            { label: 'Poly Degree', key: 'degree', min: 1, max: 5, step: 1, parse: parseInt },
            { label: 'Max Iterations', key: 'max_iterations', min: 1000, max: 50000, step: 1000, parse: parseInt },
            { label: 'Random State', key: 'random_state', min: 0, max: 9999, step: 1, parse: parseInt },
            { label: 'Tolerance', key: 'tol', min: 1e-10, max: 1e-3, step: 1e-9, parse: parseFloat },
            { label: 'N Samples', key: 'n_samples', min: 1, max: 100, step: 1, parse: parseInt },
          ].map(({ label, key, min, max, step, parse }) => (
            <div key={key}>
              <label className={labelCls}>{label}</label>
              <input type="number" min={min} max={max} step={step} className={inputCls} value={settings[key]}
                     onChange={(e) => setSettings({ ...settings, [key]: parse(e.target.value) })} />
            </div>
          ))}
          <label className="flex items-center gap-2 text-sm text-slate-700 cursor-pointer self-end pb-2">
            <input type="checkbox" className={checkCls} checked={settings.verbose}
                   onChange={(e) => setSettings({ ...settings, verbose: e.target.checked })} />
            Verbose
          </label>
        </div>

        {/* PLS */}
        {settings.cc_method === 'ours' && settings.mtd === 'pls' && (
          <div className="bg-indigo-50/60 border border-indigo-200 rounded-lg p-4 space-y-3">
            <h4 className="text-sm font-medium text-indigo-800">PLS Settings</h4>
            <div>
              <label className={labelCls}>Number of Components</label>
              <input type="number" min={1} max={10} className={inputCls} value={settings.ncomp}
                     onChange={(e) => setSettings({ ...settings, ncomp: parseInt(e.target.value) })} />
            </div>
          </div>
        )}

        {/* Neural Network */}
        {settings.cc_method === 'ours' && settings.mtd === 'nn' && (
          <div className="bg-indigo-50/60 border border-indigo-200 rounded-lg p-4 space-y-3">
            <h4 className="text-sm font-medium text-indigo-800">Neural Network Settings</h4>
            <div>
              <label className={labelCls}>Hidden Layers</label>
              <input type="text" className={inputCls}
                     value={typeof settings.hidden_layers === 'string' ? settings.hidden_layers : JSON.stringify(settings.hidden_layers)}
                     onChange={(e) => setSettings({ ...settings, hidden_layers: e.target.value })} />
              <p className="text-xs text-slate-500 mt-1">Layer sizes, e.g. 64 or [64, 32]</p>
            </div>
            <div className="grid grid-cols-2 gap-3">
              {[
                { label: 'Learning Rate', key: 'learning_rate', min: 0.0001, max: 0.1, step: 0.0001, parse: parseFloat },
                { label: 'Batch Size', key: 'batch_size', min: 4, max: 64, step: 1, parse: parseInt },
                { label: 'Patience', key: 'patience', min: 5, max: 50, step: 1, parse: parseInt },
                { label: 'Dropout Rate', key: 'dropout_rate', min: 0, max: 0.5, step: 0.05, parse: parseFloat },
              ].map(({ label, key, min, max, step, parse }) => (
                <div key={key}>
                  <label className={labelCls}>{label}</label>
                  <input type="number" min={min} max={max} step={step} className={inputCls} value={settings[key]}
                         onChange={(e) => setSettings({ ...settings, [key]: parse(e.target.value) })} />
                </div>
              ))}
              <div>
                <label className={labelCls}>Optimizer</label>
                <select className={inputCls} value={settings.optim_type} onChange={(e) => setSettings({ ...settings, optim_type: e.target.value })}>
                  <option value="adam">adam</option>
                  <option value="sgd">sgd</option>
                  <option value="rmsprop">rmsprop</option>
                </select>
              </div>
            </div>
            <label className="flex items-center gap-2 text-sm text-slate-700 cursor-pointer">
              <input type="checkbox" className={checkCls} checked={settings.use_batch_norm}
                     onChange={(e) => setSettings({ ...settings, use_batch_norm: e.target.checked })} />
              Batch Normalization
            </label>
          </div>
        )}

        <div className="bg-slate-50 border border-slate-200 rounded-lg p-3">
          <label className="flex items-center gap-2 text-sm font-medium text-slate-700 cursor-pointer">
            <input type="checkbox" className={checkCls} checked={saveCcModel} onChange={(e) => setSaveCcModel(e.target.checked)} />
            Save Color Correction Model
          </label>
        </div>

        <div className={infoCls}>
          <Info className="w-3.5 h-3.5 inline mr-1 -mt-0.5 text-slate-400" />
          Maps camera RGB values to a device-independent color space using a color checker target. Choose between ML-based methods (linear, PLS, NN) or conventional polynomial regression.
        </div>

        <button className={doneBtnCls} onClick={onClose}>Done</button>
      </div>
    </Modal>
  );
}
