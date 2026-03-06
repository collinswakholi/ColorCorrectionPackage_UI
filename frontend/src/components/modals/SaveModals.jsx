import React from 'react';
import Modal from '../Modal';
import { Save, FolderOpen, Loader2, Info } from 'lucide-react';

const inputCls =
  'w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white transition-colors ' +
  'focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 focus:outline-none';

const labelCls = 'block text-sm font-medium text-slate-700 mb-1';

// ── Model Management Modal ──────────────────────────────────────────────────
export function ModelManagementModal({
  isOpen,
  onClose,
  running,
  isSavingModel,
  modelSaveFolder,
  setModelSaveFolder,
  onSaveModel,
}) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Model Management">
      <div className="space-y-4">
        {/* Info */}
        <div className="bg-slate-50 p-4 rounded-lg border border-slate-200">
          <h3 className="font-medium text-slate-800 mb-1 text-sm">Model Management</h3>
          <p className="text-xs text-slate-600 leading-relaxed">
            Save your trained color correction models for reuse. Models include all calibration
            data and settings, allowing you to apply the same corrections to new images without
            retraining.
          </p>
        </div>

        {/* Save Model */}
        <div className="bg-white p-4 rounded-lg border border-slate-200">
          <h4 className="font-medium text-slate-800 mb-3 text-sm flex items-center gap-2">
            <Save className="w-4 h-4 text-indigo-500" />
            Save Model
          </h4>
          <input
            type="text"
            className={inputCls + ' mb-2'}
            placeholder="e.g., C:\Users\YourName\Desktop\models"
            value={modelSaveFolder}
            onChange={(e) => setModelSaveFolder(e.target.value)}
          />
          <p className="text-xs text-slate-500 mb-3">
            <Info className="w-3 h-3 inline mr-1 -mt-0.5" />
            Leave blank to save in default models/ folder
          </p>
          <button
            disabled={running || isSavingModel}
            onClick={onSaveModel}
            className={`w-full px-4 py-2.5 text-white text-sm font-medium rounded-lg transition-colors flex items-center justify-center gap-2 ${
              isSavingModel
                ? 'bg-indigo-400 cursor-not-allowed'
                : running
                  ? 'bg-slate-400 cursor-not-allowed'
                  : 'bg-indigo-600 hover:bg-indigo-700'
            }`}
          >
            {isSavingModel ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin" />
                Saving…
              </>
            ) : (
              <>
                <Save className="w-4 h-4" />
                Save Current Model
              </>
            )}
          </button>
          <p className="text-xs text-slate-400 mt-2">Requires a trained model</p>
        </div>

        {/* Load Saved Model */}
        <div className="bg-slate-50 p-4 rounded-lg border border-slate-200">
          <div className="flex items-center justify-between">
            <h4 className="font-medium text-slate-700 text-sm flex items-center gap-2">
              <FolderOpen className="w-4 h-4 text-slate-400" />
              Load Saved Model
            </h4>
            <span className="text-xs bg-indigo-100 text-indigo-700 px-2 py-0.5 rounded-full font-medium">
              Coming Soon
            </span>
          </div>
        </div>

        {/* Close */}
        <button
          onClick={onClose}
          className="w-full px-4 py-2.5 bg-slate-100 text-slate-700 text-sm font-medium rounded-lg hover:bg-slate-200 transition-colors"
        >
          Close
        </button>

        {/* Workflow */}
        <div className="bg-slate-50 p-3 rounded-lg border border-slate-200 text-xs text-slate-500 leading-relaxed">
          <strong>Workflow:</strong> Train a model using the pipeline, then save it here for
          later use. Saved models can be loaded to skip the training step and directly apply
          corrections to new images.
        </div>
      </div>
    </Modal>
  );
}

// ── Enhanced Save Dialog ────────────────────────────────────────────────────
export function EnhancedSaveDialog({
  isOpen,
  onClose,
  batchProcessComplete,
  saveDirectory,
  setSaveDirectory,
  selectedStepsToSave,
  setSelectedStepsToSave,
  selectedImagesToSave,
  setSelectedImagesToSave,
  batchImagesList,
  availableImages,
  onSaveImages,
  onSaveBatchImages,
  setBatchProcessComplete,
}) {
  const steps = [
    { key: 'FFC', label: 'FFC' },
    { key: 'GC', label: 'GC' },
    { key: 'WB', label: 'WB' },
    { key: 'CC', label: 'CC' },
  ];

  const imageList = batchProcessComplete ? batchImagesList : availableImages;

  // For non-batch mode, selection key is base_name; for batch mode, it's image_index
  const getKey = (img) => batchProcessComplete ? img.image_index : img.base_name;
  const getLabel = (img) => batchProcessComplete ? img.filename : img.base_name;

  const allSelected = imageList?.length > 0 && imageList.every((img) => selectedImagesToSave.includes(getKey(img)));

  const toggleSelectAll = () => {
    if (allSelected) {
      setSelectedImagesToSave([]);
    } else {
      setSelectedImagesToSave(imageList.map((img) => getKey(img)));
    }
  };

  const totalFiles = selectedStepsToSave.length * selectedImagesToSave.length;

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Save Processed Images">
      <div className="space-y-4">
        {/* Status banner */}
        {batchProcessComplete ? (
          <div className="bg-emerald-50 p-3 rounded-lg border border-emerald-200 text-sm text-emerald-800 font-medium">
            Batch processing complete — select images and steps to save.
          </div>
        ) : (
          <div className="bg-slate-50 p-3 rounded-lg border border-slate-200 text-sm text-slate-700 font-medium">
            Select the processed images and correction steps you want to save.
          </div>
        )}

        {/* Directory input */}
        <div>
          <label className={labelCls}>Save Directory</label>
          <input
            type="text"
            className={inputCls}
            placeholder="e.g., C:\Users\YourName\Desktop\output"
            value={saveDirectory}
            onChange={(e) => setSaveDirectory(e.target.value)}
          />
        </div>

        {/* Step selection */}
        <div>
          <label className={labelCls + ' mb-2'}>Steps to Save</label>
          <div className="grid grid-cols-2 gap-2">
            {steps.map((step) => (
              <label key={step.key} className="flex items-center gap-2 text-sm text-slate-700 cursor-pointer">
                <input
                  type="checkbox"
                  className="w-4 h-4 rounded accent-indigo-600"
                  checked={selectedStepsToSave.includes(step.key)}
                  onChange={() =>
                    setSelectedStepsToSave((prev) =>
                      prev.includes(step.key)
                        ? prev.filter((s) => s !== step.key)
                        : [...prev, step.key]
                    )
                  }
                />
                {step.label}
              </label>
            ))}
          </div>
        </div>

        {/* Image selection */}
        <div>
          <div className="flex items-center justify-between mb-2">
            <label className={labelCls + ' mb-0'}>Images to Save</label>
            <button
              type="button"
              className="text-xs text-indigo-600 hover:text-indigo-700 font-medium"
              onClick={toggleSelectAll}
            >
              {allSelected ? 'Deselect All' : 'Select All'}
            </button>
          </div>
          <div className="max-h-48 overflow-y-auto border border-slate-200 rounded-lg divide-y divide-slate-100">
            {imageList && imageList.length > 0 ? (
              imageList.map((img) => (
                <label
                  key={getKey(img)}
                  className="flex items-center gap-2 px-3 py-2 hover:bg-slate-50 cursor-pointer text-sm"
                >
                  <input
                    type="checkbox"
                    className="w-4 h-4 rounded accent-indigo-600"
                    checked={selectedImagesToSave.includes(getKey(img))}
                    onChange={() => {
                      const key = getKey(img);
                      setSelectedImagesToSave((prev) =>
                        prev.includes(key)
                          ? prev.filter((n) => n !== key)
                          : [...prev, key]
                      );
                    }}
                  />
                  <span className="text-slate-800">{getLabel(img)}</span>
                  {img.available_steps && (
                    <span className="ml-auto text-xs text-slate-400">
                      {img.available_steps.join(', ')}
                    </span>
                  )}
                </label>
              ))
            ) : (
              <div className="px-3 py-4 text-sm text-slate-400 text-center">No images available.</div>
            )}
          </div>
        </div>

        {/* Summary */}
        <div className="text-sm text-slate-600">
          Will save: <strong>{selectedStepsToSave.length}</strong> step(s) ×{' '}
          <strong>{selectedImagesToSave.length}</strong> image(s) ={' '}
          <strong>{totalFiles}</strong> file(s)
        </div>

        {/* Action buttons */}
        <div className="flex gap-3">
          <button
            onClick={() => {
              onClose();
              if (batchProcessComplete) setBatchProcessComplete(false);
            }}
            className="flex-1 px-4 py-2.5 bg-slate-100 text-slate-700 text-sm font-medium rounded-lg hover:bg-slate-200 transition-colors"
          >
            Cancel
          </button>
          {batchProcessComplete ? (
            <button
              disabled={selectedStepsToSave.length === 0}
              onClick={() => {
                onSaveBatchImages();
                setBatchProcessComplete(false);
              }}
              className={`flex-1 px-4 py-2.5 text-white text-sm font-medium rounded-lg transition-colors ${
                selectedStepsToSave.length === 0
                  ? 'bg-slate-400 cursor-not-allowed'
                  : 'bg-emerald-600 hover:bg-emerald-700'
              }`}
            >
              Save All Batch Images
            </button>
          ) : (
            <button
              disabled={selectedStepsToSave.length === 0 || selectedImagesToSave.length === 0}
              onClick={onSaveImages}
              className={`flex-1 px-4 py-2.5 text-white text-sm font-medium rounded-lg transition-colors ${
                selectedStepsToSave.length === 0 || selectedImagesToSave.length === 0
                  ? 'bg-slate-400 cursor-not-allowed'
                  : 'bg-indigo-600 hover:bg-indigo-700'
              }`}
            >
              Save Selected
            </button>
          )}
        </div>
      </div>
    </Modal>
  );
}
