import React from 'react';
import Modal from '../Modal';
import { CheckCircle2, XCircle, Wand2, Zap } from 'lucide-react';

// ── Apply Correction Dialog ─────────────────────────────────────────────────
export function ApplyDialog({ isOpen, onClose, images, selectedForApply, onToggleSelection, onApply }) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Apply Correction to Others">
      <div className="space-y-4">
        <p className="text-sm text-slate-600">Select images to apply the trained color correction model:</p>

        {/* Scrollable image list */}
        <div className="max-h-96 overflow-y-auto space-y-1.5 pr-1">
          {images.map((img, idx) => (
            <label
              key={idx}
              className="flex items-center gap-3 p-2 rounded-lg hover:bg-slate-50 cursor-pointer transition-colors"
            >
              <input
                type="checkbox"
                className="w-4 h-4 accent-indigo-600 rounded"
                checked={selectedForApply.includes(idx)}
                onChange={() => onToggleSelection(idx)}
              />
              <img
                src={img.url}
                alt={img.file.name}
                className="w-14 h-14 object-cover rounded-lg border border-slate-200"
              />
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium text-slate-800 truncate">{img.file.name}</p>
                <p className="text-xs text-slate-500">{(img.file.size / 1024).toFixed(1)} KB</p>
              </div>
            </label>
          ))}
        </div>

        {/* Info box */}
        <div className="bg-slate-50 p-3 rounded-lg border border-slate-200">
          <p className="text-sm font-medium text-slate-700">
            {selectedForApply.length} of {images.length} images selected
          </p>
          <p className="text-xs text-slate-500 mt-1">
            Images will be processed sequentially (one at a time)
          </p>
        </div>

        {/* Action buttons */}
        <div className="flex justify-end gap-3">
          <button
            onClick={onClose}
            className="px-4 py-2 sm:px-5 sm:py-2.5 bg-slate-100 text-slate-700 text-sm font-medium rounded-lg hover:bg-slate-200 transition-colors touch-manipulation"
          >
            Cancel
          </button>
          <button
            disabled={selectedForApply.length === 0}
            onClick={onApply}
            className={`px-4 py-2 sm:px-5 sm:py-2.5 text-white text-sm font-medium rounded-lg transition-colors touch-manipulation inline-flex items-center gap-2 ${
              selectedForApply.length === 0
                ? 'bg-indigo-400 cursor-not-allowed opacity-60'
                : 'bg-indigo-600 hover:bg-indigo-700'
            }`}
          >
            <Wand2 className="w-4 h-4" />
            Apply ({selectedForApply.length})
          </button>
        </div>
      </div>
    </Modal>
  );
}

// ── Process All Dialog ──────────────────────────────────────────────────────
export function ProcessAllDialog({ isOpen, onClose, images, selectedForProcess, onToggleSelection, ffcEnabled, gcEnabled, wbEnabled, ccEnabled, onProcess }) {
  const allSelected = images.length > 0 && selectedForProcess.length === images.length;

  function toggleAll() {
    if (allSelected) {
      images.forEach((_, idx) => {
        if (selectedForProcess.includes(idx)) onToggleSelection(idx);
      });
    } else {
      images.forEach((_, idx) => {
        if (!selectedForProcess.includes(idx)) onToggleSelection(idx);
      });
    }
  }

  const steps = [
    { key: 'FFC', enabled: ffcEnabled },
    { key: 'GC', enabled: gcEnabled },
    { key: 'WB', enabled: wbEnabled },
    { key: 'CC', enabled: ccEnabled },
  ];

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Process Selected Images">
      <div className="space-y-4">
        {/* Description */}
        <div className="bg-slate-50 p-4 rounded-lg border border-slate-200">
          <h3 className="font-medium text-slate-800 mb-1 text-sm">Full Pipeline Per Image</h3>
          <p className="text-sm text-slate-600">
            Each selected image will be independently trained and corrected through the full pipeline based on enabled steps.
          </p>
        </div>

        {/* Select All + image list */}
        <div>
          <label className="flex items-center gap-2 px-2 py-1.5 mb-1 cursor-pointer hover:bg-slate-50 rounded">
            <input type="checkbox" className="w-4 h-4 accent-indigo-600 rounded" checked={allSelected} onChange={toggleAll} />
            <span className="text-xs font-medium text-slate-600 uppercase tracking-wide">Select / Deselect All</span>
          </label>
          <div className="max-h-72 overflow-y-auto space-y-1.5 pr-1">
            {images.map((img, idx) => (
              <label
                key={idx}
                className="flex items-center gap-3 p-2 rounded-lg hover:bg-slate-50 cursor-pointer transition-colors"
              >
                <input
                  type="checkbox"
                  className="w-4 h-4 accent-indigo-600 rounded"
                  checked={selectedForProcess.includes(idx)}
                  onChange={() => onToggleSelection(idx)}
                />
                <img
                  src={img.url}
                  alt={img.file.name}
                  className="w-14 h-14 object-cover rounded-lg border border-slate-200"
                />
                <div className="flex-1 min-w-0">
                  <p className="text-sm font-medium text-slate-800 truncate">{img.file.name}</p>
                  <p className="text-xs text-slate-500">{(img.file.size / 1024).toFixed(1)} KB</p>
                </div>
              </label>
            ))}
          </div>
        </div>

        {/* Pipeline Steps */}
        <div className="bg-white p-3 rounded-lg border border-slate-200">
          <h4 className="font-medium text-slate-700 mb-2 text-sm">Pipeline Steps</h4>
          <div className="grid grid-cols-2 gap-1.5 text-sm">
            {steps.map((s) => (
              <div key={s.key} className="flex items-center gap-2 text-slate-700">
                {s.enabled ? (
                  <CheckCircle2 className="w-4 h-4 text-emerald-500" />
                ) : (
                  <XCircle className="w-4 h-4 text-slate-300" />
                )}
                <span className={s.enabled ? 'font-medium' : 'text-slate-400'}>{s.key}</span>
              </div>
            ))}
          </div>
        </div>

        {/* Selection count */}
        <div className="bg-indigo-50 p-3 rounded-lg border border-indigo-200">
          <p className="text-sm font-medium text-indigo-800">
            {selectedForProcess.length} of {images.length} images selected
          </p>
          <p className="text-xs text-indigo-600 mt-1">
            Each image is processed independently (full train + correct)
          </p>
        </div>

        {/* Action buttons */}
        <div className="flex justify-end gap-3">
          <button
            onClick={onClose}
            className="px-4 py-2 sm:px-5 sm:py-2.5 bg-slate-100 text-slate-700 text-sm font-medium rounded-lg hover:bg-slate-200 transition-colors touch-manipulation"
          >
            Cancel
          </button>
          <button
            disabled={selectedForProcess.length === 0}
            onClick={onProcess}
            className={`px-4 py-2 sm:px-5 sm:py-2.5 text-white text-sm font-medium rounded-lg transition-colors touch-manipulation inline-flex items-center gap-2 ${
              selectedForProcess.length === 0
                ? 'bg-indigo-400 cursor-not-allowed opacity-60'
                : 'bg-indigo-600 hover:bg-indigo-700'
            }`}
          >
            <Zap className="w-4 h-4" />
            Process ({selectedForProcess.length})
          </button>
        </div>
      </div>
    </Modal>
  );
}
