import React, { useState, useRef, useEffect, useCallback, useMemo, memo } from "react";

// Modern Modal Component with enhanced animations and glassmorphism
const Modal = memo(({ isOpen, onClose, title, children, maxWidth = 'max-w-2xl' }) => {
  if (!isOpen) return null;
  
  return (
    <div 
      className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50 p-4 animate-fadeIn" 
      onClick={onClose}
    >
      <div 
        className={`bg-white rounded-2xl shadow-2xl p-6 ${maxWidth} w-full max-h-[85vh] overflow-y-auto transform transition-all animate-slideUp`} 
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex justify-between items-start mb-6">
          <h2 className="text-2xl font-bold text-gray-900 leading-tight">{title}</h2>
          <button 
            onClick={onClose} 
            className="text-gray-400 hover:text-gray-600 transition-colors p-1 hover:bg-gray-100 rounded-lg"
            aria-label="Close"
          >
            <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
        {children}
      </div>
    </div>
  );
});

// Modern Card Component
const Card = memo(({ children, className = '', gradient = false }) => (
  <div className={`bg-white rounded-xl shadow-md border border-gray-200 p-4 transition-all hover:shadow-lg ${gradient ? 'bg-gradient-to-br from-white to-gray-50' : ''} ${className}`}>
    {children}
  </div>
));

// Modern Button Component with variants
const Button = memo(({ 
  children, 
  onClick, 
  variant = 'primary', 
  disabled = false, 
  className = '',
  fullWidth = false,
  size = 'md'
}) => {
  const baseClasses = "font-semibold rounded-lg transition-all duration-200 flex items-center justify-center gap-2";
  
  const variants = {
    primary: "bg-gradient-to-r from-indigo-600 to-purple-600 text-white hover:from-indigo-700 hover:to-purple-700 shadow-lg hover:shadow-xl active:scale-95",
    secondary: "bg-gray-100 text-gray-700 hover:bg-gray-200 border border-gray-300",
    success: "bg-gradient-to-r from-green-500 to-emerald-600 text-white hover:from-green-600 hover:to-emerald-700 shadow-lg",
    danger: "bg-gradient-to-r from-red-500 to-pink-600 text-white hover:from-red-600 hover:to-pink-700 shadow-lg",
    outline: "border-2 border-indigo-600 text-indigo-600 hover:bg-indigo-50"
  };
  
  const sizes = {
    sm: "px-3 py-1.5 text-xs",
    md: "px-4 py-2 text-sm",
    lg: "px-6 py-3 text-base"
  };
  
  const disabledClasses = "opacity-50 cursor-not-allowed hover:scale-100";
  
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className={`
        ${baseClasses}
        ${variants[variant]}
        ${sizes[size]}
        ${fullWidth ? 'w-full' : ''}
        ${disabled ? disabledClasses : ''}
        ${className}
      `}
    >
      {children}
    </button>
  );
});

// Collapsible Section Component
const CollapsibleSection = memo(({ title, icon, children, defaultOpen = false }) => {
  const [isOpen, setIsOpen] = useState(defaultOpen);
  
  return (
    <div className="border border-gray-200 rounded-xl overflow-hidden bg-white shadow-sm hover:shadow-md transition-shadow">
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="w-full px-4 py-3 flex items-center justify-between bg-gradient-to-r from-gray-50 to-white hover:from-gray-100 hover:to-gray-50 transition-colors"
      >
        <span className="font-semibold text-gray-800 flex items-center gap-2">
          <span className="text-xl">{icon}</span>
          {title}
        </span>
        <svg 
          className={`w-5 h-5 text-gray-500 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`} 
          fill="none" 
          stroke="currentColor" 
          viewBox="0 0 24 24"
        >
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>
      {isOpen && (
        <div className="p-4 bg-white border-t border-gray-100 animate-slideDown">
          {children}
        </div>
      )}
    </div>
  );
});

export default function ColorCorrectionUI() {
  // State management
  const [images, setImages] = useState([]);
  const [whiteImage, setWhiteImage] = useState(null);
  const [ccmFile, setCcmFile] = useState(null);
  const [logs, setLogs] = useState("");
  const [running, setRunning] = useState(false);
  const [selectedImage, setSelectedImage] = useState(null);
  const [method, setMethod] = useState("pls");
  const [chartDetected, setChartDetected] = useState(false);
  
  // Performance optimization: Use ref for logs buffer to reduce re-renders
  const logsBufferRef = useRef([]);
  const logsFlushTimerRef = useRef(null);
  
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
  const [saveDialogOpen, setSaveDialogOpen] = useState(false);
  
  // Save dialog states
  const [availableImages, setAvailableImages] = useState([]);
  const [selectedForSave, setSelectedForSave] = useState([]);
  const [saveDirectory, setSaveDirectory] = useState('');
  const [modelSaveFolder, setModelSaveFolder] = useState('');
  const [isSavingModel, setIsSavingModel] = useState(false); // Loading state for model save
  
  // Image preview state
  const [previewLabel, setPreviewLabel] = useState('');
  
  // DeltaE dialog state
  const [deltaEDialogOpen, setDeltaEDialogOpen] = useState(false);
  const [deltaEValues, setDeltaEValues] = useState({});
  
  // New feature states
  const [applyDialogOpen, setApplyDialogOpen] = useState(false);
  const [selectedForApply, setSelectedForApply] = useState([]);
  const [processAllDialogOpen, setProcessAllDialogOpen] = useState(false);
  const [batchProgress, setBatchProgress] = useState({ current: 0, total: 0, status: '' });
  const [batchProcessComplete, setBatchProcessComplete] = useState(false);
  
  // New comparison dialog states
  const [differenceDialogOpen, setDifferenceDialogOpen] = useState(false);
  const [beforeAfterDialogOpen, setBeforeAfterDialogOpen] = useState(false);
  const [comparisonData, setComparisonData] = useState({ original: null, corrected: null, difference: null });
  
  // Collapsible section states
  const [correctionsExpanded, setCorrectionsExpanded] = useState(false); // Collapsed by default
  const [batchOpsExpanded, setBatchOpsExpanded] = useState(false);
  const [analysisExpanded, setAnalysisExpanded] = useState(false);
  const [dataExpanded, setDataExpanded] = useState(false);
  const [systemExpanded, setSystemExpanded] = useState(false);
  
  // New feature states
  const [scatterDialogOpen, setScatterDialogOpen] = useState(false);
  const [scatterPlotData, setScatterPlotData] = useState(null);
  const [showDialogsAfterCC, setShowDialogsAfterCC] = useState(true);
  const [saveStepsDialogOpen, setSaveStepsDialogOpen] = useState(false);
  const [selectedStepsToSave, setSelectedStepsToSave] = useState(['CC']); // Default to final result
  const [selectedImagesToSave, setSelectedImagesToSave] = useState([]);
  const [batchImagesList, setBatchImagesList] = useState([]);
  
  // Settings states
  const [ffcSettings, setFfcSettings] = useState({
    manual_crop: false,
    bins: 50,
    smooth_window: 5,
    degree: 3,
    fit_method: 'pls',
    interactions: true,
    max_iter: 1000,
    tol: 1e-8,
    verbose: false,
    random_seed: 0
  });
  
  const [gcSettings, setGcSettings] = useState({
    max_degree: 5
  });
  
  const [ccSettings, setCcSettings] = useState({
    cc_method: 'ours',
    method: 'Finlayson 2015',
    mtd: 'pls',
    degree: 2,
    max_iterations: 10000,
    random_state: 0,
    tol: 1e-8,
    verbose: false,
    n_samples: 50,
    // PLS-specific
    ncomp: 1,
    // NN-specific
    nlayers: 100,
    hidden_layers: [64, 32, 16],
    learning_rate: 0.001,
    batch_size: 16,
    patience: 10,
    dropout_rate: 0.2,
    optim_type: 'adam',
    use_batch_norm: true
  });
  
  // File refs
  const fileInputRef = useRef();
  const ccmInputRef = useRef();
  const whiteInputRef = useRef();
  const logContainerRef = useRef();
  
  // Performance: Optimized log appending with batching
  const appendLog = useCallback((message) => {
    logsBufferRef.current.push(message);
    
    // Clear existing timer
    if (logsFlushTimerRef.current) {
      clearTimeout(logsFlushTimerRef.current);
    }
    
    // Batch log updates for better performance (flush after 50ms of inactivity)
    logsFlushTimerRef.current = setTimeout(() => {
      if (logsBufferRef.current.length > 0) {
        const bufferedLogs = logsBufferRef.current.join('');
        logsBufferRef.current = [];
        setLogs((prev) => prev + bufferedLogs);
      }
    }, 50);
  }, []);
  
  // Force flush logs immediately when needed (e.g., before long operations)
  const flushLogs = useCallback(() => {
    if (logsFlushTimerRef.current) {
      clearTimeout(logsFlushTimerRef.current);
    }
    if (logsBufferRef.current.length > 0) {
      const bufferedLogs = logsBufferRef.current.join('');
      logsBufferRef.current = [];
      setLogs((prev) => prev + bufferedLogs);
    }
  }, []);
  
  // Auto-scroll activity log to bottom when logs change
  useEffect(() => {
    if (logContainerRef.current) {
      logContainerRef.current.scrollTop = logContainerRef.current.scrollHeight;
    }
  }, [logs]);

  // Load images - Memoized with useCallback to prevent re-creation
  const handleLoadImages = useCallback(async (e) => {
    const files = Array.from(e.target.files || []);
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

  // Load white image for FFC
  const handleLoadWhiteImage = useCallback(async (e) => {
    const f = e.target.files && e.target.files[0];
    if (!f) return;
    
    setWhiteImage({ file: f, url: URL.createObjectURL(f) });
    appendLog(`\n📤 Uploading white image: ${f.name}...`);
    
    // Upload to backend
    try {
      const formData = new FormData();
      formData.append('white_image', f);
      
      const resp = await fetch("/api/upload-white-image", {
        method: "POST",
        body: formData
      });
      
      if (!resp.ok) {
        throw new Error(`HTTP ${resp.status}: ${await resp.text()}`);
      }
      
      const result = await resp.json();
      appendLog(`\n✓ ${result.message}`);
    } catch (err) {
      appendLog(`\n✗ White image upload failed: ${err.message}`);
    }
  }, [appendLog]);

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
    
    // Clear backend session (clears uploaded files from server)
    fetch("/api/clear-session", { 
      method: "POST" 
    }).catch(err => console.error("Failed to clear backend:", err));
    
    appendLog("\n✓ Cleared all images and reset application");
    flushLogs(); // Ensure log is displayed immediately
  }, [appendLog, flushLogs]);

  // Detect color chart
  async function detectChart() {
    if (images.length === 0) {
      setLogs((l) => l + "\n⚠ No images loaded.");
      return;
    }
    
    // Check if an image is selected
    if (!selectedImage || !images.some(img => img.url === selectedImage)) {
      setLogs((l) => l + "\n⚠ Please select an image first.");
      return;
    }
    
    // Find the selected image index
    const selectedIndex = images.findIndex(img => img.url === selectedImage);
    if (selectedIndex === -1) {
      setLogs((l) => l + "\n⚠ Selected image not found.");
      return;
    }
    
    setLogs((l) => l + `\n🔍 Detecting color chart on image ${selectedIndex + 1}...`);
    
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
        setLogs((l) => l + `\n✓ ${result.detection.message} (${(result.detection.confidence * 100).toFixed(0)}% confidence)`);
        
        // Update preview with visualization if available
        if (result.detection.visualization) {
          setSelectedImage(result.detection.visualization);
          setPreviewLabel('Chart Detected');
        }
        
        // Log patch information
        if (result.detection.patch_data && result.detection.patch_data.length > 0) {
          setLogs((l) => l + `\n  📊 Identified patches: ${result.detection.patch_data.slice(0, 6).map(p => p.name).join(', ')}...`);
        }
      } else {
        setChartDetected(false);
        setLogs((l) => l + `\n⚠ ${result.detection.message}`);
      }
    } catch (err) {
      setChartDetected(false);
      setLogs((l) => l + `\n✗ Chart detection failed: ${err.message}`);
    }
  }

  // Run color correction
  async function runCC() {
    if (images.length === 0) {
      setLogs((l) => l + "\n⚠ No images loaded.");
      return;
    }

    // Check if an image is selected, if not, prompt to select the first image
    if (!selectedImage || !images.some(img => img.url === selectedImage)) {
      const confirmSelection = window.confirm(
        "No image selected. Would you like to select the first image and proceed?"
      );
      
      if (confirmSelection && images.length > 0) {
        setSelectedImage(images[0].url);
        setLogs((l) => l + "\n✓ Selected first image automatically.");
        // Continue with the first image
      } else {
        setLogs((l) => l + "\n⚠ Please select an image first.");
        return;
      }
    }
    
    // Find the selected image index
    const selectedIndex = images.findIndex(img => img.url === selectedImage || img.url === images[0].url);
    if (selectedIndex === -1) {
      setLogs((l) => l + "\n⚠ Selected image not found.");
      return;
    }

    // Collapse the corrections tab when running
    setCorrectionsExpanded(false);

    // Clear old results before running new correction
    setPreviewLabel('');
    setDeltaEValues({});
    setDeltaEDialogOpen(false);  // Close dialog to force refresh
    setDifferenceDialogOpen(false);
    setBeforeAfterDialogOpen(false);
    setScatterDialogOpen(false);
    setComparisonData({ original: null, corrected: null, difference: null });
    setScatterPlotData(null);
    setLogs((l) => l + "\n🗑️ Cleared previous results");

    setRunning(true);
    setLogs((l) => l + `\n▶ Running Color Correction on image ${selectedIndex + 1}...\n`);

    try {
      // Get the method from ccSettings if using custom, otherwise use conventional
      const selectedMethod = ccSettings.cc_method === 'ours' ? ccSettings.mtd : 'conventional';
      
      const requestData = {
        method: selectedMethod,
        image_index: selectedIndex,  // Process only the selected image
        ffcEnabled: ffcEnabled,
        gcEnabled: gcEnabled,
        wbEnabled: wbEnabled,
        ccEnabled: ccEnabled,
        saveCcModel: saveCcModel,
        computeDeltaE: computeDeltaE,  // Pass Delta E computation preference
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
        // result.log already contains formatted text with newlines
        setLogs((l) => l + '\n' + (result.log || "✓ Completed successfully!"));
        
        // Replace detected chart preview with original image
        if (result.original_image) {
          setSelectedImage(result.original_image);
          setPreviewLabel('Original Image');
        }
        
        // Display DeltaE metrics using the summary from backend
        console.log('DEBUG: result.delta_e_summary =', result.delta_e_summary);
        
        if (result.delta_e_summary && Object.keys(result.delta_e_summary).length > 0) {
          // Display DE_mean in activity log in order: FFC, GC, WB, CC
          const deltaESteps = ['FFC', 'GC', 'WB', 'CC'];
          deltaESteps.forEach(step => {
            if (result.delta_e_summary[step] && result.delta_e_summary[step].DE_mean !== undefined) {
              const deMean = result.delta_e_summary[step].DE_mean;
              const deMeanStr = typeof deMean === 'number' ? deMean.toFixed(2) : deMean;
              
              // Add method name for CC step
              if (step === 'CC') {
                setLogs((l) => l + `\n  📊 ${step} (${selectedMethod}) - DE_mean: ${deMeanStr}`);
              } else {
                setLogs((l) => l + `\n  📊 ${step} - DE_mean: ${deMeanStr}`);
              }
            }
          });
          
          // Store method in delta_e_summary for dialog display
          const deltaEWithMethod = {
            ...result.delta_e_summary,
            _method: selectedMethod  // Store method for dialog
          };
          
          // Update DeltaE values first, then open dialog after a brief delay if enabled
          // This ensures React properly detects the state change
          setDeltaEValues(deltaEWithMethod);
          // Only show dialogs if not in batch processing mode
          if (showDialogsAfterCC && batchProgress.total === 0) {
            setTimeout(() => {
              setDeltaEDialogOpen(true);
            }, 100);
          }
        } else {
          console.log('DEBUG: No delta_e_summary in response');
          setLogs((l) => l + `\n  ℹ️ No ΔE metrics available (may not be enabled for all steps)`);
        }
        
        // If we have results, show them with difference map
        if (result.images && result.images.length > 0) {
          setLogs((l) => l + `\n✓ Generated ${result.images.length} result image(s)`);
          
          // Find the final corrected image (CC step)
          const ccImage = result.images.find(img => img.name.endsWith('_CC'));
          const finalImage = ccImage || result.images[result.images.length - 1];
          
          // Store comparison data for dialogs
          setComparisonData({
            original: result.original_image,
            corrected: finalImage.data,
            difference: result.diff_image
          });
          
          // Store scatter plot data if available
          if (result.scatter_plot) {
            setScatterPlotData(result.scatter_plot);
            setLogs((l) => l + `\n📈 RGB scatter plot generated`);
            // Only show dialogs if not in batch processing mode
            if (showDialogsAfterCC && batchProgress.total === 0) {
              setTimeout(() => {
                setScatterDialogOpen(true);
              }, 500);
            }
          }
          
          // Show difference image in dialog if available and enabled (not in batch mode)
          if (result.diff_image && ccEnabled && showDialogsAfterCC && batchProgress.total === 0) {
            setLogs((l) => l + `\n📊 Difference image available - click "View Difference" button`);
            setDifferenceDialogOpen(true);
          }
          
          // Show before/after comparison dialog if enabled (not in batch mode)
          if (result.original_image && finalImage.data && showDialogsAfterCC && batchProgress.total === 0) {
            setLogs((l) => l + `\n🔍 Before/After comparison available - click "Compare Images" button`);
            // Auto-open after difference dialog or immediately
            setTimeout(() => {
              setBeforeAfterDialogOpen(true);
            }, result.diff_image && ccEnabled ? 3000 : 100);
          }
          
          // Set preview to corrected image
          setSelectedImage(finalImage.data);
          setPreviewLabel('Corrected Image');
        }
      } else {
        setLogs((l) => l + `\n✗ Pipeline error: ${result.error}`);
      }
    } catch (err) {
      setLogs((l) => l + `\n✗ Error: ${err.message}`);
      console.error("Pipeline error:", err);
    } finally {
      setRunning(false);
    }
  }

  // Open save dialog
  async function openSaveDialog() {
    try {
      // Fetch available images from backend
      const resp = await fetch("/api/available-images");
      const result = await resp.json();
      
      if (result.success && result.images.length > 0) {
        setAvailableImages(result.images);
        setSelectedForSave(result.images.map(img => img.name)); // Select all by default
        setSaveDirectory(''); // Will use default on backend
        setSaveDialogOpen(true);
      } else {
        setLogs((l) => l + `\n⚠ No images available to save`);
      }
    } catch (err) {
      setLogs((l) => l + `\n✗ Failed to load images: ${err.message}`);
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
          // Select all images by default
          setSelectedImagesToSave(result.images.map(img => img.filename));
        }
      }
      
      setSaveStepsDialogOpen(true);
    } catch (err) {
      setLogs((l) => l + `\n✗ Failed to fetch available images: ${err.message}`);
    }
  }
  
  // Save images with step and image selection
  async function saveImages() {
    if (selectedStepsToSave.length === 0) {
      setLogs((l) => l + "\n⚠️ Please select at least one processing step.");
      return;
    }
    
    if (selectedImagesToSave.length === 0) {
      setLogs((l) => l + "\n⚠️ Please select at least one image.");
      return;
    }
    
    setSaveStepsDialogOpen(false);
    setLogs((l) => l + "\n💾 Saving selected images and steps...");
    
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
      setLogs((l) => l + `\n✓ ${result.message}`);
    } catch (err) {
      setLogs((l) => l + `\n✗ Save failed: ${err.message}`);
    }
  }
  
  // Toggle step selection for save
  function toggleStepSelection(step) {
    setSelectedStepsToSave(prev => 
      prev.includes(step) 
        ? prev.filter(s => s !== step)
        : [...prev, step]
    );
  }
  
  // Toggle image selection for save
  function toggleImageSelectionForSave(filename) {
    setSelectedImagesToSave(prev => 
      prev.includes(filename) 
        ? prev.filter(n => n !== filename)
        : [...prev, filename]
    );
  }

  // Load batch images list for selection
  async function loadBatchImagesList() {
    try {
      const resp = await fetch("/api/batch-images-list");
      const result = await resp.json();
      if (result.success) {
        setBatchImagesList(result.images || []);
        // Select all by default
        setSelectedImagesToSave(result.images.map(img => img.image_index));
      }
    } catch (err) {
      console.error("Error loading batch images list:", err);
    }
  }
  
  // Save selected batch processed images
  async function saveBatchImages() {
    if (selectedStepsToSave.length === 0) {
      setLogs((l) => l + "\n⚠️ Please select at least one processing step.");
      return;
    }
    
    // If specific images selected, use them; otherwise save all
    const imagesToSave = selectedImagesToSave.length > 0 ? selectedImagesToSave : null;
    
    setSaveStepsDialogOpen(false);
    setLogs((l) => l + `\n💾 Saving ${imagesToSave ? selectedImagesToSave.length : 'all'} batch processed image(s)...`);
    
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
        setLogs((l) => l + `\n✓ ${result.message}`);
        setLogs((l) => l + `\n📊 Saved ${result.saved_count} files from ${result.image_count} images`);
        setLogs((l) => l + `\n📁 Location: ${result.directory}`);
        
        // Clear selections after successful save
        setSelectedImagesToSave([]);
        setBatchProcessComplete(false);
      } else {
        setLogs((l) => l + `\n✗ Batch save failed: ${result.error}`);
      }
    } catch (err) {
      setLogs((l) => l + `\n✗ Batch save failed: ${err.message}`);
    }
  }

  // Save model
  async function saveModel() {
    setLogs((l) => l + "\n💾 Saving color correction model...");
    setIsSavingModel(true); // Start loading
    try {
      const resp = await fetch("/api/save-model", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ 
          name: `model_${Date.now()}`,
          folder: modelSaveFolder || null  // Send custom folder if provided
        })
      });
      
      // Check response status
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
        setLogs((l) => l + `\n✓ ${result.message}`);
        if (result.path) {
          setLogs((l) => l + `\n  📁 Saved to: ${result.path}`);
        }
        // Clear the folder input after successful save
        setModelSaveFolder('');
      } else {
        setLogs((l) => l + `\n✗ Failed: ${result.error || 'Unknown error'}`);
      }
    } catch (err) {
      setLogs((l) => l + `\n✗ Save model failed: ${err.message}`);
    } finally {
      setIsSavingModel(false); // Stop loading
    }
  }

  // Restart Backend - Automatic restart with backend support
  async function restartBackend() {
    if (!confirm("🔄 RESTART BACKEND\n\nThis will:\n✓ Automatically stop the current backend server\n✓ Start a new backend server process\n✓ Reconnect once online\n\nThe restart will happen automatically - no manual action needed!\n\nContinue?")) {
      return;
    }
    
    setLogs((l) => l + "\n" + "=".repeat(60));
    setLogs((l) => l + "\n🔄 AUTOMATIC BACKEND RESTART");
    setLogs((l) => l + "\n" + "=".repeat(60));
    setRunning(true);
    
    // Phase 1: Send restart command
    setLogs((l) => l + "\n\n📍 Phase 1: Sending restart command to backend");
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
        setLogs((l) => l + "\n  ✅ Restart initiated (PID: " + (data.pid || 'unknown') + ")");
        setLogs((l) => l + "\n  🔄 Backend will restart automatically...");
      }
    } catch (err) {
      if (err.name === 'AbortError' || err.message.includes('Failed to fetch')) {
        setLogs((l) => l + "\n  ✅ Restart command sent (connection closed as expected)");
      } else {
        setLogs((l) => l + "\n  ⚠️  " + err.message);
      }
    }
    
    // Wait for restart to begin
    await new Promise(resolve => setTimeout(resolve, 2000));
    
    // Phase 2: Verify shutdown
    setLogs((l) => l + "\n\n📍 Phase 2: Verifying old server shutdown");
    try {
      await fetch("/api/health", {
        signal: AbortSignal.timeout(1500)
      });
      setLogs((l) => l + "\n  ⚠️  Old server still responding");
    } catch {
      setLogs((l) => l + "\n  ✅ Old server confirmed offline");
    }
    
    // Phase 3: Wait for new server to start
    setLogs((l) => l + "\n\n📍 Phase 3: Waiting for new backend server");
    setLogs((l) => l + "\n  � Backend is restarting automatically...");
    setLogs((l) => l + "\n  ⏳ Polling for backend (60 seconds)...");
    
    // Poll for backend to come back up
    let attempts = 0;
    const maxAttempts = 60;
    const pollInterval = setInterval(async () => {
      attempts++;
      try {
        const healthResp = await fetch("/api/health", {
          signal: AbortSignal.timeout(1000)
        });
        if (healthResp.ok) {
          clearInterval(pollInterval);
          setLogs((l) => l + "\n\n✅ BACKEND IS BACK ONLINE!");
          setLogs((l) => l + "\n🎉 Automatic restart completed successfully!");
          setLogs((l) => l + "\n" + "=".repeat(60));
          setRunning(false);
        }
      } catch {
        if (attempts >= maxAttempts) {
          clearInterval(pollInterval);
          setLogs((l) => l + "\n\n⏱️  Timeout reached - backend not detected");
          setLogs((l) => l + "\n💡 Manual restart may be needed:");
          setLogs((l) => l + "\n   cd backend && python server_enhanced.py");
          setLogs((l) => l + "\n" + "=".repeat(60));
          setRunning(false);
        } else if (attempts % 5 === 0) {
          setLogs((l) => l + `\n  ⏳ Still waiting... (${attempts}s)`);
        }
      }
    }, 1000);
  }

  // Exit Application - Automatic Shutdown with Tab Close
  async function exitApplication() {
    const confirmMessage = `⚠️  APPLICATION SHUTDOWN

This will automatically:

🔴 BACKEND:
  ✓ Stop Python Flask server (Port 5000)
  ✓ Cleanup temporary files
  ✓ Terminate all child processes

🔴 FRONTEND:
  ✓ Close this browser tab

═══════════════════════════════════════

⚡ Both backend and frontend will shut down automatically.

Continue?`;

    if (!confirm(confirmMessage)) {
      return;
    }
    
    setLogs((l) => l + "\n" + "=".repeat(70));
    setLogs((l) => l + "\n🛑 AUTOMATIC SHUTDOWN INITIATED");
    setLogs((l) => l + "\n" + "=".repeat(70));
    setRunning(true);
    
    // Phase 1: Backend Termination (simulating Ctrl+C)
    setLogs((l) => l + "\n\n📍 PHASE 1: Backend Server Shutdown");
    setLogs((l) => l + "\n   Sending shutdown signal (equivalent to Ctrl+C)...");
    
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
        const data = await response.json();
        setLogs((l) => l + "\n   ✅ Backend shutdown signal sent");
        setLogs((l) => l + "\n   ⏳ Backend terminating gracefully...");
        backendShutdownSuccess = true;
      }
    } catch (err) {
      if (err.name === 'AbortError' || err.message.includes('Failed to fetch')) {
        // Connection closed means backend is shutting down - this is expected
        setLogs((l) => l + "\n   ✅ Backend shutdown initiated (connection closed)");
        backendShutdownSuccess = true;
      } else {
        setLogs((l) => l + "\n   ⚠️  Error: " + err.message);
      }
    }
    
    // Wait for backend to clean up
    setLogs((l) => l + "\n   ⏳ Waiting for backend cleanup...");
    await new Promise(resolve => setTimeout(resolve, 1500));
    
    // Phase 2: Verification
    setLogs((l) => l + "\n\n📍 PHASE 2: Verification");
    try {
      const verifyController = new AbortController();
      setTimeout(() => verifyController.abort(), 1000);
      
      await fetch("/api/health", {
        signal: verifyController.signal
      });
      setLogs((l) => l + "\n   ⚠️  Backend still responding (may need manual Ctrl+C)");
    } catch {
      setLogs((l) => l + "\n   ✅ Backend successfully terminated");
      setLogs((l) => l + "\n   ✅ Port 5000 is now free");
    }
    
    // Phase 3: Session Data Cleanup
    setLogs((l) => l + "\n\n📍 PHASE 3: Frontend Cleanup");
    try {
      setLogs((l) => l + "\n   🗑️  Clearing session data...");
      setImages([]);
      setSelectedImage(null);
      setWhiteImage(null);
      setDeltaEValues({});
      setScatterPlotData(null);
      setComparisonData({ original: null, corrected: null, difference: null });
      setLogs((l) => l + "\n   ✅ Session data cleared");
    } catch (err) {
      setLogs((l) => l + "\n   ⚠️  Cleanup warning: " + err.message);
    }
    
    // Phase 4: Final Summary & Auto-close
    setLogs((l) => l + "\n\n" + "=".repeat(70));
    setLogs((l) => l + "\n✅ SHUTDOWN COMPLETE");
    setLogs((l) => l + "\n" + "=".repeat(70));
    setLogs((l) => l + "\n\n✅ Backend: Stopped");
    setLogs((l) => l + "\n✅ Cleanup: Complete");
    setLogs((l) => l + "\n✅ Terminal: Ready for new commands");
    setLogs((l) => l + "\n\n🔄 Closing browser tab in 2 seconds...");
    setLogs((l) => l + "\n👋 Thank you for using Color Correction Studio!");
    
    // Stop the running flag and show completion message
    setTimeout(() => {
      setRunning(false);
      
      // Show a simple shutdown completion message without replacing the entire page
      const shutdownBanner = document.createElement('div');
      shutdownBanner.innerHTML = `
        <div style="position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.7); backdrop-filter: blur(4px); display: flex; align-items: center; justify-content: center; z-index: 10000; animation: fadeIn 0.3s ease-in;">
          <div style="background: white; padding: 2rem; border-radius: 1rem; box-shadow: 0 20px 40px rgba(0,0,0,0.3); max-width: 500px; width: 90%; text-align: center; animation: slideUp 0.3s ease-out;">
            <div style="font-size: 4rem; margin-bottom: 1rem;">✅</div>
            <h2 style="color: #1f2937; margin-bottom: 1rem; font-size: 1.5rem; font-weight: 700;">Backend Shutdown Complete</h2>
            
            <div style="background: linear-gradient(135deg, #f0fdf4 0%, #dcfce7 100%); border: 2px solid #86efac; padding: 1rem; border-radius: 0.5rem; margin-bottom: 1rem;">
              <p style="color: #166534; font-weight: 600; margin-bottom: 0.5rem;">✓ Backend Server Stopped</p>
              <p style="color: #15803d; font-size: 0.875rem; margin: 0.5rem 0;">🗑️ Temporary files cleaned</p>
              <p style="color: #15803d; font-size: 0.875rem; margin: 0.5rem 0;">� Terminal ready for commands</p>
            </div>
            
            <div style="background: #f9fafb; padding: 1rem; border-radius: 0.5rem; margin-bottom: 1rem; border: 1px solid #e5e7eb; text-align: left;">
              <p style="color: #374151; font-size: 0.875rem; margin-bottom: 0.75rem; font-weight: 600;">Next Steps:</p>
              <ul style="color: #6b7280; font-size: 0.875rem; margin: 0; padding-left: 1.25rem; list-style-type: none;">
                <li style="margin-bottom: 0.5rem;">✓ Terminal is in initial state (ready for commands)</li>
                <li style="margin-bottom: 0.5rem;">✓ Frontend dev server still running (reusable)</li>
                <li style="margin-bottom: 0.5rem;">✓ Close this tab when done</li>
              </ul>
            </div>
            
            <div style="display: flex; gap: 0.5rem; justify-content: center;">
              <button onclick="this.parentElement.parentElement.parentElement.remove()" style="background: #6366f1; color: white; border: none; padding: 0.75rem 1.5rem; border-radius: 0.5rem; font-size: 0.875rem; font-weight: 600; cursor: pointer; box-shadow: 0 2px 4px rgba(0,0,0,0.1); transition: all 0.2s;">
                Continue Working
              </button>
              <button onclick="window.close()" style="background: #ef4444; color: white; border: none; padding: 0.75rem 1.5rem; border-radius: 0.5rem; font-size: 0.875rem; font-weight: 600; cursor: pointer; box-shadow: 0 2px 4px rgba(0,0,0,0.1); transition: all 0.2s;">
                Close Tab
              </button>
            </div>
            
            <p style="color: #9ca3af; font-size: 0.75rem; margin-top: 1rem;">
              👋 Thank you for using Color Correction Studio
            </p>
          </div>
        </div>
        
        <style>
          @keyframes fadeIn {
            from { opacity: 0; }
            to { opacity: 1; }
          }
          @keyframes slideUp {
            from { opacity: 0; transform: translateY(20px); }
            to { opacity: 1; transform: translateY(0); }
          }
          button:hover {
            transform: translateY(-2px);
            box-shadow: 0 4px 8px rgba(0,0,0,0.2);
          }
        </style>
      `;
      
      document.body.appendChild(shutdownBanner);
      
      // Automatically close the tab after 1 second
      setTimeout(() => {
        window.close();
        
        // If window.close() doesn't work (browser security), show fallback after 800ms
        setTimeout(() => {
          if (!document.hidden) {
            shutdownBanner.innerHTML = `
              <div style="position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.95); backdrop-filter: blur(8px); display: flex; align-items: center; justify-center; z-index: 10000;">
                <div style="background: white; padding: 2.5rem; border-radius: 1rem; max-width: 450px; text-align: center; box-shadow: 0 20px 40px rgba(0,0,0,0.5);">
                  <div style="font-size: 3.5rem; margin-bottom: 1rem;">✅</div>
                  <h2 style="color: #1f2937; font-size: 1.75rem; font-weight: 700; margin-bottom: 1rem;">Backend Shutdown Complete</h2>
                  <div style="background: #f0fdf4; border: 2px solid #86efac; padding: 1rem; border-radius: 0.75rem; margin-bottom: 1.5rem;">
                    <p style="color: #166534; font-weight: 600; margin: 0.25rem 0;">✓ Backend Server Stopped</p>
                    <p style="color: #15803d; font-size: 0.875rem; margin: 0.25rem 0;">✓ Terminal ready for commands</p>
                  </div>
                  <p style="color: #6b7280; margin-bottom: 1.5rem; font-size: 0.95rem;">Please close this tab manually:</p>
                  <div style="display: flex; gap: 0.75rem; justify-center; flex-wrap: wrap; margin-bottom: 1rem;">
                    <kbd style="background: #f3f4f6; padding: 0.625rem 1.25rem; border-radius: 0.5rem; border: 1px solid #d1d5db; font-weight: 600; font-size: 0.95rem;">Ctrl + W</kbd>
                    <span style="color: #9ca3af; font-size: 1.25rem;">or</span>
                    <kbd style="background: #f3f4f6; padding: 0.625rem 1.25rem; border-radius: 0.5rem; border: 1px solid #d1d5db; font-weight: 600; font-size: 0.95rem;">⌘ + W</kbd>
                  </div>
                  <p style="color: #9ca3af; font-size: 0.8rem; margin-top: 1rem;">Or click the × on your browser tab</p>
                </div>
              </div>
            `;
          }
        }, 800);
      }, 1000);
    }, 2000);
  }

  // Open Apply CC Dialog
  async function openApplyDialog() {
    if (images.length === 0) {
      setLogs((l) => l + "\n⚠️ No images loaded.");
      return;
    }
    
    // Check if a trained model exists in the backend (from training or uploaded .pkl file)
    try {
      const resp = await fetch("/api/check-model");
      const result = await resp.json();
      
      if (!result.model_available) {
        setLogs((l) => l + "\n⚠️ No trained model available. Please:");
        setLogs((l) => l + "\n   1. Run color correction on at least one image, OR");
        setLogs((l) => l + "\n   2. Load a saved model file (.pkl) using 'Model/CCM File' button");
        return;
      }
      
      // Show what model source is being used
      if (result.model_source) {
        setLogs((l) => l + `\n✓ Using ${result.model_source} for application`);
      }
    } catch (err) {
      setLogs((l) => l + "\n⚠️ Could not check model status. Please run color correction or load a model file first.");
      return;
    }
    
    // Select all images by default (or all except selected if one is selected)
    let indicesToSelect;
    if (selectedImage) {
      const selectedIndex = images.findIndex(img => img.url === selectedImage);
      if (selectedIndex >= 0) {
        // Exclude the currently selected one
        indicesToSelect = images.map((_, idx) => idx).filter(idx => idx !== selectedIndex);
      } else {
        // Select all if selected image not found in list
        indicesToSelect = images.map((_, idx) => idx);
      }
    } else {
      // No image selected, select all
      indicesToSelect = images.map((_, idx) => idx);
    }
    
    setSelectedForApply(indicesToSelect);
    setApplyDialogOpen(true);
  }

  // Apply Color Correction to selected images using EXISTING trained model (inference only)
  // NOTE: Backend uses limited parallelism (max 2 workers) due to model thread-safety
  async function applyColorCorrection() {
    if (selectedForApply.length === 0) {
      setLogs((l) => l + "\n⚠️ No images selected.");
      return;
    }
    
    setApplyDialogOpen(false);
    setRunning(true);
    // Set batch progress to indicate batch processing (prevents dialogs from showing)
    setBatchProgress({ current: 0, total: selectedForApply.length, status: 'Initializing...' });
    setLogs((l) => l + "\n" + "=".repeat(70));
    setLogs((l) => l + "\n🎨 APPLY TO OTHERS - Model Application");
    setLogs((l) => l + "\n" + "=".repeat(70));
    setLogs((l) => l + `\n📊 Applying trained model to ${selectedForApply.length} image(s)...`);
    setLogs((l) => l + "\n💡 Mode: Sequential processing (one image at a time)");
    
    try {
      const selectedMethod = ccSettings.cc_method === 'ours' ? ccSettings.mtd : 'conventional';
      
      let processedCount = 0;
      let failedCount = 0;
      
      // Process each selected image sequentially
      for (let i = 0; i < selectedForApply.length; i++) {
        const imageIndex = selectedForApply[i];
        const img = images[imageIndex];
        
        setBatchProgress({ 
          current: i + 1, 
          total: selectedForApply.length, 
          status: `Processing ${img.file.name}...` 
        });
        
        setLogs((l) => l + `\n\n${"─".repeat(70)}`);
        setLogs((l) => l + `\n[${i + 1}/${selectedForApply.length}] ${img.file.name}`);
        setLogs((l) => l + `\n${"─".repeat(70)}`);
        setLogs((l) => l + `\n  🔧 Applying trained model...`);
        
        try {
          const applyResp = await fetch("/api/apply-single", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              image_index: imageIndex,
              method: selectedMethod,
              ffcEnabled: ffcEnabled,
              gcEnabled: gcEnabled,
              wbEnabled: wbEnabled,
              ccEnabled: ccEnabled,
              ffcSettings: ffcSettings,
              gcSettings: gcSettings,
              ccSettings: ccSettings
            })
          });
          
          let applyResult;
          try {
            applyResult = await applyResp.json();
          } catch (parseErr) {
            setLogs((l) => l + `\n  ❌ Failed to parse response: ${parseErr?.message || parseErr}`);
            failedCount++;
            continue;
          }
          
          if (applyResp.ok && applyResult.success) {
            processedCount++;
            setLogs((l) => l + `\n  ✅ Model applied successfully`);
            
            if (applyResult.corrected_images) {
              const stepCount = applyResult.corrected_images.length;
              setLogs((l) => l + `\n  📊 Generated ${stepCount} correction step(s)`);
            }
          } else {
            const message = applyResult?.error || `HTTP ${applyResp.status}`;
            setLogs((l) => l + `\n  ❌ Application error: ${message}`);
            failedCount++;
          }
        } catch (imgErr) {
          setLogs((l) => l + `\n  ❌ Error: ${imgErr?.message || imgErr}`);
          failedCount++;
        }
      }
      
      setBatchProgress({ 
        current: selectedForApply.length, 
        total: selectedForApply.length, 
        status: 'Complete!' 
      });
      
      setLogs((l) => l + `\n\n${"=".repeat(70)}`);
      setLogs((l) => l + `\n✅ BATCH APPLICATION COMPLETE`);
      setLogs((l) => l + `\n${"=".repeat(70)}`);
      setLogs((l) => l + `\n📊 Summary:`);
      setLogs((l) => l + `\n   • Total images: ${selectedForApply.length}`);
      setLogs((l) => l + `\n   • Successfully processed: ${processedCount}`);
      setLogs((l) => l + `\n   • Failed: ${failedCount}`);
      setLogs((l) => l + `\n${"=".repeat(70)}`);
      
      if (processedCount > 0) {
        setBatchProcessComplete(true);
        setLogs((l) => l + `\n💡 Tip: Click "Save Images" to save all ${processedCount} corrected images`);
      }
      
    } catch (err) {
      setLogs((l) => l + `\n\n❌ ERROR: ${err.message}`);
      setLogs((l) => l + "\n" + "=".repeat(60));
    } finally {
      setRunning(false);
      // Clear batch progress after completion
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

  // Process All Images - Train new model for EACH image with color chart
  async function processAllImages() {
    if (images.length === 0) {
      setLogs((l) => l + "\n⚠️ No images loaded.");
      return;
    }
    
    setProcessAllDialogOpen(false);
    setRunning(true);
    setBatchProgress({ current: 0, total: images.length, status: 'Starting...' });
    setLogs((l) => l + "\n" + "=".repeat(70));
    setLogs((l) => l + "\n⚡ PROCESS ALL - Train Model for Each Image");
    setLogs((l) => l + "\n" + "=".repeat(70));
    setLogs((l) => l + `\n📋 Processing ${images.length} image(s)...`);
    setLogs((l) => l + "\n💡 Mode: Sequential processing (one image at a time)");
    setLogs((l) => l + "\n📝 Starting batch processing...");
    
  let processedCount = 0;
  let failedCount = 0;
    
    try {
      for (let i = 0; i < images.length; i++) {
        const img = images[i];
        setBatchProgress({ current: i + 1, total: images.length, status: `Processing ${img.file.name}...` });
        setLogs((l) => l + `\n\n${"─".repeat(70)}`);
        setLogs((l) => l + `\n[${i + 1}/${images.length}] ${img.file.name}`);
        setLogs((l) => l + `\n${"─".repeat(70)}`);
        
        setLogs((l) => l + `\n  🔍 Step 1: Detecting color chart (optional)...`);

        let chartDetected = false;
        try {
          const detectResp = await fetch("/api/detect-chart", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ image_index: i })
          });

          const detectResult = await detectResp.json();
          const detection = detectResult?.detection;

          if (detectResp.ok && detectResult.success && detection?.detected) {
            chartDetected = true;
            const confidenceLabel = typeof detection?.confidence === 'number'
              ? ` (${(detection.confidence * 100).toFixed(0)}% confidence)`
              : '';
            setLogs((l) => l + `\n  ✅ Color chart detected${confidenceLabel}`);
          } else {
            const message = detection?.message || detectResult?.error || 'Chart detection did not confirm a chart.';
            setLogs((l) => l + `\n  ⚠️ ${message}`);
          }
        } catch (detectErr) {
          setLogs((l) => l + `\n  ⚠️ Chart detection skipped: ${detectErr?.message || detectErr}`);
        }

        if (!chartDetected) {
          setLogs((l) => l + `\n  ℹ️ Continuing without chart-guided adjustments.`);
        }

        setLogs((l) => l + `\n  🔧 Step 2: Running correction pipeline...`);

        // Run full correction pipeline with NEW MODEL for this image
        const ccResp = await fetch("/api/run-cc", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            image_index: i,
            is_batch_mode: true,
            computeDeltaE: false,
            method: ccSettings.cc_method === 'ours' ? ccSettings.mtd : 'conventional',
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

        let ccResult;
        try {
          ccResult = await ccResp.json();
        } catch (parseErr) {
          setLogs((l) => l + `\n  ❌ Failed to parse pipeline response: ${parseErr?.message || parseErr}`);
          failedCount++;
          continue;
        }

        if (ccResp.ok && ccResult.success) {
          processedCount++;
          setLogs((l) => l + `\n  ✅ Pipeline completed successfully`);

          if (ccResult.delta_e_summary) {
            setLogs((l) => l + `\n  📊 Quality Metrics:`);
            const steps = ['FFC', 'GC', 'WB', 'CC'];
            steps.forEach(step => {
              if (ccResult.delta_e_summary[step]?.DE_mean !== undefined) {
                const deMean = ccResult.delta_e_summary[step].DE_mean.toFixed(2);
                setLogs((l) => l + `\n     • ${step}: ΔE = ${deMean}`);
              }
            });
          }
        } else {
          const message = ccResult?.error || `HTTP ${ccResp.status}`;
          setLogs((l) => l + `\n  ❌ Pipeline error: ${message}`);
          failedCount++;
        }
      }
      
      setBatchProgress({ current: images.length, total: images.length, status: 'Complete!' });
      setLogs((l) => l + `\n\n${"=".repeat(70)}`);
      setLogs((l) => l + `\n✅ BATCH PROCESSING COMPLETE`);
      setLogs((l) => l + `\n${"=".repeat(70)}`);
      setLogs((l) => l + `\n📊 Summary:`);
      setLogs((l) => l + `\n   • Total images: ${images.length}`);
      setLogs((l) => l + `\n   • Successfully processed: ${processedCount}`);
      setLogs((l) => l + `\n   • Failed: ${failedCount}`);
      setLogs((l) => l + `\n${"=".repeat(70)}`);
      
      // Set batch complete flag if any images were processed
      if (processedCount > 0) {
        setBatchProcessComplete(true);
        setLogs((l) => l + `\n💡 Tip: Click "Save Images" to save all ${processedCount} corrected images`);
      }
      
    } catch (err) {
      setLogs((l) => l + `\n\n❌ Batch processing error: ${err.message}`);
      setLogs((l) => l + `\n${"=".repeat(70)}`);
    } finally {
      setRunning(false);
      setTimeout(() => {
        setBatchProgress({ current: 0, total: 0, status: '' });
      }, 3000);
    }
  }

  return (
    <div className="min-h-screen bg-gradient-to-br from-gray-950 via-indigo-950 to-gray-950 p-2 sm:p-4 md:p-6 lg:p-8">
      {/* Modern Header with glassmorphism */}
      <div className="max-w-[1600px] mx-auto mb-4 md:mb-6">
        <div className="bg-white/10 backdrop-blur-md rounded-2xl border border-white/20 p-4 md:p-6 shadow-2xl">
          <h1 className="text-2xl sm:text-3xl md:text-4xl font-bold text-white mb-2 flex items-center gap-3">
            <span className="text-3xl sm:text-4xl md:text-5xl">🎨</span>
            <span className="truncate bg-gradient-to-r from-blue-400 to-purple-400 bg-clip-text text-transparent">
              Color Correction Studio
            </span>
          </h1>
          <p className="text-gray-300 text-sm md:text-base ml-12 md:ml-14">
            A custom image color correction powered by ML algorithms
          </p>
        </div>
      </div>

      <div className="max-w-[1600px] mx-auto bg-gradient-to-br from-white/95 to-gray-50/95 backdrop-blur-xl rounded-2xl shadow-2xl p-3 sm:p-4 md:p-6 lg:p-8 border border-white/50">
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-3 md:gap-6 lg:gap-8">
          {/* Left control panel - Stacks on small screens, sidebar on large */}
          <div className="lg:col-span-3 space-y-3 md:space-y-4 lg:space-y-6">
            {/* File Management Section */}
            <div className="bg-gradient-to-br from-blue-50 via-indigo-50 to-blue-50 rounded-xl p-3 sm:p-4 border-2 border-blue-200 shadow-lg hover:shadow-xl transition-shadow">
              <h2 className="text-sm sm:text-base font-bold text-gray-800 mb-3 flex items-center gap-2">
                <span className="text-xl">📁</span> 
                <span className="truncate bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent">File Management</span>
              </h2>
              
              <div className="space-y-2">
                <label className="block">
                  <input
                    ref={fileInputRef}
                    type="file"
                    accept="image/*"
                    multiple
                    className="hidden"
                    onChange={handleLoadImages}
                  />
                  <button
                    onClick={() => fileInputRef.current.click()}
                    className="w-full px-3 sm:px-4 py-2.5 rounded-lg bg-gradient-to-r from-blue-500 to-indigo-600 text-white text-sm font-semibold hover:from-blue-600 hover:to-indigo-700 transition-all duration-200 shadow-md hover:shadow-lg active:scale-98 truncate"
                  >
                    📸 Load Images
                  </button>
                </label>

                <label className="block">
                  <input
                    ref={whiteInputRef}
                    type="file"
                    accept="image/*"
                    className="hidden"
                    onChange={handleLoadWhiteImage}
                  />
                  <button
                    onClick={() => whiteInputRef.current.click()}
                    className="w-full px-3 sm:px-4 py-2.5 rounded-lg bg-gradient-to-r from-cyan-500 to-blue-600 text-white text-sm font-semibold hover:from-cyan-600 hover:to-blue-700 transition-all duration-200 shadow-md hover:shadow-lg active:scale-98 truncate"
                  >
                    🟦 White Image
                  </button>
                </label>

                <label className="block">
                  <input
                    ref={ccmInputRef}
                    type="file"
                    accept=".pkl,.csv,.txt,.json,*"
                    className="hidden"
                    onChange={handleLoadCCM}
                  />
                  <button
                    onClick={() => ccmInputRef.current.click()}
                    className="w-full px-3 sm:px-4 py-2.5 rounded-lg bg-gradient-to-r from-purple-500 to-pink-600 text-white text-sm font-semibold hover:from-purple-600 hover:to-pink-700 transition-all duration-200 shadow-md hover:shadow-lg active:scale-98 truncate"
                  >
                    📊 Model/CCM File
                  </button>
                </label>

                <button
                  onClick={detectChart}
                  disabled={images.length === 0}
                  className="w-full px-3 sm:px-4 py-2.5 rounded-lg bg-gradient-to-r from-amber-500 to-orange-600 text-white text-sm font-semibold hover:from-amber-600 hover:to-orange-700 transition-all duration-200 shadow-md hover:shadow-lg disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:shadow-md disabled:active:scale-100 active:scale-98 truncate"
                >
                  🔍 Detect Chart
                </button>

                {images.length > 0 && (
                  <button
                    onClick={clearImages}
                    className="w-full px-3 sm:px-4 py-2.5 rounded-lg bg-white text-gray-700 text-sm font-semibold hover:bg-gray-50 transition-all duration-200 border-2 border-gray-300 hover:border-red-400 hover:text-red-600 shadow-sm hover:shadow-md active:scale-98 truncate"
                  >
                    🗑️ Clear All ({images.length})
                  </button>
                )}
              </div>
            </div>

            {/* Correction Settings Section */}
            <div className="bg-gradient-to-br from-green-50 via-emerald-50 to-teal-50 rounded-xl border-2 border-green-200 shadow-lg hover:shadow-xl transition-shadow overflow-hidden">
              <button
                onClick={() => setCorrectionsExpanded(!correctionsExpanded)}
                className="w-full px-4 py-3 flex items-center justify-between text-left hover:bg-green-100/50 transition-colors"
              >
                <span className="text-sm font-bold text-gray-800 flex items-center gap-2">
                  <span className="text-xl">⚙️</span> 
                  <span className="bg-gradient-to-r from-green-600 to-emerald-600 bg-clip-text text-transparent">
                    Corrections
                  </span>
                </span>
                <span className="text-gray-500 text-lg font-bold">{correctionsExpanded ? '▼' : '▶'}</span>
              </button>
              
              {correctionsExpanded && (
              <div className="px-3 pb-3 space-y-1.5">
                {[
                  { label: "FFC", state: ffcEnabled, setState: setFfcEnabled, openModal: () => setFfcModalOpen(true) },
                  { label: "GC", state: gcEnabled, setState: setGcEnabled, openModal: () => setGcModalOpen(true) },
                  { label: "WB", state: wbEnabled, setState: setWbEnabled, openModal: () => setWbModalOpen(true) },
                  { label: "CC", state: ccEnabled, setState: setCcEnabled, openModal: () => setCcModalOpen(true) }
                ].map(({ label, state, setState, openModal }) => (
                  <div key={label} className="flex items-center gap-2 bg-white p-2 rounded-lg border border-gray-200 shadow-sm">
                    <label className="flex items-center gap-2 flex-1 cursor-pointer">
                      <input 
                        type="checkbox" 
                        checked={state} 
                        onChange={(e) => setState(e.target.checked)}
                        className="w-4 h-4 rounded accent-indigo-600 cursor-pointer"
                      />
                      <span className="font-bold text-gray-700 text-xs">{label}</span>
                    </label>
                    <button 
                      onClick={openModal}
                      className="px-2 py-1 rounded-md text-xs font-medium bg-indigo-100 text-indigo-700 hover:bg-indigo-200 transition-colors border border-indigo-300"
                    >
                      Settings
                    </button>
                  </div>
                ))}
                
                {/* Show Dialogs Preference */}
                <div className="mt-3 pt-3 border-t border-green-200 space-y-2">
                  <label className="flex items-center gap-2 cursor-pointer bg-white p-2 rounded-lg border border-gray-200 shadow-sm hover:bg-green-50 transition">
                    <input 
                      type="checkbox" 
                      checked={showDialogsAfterCC} 
                      onChange={(e) => setShowDialogsAfterCC(e.target.checked)}
                      className="w-4 h-4 rounded accent-indigo-600 cursor-pointer"
                    />
                    <span className="text-xs text-gray-700">
                      <strong>Show result dialogs</strong> after correction
                    </span>
                  </label>
                  
                  {/* Delta E Computation Toggle */}
                  <label className="flex items-center gap-2 cursor-pointer bg-white p-2 rounded-lg border border-gray-200 shadow-sm hover:bg-blue-50 transition">
                    <input 
                      type="checkbox" 
                      checked={computeDeltaE} 
                      onChange={(e) => setComputeDeltaE(e.target.checked)}
                      className="w-4 h-4 rounded accent-blue-600 cursor-pointer"
                    />
                    <span className="text-xs text-gray-700">
                      <strong>Compute ΔE metrics</strong> (single image only)
                    </span>
                  </label>
                  <p className="text-xs text-gray-500 ml-6">
                    💡 Disable for faster processing without quality metrics
                  </p>
                </div>
              </div>
              )}
            </div>

            {/* Primary Action - Always Visible with responsive sizing */}
            <div className="bg-gradient-to-br from-green-400/20 via-emerald-400/20 to-teal-400/20 rounded-xl p-3 sm:p-4 border-2 border-green-400 shadow-lg hover:shadow-xl transition-shadow">
              <button
                onClick={runCC}
                disabled={running || images.length === 0}
                className={`w-full px-4 sm:px-5 py-3 sm:py-4 rounded-xl font-bold text-sm sm:text-base transition-all duration-200 touch-manipulation ${
                  running || images.length === 0
                    ? "bg-gray-300 text-gray-500 cursor-not-allowed shadow-inner"
                    : "bg-gradient-to-r from-green-500 via-emerald-600 to-teal-600 text-white hover:from-green-600 hover:via-emerald-700 hover:to-teal-700 shadow-lg hover:shadow-2xl active:scale-95 animate-pulse-slow"
                }`}
              >
                {running ? (
                  <span className="flex items-center justify-center gap-2">
                    <svg className="animate-spin h-5 w-5" viewBox="0 0 24 24">
                      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
                      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
                    </svg>
                    Processing...
                  </span>
                ) : (
                  "▶️ Run Correction"
                )}
              </button>
            </div>

            {/* Batch Operations - Collapsible with responsive touch */}
            <div className="bg-gradient-to-br from-orange-50 to-red-50 rounded-lg border border-orange-200 shadow-sm p-2 sm:p-0">
              <button
                onClick={() => setBatchOpsExpanded(!batchOpsExpanded)}
                className="w-full px-2 sm:px-3 py-2 flex items-center justify-between text-left hover:bg-orange-100/50 rounded-lg transition-colors touch-manipulation"
              >
                <span className="text-xs font-bold text-gray-700 uppercase tracking-wide truncate">⚡ Batch Ops</span>
                <span className="text-gray-500 text-sm flex-shrink-0 ml-2">{batchOpsExpanded ? '▼' : '▶'}</span>
              </button>
              {batchOpsExpanded && (
                <div className="px-2 sm:px-3 pb-2 sm:pb-3 space-y-1.5">
                  <button 
                    onClick={openApplyDialog}
                    disabled={running || images.length === 0}
                    className={`w-full px-2 sm:px-3 py-1.5 rounded text-xs font-medium transition-all touch-manipulation truncate ${
                      running || images.length === 0
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-gradient-to-r from-cyan-500 to-teal-600 text-white hover:from-cyan-600 hover:to-teal-700"
                    }`}
                  >
                    🎨 Apply to Others
                  </button>
                  <button 
                    onClick={() => setProcessAllDialogOpen(true)}
                    disabled={running || images.length === 0}
                    className={`w-full px-3 py-1.5 rounded text-xs font-medium transition-all ${
                      running || images.length === 0
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-gradient-to-r from-orange-500 to-red-600 text-white hover:from-orange-600 hover:to-red-700"
                    }`}
                  >
                    ⚡ Process All
                  </button>
                </div>
              )}
            </div>

            {/* Results & Analysis - Collapsible */}
            <div className="bg-gradient-to-br from-amber-50 to-yellow-50 rounded-lg border border-amber-200 shadow-sm">
              <button
                onClick={() => setAnalysisExpanded(!analysisExpanded)}
                className="w-full px-3 py-2 flex items-center justify-between text-left hover:bg-amber-100/50 rounded-lg transition-colors"
              >
                <span className="text-xs font-bold text-gray-700 uppercase tracking-wide">📊 Analysis</span>
                <span className="text-gray-500 text-sm">{analysisExpanded ? '▼' : '▶'}</span>
              </button>
              {analysisExpanded && (
                <div className="px-3 pb-3 space-y-1.5">
                  <button 
                    onClick={() => setDifferenceDialogOpen(true)}
                    disabled={!comparisonData.difference}
                    className={`w-full px-3 py-1.5 rounded text-xs font-medium transition-all ${
                      !comparisonData.difference
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-white border border-yellow-400 text-yellow-700 hover:bg-yellow-50"
                    }`}
                  >
                    🔬 Difference
                  </button>
                  <button 
                    onClick={() => setBeforeAfterDialogOpen(true)}
                    disabled={!comparisonData.original || !comparisonData.corrected}
                    className={`w-full px-3 py-1.5 rounded text-xs font-medium transition-all ${
                      !comparisonData.original || !comparisonData.corrected
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-white border border-green-400 text-green-700 hover:bg-green-50"
                    }`}
                  >
                    📷 Before/After
                  </button>
                  <button 
                    onClick={() => setScatterDialogOpen(true)}
                    disabled={!scatterPlotData}
                    className={`w-full px-3 py-1.5 rounded text-xs font-medium transition-all ${
                      !scatterPlotData
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-white border border-purple-400 text-purple-700 hover:bg-purple-50"
                    }`}
                  >
                    � RGB Scatter
                  </button>
                  <button 
                    onClick={() => setDeltaEDialogOpen(true)}
                    disabled={Object.keys(deltaEValues).length === 0}
                    className={`w-full px-3 py-1.5 rounded text-xs font-medium transition-all ${
                      Object.keys(deltaEValues).length === 0
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-white border border-blue-400 text-blue-700 hover:bg-blue-50"
                    }`}
                  >
                    📊 ΔE Metrics
                  </button>
                </div>
              )}
            </div>

            {/* Data Management - Collapsible */}
            <div className="bg-gradient-to-br from-indigo-50 to-purple-50 rounded-lg border border-indigo-200 shadow-sm">
              <button
                onClick={() => setDataExpanded(!dataExpanded)}
                className="w-full px-3 py-2 flex items-center justify-between text-left hover:bg-indigo-100/50 rounded-lg transition-colors"
              >
                <span className="text-xs font-bold text-gray-700 uppercase tracking-wide">💾 Data</span>
                <span className="text-gray-500 text-sm">{dataExpanded ? '▼' : '▶'}</span>
              </button>
              {dataExpanded && (
                <div className="px-3 pb-3 space-y-1.5">
                  <button 
                    onClick={openSaveDialog}
                    className="w-full px-3 py-1.5 rounded bg-gradient-to-r from-indigo-500 to-blue-600 text-white text-xs font-medium hover:from-indigo-600 hover:to-blue-700 transition-all"
                  >
                    💾 Save
                  </button>
                  <button 
                    onClick={() => setModelModalOpen(true)}
                    className="w-full px-3 py-1.5 rounded bg-gradient-to-r from-violet-500 to-purple-600 text-white text-xs font-medium hover:from-violet-600 hover:to-purple-700 transition-all"
                  >
                    📦 Models
                  </button>
                </div>
              )}
            </div>

            {/* System Controls - Collapsible */}
            <div className="bg-gradient-to-br from-rose-50 to-red-50 rounded-lg border border-rose-200 shadow-sm">
              <button
                onClick={() => setSystemExpanded(!systemExpanded)}
                className="w-full px-3 py-2 flex items-center justify-between text-left hover:bg-rose-100/50 rounded-lg transition-colors"
              >
                <span className="text-xs font-bold text-gray-700 uppercase tracking-wide">🔧 System</span>
                <span className="text-gray-500 text-sm">{systemExpanded ? '▼' : '▶'}</span>
              </button>
              {systemExpanded && (
                <div className="px-3 pb-3 space-y-1.5">
                  <button 
                    onClick={restartBackend}
                    disabled={running}
                    className={`w-full px-3 py-1.5 rounded text-xs font-medium transition-all ${
                      running
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-white border border-rose-400 text-rose-600 hover:bg-rose-50"
                    }`}
                  >
                    🔄 Restart
                  </button>
                  <button 
                    onClick={exitApplication}
                    disabled={running}
                    className={`w-full px-3 py-1.5 rounded font-bold text-xs transition-all ${
                      running
                        ? "bg-gray-200 text-gray-400 cursor-not-allowed"
                        : "bg-gradient-to-r from-red-600 to-red-700 text-white hover:from-red-700 hover:to-red-800"
                    }`}
                  >
                    🚪 Exit
                  </button>
                </div>
              )}
            </div>

            {/* Batch Progress Indicator - Modern design with better visibility */}
            {batchProgress.total > 0 && (
              <div className="bg-gradient-to-br from-blue-50 via-indigo-50 to-blue-50 border-2 border-blue-400 rounded-xl p-4 shadow-lg">
                <div className="text-sm sm:text-base font-bold text-blue-900 mb-3 flex flex-wrap items-center gap-2">
                  <span className="whitespace-nowrap">Batch Progress: {batchProgress.current}/{batchProgress.total}</span>
                  <span className="text-sm font-bold text-white bg-blue-600 px-2 py-1 rounded-lg whitespace-nowrap">
                    {Math.round((batchProgress.current / batchProgress.total) * 100)}%
                  </span>
                </div>
                <div className="w-full bg-blue-200 rounded-full h-3 sm:h-4 mb-3 overflow-hidden shadow-inner border border-blue-300">
                  <div 
                    className="bg-gradient-to-r from-blue-500 via-indigo-600 to-blue-600 h-3 sm:h-4 rounded-full transition-all duration-300 ease-out shadow-md relative overflow-hidden"
                    style={{ width: `${(batchProgress.current / batchProgress.total) * 100}%` }}
                  >
                    <div className="absolute inset-0 animate-shimmer" />
                  </div>
                </div>
                <div className="text-xs sm:text-sm text-blue-700 font-medium truncate bg-white/50 px-3 py-1.5 rounded-lg">
                  {batchProgress.status}
                </div>
              </div>
            )}

            {/* Log Panel - Responsive sizing */}
            <div className="bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900 rounded-xl p-4 border-2 border-gray-700 shadow-2xl">
              <h3 className="text-sm sm:text-base font-bold text-gray-200 mb-3 flex items-center gap-2">
                <span className="text-xl">📋</span> 
                <span className="truncate bg-gradient-to-r from-green-400 to-emerald-400 bg-clip-text text-transparent">
                  Activity Log
                </span>
              </h3>
              <div 
                ref={logContainerRef}
                className="h-36 sm:h-40 md:h-44 overflow-auto text-xs sm:text-sm font-mono text-green-400 whitespace-pre-wrap scrollbar-thin scrollbar-thumb-gray-600 scrollbar-track-gray-800 bg-black/30 rounded-lg p-3 border border-gray-700"
              >
                {logs || <span className="text-gray-500 italic">Ready to process images...</span>}
              </div>
            </div>
          </div>

          {/* Right image preview panel - Responsive layout */}
          <div className="lg:col-span-9">
            <div className="bg-gradient-to-br from-gray-50 via-white to-gray-50 rounded-2xl p-4 sm:p-5 md:p-6 border-2 border-gray-200 shadow-xl h-full">
              <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center mb-4 md:mb-5 gap-3">
                <div className="flex flex-col gap-2 w-full sm:w-auto">
                  <h2 className="text-lg sm:text-xl md:text-2xl font-bold text-gray-800 flex items-center gap-3">
                    <span className="text-2xl sm:text-3xl">🖼️</span> 
                    <span className="truncate bg-gradient-to-r from-gray-800 to-gray-600 bg-clip-text text-transparent">
                      Image Preview
                    </span>
                  </h2>
                  {previewLabel && (
                    <p className="text-sm sm:text-base font-semibold text-indigo-600 ml-10 sm:ml-12 truncate px-3 py-1 bg-indigo-50 rounded-lg inline-block">
                      {previewLabel}
                    </p>
                  )}
                </div>
                {chartDetected && (
                  <span className="px-3 sm:px-4 py-2 bg-gradient-to-r from-green-100 to-emerald-100 text-green-700 rounded-xl border-2 border-green-400 text-sm font-bold flex items-center gap-2 whitespace-nowrap shadow-md">
                    <span className="text-lg">✓</span> Chart Detected
                  </span>
                )}
              </div>
              
              {images.length > 0 ? (
                <div className="space-y-4 md:space-y-5">
                  <div className="bg-gradient-to-br from-white to-gray-50 rounded-xl p-3 sm:p-4 border-2 border-gray-200 shadow-lg">
                    <img 
                      src={selectedImage || images[0].url} 
                      alt="Preview" 
                      className="max-h-[250px] sm:max-h-[350px] md:max-h-[450px] lg:max-h-[550px] w-full object-contain mx-auto rounded-lg shadow-xl"
                    />
                  </div>
                  
                  {images.length > 1 && (
                    <div className="bg-white/50 backdrop-blur-sm rounded-xl p-3 border border-gray-200">
                      <p className="text-xs font-semibold text-gray-600 mb-2 uppercase tracking-wide">Image Gallery ({images.length})</p>
                      <div className="flex gap-3 overflow-x-auto pb-2 scrollbar-thin scrollbar-thumb-indigo-400 scrollbar-track-gray-200">
                        {images.map((img, idx) => (
                          <img
                            key={idx}
                            src={img.url}
                            alt={`Thumbnail ${idx + 1}`}
                            onClick={() => setSelectedImage(img.url)}
                            className={`h-14 w-14 sm:h-18 sm:w-18 md:h-20 md:w-20 object-cover rounded-xl cursor-pointer border-3 transition-all hover:scale-110 hover:shadow-lg flex-shrink-0 ${
                              selectedImage === img.url ? "border-4 border-indigo-600 shadow-xl ring-2 ring-indigo-300" : "border-2 border-gray-300 opacity-70 hover:opacity-100"
                            }`}
                          />
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              ) : (
                <div className="h-[300px] sm:h-[400px] md:h-[500px] lg:h-[600px] flex flex-col items-center justify-center text-gray-400 bg-gradient-to-br from-gray-50 to-white rounded-xl border-2 border-dashed border-gray-300">
                  <div className="text-6xl sm:text-7xl md:text-8xl lg:text-9xl mb-4 md:mb-6 opacity-50">📷</div>
                  <p className="text-lg sm:text-xl md:text-2xl font-bold text-gray-600">No images loaded</p>
                  <p className="text-sm sm:text-base mt-2 text-gray-500">Click "Load Images" to get started</p>
                  <div className="mt-6 px-6 py-3 bg-blue-50 border border-blue-200 rounded-lg text-sm text-blue-700">
                    💡 Tip: You can load multiple images for batch processing
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* FFC Settings Modal */}
      <Modal isOpen={ffcModalOpen} onClose={() => setFfcModalOpen(false)} title="Flat Field Correction Settings">
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-semibold text-gray-700 mb-2">Fit Method</label>
            <select
              value={ffcSettings.fit_method}
              onChange={(e) => setFfcSettings({...ffcSettings, fit_method: e.target.value})}
              className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
            >
              <option value="linear">Linear Regression</option>
              <option value="pls">PLS Regression</option>
              <option value="nn">Neural Network</option>
              <option value="svm">Support Vector Machine</option>
            </select>
            <p className="text-xs text-gray-500 mt-1">Method for fitting the intensity profile</p>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Bins</label>
              <input
                type="number"
                value={ffcSettings.bins}
                onChange={(e) => setFfcSettings({...ffcSettings, bins: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="10"
                max="200"
              />
              <p className="text-xs text-gray-500 mt-1">Bins for intensity sampling</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Smooth Window</label>
              <input
                type="number"
                value={ffcSettings.smooth_window}
                onChange={(e) => setFfcSettings({...ffcSettings, smooth_window: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="3"
                max="21"
                step="2"
              />
              <p className="text-xs text-gray-500 mt-1">Smoothing window size (odd)</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Polynomial Degree</label>
              <input
                type="number"
                value={ffcSettings.degree}
                onChange={(e) => setFfcSettings({...ffcSettings, degree: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="1"
                max="10"
              />
              <p className="text-xs text-gray-500 mt-1">Polynomial expansion degree</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Max Iterations</label>
              <input
                type="number"
                value={ffcSettings.max_iter}
                onChange={(e) => setFfcSettings({...ffcSettings, max_iter: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="100"
                max="10000"
              />
              <p className="text-xs text-gray-500 mt-1">Max iterations for fitting</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Tolerance</label>
              <input
                type="number"
                value={ffcSettings.tol}
                onChange={(e) => setFfcSettings({...ffcSettings, tol: parseFloat(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                step="1e-9"
                min="1e-10"
                max="1e-3"
              />
              <p className="text-xs text-gray-500 mt-1">Stopping criterion tolerance</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Random Seed</label>
              <input
                type="number"
                value={ffcSettings.random_seed}
                onChange={(e) => setFfcSettings({...ffcSettings, random_seed: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="0"
                max="9999"
              />
              <p className="text-xs text-gray-500 mt-1">For reproducible results</p>
            </div>
          </div>

          <div className="space-y-2 border-t pt-3">
            <div className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={ffcSettings.manual_crop}
                onChange={(e) => setFfcSettings({...ffcSettings, manual_crop: e.target.checked})}
                className="w-5 h-5 rounded accent-indigo-600"
                id="manual_crop"
              />
              <label htmlFor="manual_crop" className="text-sm font-medium text-gray-700 cursor-pointer">
                Manual Crop (Select ROI manually)
              </label>
            </div>

            <div className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={ffcSettings.interactions}
                onChange={(e) => setFfcSettings({...ffcSettings, interactions: e.target.checked})}
                className="w-5 h-5 rounded accent-indigo-600"
                id="interactions"
              />
              <label htmlFor="interactions" className="text-sm font-medium text-gray-700 cursor-pointer">
                Include polynomial interactions
              </label>
            </div>

            <div className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={ffcSettings.verbose}
                onChange={(e) => setFfcSettings({...ffcSettings, verbose: e.target.checked})}
                className="w-5 h-5 rounded accent-indigo-600"
                id="ffc_verbose"
              />
              <label htmlFor="ffc_verbose" className="text-sm font-medium text-gray-700 cursor-pointer">
                Verbose output (detailed logging)
              </label>
            </div>
          </div>

          <div className="bg-blue-50 p-3 rounded-lg border border-blue-200">
            <p className="text-xs text-blue-800">
              <strong>Flat Field Correction</strong> removes vignetting and illumination non-uniformity using a white field image.
            </p>
          </div>

          <button
            onClick={() => setFfcModalOpen(false)}
            className="w-full px-4 py-3 bg-indigo-600 text-white font-semibold rounded-lg hover:bg-indigo-700 transition"
          >
            Save Settings
          </button>
        </div>
      </Modal>

      {/* GC Settings Modal */}
      <Modal isOpen={gcModalOpen} onClose={() => setGcModalOpen(false)} title="Gamma Correction Settings">
        <div className="space-y-4">
          <div>
            <label className="block text-sm font-semibold text-gray-700 mb-2">Maximum Polynomial Degree</label>
            <input
              type="number"
              value={gcSettings.max_degree}
              onChange={(e) => setGcSettings({...gcSettings, max_degree: parseInt(e.target.value)})}
              className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
              min="1"
              max="10"
            />
            <p className="text-sm text-gray-500 mt-1">Polynomial degree for fitting gamma profile (1-10)</p>
          </div>

          <div className="bg-blue-50 p-4 rounded-lg border border-blue-200">
            <p className="text-sm text-blue-800">
              <strong>Gamma Correction</strong> fits an optimum polynomial mapping between measured neutral patch 
              intensities and reference values, then applies it to the entire image.
            </p>
          </div>

          <button
            onClick={() => setGcModalOpen(false)}
            className="w-full px-4 py-3 bg-indigo-600 text-white font-semibold rounded-lg hover:bg-indigo-700 transition"
          >
            Save Settings
          </button>
        </div>
      </Modal>

      {/* WB Settings Modal */}
      <Modal isOpen={wbModalOpen} onClose={() => setWbModalOpen(false)} title="White Balance Settings">
        <div className="space-y-4">
          <div className="bg-blue-50 p-4 rounded-lg border border-blue-200">
            <p className="text-sm text-blue-800">
              <strong>White Balance</strong> performs diagonal white-balance correction using the neutral patches 
              of the color checker. Computes diagonal matrix and applies it to the entire image.
            </p>
          </div>

          <div className="bg-green-50 p-4 rounded-lg border border-green-200">
            <p className="text-sm text-green-800">
              ✓ This step uses default settings optimized for most cases.
            </p>
          </div>

          <button
            onClick={() => setWbModalOpen(false)}
            className="w-full px-4 py-3 bg-indigo-600 text-white font-semibold rounded-lg hover:bg-indigo-700 transition"
          >
            Close
          </button>
        </div>
      </Modal>

      {/* CC Settings Modal */}
      <Modal isOpen={ccModalOpen} onClose={() => setCcModalOpen(false)} title="Color Correction Settings">
        <div className="space-y-4 max-h-[70vh] overflow-y-auto">
          <div>
            <label className="block text-sm font-semibold text-gray-700 mb-2">CC Method</label>
            <select
              value={ccSettings.cc_method}
              onChange={(e) => setCcSettings({...ccSettings, cc_method: e.target.value})}
              className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
            >
              <option value="ours">Custom (ML-based)</option>
              <option value="conv">Conventional (Finlayson 2015)</option>
            </select>
            <p className="text-xs text-gray-500 mt-1">Choose between ML-based or conventional methods</p>
          </div>

          {ccSettings.cc_method === 'ours' && (
            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">ML Method</label>
              <select
                value={ccSettings.mtd}
                onChange={(e) => setCcSettings({...ccSettings, mtd: e.target.value})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
              >
                <option value="linear">Linear Regression</option>
                <option value="pls">PLS Regression</option>
                <option value="nn">Neural Network</option>
              </select>
              <p className="text-xs text-gray-500 mt-1">Machine learning method for color transformation</p>
            </div>
          )}

          {ccSettings.cc_method === 'conv' && (
            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Conventional Method</label>
              <select
                value={ccSettings.method}
                onChange={(e) => setCcSettings({...ccSettings, method: e.target.value})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
              >
                <option value="Finlayson 2015">Finlayson 2015</option>
              </select>
              <p className="text-xs text-gray-500 mt-1">Classical color correction algorithm</p>
            </div>
          )}

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Polynomial Degree</label>
              <input
                type="number"
                value={ccSettings.degree}
                onChange={(e) => setCcSettings({...ccSettings, degree: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="1"
                max="5"
              />
              <p className="text-xs text-gray-500 mt-1">Polynomial expansion degree</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Max Iterations</label>
              <input
                type="number"
                value={ccSettings.max_iterations}
                onChange={(e) => setCcSettings({...ccSettings, max_iterations: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="1000"
                max="50000"
              />
              <p className="text-xs text-gray-500 mt-1">Max iterations for fitting</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Random State</label>
              <input
                type="number"
                value={ccSettings.random_state}
                onChange={(e) => setCcSettings({...ccSettings, random_state: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="0"
                max="9999"
              />
              <p className="text-xs text-gray-500 mt-1">Random seed for reproducibility</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">Tolerance</label>
              <input
                type="number"
                value={ccSettings.tol}
                onChange={(e) => setCcSettings({...ccSettings, tol: parseFloat(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                step="1e-9"
                min="1e-10"
                max="1e-3"
              />
              <p className="text-xs text-gray-500 mt-1">Stopping criterion tolerance</p>
            </div>

            <div>
              <label className="block text-sm font-semibold text-gray-700 mb-2">N Samples</label>
              <input
                type="number"
                value={ccSettings.n_samples}
                onChange={(e) => setCcSettings({...ccSettings, n_samples: parseInt(e.target.value)})}
                className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500"
                min="1"
                max="100"
              />
              <p className="text-xs text-gray-500 mt-1">Pixel samples per patch</p>
            </div>

            <div className="flex items-center">
              <div className="flex items-center gap-2">
                <input
                  type="checkbox"
                  checked={ccSettings.verbose}
                  onChange={(e) => setCcSettings({...ccSettings, verbose: e.target.checked})}
                  className="w-5 h-5 rounded accent-indigo-600"
                  id="cc_verbose"
                />
                <label htmlFor="cc_verbose" className="text-sm font-medium text-gray-700 cursor-pointer">
                  Verbose output
                </label>
              </div>
            </div>
          </div>

          {/* PLS-specific settings */}
          {ccSettings.cc_method === 'ours' && ccSettings.mtd === 'pls' && (
            <div className="bg-purple-50 p-4 rounded-lg border-2 border-purple-300">
              <h4 className="font-bold text-gray-800 mb-3 flex items-center gap-2">
                <span className="text-purple-600">📊</span> PLS Regression Settings
              </h4>
              <div>
                <label className="block text-sm font-semibold text-gray-700 mb-2">Number of Components</label>
                <input
                  type="number"
                  value={ccSettings.ncomp}
                  onChange={(e) => setCcSettings({...ccSettings, ncomp: parseInt(e.target.value)})}
                  className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-purple-400"
                  min="1"
                  max="10"
                />
                <p className="text-xs text-gray-600 mt-1">Number of PLS components to use (latent variables)</p>
              </div>
            </div>
          )}

          {/* NN-specific settings */}
          {ccSettings.cc_method === 'ours' && ccSettings.mtd === 'nn' && (
            <div className="bg-cyan-50 p-4 rounded-lg border-2 border-cyan-300 space-y-4">
              <h4 className="font-bold text-gray-800 mb-3 flex items-center gap-2">
                <span className="text-cyan-600">🧠</span> Neural Network Settings
              </h4>
              
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-semibold text-gray-700 mb-2">Number of Layers</label>
                  <input
                    type="number"
                    value={ccSettings.nlayers}
                    onChange={(e) => setCcSettings({...ccSettings, nlayers: parseInt(e.target.value)})}
                    className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-cyan-400"
                    min="10"
                    max="500"
                  />
                  <p className="text-xs text-gray-600 mt-1">Total training epochs</p>
                </div>

                <div>
                  <label className="block text-sm font-semibold text-gray-700 mb-2">Learning Rate</label>
                  <input
                    type="number"
                    value={ccSettings.learning_rate}
                    onChange={(e) => setCcSettings({...ccSettings, learning_rate: parseFloat(e.target.value)})}
                    className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-cyan-400"
                    step="0.0001"
                    min="0.0001"
                    max="0.1"
                  />
                  <p className="text-xs text-gray-600 mt-1">Neural network learning rate</p>
                </div>

                <div>
                  <label className="block text-sm font-semibold text-gray-700 mb-2">Batch Size</label>
                  <input
                    type="number"
                    value={ccSettings.batch_size}
                    onChange={(e) => setCcSettings({...ccSettings, batch_size: parseInt(e.target.value)})}
                    className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-cyan-400"
                    min="4"
                    max="64"
                  />
                  <p className="text-xs text-gray-600 mt-1">Training batch size</p>
                </div>

                <div>
                  <label className="block text-sm font-semibold text-gray-700 mb-2">Patience</label>
                  <input
                    type="number"
                    value={ccSettings.patience}
                    onChange={(e) => setCcSettings({...ccSettings, patience: parseInt(e.target.value)})}
                    className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-cyan-400"
                    min="5"
                    max="50"
                  />
                  <p className="text-xs text-gray-600 mt-1">Early stopping patience</p>
                </div>

                <div>
                  <label className="block text-sm font-semibold text-gray-700 mb-2">Dropout Rate</label>
                  <input
                    type="number"
                    value={ccSettings.dropout_rate}
                    onChange={(e) => setCcSettings({...ccSettings, dropout_rate: parseFloat(e.target.value)})}
                    className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-cyan-400"
                    step="0.05"
                    min="0"
                    max="0.5"
                  />
                  <p className="text-xs text-gray-600 mt-1">Dropout for regularization</p>
                </div>

                <div>
                  <label className="block text-sm font-semibold text-gray-700 mb-2">Optimizer</label>
                  <select
                    value={ccSettings.optim_type}
                    onChange={(e) => setCcSettings({...ccSettings, optim_type: e.target.value})}
                    className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-cyan-400"
                  >
                    <option value="adam">Adam</option>
                    <option value="sgd">SGD</option>
                    <option value="rmsprop">RMSprop</option>
                  </select>
                  <p className="text-xs text-gray-600 mt-1">Optimization algorithm</p>
                </div>
              </div>

              <div>
                <label className="block text-sm font-semibold text-gray-700 mb-2">Hidden Layers</label>
                <input
                  type="text"
                  value={JSON.stringify(ccSettings.hidden_layers)}
                  onChange={(e) => {
                    try {
                      const parsed = JSON.parse(e.target.value);
                      if (Array.isArray(parsed)) {
                        setCcSettings({...ccSettings, hidden_layers: parsed});
                      }
                    } catch (err) {
                      // Invalid JSON, ignore
                    }
                  }}
                  className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-cyan-400 font-mono text-sm"
                  placeholder="[64, 32, 16]"
                />
                <p className="text-xs text-gray-600 mt-1">Array of hidden layer sizes, e.g., [64, 32, 16]</p>
              </div>

              <div className="flex items-center gap-2">
                <input
                  type="checkbox"
                  checked={ccSettings.use_batch_norm}
                  onChange={(e) => setCcSettings({...ccSettings, use_batch_norm: e.target.checked})}
                  className="w-5 h-5 rounded accent-cyan-600"
                  id="use_batch_norm"
                />
                <label htmlFor="use_batch_norm" className="text-sm font-medium text-gray-700 cursor-pointer">
                  Use Batch Normalization
                </label>
              </div>
            </div>
          )}

          <div className="bg-amber-50 p-4 rounded-lg border border-amber-200">
            <div className="flex items-center gap-3">
              <input 
                type="checkbox" 
                checked={saveCcModel} 
                onChange={(e) => setSaveCcModel(e.target.checked)}
                className="w-5 h-5 rounded accent-indigo-600 cursor-pointer"
                id="saveCcModelInModal"
              />
              <label htmlFor="saveCcModelInModal" className="font-semibold text-gray-700 cursor-pointer flex items-center gap-2">
                <span className="text-lg">💾</span> Save CC Model After Correction
              </label>
            </div>
            <p className="text-xs text-gray-600 mt-2 ml-8">
              Automatically save the trained color correction model for future use
            </p>
          </div>

          <div className="bg-blue-50 p-3 rounded-lg border border-blue-200">
            <p className="text-xs text-blue-800">
              <strong>Color Correction</strong> applies machine learning or conventional methods to map color checker 
              patches to their reference values, then applies the transformation to the entire image.
            </p>
          </div>

          <button
            onClick={() => setCcModalOpen(false)}
            className="w-full px-4 py-3 bg-indigo-600 text-white font-semibold rounded-lg hover:bg-indigo-700 transition"
          >
            Save Settings
          </button>
        </div>
      </Modal>

      {/* Model Management Modal */}
      <Modal isOpen={modelModalOpen} onClose={() => setModelModalOpen(false)} title="Model Management">
        <div className="space-y-4">
          <div className="bg-gradient-to-r from-violet-50 to-purple-50 p-4 rounded-lg border border-purple-200">
            <h3 className="font-bold text-gray-800 mb-2">📦 Model Management</h3>
            <p className="text-sm text-gray-600">Save trained color correction models for reuse across images</p>
          </div>

          {/* Save Directory Selection */}
          <div className="bg-gray-50 p-4 rounded-lg border border-gray-200">
            <h4 className="font-semibold text-gray-800 mb-3">💾 Save Model</h4>
            <p className="text-sm text-gray-600 mb-3">
              <span className="font-medium">Custom Save Directory (optional):</span>
            </p>
            <input
              type="text"
              value={modelSaveFolder}
              onChange={(e) => setModelSaveFolder(e.target.value)}
              placeholder="e.g., C:\Users\YourName\Desktop\models"
              className="w-full px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-purple-500 font-mono text-sm mb-2"
            />
            <div className="flex items-start gap-2 mb-3">
              <span className="text-xs text-gray-500">💡</span>
              <p className="text-xs text-gray-500">
                Leave blank to save in the default <code className="bg-gray-200 px-1 rounded">models/</code> folder.
                You can specify any directory (e.g., Desktop, Documents, external drive).
              </p>
            </div>
            
            <button
              onClick={saveModel}
              className={`w-full px-4 py-3 text-white font-semibold rounded-lg transition flex items-center justify-center gap-2 shadow-md ${
                isSavingModel 
                  ? 'bg-blue-500 cursor-wait' 
                  : 'bg-green-500 hover:bg-green-600'
              }`}
              disabled={running || isSavingModel}
            >
              {isSavingModel ? (
                <>
                  <svg className="animate-spin h-5 w-5 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                  </svg>
                  <span>Saving...</span>
                </>
              ) : (
                <>
                  <span className="text-xl">💾</span>
                  <span>Save Current Model</span>
                </>
              )}
            </button>
            
            <p className="text-xs text-gray-500 mt-2 italic">
              ⚠️ Requires a trained model from a completed color correction
            </p>
          </div>

          <div className="bg-blue-50 p-4 rounded-lg border border-blue-200">
            <h4 className="font-semibold text-gray-800 mb-2 flex items-center gap-2">
              <span>📂</span>
              <span>Load Saved Model</span>
              <span className="text-xs bg-yellow-200 text-yellow-800 px-2 py-0.5 rounded-full">Coming Soon</span>
            </h4>
            <p className="text-sm text-gray-600">
              Load previously saved models to apply corrections without retraining.
            </p>
          </div>

          {/* Close Button */}
          <button
            onClick={() => setModelModalOpen(false)}
            className="w-full px-4 py-3 bg-gray-600 text-white font-semibold rounded-lg hover:bg-gray-700 transition flex items-center justify-center gap-2"
          >
            <span>✕</span>
            <span>Close</span>
          </button>

          <div className="bg-gray-50 p-3 rounded-lg border border-gray-200">
            <p className="text-xs text-gray-600">
              <span className="font-semibold">How it works:</span> After running color correction with a color chart,
              click "Save Current Model" to store the trained model. You can then use it later without needing a color chart.
            </p>
          </div>
        </div>
      </Modal>

      {/* Save Images Dialog Modal */}
      <Modal isOpen={saveDialogOpen} onClose={() => setSaveDialogOpen(false)} title="Save Corrected Images">
        <div className="space-y-4">
          <div className="bg-gradient-to-r from-blue-50 to-cyan-50 p-4 rounded-lg border border-blue-200">
            <h3 className="font-bold text-gray-800 mb-2">💾 Select Images to Save</h3>
            <p className="text-sm text-gray-600">Choose which processed images you want to export</p>
          </div>

          {/* Image Selection */}
          <div className="space-y-2 max-h-64 overflow-y-auto">
            {availableImages.length > 0 ? (
              <>
                <div className="flex items-center gap-2 mb-3 pb-2 border-b">
                  <input
                    type="checkbox"
                    checked={selectedForSave.length === availableImages.length}
                    onChange={(e) => {
                      if (e.target.checked) {
                        setSelectedForSave(availableImages.map(img => img.name));
                      } else {
                        setSelectedForSave([]);
                      }
                    }}
                    className="w-5 h-5 rounded accent-indigo-600"
                    id="select-all"
                  />
                  <label htmlFor="select-all" className="font-semibold text-gray-700 cursor-pointer">
                    Select All ({availableImages.length} images)
                  </label>
                </div>
                
                {availableImages.map((img, idx) => (
                  <div key={idx} className="flex items-center gap-3 bg-white p-3 rounded-lg border border-gray-200 hover:border-indigo-400 transition">
                    <input
                      type="checkbox"
                      checked={selectedForSave.includes(img.name)}
                      onChange={() => toggleImageSelection(img.name)}
                      className="w-5 h-5 rounded accent-indigo-600 cursor-pointer"
                      id={`img-${idx}`}
                    />
                    <label htmlFor={`img-${idx}`} className="flex-1 text-sm font-medium text-gray-700 cursor-pointer">
                      {img.name}
                    </label>
                    <span className="text-xs text-gray-500">
                      {img.name.includes('FFC') && '🟦 FFC'}
                      {img.name.includes('GC') && '🌈 GC'}
                      {img.name.includes('WB') && '⚖️ WB'}
                      {img.name.includes('CC') && '🎯 CC'}
                    </span>
                  </div>
                ))}
              </>
            ) : (
              <div className="text-center py-8 text-gray-500">
                <p className="text-4xl mb-2">📭</p>
                <p>No processed images available</p>
              </div>
            )}
          </div>

          {/* Directory Selection */}
          <div>
            <label className="block text-sm font-semibold text-gray-700 mb-2">
              Save Directory (Optional)
            </label>
            <input
              type="text"
              value={saveDirectory}
              onChange={(e) => setSaveDirectory(e.target.value)}
              placeholder="Leave empty for default (results folder)"
              className="w-full px-3 py-2 border-2 border-gray-300 rounded-lg focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200 text-sm"
            />
            <p className="text-xs text-gray-500 mt-1">
              Default: results/ folder in project directory
            </p>
          </div>

          {/* Action Buttons */}
          <div className="flex gap-3">
            <button
              onClick={() => setSaveDialogOpen(false)}
              className="flex-1 px-4 py-3 bg-gray-200 text-gray-700 font-semibold rounded-lg hover:bg-gray-300 transition"
            >
              Cancel
            </button>
            <button
              onClick={saveImages}
              disabled={selectedForSave.length === 0}
              className={`flex-1 px-4 py-3 font-semibold rounded-lg transition ${
                selectedForSave.length > 0
                  ? 'bg-indigo-600 text-white hover:bg-indigo-700'
                  : 'bg-gray-300 text-gray-500 cursor-not-allowed'
              }`}
            >
              💾 Save {selectedForSave.length > 0 ? `(${selectedForSave.length})` : ''}
            </button>
          </div>
        </div>
      </Modal>

      {/* DeltaE Results Dialog */}
      <Modal
        isOpen={deltaEDialogOpen}
        onClose={() => setDeltaEDialogOpen(false)}
        title="ΔE (Delta E) Metrics - Color Accuracy Results"
        maxWidth="max-w-7xl"
      >
        <div className="space-y-4">
          <div className="bg-indigo-50 p-4 rounded-lg border-2 border-indigo-200">
            <p className="text-sm text-gray-700 mb-3">
              <strong>Delta E (ΔE)</strong> measures color difference between images. Lower values = better color accuracy.
            </p>
            <ul className="text-xs text-gray-600 space-y-1">
              <li>• <strong>ΔE &lt; 1.0:</strong> Not perceptible by human eye</li>
              <li>• <strong>ΔE 1.0-2.0:</strong> Perceptible only to trained observers</li>
              <li>• <strong>ΔE 2.0-3.5:</strong> Perceptible at a glance</li>
              <li>• <strong>ΔE 3.5-5.0:</strong> Clearly noticeable difference</li>
              <li>• <strong>ΔE &gt; 5.0:</strong> Obvious color difference</li>
            </ul>
          </div>

          {Object.keys(deltaEValues).length > 0 ? (
            <div className="space-y-4">
              <h3 className="font-bold text-gray-800 text-lg mb-3">📊 Delta E Metrics Table</h3>
              
              {/* Table view */}
              <div className="overflow-x-auto">
                <table className="w-full border-collapse">
                  <thead>
                    <tr className="bg-indigo-100">
                      <th className="border border-indigo-300 px-3 py-2 text-left font-bold text-gray-800">Step</th>
                      <th className="border border-indigo-300 px-3 py-2 text-center font-bold text-gray-800">DE_mean</th>
                      <th className="border border-indigo-300 px-3 py-2 text-center font-bold text-gray-800">DE_min</th>
                      <th className="border border-indigo-300 px-3 py-2 text-center font-bold text-gray-800">DE_max</th>
                      <th className="border border-indigo-300 px-3 py-2 text-center font-bold text-gray-800">DE_std</th>
                      <th className="border border-indigo-300 px-3 py-2 text-center font-bold text-gray-800">Quality</th>
                    </tr>
                  </thead>
                  <tbody>
                    {/* Display in order: FFC, GC, WB, CC */}
                    {['FFC', 'GC', 'WB', 'CC'].filter(step => deltaEValues[step]).map(step => {
                      const metrics = deltaEValues[step];
                      const ccMethod = deltaEValues._method || '';
                      const deMean = metrics.DE_mean;
                      const numValue = typeof deMean === 'number' ? deMean : parseFloat(deMean);
                      
                      let quality = '';
                      let rowBg = '';
                      let emoji = '';
                      
                      if (numValue < 1.0) {
                        quality = 'Excellent';
                        rowBg = 'bg-green-50';
                        emoji = '🟢';
                      } else if (numValue < 2.0) {
                        quality = 'Very Good';
                        rowBg = 'bg-green-50';
                        emoji = '🟢';
                      } else if (numValue < 3.5) {
                        quality = 'Good';
                        rowBg = 'bg-yellow-50';
                        emoji = '🟡';
                      } else if (numValue < 5.0) {
                        quality = 'Fair';
                        rowBg = 'bg-orange-50';
                        emoji = '🟠';
                      } else {
                        quality = 'Needs Improvement';
                        rowBg = 'bg-red-50';
                        emoji = '🔴';
                      }
                      
                      const formatValue = (val) => {
                        if (val === null || val === undefined) return '-';
                        return typeof val === 'number' ? val.toFixed(2) : val;
                      };
                      
                      return (
                        <tr key={step} className={rowBg}>
                          <td className="border border-gray-300 px-3 py-2 font-bold text-gray-800">
                            {step === 'FFC' && '🟦 FFC'}
                            {step === 'GC' && '🌈 GC'}
                            {step === 'WB' && '⚖️ WB'}
                            {step === 'CC' && `🎯 CC${ccMethod ? ` (${ccMethod})` : ''}`}
                          </td>
                          <td className="border border-gray-300 px-3 py-2 text-center font-bold text-lg text-gray-900">
                            {formatValue(metrics.DE_mean)}
                          </td>
                          <td className="border border-gray-300 px-3 py-2 text-center text-sm">
                            {formatValue(metrics.DE_min)}
                          </td>
                          <td className="border border-gray-300 px-3 py-2 text-center text-sm">
                            {formatValue(metrics.DE_max)}
                          </td>
                          <td className="border border-gray-300 px-3 py-2 text-center text-sm">
                            {formatValue(metrics.DE_std)}
                          </td>
                          <td className="border border-gray-300 px-3 py-2 text-center">
                            <span className="inline-flex items-center gap-1">
                              <span className="text-xl">{emoji}</span>
                              <span className="text-xs font-semibold">{quality}</span>
                            </span>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
              
              {/* Summary cards for quick view - in order: FFC, GC, WB, CC */}
              <div className="grid grid-cols-2 gap-3 mt-4">
                {['FFC', 'GC', 'WB', 'CC'].filter(step => deltaEValues[step]).map(step => {
                  const metrics = deltaEValues[step];
                  const ccMethod = deltaEValues._method || '';
                  const deMean = metrics.DE_mean;
                  const numValue = typeof deMean === 'number' ? deMean : parseFloat(deMean);
                  
                  let bgColor = '';
                  let emoji = '';
                  
                  if (numValue < 1.0) {
                    bgColor = 'bg-green-100 border-green-400';
                    emoji = '🟢';
                  } else if (numValue < 2.0) {
                    bgColor = 'bg-green-50 border-green-300';
                    emoji = '🟢';
                  } else if (numValue < 3.5) {
                    bgColor = 'bg-yellow-50 border-yellow-300';
                    emoji = '🟡';
                  } else if (numValue < 5.0) {
                    bgColor = 'bg-orange-50 border-orange-300';
                    emoji = '🟠';
                  } else {
                    bgColor = 'bg-red-50 border-red-300';
                    emoji = '🔴';
                  }
                  
                  return (
                    <div key={step} className={`p-3 rounded-lg border-2 ${bgColor}`}>
                      <div className="flex items-center gap-2 mb-1">
                        <span className="text-xl">{emoji}</span>
                        <span className="font-bold text-sm text-gray-800">
                          {step === 'FFC' && '🟦 FFC'}
                          {step === 'GC' && '🌈 GC'}
                          {step === 'WB' && '⚖️ WB'}
                          {step === 'CC' && `🎯 CC${ccMethod ? ` (${ccMethod})` : ''}`}
                        </span>
                      </div>
                      <div className="text-2xl font-bold text-gray-900">
                        {typeof deMean === 'number' ? deMean.toFixed(2) : deMean}
                      </div>
                      <div className="text-xs text-gray-600">DE_mean</div>
                    </div>
                  );
                })}
              </div>
            </div>
          ) : (
            <div className="text-center py-8 text-gray-500">
              <p className="text-4xl mb-2">📊</p>
              <p>No Delta E metrics available</p>
            </div>
          )}

          <button
            onClick={() => setDeltaEDialogOpen(false)}
            className="w-full px-4 py-3 bg-indigo-600 text-white font-semibold rounded-lg hover:bg-indigo-700 transition"
          >
            Close
          </button>
        </div>
      </Modal>

      {/* Apply Color Correction Dialog */}
      <Modal
        isOpen={applyDialogOpen}
        onClose={() => setApplyDialogOpen(false)}
        title="Apply Correction to Others"
      >
        <div className="space-y-4">
          <p className="text-gray-600">
            Select images to apply the trained color correction model:
          </p>

          {/* Image Selection */}
          <div className="max-h-96 overflow-y-auto border rounded-lg p-3 space-y-2">
            {images.map((img, idx) => (
              <label
                key={idx}
                className="flex items-center gap-3 p-3 border rounded-lg hover:bg-gray-50 cursor-pointer"
              >
                <input
                  type="checkbox"
                  checked={selectedForApply.includes(idx)}
                  onChange={() => toggleApplySelection(idx)}
                  className="w-5 h-5 rounded accent-indigo-600"
                />
                <img
                  src={img.url}
                  alt={img.file.name}
                  className="w-16 h-16 object-cover rounded"
                />
                <div className="flex-1">
                  <div className="font-medium text-sm text-gray-800">{img.file.name}</div>
                  <div className="text-xs text-gray-500">
                    {(img.file.size / 1024).toFixed(1)} KB
                  </div>
                </div>
              </label>
            ))}
          </div>

          {/* Selection Summary */}
          <div className="bg-blue-50 border border-blue-200 rounded-lg p-3">
            <p className="text-sm text-blue-800">
              <strong>{selectedForApply.length}</strong> of <strong>{images.length}</strong> images selected
            </p>
            <p className="text-xs text-blue-700 mt-1">
              � Images will be processed sequentially (one at a time)
            </p>
          </div>

          {/* Action Buttons - Responsive touch targets */}
          <div className="flex gap-2 sm:gap-3">
            <button
              onClick={() => setApplyDialogOpen(false)}
              className="flex-1 px-3 sm:px-4 py-2.5 sm:py-3 bg-gray-200 text-gray-700 text-sm sm:text-base font-semibold rounded-lg hover:bg-gray-300 transition touch-manipulation"
            >
              Cancel
            </button>
            <button
              onClick={applyColorCorrection}
              disabled={selectedForApply.length === 0}
              className={`flex-1 px-3 sm:px-4 py-2.5 sm:py-3 text-sm sm:text-base font-semibold rounded-lg transition touch-manipulation ${
                selectedForApply.length > 0
                  ? 'bg-cyan-600 text-white hover:bg-cyan-700 active:scale-95'
                  : 'bg-gray-300 text-gray-500 cursor-not-allowed'
              }`}
            >
              🎨 Apply ({selectedForApply.length})
            </button>
          </div>
        </div>
      </Modal>

      {/* Process All Images Dialog */}
      <Modal
        isOpen={processAllDialogOpen}
        onClose={() => setProcessAllDialogOpen(false)}
        title="Process All Images"
      >
        <div className="space-y-4">
          <div className="bg-orange-50 border border-orange-200 rounded-lg p-4">
            <p className="text-orange-800 font-semibold mb-2">⚡ Batch Processing Mode</p>
            <p className="text-sm text-orange-700">
              This will process all <strong>{images.length}</strong> loaded images through the complete pipeline.
            </p>
          </div>

          <div className="space-y-3">
            <div className="flex items-start gap-3">
              <span className="text-2xl">🔍</span>
              <div className="flex-1">
                <h4 className="font-semibold text-gray-800">1. Chart Detection</h4>
                <p className="text-sm text-gray-600">
                  Each image will be checked for color charts
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <span className="text-2xl">✅</span>
              <div className="flex-1">
                <h4 className="font-semibold text-gray-800">2. If Chart Detected</h4>
                <p className="text-sm text-gray-600">
                  Full correction pipeline will run (FFC → GC → WB → CC)
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <span className="text-2xl">⚠️</span>
              <div className="flex-1">
                <h4 className="font-semibold text-gray-800">3. If No Chart</h4>
                <p className="text-sm text-gray-600">
                  Image will be skipped (or you can apply previous correction)
                </p>
              </div>
            </div>
          </div>

          <div className="bg-gray-50 border border-gray-200 rounded-lg p-3">
            <p className="text-sm text-gray-700">
              <strong>Current Settings:</strong>
            </p>
            <ul className="text-sm text-gray-600 mt-2 space-y-1 ml-4">
              <li>• FFC: {ffcEnabled ? '✅ Enabled' : '❌ Disabled'}</li>
              <li>• GC: {gcEnabled ? '✅ Enabled' : '❌ Disabled'}</li>
              <li>• WB: {wbEnabled ? '✅ Enabled' : '❌ Disabled'}</li>
              <li>• CC: {ccEnabled ? '✅ Enabled' : '❌ Disabled'}</li>
              <li>• Processing: Sequential (one at a time)</li>
            </ul>
          </div>

          {/* Action Buttons - Responsive touch targets */}
          <div className="flex gap-2 sm:gap-3">
            <button
              onClick={() => setProcessAllDialogOpen(false)}
              className="flex-1 px-3 sm:px-4 py-2.5 sm:py-3 bg-gray-200 text-gray-700 text-sm sm:text-base font-semibold rounded-lg hover:bg-gray-300 transition touch-manipulation"
            >
              Cancel
            </button>
            <button
              onClick={processAllImages}
              className="flex-1 px-3 sm:px-4 py-2.5 sm:py-3 bg-orange-600 text-white text-sm sm:text-base font-semibold rounded-lg hover:bg-orange-700 transition touch-manipulation active:scale-95"
            >
              ⚡ Start Processing
            </button>
          </div>
        </div>
      </Modal>

      {/* Difference Image Dialog */}
      <Modal
        isOpen={differenceDialogOpen}
        onClose={() => setDifferenceDialogOpen(false)}
        title="Difference Image (JET Colormap)"
        maxWidth="max-w-6xl"
      >
        <div className="space-y-4">
          <p className="text-gray-600">
            This image shows the pixel-wise differences between the original and corrected images.
            <br />
            <span className="text-sm text-gray-500">Blue = minimal change, Red = maximum change</span>
          </p>

          {comparisonData.difference && (
            <div className="border-2 border-gray-300 rounded-lg overflow-hidden">
              <img 
                src={comparisonData.difference} 
                alt="Difference Map" 
                className="w-full h-auto"
              />
            </div>
          )}

          <button
            onClick={() => setDifferenceDialogOpen(false)}
            className="w-full px-4 py-3 bg-gray-200 text-gray-700 font-semibold rounded-lg hover:bg-gray-300 transition"
          >
            Close
          </button>
        </div>
      </Modal>

      {/* Before/After Comparison Dialog */}
      <Modal
        isOpen={beforeAfterDialogOpen}
        onClose={() => setBeforeAfterDialogOpen(false)}
        title="Before & After Comparison"
      >
        <div className="space-y-4">
          <p className="text-gray-600 mb-4">
            Side-by-side comparison of original and color-corrected images
          </p>

          {comparisonData.original && comparisonData.corrected && (
            <div className="grid grid-cols-2 gap-4">
              <div className="border-2 border-gray-300 rounded-lg overflow-hidden">
                <div className="bg-gray-100 px-3 py-2 font-semibold text-gray-700 text-sm border-b border-gray-300">
                  Before (Original)
                </div>
                <img 
                  src={comparisonData.original} 
                  alt="Original" 
                  className="w-full h-auto"
                />
              </div>

              <div className="border-2 border-green-300 rounded-lg overflow-hidden">
                <div className="bg-green-100 px-3 py-2 font-semibold text-green-700 text-sm border-b border-green-300">
                  After (Corrected)
                </div>
                <img 
                  src={comparisonData.corrected} 
                  alt="Corrected" 
                  className="w-full h-auto"
                />
              </div>
            </div>
          )}

          <button
            onClick={() => setBeforeAfterDialogOpen(false)}
            className="w-full px-4 py-3 bg-gray-200 text-gray-700 font-semibold rounded-lg hover:bg-gray-300 transition"
          >
            Close
          </button>
        </div>
      </Modal>

      {/* RGB Scatter Plot Dialog */}
      <Modal
        isOpen={scatterDialogOpen}
        onClose={() => setScatterDialogOpen(false)}
        title="RGB Scatter Plot Comparison"
        // maxWidth="max-w-6xl"
      >
        <div className="space-y-4">
          <p className="text-gray-600">
            Comparison of RGB color distribution before and after color correction
          </p>

          {scatterPlotData && (
            <div className="border-2 border-gray-300 rounded-lg overflow-hidden bg-white">
              <img 
                src={scatterPlotData} 
                alt="RGB Scatter Plot" 
                className="w-full h-auto"
              />
            </div>
          )}

          <button
            onClick={() => setScatterDialogOpen(false)}
            className="w-full px-4 py-3 bg-gray-200 text-gray-700 font-semibold rounded-lg hover:bg-gray-300 transition"
          >
            Close
          </button>
        </div>
      </Modal>

      {/* Enhanced Save Images Dialog with Step Selection */}
      <Modal
        isOpen={saveStepsDialogOpen}
        onClose={() => setSaveStepsDialogOpen(false)}
        title="Save Processed Images"
      >
        <div className="space-y-4">
          <div className={`${batchProcessComplete ? 'bg-green-50 border-green-200' : 'bg-blue-50 border-blue-200'} border rounded-lg p-4`}>
            <p className={`${batchProcessComplete ? 'text-green-800' : 'text-blue-800'} font-semibold mb-2`}>
              {batchProcessComplete ? '🎉 Batch Processing Complete!' : '💾 Select Steps & Images to Save'}
            </p>
            <p className="text-sm text-blue-700">
              {batchProcessComplete 
                ? 'All processed images from "Process All" are ready to save. Select the steps you want to save.'
                : 'Choose which processing steps and images you want to save.'}
            </p>
          </div>

          {/* Directory Selection */}
          <div>
            <h4 className="font-semibold text-gray-800 mb-3">Save Directory:</h4>
            <p className="text-sm text-gray-600 mb-2">Enter full path (e.g., C:\Users\YourName\Desktop\results)</p>
            <div className="flex gap-2">
              <input
                type="text"
                value={saveDirectory}
                onChange={(e) => setSaveDirectory(e.target.value)}
                placeholder="Default: results folder in backend directory"
                className="flex-1 px-3 py-2 border border-gray-300 rounded-lg focus:ring-2 focus:ring-indigo-500 font-mono text-sm"
              />
            </div>
            <p className="text-xs text-gray-500 mt-1">Leave blank to use default results folder</p>
          </div>

          {/* Step Selection */}
          <div>
            <h4 className="font-semibold text-gray-800 mb-3">Processing Steps:</h4>
            <div className="grid grid-cols-2 gap-2">
              {['FFC', 'GC', 'WB', 'CC'].map(step => (
                <label key={step} className="flex items-center gap-2 cursor-pointer bg-gray-50 p-3 rounded-lg border border-gray-200 hover:bg-gray-100 transition">
                  <input
                    type="checkbox"
                    checked={selectedStepsToSave.includes(step)}
                    onChange={() => toggleStepSelection(step)}
                    className="w-5 h-5 rounded accent-indigo-600 cursor-pointer"
                  />
                  <span className="font-bold text-gray-700">{step}</span>
                </label>
              ))}
            </div>
          </div>

          {/* Image Selection */}
          <div>
            <div className="flex items-center justify-between mb-3">
              <h4 className="font-semibold text-gray-800">
                Images {batchProcessComplete ? `(${batchImagesList.length} batch processed)` : `(${availableImages.length} processed)`}:
              </h4>
              <button
                onClick={() => {
                  if (batchProcessComplete) {
                    // Toggle all batch images
                    if (selectedImagesToSave.length === batchImagesList.length) {
                      setSelectedImagesToSave([]);
                    } else {
                      setSelectedImagesToSave(batchImagesList.map(img => img.image_index));
                    }
                  } else {
                    // Toggle all regular images
                    if (selectedImagesToSave.length === availableImages.length) {
                      setSelectedImagesToSave([]);
                    } else {
                      setSelectedImagesToSave(availableImages.map(img => img.filename));
                    }
                  }
                }}
                className="text-xs px-2 py-1 bg-indigo-100 text-indigo-700 rounded hover:bg-indigo-200 transition"
              >
                {(batchProcessComplete && selectedImagesToSave.length === batchImagesList.length) ||
                 (!batchProcessComplete && selectedImagesToSave.length === availableImages.length)
                  ? 'Deselect All'
                  : 'Select All'}
              </button>
            </div>
            
            <div className="max-h-48 overflow-y-auto border border-gray-200 rounded-lg">
              {batchProcessComplete ? (
                // Batch mode: show batch images with step info
                batchImagesList.map((img, idx) => (
                  <label 
                    key={idx} 
                    className="flex items-center gap-2 cursor-pointer px-3 py-2 hover:bg-gray-50 transition border-b border-gray-100 last:border-b-0"
                  >
                    <input
                      type="checkbox"
                      checked={selectedImagesToSave.includes(img.image_index)}
                      onChange={() => {
                        setSelectedImagesToSave(prev =>
                          prev.includes(img.image_index)
                            ? prev.filter(i => i !== img.image_index)
                            : [...prev, img.image_index]
                        );
                      }}
                      className="w-4 h-4 rounded accent-green-600 cursor-pointer"
                    />
                    <div className="flex-1">
                      <span className="text-sm font-medium text-gray-700">{img.filename}</span>
                      <span className="text-xs text-gray-500 ml-2">
                        ({img.available_steps.join(', ')})
                      </span>
                    </div>
                  </label>
                ))
              ) : (
                // Regular mode: show available images
                availableImages.map((img, idx) => (
                  <label 
                    key={idx} 
                    className="flex items-center gap-2 cursor-pointer px-3 py-2 hover:bg-gray-50 transition border-b border-gray-100 last:border-b-0"
                  >
                    <input
                      type="checkbox"
                      checked={selectedImagesToSave.includes(img.filename)}
                      onChange={() => toggleImageSelectionForSave(img.filename)}
                      className="w-4 h-4 rounded accent-indigo-600 cursor-pointer"
                    />
                    <span className="text-sm text-gray-700">{img.filename}</span>
                  </label>
                ))
              )}
            </div>
          </div>

          {/* Summary */}
          <div className="bg-gray-50 border border-gray-200 rounded-lg p-3">
            <p className="text-sm text-gray-700">
              <strong>Will save:</strong> {selectedStepsToSave.length} step(s) × {selectedImagesToSave.length} image(s) 
              {' = '}{selectedStepsToSave.length * selectedImagesToSave.length} file(s)
            </p>
            {saveDirectory && (
              <p className="text-xs text-gray-600 mt-1">
                <strong>Directory:</strong> {saveDirectory}
              </p>
            )}
          </div>

          {/* Action Buttons */}
          <div className="flex gap-3">
            <button
              onClick={() => {
                setSaveStepsDialogOpen(false);
                if (batchProcessComplete) {
                  setBatchProcessComplete(false); // Reset batch flag
                }
              }}
              className="flex-1 px-4 py-3 bg-gray-200 text-gray-700 font-semibold rounded-lg hover:bg-gray-300 transition"
            >
              Cancel
            </button>
            {batchProcessComplete ? (
              <button
                onClick={() => {
                  saveBatchImages();
                  setBatchProcessComplete(false); // Reset after save
                }}
                disabled={selectedStepsToSave.length === 0}
                className={`flex-1 px-4 py-3 font-semibold rounded-lg transition ${
                  selectedStepsToSave.length > 0
                    ? 'bg-green-600 text-white hover:bg-green-700'
                    : 'bg-gray-300 text-gray-500 cursor-not-allowed'
                }`}
              >
                💾 Save All Batch Images
              </button>
            ) : (
              <button
                onClick={saveImages}
                disabled={selectedStepsToSave.length === 0 || selectedImagesToSave.length === 0}
                className={`flex-1 px-4 py-3 font-semibold rounded-lg transition ${
                  selectedStepsToSave.length > 0 && selectedImagesToSave.length > 0
                    ? 'bg-indigo-600 text-white hover:bg-indigo-700'
                    : 'bg-gray-300 text-gray-500 cursor-not-allowed'
                }`}
              >
                💾 Save Selected
              </button>
            )}
          </div>
        </div>
      </Modal>
    </div>
  );
}


