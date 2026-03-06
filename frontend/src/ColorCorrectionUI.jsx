import React, { useState, useRef, useEffect, useCallback } from "react";

// API utilities
import { apiFetch, apiPost, apiUpload } from "./api";

// Constants
import { DEFAULT_FFC_SETTINGS, DEFAULT_GC_SETTINGS, DEFAULT_CC_SETTINGS } from "./constants";

// Custom hooks
import { useLogger } from "./hooks/useLogger";
import { useDragDrop } from "./hooks/useDragDrop";

// Components
import ShutdownOverlay from "./components/ShutdownOverlay";
import CollapsibleSection from "./components/CollapsibleSection";
import ConfirmDialog from "./components/ConfirmDialog";

// Lucide icons
import {
  Upload, Image as ImageIcon, Grid3x3, Palette, Scale, Target,
  Play, Wand2, Zap, Save, Package, RotateCcw, LogOut, Search,
  Trash2, FileImage, BarChart3, GitCompare, Maximize2, ScatterChart,
  Activity, Settings2, Loader2, X as XIcon, CheckCircle2, Info
} from "lucide-react";

// Modal components
import { FFCSettingsModal, GCSettingsModal, WBSettingsModal, CCSettingsModal } from "./components/modals/SettingsModals";
import { DeltaEModal, DifferenceDialog, BeforeAfterDialog, ScatterPlotDialog } from "./components/modals/AnalysisModals";
import { ModelManagementModal, EnhancedSaveDialog } from "./components/modals/SaveModals";
import { ApplyDialog, ProcessAllDialog } from "./components/modals/ActionModals";

export default function ColorCorrectionUI() {
  // State management
  const [images, setImages] = useState([]);
  const [whiteImage, setWhiteImage] = useState(null);
  const [ccmFile, setCcmFile] = useState(null);
  const [running, setRunning] = useState(false);
  const [selectedImage, setSelectedImage] = useState(null);
  const [chartDetected, setChartDetected] = useState(false);

  // Correction toggles - All enabled by default
  const [ffcEnabled, setFfcEnabled] = useState(true);
  const [gcEnabled, setGcEnabled] = useState(true);
  const [wbEnabled, setWbEnabled] = useState(true);
  const [ccEnabled, setCcEnabled] = useState(true);
  const [saveCcModel, setSaveCcModel] = useState(false);

  // Delta E computation toggle (only for single image mode)
  const [computeDeltaE, setComputeDeltaE] = useState(true);

  // Modal states
  const [ffcModalOpen, setFfcModalOpen] = useState(false);
  const [gcModalOpen, setGcModalOpen] = useState(false);
  const [wbModalOpen, setWbModalOpen] = useState(false);
  const [ccModalOpen, setCcModalOpen] = useState(false);
  const [modelModalOpen, setModelModalOpen] = useState(false);

  // Save dialog states
  const [availableImages, setAvailableImages] = useState([]);
  const [saveDirectory, setSaveDirectory] = useState('');
  const [modelSaveFolder, setModelSaveFolder] = useState('');
  const [isSavingModel, setIsSavingModel] = useState(false);

  // Image preview state
  const [previewLabel, setPreviewLabel] = useState('');

  // DeltaE dialog state
  const [deltaEDialogOpen, setDeltaEDialogOpen] = useState(false);
  const [deltaEValues, setDeltaEValues] = useState({});

  // Apply dialog states
  const [applyDialogOpen, setApplyDialogOpen] = useState(false);
  const [selectedForApply, setSelectedForApply] = useState([]);

  // Process all dialog
  const [processAllDialogOpen, setProcessAllDialogOpen] = useState(false);
  const [selectedForProcess, setSelectedForProcess] = useState([]);
  const [batchProgress, setBatchProgress] = useState({ current: 0, total: 0, status: '' });
  const [batchProcessComplete, setBatchProcessComplete] = useState(false);

  // Comparison dialog states
  const [differenceDialogOpen, setDifferenceDialogOpen] = useState(false);
  const [beforeAfterDialogOpen, setBeforeAfterDialogOpen] = useState(false);
  const [comparisonData, setComparisonData] = useState({ original: null, corrected: null });

  // Collapsible section states
  const [correctionsExpanded, setCorrectionsExpanded] = useState(false);
  const [batchOpsExpanded, setBatchOpsExpanded] = useState(false);
  const [analysisExpanded, setAnalysisExpanded] = useState(false);
  const [dataExpanded, setDataExpanded] = useState(false);
  const [systemExpanded, setSystemExpanded] = useState(false);

  // Scatter plot, save steps, and batch images
  const [scatterDialogOpen, setScatterDialogOpen] = useState(false);
  const [showDialogsAfterCC, setShowDialogsAfterCC] = useState(true);
  const [saveStepsDialogOpen, setSaveStepsDialogOpen] = useState(false);
  const [selectedStepsToSave, setSelectedStepsToSave] = useState(['CC']);
  const [selectedImagesToSave, setSelectedImagesToSave] = useState([]);
  const [batchImagesList, setBatchImagesList] = useState([]);

  // Settings states
  const [ffcSettings, setFfcSettings] = useState({ ...DEFAULT_FFC_SETTINGS });
  const [gcSettings, setGcSettings] = useState({ ...DEFAULT_GC_SETTINGS });
  const [ccSettings, setCcSettings] = useState({ ...DEFAULT_CC_SETTINGS });

  // Shutdown overlay state
  const [showShutdownOverlay, setShowShutdownOverlay] = useState(false);

  // Confirm dialog state
  const [confirmAction, setConfirmAction] = useState({ type: null });

  // File refs
  const fileInputRef = useRef();
  const ccmInputRef = useRef();
  const whiteInputRef = useRef();
  const whiteDragCounter = useRef(0);
  const pollIntervalRef = useRef(null);

  // White image drop zone drag state
  const [isWhiteDragging, setIsWhiteDragging] = useState(false);

  // Logger hook
  const { logs, appendLog, flushLogs, directLog, logContainerRef } = useLogger();

  // Auto-scroll activity log to bottom when logs change
  useEffect(() => {
    const el = logContainerRef.current;
    if (el) requestAnimationFrame(() => { el.scrollTop = el.scrollHeight; });
  }, [logs]);

  // Cleanup on unmount: revoke Object URLs and clear polling interval
  useEffect(() => {
    return () => {
      if (pollIntervalRef.current) clearInterval(pollIntervalRef.current);
    };
  }, []);

  // Process files (used by both file input and drag-drop)
  const processImageFiles = useCallback(async (files) => {
    if (files.length === 0) return;

    const mapped = files.map((f) => ({ file: f, url: URL.createObjectURL(f) }));
    setImages((prev) => [...prev, ...mapped]);
    appendLog(`\n📤 Uploading ${files.length} image(s) to backend...`);

    // Upload to backend
    try {
      const formData = new FormData();
      files.forEach(file => formData.append('images', file));

      const resp = await fetch("/api/upload-images", {
        method: "POST",
        body: formData
      });

      if (!resp.ok) {
        throw new Error(`HTTP ${resp.status}: ${await resp.text()}`);
      }

      const result = await resp.json();
      appendLog(`\n✓ ${result.message}`);

      if (files.length > 0 && !selectedImage) {
        setSelectedImage(mapped[0].url);
        setPreviewLabel('Original Image');
      }
    } catch (err) {
      appendLog(`\n✗ Upload failed: ${err.message}`);
    }
  }, [selectedImage, appendLog]);

  const handleLoadImages = useCallback(async (e) => {
    const files = Array.from(e.target.files || []);
    await processImageFiles(files);
  }, [processImageFiles]);

  // Drag and drop hook
  const { isDragging, handleDragEnter, handleDragLeave, handleDragOver, handleDrop } = useDragDrop(processImageFiles, appendLog);

  // Upload a single white image file to backend
  const processWhiteImageFile = useCallback(async (file) => {
    if (!file) return;
    setWhiteImage({ file, url: URL.createObjectURL(file) });
    appendLog(`\n📤 Uploading white image: ${file.name}...`);
    try {
      const formData = new FormData();
      formData.append('white_image', file);
      const resp = await fetch("/api/upload-white-image", {
        method: "POST",
        body: formData,
      });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}: ${await resp.text()}`);
      const result = await resp.json();
      appendLog(`\n✓ ${result.message}`);
    } catch (err) {
      appendLog(`\n✗ White image upload failed: ${err.message}`);
    }
  }, [appendLog]);

  // File-input wrapper for white image
  const handleLoadWhiteImage = useCallback(
    async (e) => {
      const f = e.target.files && e.target.files[0];
      await processWhiteImageFile(f);
    },
    [processWhiteImageFile]
  );

  // Load CCM file or Model file (.pkl)
  const handleLoadCCM = useCallback(async (e) => {
    const f = e.target.files && e.target.files[0];
    setCcmFile(f || null);
    if (!f) return;

    const fileName = f.name.toLowerCase();
    const isPklFile = fileName.endsWith('.pkl');

    if (isPklFile) {
      // Upload .pkl model file to backend
      appendLog(`\n📤 Uploading model file: ${f.name}...`);

      try {
        const formData = new FormData();
        formData.append('model_file', f);

        const resp = await fetch("/api/upload-model", {
          method: "POST",
          body: formData
        });

        if (!resp.ok) {
          throw new Error(`HTTP ${resp.status}: ${await resp.text()}`);
        }

        const result = await resp.json();
        appendLog(`\n✓ ${result.message}`);

        if (result.model_loaded) {
          appendLog(`\n✅ Model loaded and ready for "Apply to Others"`);
        }
      } catch (err) {
        appendLog(`\n✗ Model upload failed: ${err.message}`);
      }
    } else {
      // Regular CCM file (CSV, TXT, JSON)
      appendLog(`\n✓ Loaded CCM file: ${f.name}`);
    }
  }, [appendLog]);

  // Clear images and reset all state
  const clearImages = useCallback(() => {
    // Revoke object URLs to prevent memory leaks
    images.forEach(img => URL.revokeObjectURL(img.url));
    if (whiteImage) URL.revokeObjectURL(whiteImage.url);

    // Clear uploaded images
    setImages([]);
    setWhiteImage(null);
    setCcmFile(null);

    // Reset preview and results
    setSelectedImage(null);
    setChartDetected(false);
    setPreviewLabel('');

    // Clear correction results
    setAvailableImages([]);
    setDeltaEValues({});
    setDeltaEDialogOpen(false);

    // Reset running state
    setRunning(false);

    // Reset file input refs so same files can be uploaded again
    if (fileInputRef.current) fileInputRef.current.value = '';
    if (whiteInputRef.current) whiteInputRef.current.value = '';
    if (ccmInputRef.current) ccmInputRef.current.value = '';

    // Clear backend session
    apiPost("/api/clear-session").catch(err => console.error("Failed to clear backend:", err));

    appendLog("\n✓ Cleared all images and reset application");
    flushLogs();
  }, [appendLog, flushLogs]);

  // Detect color chart
  async function detectChart() {
    if (images.length === 0) {
      appendLog("\n⚠ No images loaded.");
      return;
    }

    // Check if an image is selected
    if (!selectedImage || !images.some(img => img.url === selectedImage)) {
      appendLog("\n⚠ Please select an image first.");
      return;
    }

    // Find the selected image index
    const selectedIndex = images.findIndex(img => img.url === selectedImage);
    if (selectedIndex === -1) {
      appendLog("\n⚠ Selected image not found.");
      return;
    }

    appendLog(`\n🔍 Detecting color chart on image ${selectedIndex + 1}...`);

    try {
      const resp = await fetch("/api/detect-chart", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          image_index: selectedIndex
        })
      });

      if (!resp.ok) {
        throw new Error(`HTTP ${resp.status}: ${await resp.text()}`);
      }

      const result = await resp.json();

      if (result.success && result.detection.detected) {
        setChartDetected(true);
        appendLog(`\n✓ ${result.detection.message} (${(result.detection.confidence * 100).toFixed(0)}% confidence)`);

        // Update preview with visualization if available
        if (result.detection.visualization) {
          setSelectedImage(result.detection.visualization);
          setPreviewLabel('Chart Detected');
        }

        // Log patch information
        if (result.detection.patch_data && result.detection.patch_data.length > 0) {
          appendLog(`\n  📊 Identified patches: ${result.detection.patch_data.slice(0, 6).map(p => p.name).join(', ')}...`);
        }
      } else {
        setChartDetected(false);
        appendLog(`\n⚠ ${result.detection.message}`);
      }
    } catch (err) {
      setChartDetected(false);
      appendLog(`\n✗ Chart detection failed: ${err.message}`);
    }
  }

  // Run color correction
  async function runCC() {
    if (images.length === 0) {
      appendLog("\n⚠ No images loaded.");
      return;
    }

    // Check if an image is selected, if not, prompt to select the first image
    if (!selectedImage || !images.some(img => img.url === selectedImage)) {
      setConfirmAction({
        type: 'select-first-image',
        title: 'No Image Selected',
        message: 'No image selected. Would you like to select the first image and proceed?',
        confirmLabel: 'Select & Proceed',
        variant: 'primary',
      });
      return;
    }

    // Find the selected image index
    const selectedIndex = images.findIndex(img => img.url === selectedImage || img.url === images[0].url);
    if (selectedIndex === -1) {
      appendLog("\n⚠ Selected image not found.");
      return;
    }

    // Collapse the corrections tab when running
    setCorrectionsExpanded(false);

    // Clear old results before running new correction
    setPreviewLabel('');
    setDeltaEValues({});
    setDeltaEDialogOpen(false);
    setDifferenceDialogOpen(false);
    setBeforeAfterDialogOpen(false);
    setScatterDialogOpen(false);
    setComparisonData({ original: null, corrected: null });
    appendLog("\n🗑️ Cleared previous results");

    setRunning(true);
    appendLog(`\n▶ Running Color Correction on image ${selectedIndex + 1}...\n`);

    try {
      const selectedMethod = ccSettings.cc_method === 'ours' ? ccSettings.mtd : 'conventional';

      const requestData = {
        method: selectedMethod,
        image_index: selectedIndex,
        ffcEnabled: ffcEnabled,
        gcEnabled: gcEnabled,
        wbEnabled: wbEnabled,
        ccEnabled: ccEnabled,
        saveCcModel: saveCcModel,
        computeDeltaE: computeDeltaE,
        ffcSettings: ffcSettings,
        gcSettings: gcSettings,
        ccSettings: ccSettings
      };

      const resp = await fetch("/api/run-cc", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(requestData)
      });

      if (!resp.ok) {
        const errorText = await resp.text();
        throw new Error(`HTTP ${resp.status}: ${errorText}`);
      }

      const result = await resp.json();

      if (result.success) {
        appendLog('\n' + (result.log || "✓ Completed successfully!"));

        // Replace detected chart preview with original image
        if (result.original_image) {
          setSelectedImage(result.original_image);
          setPreviewLabel('Original Image');
        }

        // Display DeltaE metrics using the summary from backend
        console.log('DEBUG: result.delta_e_summary =', result.delta_e_summary);

        if (result.delta_e_summary && Object.keys(result.delta_e_summary).length > 0) {
          const deltaESteps = ['FFC', 'GC', 'WB', 'CC'];
          deltaESteps.forEach(step => {
            if (result.delta_e_summary[step] && result.delta_e_summary[step].DE_mean !== undefined) {
              const deMean = result.delta_e_summary[step].DE_mean;
              const deMeanStr = typeof deMean === 'number' ? deMean.toFixed(2) : deMean;

              if (step === 'CC') {
                appendLog(`\n  📊 ${step} (${selectedMethod}) - DE_mean: ${deMeanStr}`);
              } else {
                appendLog(`\n  📊 ${step} - DE_mean: ${deMeanStr}`);
              }
            }
          });

          const deltaEWithMethod = {
            ...result.delta_e_summary,
            _method: selectedMethod
          };

          setDeltaEValues(deltaEWithMethod);
          if (showDialogsAfterCC && batchProgress.total === 0) {
            setTimeout(() => {
              setDeltaEDialogOpen(true);
            }, 100);
          }
        } else {
          console.log('DEBUG: No delta_e_summary in response');
          appendLog(`\n  ℹ️ No ΔE metrics available (may not be enabled for all steps)`);
        }

        // If we have results, show them with difference map
        if (result.images && result.images.length > 0) {
          appendLog(`\n✓ Generated ${result.images.length} result image(s)`);

          const ccImage = result.images.find(img => img.name.endsWith('_CC'));
          const finalImage = ccImage || result.images[result.images.length - 1];

          setComparisonData({
            original: result.original_image,
            corrected: finalImage.data,
          });

          // Diff, scatter & before/after are lazy-loaded when their dialogs open
          if (ccEnabled && showDialogsAfterCC && batchProgress.total === 0) {
            appendLog(`\n📊 Analysis views available (Difference, Scatter, Before/After)`);
            setDifferenceDialogOpen(true);
            setTimeout(() => setScatterDialogOpen(true), 500);
          }

          if (result.original_image && finalImage.data && showDialogsAfterCC && batchProgress.total === 0) {
            setTimeout(() => {
              setBeforeAfterDialogOpen(true);
            }, ccEnabled ? 3000 : 100);
          }

          setSelectedImage(finalImage.data);
          setPreviewLabel('Corrected Image');
        }
      } else {
        appendLog(`\n✗ Pipeline error: ${result.error}`);
      }
    } catch (err) {
      appendLog(`\n✗ Error: ${err.message}`);
      console.error("Pipeline error:", err);
    } finally {
      setRunning(false);
    }
  }

  // Open enhanced save dialog
  async function openSaveDialog() {
    try {
      // If in batch mode, load batch images list
      if (batchProcessComplete) {
        await loadBatchImagesList();
      } else {
        // Get available images from backend for regular save
        const resp = await fetch("/api/available-images");
        const result = await resp.json();

        if (result.success && result.images) {
          setAvailableImages(result.images);
          setSelectedImagesToSave(result.images.map(img => img.base_name));
        }
      }

      setSaveStepsDialogOpen(true);
    } catch (err) {
      appendLog(`\n✗ Failed to fetch available images: ${err.message}`);
    }
  }

  // Save images with step and image selection
  async function saveImages() {
    if (selectedStepsToSave.length === 0) {
      appendLog("\n⚠️ Please select at least one processing step.");
      return;
    }

    if (selectedImagesToSave.length === 0) {
      appendLog("\n⚠️ Please select at least one image.");
      return;
    }

    setSaveStepsDialogOpen(false);
    appendLog("\n💾 Saving selected images and steps...");

    try {
      const resp = await fetch("/api/save-images", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          selected_steps: selectedStepsToSave,
          selected_images: selectedImagesToSave,
          directory: saveDirectory || undefined
        })
      });
      const result = await resp.json();
      appendLog(`\n✓ ${result.message}`);
    } catch (err) {
      appendLog(`\n✗ Save failed: ${err.message}`);
    }
  }

  // Load batch images list for selection
  async function loadBatchImagesList() {
    try {
      const resp = await fetch("/api/batch-images-list");
      const result = await resp.json();
      if (result.success) {
        setBatchImagesList(result.images || []);
        setSelectedImagesToSave(result.images.map(img => img.image_index));
      }
    } catch (err) {
      console.error("Error loading batch images list:", err);
    }
  }

  // Save selected batch processed images
  async function saveBatchImages() {
    if (selectedStepsToSave.length === 0) {
      appendLog("\n⚠️ Please select at least one processing step.");
      return;
    }

    const imagesToSave = selectedImagesToSave.length > 0 ? selectedImagesToSave : null;

    setSaveStepsDialogOpen(false);
    appendLog(`\n💾 Saving ${imagesToSave ? selectedImagesToSave.length : 'all'} batch processed image(s)...`);

    try {
      const resp = await fetch("/api/save-batch-images", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          selected_steps: selectedStepsToSave,
          selected_images: imagesToSave,
          directory: saveDirectory || undefined
        })
      });
      const result = await resp.json();
      if (result.success) {
        appendLog(`\n✓ ${result.message}`);
        appendLog(`\n📊 Saved ${result.saved_count} files from ${result.image_count} images`);
        appendLog(`\n📁 Location: ${result.directory}`);

        setSelectedImagesToSave([]);
        setBatchProcessComplete(false);
      } else {
        appendLog(`\n✗ Batch save failed: ${result.error}`);
      }
    } catch (err) {
      appendLog(`\n✗ Batch save failed: ${err.message}`);
    }
  }

  // Save model
  async function saveModel() {
    appendLog("\n💾 Saving color correction model...");
    setIsSavingModel(true);
    try {
      const resp = await fetch("/api/save-model", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: `model_${Date.now()}`,
          folder: modelSaveFolder || null
        })
      });

      if (!resp.ok) {
        const errorText = await resp.text();
        let errorMsg = `HTTP ${resp.status}`;
        try {
          const errorJson = JSON.parse(errorText);
          errorMsg = errorJson.error || errorText;
        } catch {
          errorMsg = errorText;
        }
        throw new Error(errorMsg);
      }

      const result = await resp.json();

      if (result.success) {
        appendLog(`\n✓ ${result.message}`);
        if (result.path) {
          appendLog(`\n  📁 Saved to: ${result.path}`);
        }
        setModelSaveFolder('');
      } else {
        appendLog(`\n✗ Failed: ${result.error || 'Unknown error'}`);
      }
    } catch (err) {
      appendLog(`\n✗ Save model failed: ${err.message}`);
    } finally {
      setIsSavingModel(false);
    }
  }

  // Restart Backend
  async function restartBackend() {
    setConfirmAction({
      type: 'restart',
      title: 'Restart Backend',
      message: 'This will stop the current backend server and start a new one. The restart will happen automatically.',
      confirmLabel: 'Restart',
      variant: 'primary',
    });
  }

  // Actually perform restart after confirmation
  async function doRestart() {
    setConfirmAction({ type: null });

    appendLog("\n" + "=".repeat(60));
    appendLog("\n🔄 AUTOMATIC BACKEND RESTART");
    appendLog("\n" + "=".repeat(60));
    setRunning(true);

    // Phase 1: Send restart command
    appendLog("\n\n📍 Phase 1: Sending restart command to backend");
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 5000);

      const response = await fetch("/api/restart", {
        method: "POST",
        signal: controller.signal
      });
      clearTimeout(timeoutId);

      if (response.ok) {
        const data = await response.json();
        appendLog("\n  ✅ Restart initiated (PID: " + (data.pid || 'unknown') + ")");
        appendLog("\n  🔄 Backend will restart automatically...");
      }
    } catch (err) {
      if (err.name === 'AbortError' || err.message.includes('Failed to fetch')) {
        appendLog("\n  ✅ Restart command sent (connection closed as expected)");
      } else {
        appendLog("\n  ⚠️  " + err.message);
      }
    }

    // Wait for restart to begin
    await new Promise(resolve => setTimeout(resolve, 2000));

    // Phase 2: Verify shutdown
    appendLog("\n\n📍 Phase 2: Verifying old server shutdown");
    try {
      await fetch("/api/health", {
        signal: AbortSignal.timeout(1500)
      });
      appendLog("\n  ⚠️  Old server still responding");
    } catch {
      appendLog("\n  ✅ Old server confirmed offline");
    }

    // Phase 3: Wait for new server to start
    appendLog("\n\n📍 Phase 3: Waiting for new backend server");
    appendLog("\n  🔄 Backend is restarting automatically...");
    appendLog("\n  ⏳ Polling for backend (60 seconds)...");

    let attempts = 0;
    const maxAttempts = 60;
    pollIntervalRef.current = setInterval(async () => {
      attempts++;
      try {
        const healthResp = await fetch("/api/health", {
          signal: AbortSignal.timeout(1000)
        });
        if (healthResp.ok) {
          clearInterval(pollIntervalRef.current);
          pollIntervalRef.current = null;
          appendLog("\n\n✅ BACKEND IS BACK ONLINE!");
          appendLog("\n🎉 Automatic restart completed successfully!");
          appendLog("\n" + "=".repeat(60));
          setRunning(false);
        }
      } catch {
        if (attempts >= maxAttempts) {
          clearInterval(pollIntervalRef.current);
          pollIntervalRef.current = null;
          appendLog("\n\n⏱️  Timeout reached - backend not detected");
          appendLog("\n💡 Manual restart may be needed:");
          appendLog("\n   cd backend && python server_enhanced.py");
          appendLog("\n" + "=".repeat(60));
          setRunning(false);
        } else if (attempts % 5 === 0) {
          appendLog(`\n  ⏳ Still waiting... (${attempts}s)`);
        }
      }
    }, 1000);
  }

  // Exit Application
  async function exitApplication() {
    setConfirmAction({
      type: 'exit',
      title: 'Shutdown Application',
      message: 'This will stop the backend server, clean up temporary files, and close this tab.',
      confirmLabel: 'Shutdown',
      variant: 'danger',
    });
  }

  // Actually perform exit after confirmation
  async function doExit() {
    setConfirmAction({ type: null });

    appendLog("\n" + "=".repeat(70));
    appendLog("\n🛑 AUTOMATIC SHUTDOWN INITIATED");
    appendLog("\n" + "=".repeat(70));
    setRunning(true);

    // Phase 1: Backend Termination
    appendLog("\n\n📍 PHASE 1: Backend Server Shutdown");
    appendLog("\n   Sending shutdown signal (equivalent to Ctrl+C)...");

    let backendShutdownSuccess = false;
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 5000);

      const response = await fetch("/api/shutdown", {
        method: "POST",
        headers: { 'Content-Type': 'application/json' },
        signal: controller.signal
      });
      clearTimeout(timeoutId);

      if (response.ok) {
        appendLog("\n   ✅ Backend shutdown signal sent");
        appendLog("\n   ⏳ Backend terminating gracefully...");
        backendShutdownSuccess = true;
      }
    } catch (err) {
      if (err.name === 'AbortError' || err.message.includes('Failed to fetch')) {
        appendLog("\n   ✅ Backend shutdown initiated (connection closed)");
        backendShutdownSuccess = true;
      } else {
        appendLog("\n   ⚠️  Error: " + err.message);
      }
    }

    // Wait for backend to clean up
    appendLog("\n   ⏳ Waiting for backend cleanup...");
    await new Promise(resolve => setTimeout(resolve, 1500));

    // Phase 2: Verification
    appendLog("\n\n📍 PHASE 2: Verification");
    try {
      const verifyController = new AbortController();
      setTimeout(() => verifyController.abort(), 1000);

      await fetch("/api/health", {
        signal: verifyController.signal
      });
      appendLog("\n   ⚠️  Backend still responding (may need manual Ctrl+C)");
    } catch {
      appendLog("\n   ✅ Backend successfully terminated");
      appendLog("\n   ✅ Port 5000 is now free");
    }

    // Phase 3: Session Data Cleanup
    appendLog("\n\n📍 PHASE 3: Frontend Cleanup");
    try {
      appendLog("\n   🗑️  Clearing session data...");
      setImages([]);
      setSelectedImage(null);
      setWhiteImage(null);
      setDeltaEValues({});
      setComparisonData({ original: null, corrected: null });
      appendLog("\n   ✅ Session data cleared");
    } catch (err) {
      appendLog("\n   ⚠️  Cleanup warning: " + err.message);
    }

    // Phase 4: Final Summary & Auto-close
    appendLog("\n\n" + "=".repeat(70));
    appendLog("\n✅ SHUTDOWN COMPLETE");
    appendLog("\n" + "=".repeat(70));
    appendLog("\n\n✅ Backend: Stopped");
    appendLog("\n✅ Cleanup: Complete");
    appendLog("\n✅ Terminal: Ready for new commands");
    appendLog("\n\n🔄 Closing browser tab in 2 seconds...");
    appendLog("\n👋 Thank you for using Color Correction Studio!");

    // Show shutdown overlay and attempt to close tab
    setTimeout(() => {
      setRunning(false);
      setShowShutdownOverlay(true);
      setTimeout(() => window.close(), 1000);
    }, 2000);
  }

  // Open Apply CC Dialog
  async function openApplyDialog() {
    if (images.length === 0) {
      appendLog("\n⚠️ No images loaded.");
      return;
    }

    // Check if a trained model exists in the backend
    try {
      const resp = await fetch("/api/check-model");
      const result = await resp.json();

      if (!result.model_available) {
        appendLog("\n⚠️ No trained model available. Please:");
        appendLog("\n   1. Run color correction on at least one image, OR");
        appendLog("\n   2. Load a saved model file (.pkl) using 'Model/CCM File' button");
        return;
      }

      if (result.model_source) {
        appendLog(`\n✓ Using ${result.model_source} for application`);
      }
    } catch (err) {
      appendLog("\n⚠️ Could not check model status. Please run color correction or load a model file first.");
      return;
    }

    let indicesToSelect;
    if (selectedImage) {
      const selectedIndex = images.findIndex(img => img.url === selectedImage);
      if (selectedIndex >= 0) {
        indicesToSelect = images.map((_, idx) => idx).filter(idx => idx !== selectedIndex);
      } else {
        indicesToSelect = images.map((_, idx) => idx);
      }
    } else {
      indicesToSelect = images.map((_, idx) => idx);
    }

    setSelectedForApply(indicesToSelect);
    setApplyDialogOpen(true);
  }

  // Apply Color Correction to selected images using EXISTING trained model
  async function applyColorCorrection() {
    if (selectedForApply.length === 0) {
      appendLog("\n⚠️ No images selected.");
      return;
    }

    setApplyDialogOpen(false);
    setRunning(true);
    setBatchProgress({ current: 0, total: selectedForApply.length, status: 'Initializing...' });
    appendLog("\n" + "=".repeat(70));
    appendLog("\n🎨 APPLY TO OTHERS - Model Application");
    appendLog("\n" + "=".repeat(70));
    appendLog(`\n📊 Applying trained model to ${selectedForApply.length} image(s)...`);
    appendLog("\n💡 Mode: Parallel batch inference (predict_images)");

    try {
      const resp = await fetch("/api/apply-cc", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ image_indices: selectedForApply })
      });

      const result = await resp.json();
      if (!resp.ok || !result.success) {
        throw new Error(result?.error || `HTTP ${resp.status}`);
      }

      appendLog(`\n🚀 Batch started (ID: ${result.batch_id})`);

      // Poll /api/batch-progress until complete
      let done = false;
      while (!done) {
        await new Promise((r) => setTimeout(r, 500));

        const progressResp = await fetch("/api/batch-progress");
        const progressData = await progressResp.json();
        if (!progressData.success) continue;

        const { completed, failed, total, active, progress } = progressData;
        setBatchProgress({
          current: completed + failed,
          total,
          status: active
            ? `Processing... (${completed}/${total} done${failed ? `, ${failed} failed` : ''})`
            : 'Complete!'
        });

        if (!active) {
          done = true;
          const processedCount = completed;
          const failedCount = failed;

          setBatchProgress({ current: total, total, status: 'Complete!' });

          appendLog(`\n\n${"=".repeat(70)}`);
          appendLog(`\n✅ BATCH APPLICATION COMPLETE`);
          appendLog(`\n${"=".repeat(70)}`);
          appendLog(`\n📊 Summary:`);
          appendLog(`\n   • Total images: ${total}`);
          appendLog(`\n   • Successfully processed: ${processedCount}`);
          appendLog(`\n   • Failed: ${failedCount}`);
          appendLog(`\n${"=".repeat(70)}`);

          if (processedCount > 0) {
            setBatchProcessComplete(true);
            appendLog(`\n💡 Tip: Click "Save Images" to save all ${processedCount} corrected images`);
          }
        }
      }

    } catch (err) {
      appendLog(`\n\n❌ ERROR: ${err.message}`);
      appendLog("\n" + "=".repeat(60));
    } finally {
      setRunning(false);
      setTimeout(() => {
        setBatchProgress({ current: 0, total: 0, status: '' });
      }, 3000);
    }
  }

  // Toggle image selection for apply
  function toggleApplySelection(idx) {
    setSelectedForApply(prev =>
      prev.includes(idx)
        ? prev.filter(i => i !== idx)
        : [...prev, idx]
    );
  }

  // Toggle image selection for process all
  function toggleProcessSelection(idx) {
    setSelectedForProcess(prev =>
      prev.includes(idx)
        ? prev.filter(i => i !== idx)
        : [...prev, idx]
    );
  }

  // Open Process All dialog (pre-select all images)
  function openProcessAllDialog() {
    if (images.length === 0) {
      appendLog("\n⚠️ No images loaded.");
      return;
    }
    setSelectedForProcess(images.map((_, idx) => idx));
    setProcessAllDialogOpen(true);
  }

  // Process All Images
  async function processAllImages() {
    if (images.length === 0) {
      appendLog("\n⚠️ No images loaded.");
      return;
    }

    const indicesToProcess = [...selectedForProcess];
    setProcessAllDialogOpen(false);
    setRunning(true);
    setBatchProgress({ current: 0, total: indicesToProcess.length, status: 'Starting...' });
    appendLog("\n" + "=".repeat(70));
    appendLog("\n⚡ PROCESS ALL - Full Pipeline Per Image");
    appendLog("\n" + "=".repeat(70));
    appendLog(`\n📋 Processing ${indicesToProcess.length} of ${images.length} image(s)...`);
    appendLog("\n💡 Mode: Full pipeline (train + correct) for each image");
    appendLog("\n📝 Starting batch processing...");

    try {
      const selectedMethod = ccSettings.cc_method === 'ours' ? ccSettings.mtd : 'conventional';

      const resp = await fetch("/api/run-cc-parallel", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          image_indices: indicesToProcess,
          method: selectedMethod,
          ffcEnabled: ffcEnabled,
          gcEnabled: gcEnabled,
          wbEnabled: wbEnabled,
          ccEnabled: ccEnabled,
          saveCcModel: saveCcModel,
          ffcSettings: ffcSettings,
          gcSettings: gcSettings,
          ccSettings: ccSettings
        })
      });

      const result = await resp.json();
      if (!resp.ok || !result.success) {
        throw new Error(result?.error || `HTTP ${resp.status}`);
      }

      appendLog(`\n🚀 Batch started (ID: ${result.batch_id})`);

      // Poll /api/batch-progress until complete
      let done = false;
      let lastCompleted = 0;
      while (!done) {
        await new Promise((r) => setTimeout(r, 600));

        const progressResp = await fetch("/api/batch-progress");
        const progressData = await progressResp.json();
        if (!progressData.success) continue;

        const { completed, failed, total, active, progress } = progressData;
        setBatchProgress({
          current: completed + failed,
          total,
          status: active
            ? `Processing... (${completed}/${total} done${failed ? `, ${failed} failed` : ''})`
            : 'Complete!'
        });

        // Log newly completed images
        if (completed > lastCompleted && progress) {
          const newlyDone = progress.filter(p => p.status === 'completed' || p.status === 'failed');
          for (const p of newlyDone.slice(lastCompleted)) {
            if (p.status === 'completed') {
              appendLog(`\n  ✅ ${p.filename} — completed`);
            } else if (p.status === 'failed') {
              appendLog(`\n  ❌ ${p.filename} — ${p.error || 'failed'}`);
            }
          }
          lastCompleted = completed;
        }

        if (!active) {
          done = true;

          setBatchProgress({ current: total, total, status: 'Complete!' });
          appendLog(`\n\n${"=".repeat(70)}`);
          appendLog(`\n✅ BATCH PROCESSING COMPLETE`);
          appendLog(`\n${"=".repeat(70)}`);
          appendLog(`\n📊 Summary:`);
          appendLog(`\n   • Total images: ${total}`);
          appendLog(`\n   • Successfully processed: ${completed}`);
          appendLog(`\n   • Failed: ${failed}`);
          appendLog(`\n${"=".repeat(70)}`);

          if (completed > 0) {
            setBatchProcessComplete(true);
            appendLog(`\n💡 Tip: Click "Save Images" to save all ${completed} corrected images`);
          }
        }
      }

    } catch (err) {
      appendLog(`\n\n❌ Batch processing error: ${err.message}`);
      appendLog(`\n${"=".repeat(70)}`);
    } finally {
      setRunning(false);
      setTimeout(() => {
        setBatchProgress({ current: 0, total: 0, status: '' });
      }, 3000);
    }
  }

  return (
    <div
      className="min-h-screen bg-gradient-to-br from-slate-950 via-indigo-950 to-slate-950 p-2 sm:p-4 md:p-6 lg:p-8 relative"
      onDragEnter={handleDragEnter}
      onDragLeave={handleDragLeave}
      onDragOver={handleDragOver}
      onDrop={handleDrop}
    >
      {/* Drag Overlay */}
      {isDragging && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/90 backdrop-blur-sm pointer-events-none">
          <div className="bg-white rounded-2xl shadow-2xl p-12 text-center border-2 border-dashed border-indigo-400 animate-fade-in">
            <Upload className="w-16 h-16 text-indigo-400 mx-auto mb-4" />
            <h2 className="text-2xl font-bold text-slate-800 mb-2">Drop Images Here</h2>
            <p className="text-slate-500">Release to upload your images</p>
          </div>
        </div>
      )}

      {/* Shutdown Overlay */}
      {showShutdownOverlay && (
        <ShutdownOverlay
          onContinue={() => setShowShutdownOverlay(false)}
          onClose={() => setShowShutdownOverlay(false)}
        />
      )}

      {/* Confirm Dialog */}
      {confirmAction.type !== null && (
        <ConfirmDialog
          isOpen
          onCancel={() => setConfirmAction({ type: null })}
          onConfirm={() => {
            if (confirmAction.type === 'restart') doRestart();
            else if (confirmAction.type === 'exit') doExit();
            else if (confirmAction.type === 'select-first-image') {
              setConfirmAction({ type: null });
              if (images.length > 0) {
                setSelectedImage(images[0].url);
                appendLog("\n✓ Selected first image automatically.");
                setTimeout(() => runCC(), 50);
              }
            }
          }}
          title={confirmAction.title || ''}
          message={confirmAction.message || ''}
          confirmLabel={confirmAction.confirmLabel || 'Confirm'}
          variant={confirmAction.variant || 'primary'}
        />
      )}

      {/* Header */}
      <div className="max-w-[1600px] mx-auto mb-4 md:mb-6">
        <div className="bg-slate-800/90 rounded-2xl border border-white/10 p-4 md:p-6">
          <h1 className="text-2xl sm:text-3xl md:text-4xl font-bold text-white mb-1 flex items-center gap-3">
            <Palette className="w-8 h-8 text-indigo-400 flex-shrink-0" />
            <span className="truncate bg-gradient-to-r from-indigo-300 to-violet-300 bg-clip-text text-transparent">
              Color Correction Studio
            </span>
            <span className="text-xs font-medium bg-indigo-500/20 text-indigo-300 px-2 py-0.5 rounded-full border border-indigo-500/30 hidden sm:inline">
              v2.2.1
            </span>
          </h1>
          <p className="text-slate-400 text-sm md:text-base ml-11 md:ml-11">
            Image color correction powered by ML algorithms
          </p>
        </div>
      </div>

      {/* Main content */}
      <div className="max-w-[1600px] mx-auto bg-white rounded-2xl shadow-2xl p-3 sm:p-4 md:p-6 lg:p-8 border border-slate-200">
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-3 md:gap-6 lg:gap-8">
          {/* Left control panel */}
          <div className="lg:col-span-3 space-y-3 md:space-y-4">
            {/* File Management */}
            <div className="bg-white rounded-xl p-3 sm:p-4 border border-slate-200 shadow-sm">
              <h2 className="text-sm font-semibold text-slate-800 mb-3 flex items-center gap-2">
                <FileImage className="w-4 h-4 text-indigo-500" />
                File Management
              </h2>

              <div className="space-y-2">
                <label className="block">
                  <input ref={fileInputRef} type="file" accept="image/*" multiple className="hidden" onChange={handleLoadImages} />
                  <button
                    onClick={() => fileInputRef.current.click()}
                    className="w-full px-3 py-2.5 rounded-lg bg-indigo-600 text-white text-sm font-medium hover:bg-indigo-700 transition-colors inline-flex items-center justify-center gap-2"
                  >
                    <Upload className="w-4 h-4" />
                    Load Images
                  </button>
                </label>

                <label className="block">
                  <input ref={whiteInputRef} type="file" accept="image/*" className="hidden" onChange={handleLoadWhiteImage} />
                  <button
                    onClick={() => whiteInputRef.current.click()}
                    className="w-full px-3 py-2.5 rounded-lg bg-white text-slate-700 text-sm font-medium border border-slate-300 hover:bg-slate-50 transition-colors inline-flex items-center justify-center gap-2"
                  >
                    <ImageIcon className="w-4 h-4" />
                    White Image
                  </button>
                </label>

                {/* White Image Drop Zone */}
                <div
                  onDragEnter={(e) => { e.preventDefault(); e.stopPropagation(); whiteDragCounter.current++; setIsWhiteDragging(true); }}
                  onDragLeave={(e) => { e.preventDefault(); e.stopPropagation(); whiteDragCounter.current--; if (whiteDragCounter.current === 0) setIsWhiteDragging(false); }}
                  onDragOver={(e) => { e.preventDefault(); e.stopPropagation(); }}
                  onDrop={async (e) => {
                    e.preventDefault(); e.stopPropagation(); setIsWhiteDragging(false); whiteDragCounter.current = 0;
                    const files = Array.from(e.dataTransfer.files).filter((f) => f.type.startsWith('image/'));
                    if (files.length > 0) await processWhiteImageFile(files[0]);
                    else appendLog('\n⚠ No image file found in dropped items');
                  }}
                  onClick={() => whiteInputRef.current.click()}
                  className={`w-full rounded-lg border-2 border-dashed p-2 text-center cursor-pointer transition-all duration-200 ${
                    isWhiteDragging
                      ? 'border-indigo-400 bg-indigo-50 scale-[1.02]'
                      : whiteImage
                        ? 'border-indigo-300 bg-indigo-50/50 hover:bg-indigo-50'
                        : 'border-slate-300 bg-slate-50 hover:border-indigo-400 hover:bg-indigo-50/30'
                  }`}
                >
                  {whiteImage ? (
                    <div className="flex items-center gap-2">
                      <img src={whiteImage.url} alt="White ref" className="w-10 h-10 object-cover rounded border border-slate-200" />
                      <span className="text-xs text-slate-600 truncate flex-1 text-left">{whiteImage.file.name}</span>
                      <button
                        onClick={(ev) => { ev.stopPropagation(); setWhiteImage(null); if (whiteInputRef.current) whiteInputRef.current.value = ''; appendLog('\n✓ White image removed'); }}
                        className="text-slate-400 hover:text-red-500 p-0.5 rounded transition-colors"
                        title="Remove white image"
                      >
                        <XIcon className="w-3.5 h-3.5" />
                      </button>
                    </div>
                  ) : (
                    <div className="py-1">
                      <ImageIcon className="w-5 h-5 mx-auto text-slate-400 mb-0.5" />
                      <p className="text-[10px] text-slate-400">
                        {isWhiteDragging ? 'Drop here!' : 'Drop white image here'}
                      </p>
                    </div>
                  )}
                </div>

                <label className="block">
                  <input ref={ccmInputRef} type="file" accept=".pkl,.csv,.txt,.json,*" className="hidden" onChange={handleLoadCCM} />
                  <button
                    onClick={() => ccmInputRef.current.click()}
                    className="w-full px-3 py-2.5 rounded-lg bg-white text-slate-700 text-sm font-medium border border-slate-300 hover:bg-slate-50 transition-colors inline-flex items-center justify-center gap-2"
                  >
                    <Package className="w-4 h-4" />
                    Model/CCM File
                  </button>
                </label>

                <button
                  onClick={detectChart}
                  disabled={images.length === 0}
                  className="w-full px-3 py-2.5 rounded-lg bg-white text-slate-700 text-sm font-medium border border-slate-300 hover:bg-slate-50 transition-colors disabled:opacity-50 disabled:cursor-not-allowed inline-flex items-center justify-center gap-2"
                >
                  <Search className="w-4 h-4" />
                  Detect Chart
                </button>

                {images.length > 0 && (
                  <button
                    onClick={clearImages}
                    className="w-full px-3 py-2.5 rounded-lg bg-white text-slate-700 text-sm font-medium border border-slate-300 hover:border-red-400 hover:text-red-600 transition-colors inline-flex items-center justify-center gap-2"
                  >
                    <Trash2 className="w-4 h-4" />
                    Clear All ({images.length})
                  </button>
                )}
              </div>
            </div>

            {/* Pipeline Settings */}
            <CollapsibleSection icon={Settings2} title="Pipeline Settings" isOpen={correctionsExpanded} onToggle={() => setCorrectionsExpanded(!correctionsExpanded)}>
              <div className="space-y-1.5">
                {[
                  { label: "FFC", icon: Grid3x3, state: ffcEnabled, setState: setFfcEnabled, openModal: () => setFfcModalOpen(true) },
                  { label: "GC", icon: Palette, state: gcEnabled, setState: setGcEnabled, openModal: () => setGcModalOpen(true) },
                  { label: "WB", icon: Scale, state: wbEnabled, setState: setWbEnabled, openModal: () => setWbModalOpen(true) },
                  { label: "CC", icon: Target, state: ccEnabled, setState: setCcEnabled, openModal: () => setCcModalOpen(true) }
                ].map(({ label, icon: Icon, state, setState, openModal }) => (
                  <div key={label} className="flex items-center gap-2 bg-white p-2 rounded-lg border border-slate-200">
                    <label className="flex items-center gap-2 flex-1 cursor-pointer">
                      <input type="checkbox" checked={state} onChange={(e) => setState(e.target.checked)}
                             className="w-4 h-4 rounded accent-indigo-600 cursor-pointer" />
                      <Icon className="w-3.5 h-3.5 text-indigo-500" />
                      <span className="font-medium text-slate-700 text-xs">{label}</span>
                    </label>
                    <button onClick={openModal}
                            className="p-1.5 rounded-md text-slate-400 hover:text-indigo-600 hover:bg-indigo-50 transition-colors">
                      <Settings2 className="w-3.5 h-3.5" />
                    </button>
                  </div>
                ))}

                <div className="mt-3 pt-3 border-t border-slate-200 space-y-2">
                  <label className="flex items-center gap-2 cursor-pointer bg-white p-2 rounded-lg border border-slate-200 hover:bg-slate-50 transition">
                    <input type="checkbox" checked={showDialogsAfterCC} onChange={(e) => setShowDialogsAfterCC(e.target.checked)}
                           className="w-4 h-4 rounded accent-indigo-600 cursor-pointer" />
                    <span className="text-xs text-slate-700"><strong>Show result dialogs</strong> after correction</span>
                  </label>

                  <label className="flex items-center gap-2 cursor-pointer bg-white p-2 rounded-lg border border-slate-200 hover:bg-slate-50 transition">
                    <input type="checkbox" checked={computeDeltaE} onChange={(e) => setComputeDeltaE(e.target.checked)}
                           className="w-4 h-4 rounded accent-indigo-600 cursor-pointer" />
                    <span className="text-xs text-slate-700"><strong>Compute ΔE metrics</strong> (single image only)</span>
                  </label>
                  <p className="text-xs text-slate-400 ml-6 flex items-center gap-1">
                    <Info className="w-3 h-3" /> Disable for faster processing
                  </p>
                </div>
              </div>
            </CollapsibleSection>

            {/* Run Action */}
            <div className="bg-white rounded-xl p-3 sm:p-4 border border-slate-200 shadow-sm">
              <button
                onClick={runCC}
                disabled={running || images.length === 0}
                className={`w-full px-4 py-3 sm:py-4 rounded-xl font-semibold text-sm sm:text-base transition-all duration-200 touch-manipulation inline-flex items-center justify-center gap-2 ${
                  running || images.length === 0
                    ? "bg-slate-100 text-slate-400 cursor-not-allowed"
                    : "bg-indigo-600 text-white hover:bg-indigo-700 shadow-md hover:shadow-lg hover:animate-glow"
                }`}
              >
                {running ? (
                  <>
                    <Loader2 className="w-5 h-5 animate-spin" />
                    Processing…
                  </>
                ) : (
                  <>
                    <Play className="w-5 h-5" />
                    Run Correction
                  </>
                )}
              </button>
            </div>

            {/* Batch Operations */}
            <CollapsibleSection icon={Zap} title="Batch Operations" isOpen={batchOpsExpanded} onToggle={() => setBatchOpsExpanded(!batchOpsExpanded)}>
              <div className="space-y-1.5">
                <button
                  onClick={openApplyDialog}
                  disabled={running || images.length === 0}
                  className={`w-full px-3 py-2 rounded-lg text-xs font-medium transition-colors inline-flex items-center justify-center gap-1.5 ${
                    running || images.length === 0
                      ? "bg-slate-100 text-slate-400 cursor-not-allowed"
                      : "bg-indigo-600 text-white hover:bg-indigo-700"
                  }`}
                >
                  <Wand2 className="w-3.5 h-3.5" /> Apply to Others
                </button>
                <button
                  onClick={openProcessAllDialog}
                  disabled={running || images.length === 0}
                  className={`w-full px-3 py-2 rounded-lg text-xs font-medium transition-colors inline-flex items-center justify-center gap-1.5 ${
                    running || images.length === 0
                      ? "bg-slate-100 text-slate-400 cursor-not-allowed"
                      : "bg-indigo-600 text-white hover:bg-indigo-700"
                  }`}
                >
                  <Zap className="w-3.5 h-3.5" /> Process All
                </button>
              </div>
            </CollapsibleSection>

            {/* Analysis */}
            <CollapsibleSection icon={BarChart3} title="Analysis" isOpen={analysisExpanded} onToggle={() => setAnalysisExpanded(!analysisExpanded)}>
              <div className="space-y-1.5">
                {[
                  { label: 'Difference', icon: GitCompare, onClick: () => setDifferenceDialogOpen(true), disabled: !comparisonData.corrected },
                  { label: 'Before / After', icon: Maximize2, onClick: () => setBeforeAfterDialogOpen(true), disabled: !comparisonData.original || !comparisonData.corrected },
                  { label: 'RGB Scatter', icon: ScatterChart, onClick: () => setScatterDialogOpen(true), disabled: !comparisonData.corrected },
                  { label: 'ΔE Metrics', icon: BarChart3, onClick: () => setDeltaEDialogOpen(true), disabled: Object.keys(deltaEValues).length === 0 },
                ].map(({ label, icon: Icon, onClick, disabled }) => (
                  <button key={label} onClick={onClick} disabled={disabled}
                    className={`w-full px-3 py-2 rounded-lg text-xs font-medium transition-colors inline-flex items-center gap-1.5 ${
                      disabled
                        ? "bg-slate-100 text-slate-400 cursor-not-allowed"
                        : "bg-white border border-slate-200 text-slate-700 hover:bg-slate-50 hover:border-indigo-300"
                    }`}
                  >
                    <Icon className="w-3.5 h-3.5" /> {label}
                  </button>
                ))}
              </div>
            </CollapsibleSection>

            {/* Data */}
            <CollapsibleSection icon={Save} title="Data" isOpen={dataExpanded} onToggle={() => setDataExpanded(!dataExpanded)}>
              <div className="space-y-1.5">
                <button onClick={openSaveDialog}
                  className="w-full px-3 py-2 rounded-lg bg-indigo-600 text-white text-xs font-medium hover:bg-indigo-700 transition-colors inline-flex items-center justify-center gap-1.5">
                  <Save className="w-3.5 h-3.5" /> Save Images
                </button>
                <button onClick={() => setModelModalOpen(true)}
                  className="w-full px-3 py-2 rounded-lg bg-indigo-600 text-white text-xs font-medium hover:bg-indigo-700 transition-colors inline-flex items-center justify-center gap-1.5">
                  <Package className="w-3.5 h-3.5" /> Models
                </button>
              </div>
            </CollapsibleSection>

            {/* System */}
            <CollapsibleSection icon={Activity} title="System" isOpen={systemExpanded} onToggle={() => setSystemExpanded(!systemExpanded)}>
              <div className="space-y-1.5">
                <button onClick={restartBackend} disabled={running}
                  className={`w-full px-3 py-2 rounded-lg text-xs font-medium transition-colors inline-flex items-center justify-center gap-1.5 ${
                    running ? "bg-slate-100 text-slate-400 cursor-not-allowed" : "bg-white border border-slate-200 text-slate-600 hover:bg-slate-50"
                  }`}>
                  <RotateCcw className="w-3.5 h-3.5" /> Restart Backend
                </button>
                <button onClick={exitApplication} disabled={running}
                  className={`w-full px-3 py-2 rounded-lg text-xs font-medium transition-colors inline-flex items-center justify-center gap-1.5 ${
                    running ? "bg-slate-100 text-slate-400 cursor-not-allowed" : "bg-red-600 text-white hover:bg-red-700"
                  }`}>
                  <LogOut className="w-3.5 h-3.5" /> Exit
                </button>
              </div>
            </CollapsibleSection>

            {/* Batch Progress */}
            {batchProgress.total > 0 && (
              <div className="bg-white border border-indigo-200 rounded-xl p-4 shadow-sm">
                <div className="text-sm font-semibold text-slate-800 mb-3 flex items-center gap-2">
                  <span>Batch Progress: {batchProgress.current}/{batchProgress.total}</span>
                  <span className="text-xs font-medium text-indigo-700 bg-indigo-100 px-2 py-0.5 rounded-full">
                    {Math.round((batchProgress.current / batchProgress.total) * 100)}%
                  </span>
                </div>
                <div className="w-full bg-slate-100 rounded-full h-2.5 mb-3 overflow-hidden">
                  <div
                    className="bg-indigo-600 h-2.5 rounded-full transition-all duration-300 ease-out relative overflow-hidden"
                    style={{ width: `${(batchProgress.current / batchProgress.total) * 100}%` }}
                  >
                    <div className="absolute inset-0 shimmer-overlay" />
                  </div>
                </div>
                <div className="text-xs text-slate-600 truncate bg-slate-50 px-3 py-1.5 rounded-lg">
                  {batchProgress.status}
                </div>
              </div>
            )}

            {/* Activity Log */}
            <div className="bg-slate-900 rounded-xl p-4 border border-slate-700">
              <h3 className="text-sm font-semibold text-slate-200 mb-3 flex items-center gap-2">
                <Activity className="w-4 h-4 text-emerald-400" />
                Activity Log
              </h3>
              <div
                ref={logContainerRef}
                className="h-36 sm:h-40 md:h-44 overflow-auto text-xs sm:text-sm font-mono text-green-400 whitespace-pre-wrap dark-scroll bg-black/30 rounded-lg p-3 border border-slate-700"
              >
                {logs || <span className="text-slate-500 italic">Ready to process images…</span>}
              </div>
            </div>
          </div>

          {/* Right image preview panel */}
          <div className="lg:col-span-9">
            <div className="bg-white rounded-2xl p-4 sm:p-5 md:p-6 border border-slate-200 shadow-sm h-full">
              <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center mb-4 md:mb-5 gap-3">
                <div className="flex flex-col gap-2 w-full sm:w-auto">
                  <h2 className="text-lg sm:text-xl md:text-2xl font-bold text-slate-800 flex items-center gap-2">
                    <FileImage className="w-5 h-5 text-indigo-500" />
                    Image Preview
                  </h2>
                  {previewLabel && (
                    <p className="text-sm font-medium text-indigo-600 ml-7 px-3 py-1 bg-indigo-50 rounded-lg inline-block">
                      {previewLabel}
                    </p>
                  )}
                </div>
                {chartDetected && (
                  <span className="px-3 py-1.5 bg-emerald-50 text-emerald-700 rounded-lg border border-emerald-200 text-sm font-medium flex items-center gap-1.5">
                    <CheckCircle2 className="w-4 h-4" /> Chart Detected
                  </span>
                )}
              </div>

              {images.length > 0 ? (
                <div className="space-y-4 md:space-y-5">
                  <div className="bg-slate-50 rounded-xl p-3 sm:p-4 border border-slate-200">
                    <img
                      src={selectedImage || images[0].url}
                      alt="Preview"
                      className="max-h-[250px] sm:max-h-[350px] md:max-h-[450px] lg:max-h-[550px] w-full object-contain mx-auto rounded-lg"
                    />
                  </div>

                  {images.length > 1 && (
                    <div className="bg-slate-50/50 rounded-xl p-3 border border-slate-200">
                      <p className="text-xs font-medium text-slate-500 mb-2 uppercase tracking-wide">Image Gallery ({images.length})</p>
                      <div className="flex gap-2.5 overflow-x-auto pb-2 dark-scroll">
                        {images.map((img) => (
                          <img
                            key={img.url}
                            src={img.url}
                            alt="Thumbnail"
                            onClick={() => setSelectedImage(img.url)}
                            className={`h-14 w-14 sm:h-16 sm:w-16 md:h-20 md:w-20 object-cover rounded-lg cursor-pointer transition-all flex-shrink-0 ${
                              selectedImage === img.url
                                ? "ring-2 ring-indigo-500 ring-offset-2 shadow-md"
                                : "ring-1 ring-slate-200 opacity-70 hover:opacity-100 hover:ring-slate-300"
                            }`}
                          />
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              ) : (
                <div className="h-[300px] sm:h-[400px] md:h-[500px] lg:h-[600px] flex flex-col items-center justify-center text-slate-400 bg-slate-50 rounded-xl border-2 border-dashed border-slate-200">
                  <FileImage className="w-16 h-16 sm:w-20 sm:h-20 text-slate-300 mb-4" />
                  <p className="text-lg sm:text-xl font-semibold text-slate-500">No images loaded</p>
                  <p className="text-sm mt-2 text-slate-400">Click "Load Images" to get started</p>
                  <div className="mt-6 px-4 py-2 bg-indigo-50 border border-indigo-200 rounded-lg text-sm text-indigo-600 flex items-center gap-2">
                    <Info className="w-4 h-4" /> You can load multiple images for batch processing
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* All Modals — conditionally rendered for performance */}
      {ffcModalOpen && <FFCSettingsModal isOpen onClose={() => setFfcModalOpen(false)} settings={ffcSettings} setSettings={setFfcSettings} />}
      {gcModalOpen && <GCSettingsModal isOpen onClose={() => setGcModalOpen(false)} settings={gcSettings} setSettings={setGcSettings} />}
      {wbModalOpen && <WBSettingsModal isOpen onClose={() => setWbModalOpen(false)} />}
      {ccModalOpen && <CCSettingsModal isOpen onClose={() => setCcModalOpen(false)} settings={ccSettings} setSettings={setCcSettings} saveCcModel={saveCcModel} setSaveCcModel={setSaveCcModel} />}
      {modelModalOpen && <ModelManagementModal isOpen onClose={() => setModelModalOpen(false)} running={running} isSavingModel={isSavingModel} modelSaveFolder={modelSaveFolder} setModelSaveFolder={setModelSaveFolder} onSaveModel={saveModel} />}
      {deltaEDialogOpen && <DeltaEModal isOpen onClose={() => setDeltaEDialogOpen(false)} deltaEValues={deltaEValues} />}
      {applyDialogOpen && <ApplyDialog isOpen onClose={() => setApplyDialogOpen(false)} images={images} selectedForApply={selectedForApply} onToggleSelection={toggleApplySelection} onApply={applyColorCorrection} />}
      {processAllDialogOpen && <ProcessAllDialog isOpen onClose={() => setProcessAllDialogOpen(false)} images={images} selectedForProcess={selectedForProcess} onToggleSelection={toggleProcessSelection} ffcEnabled={ffcEnabled} gcEnabled={gcEnabled} wbEnabled={wbEnabled} ccEnabled={ccEnabled} onProcess={processAllImages} />}
      {differenceDialogOpen && <DifferenceDialog isOpen onClose={() => setDifferenceDialogOpen(false)} />}
      {beforeAfterDialogOpen && <BeforeAfterDialog isOpen onClose={() => setBeforeAfterDialogOpen(false)} original={comparisonData.original} corrected={comparisonData.corrected} />}
      {scatterDialogOpen && <ScatterPlotDialog isOpen onClose={() => setScatterDialogOpen(false)} />}
      {saveStepsDialogOpen && <EnhancedSaveDialog isOpen onClose={() => setSaveStepsDialogOpen(false)} batchProcessComplete={batchProcessComplete} saveDirectory={saveDirectory} setSaveDirectory={setSaveDirectory} selectedStepsToSave={selectedStepsToSave} setSelectedStepsToSave={setSelectedStepsToSave} selectedImagesToSave={selectedImagesToSave} setSelectedImagesToSave={setSelectedImagesToSave} batchImagesList={batchImagesList} availableImages={availableImages} onSaveImages={saveImages} onSaveBatchImages={saveBatchImages} setBatchProcessComplete={setBatchProcessComplete} />}
    </div>
  );
}
