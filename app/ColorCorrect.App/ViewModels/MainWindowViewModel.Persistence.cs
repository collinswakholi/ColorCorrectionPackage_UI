using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ColorCorrect.App.Controls;
using ColorCorrect.App.Models;
using ColorCorrect.App.Services;
using ColorCorrect.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ColorCorrect.App.ViewModels;

/// <summary>
/// Settings and session persistence: scheduling saves (through <see cref="Services.SettingsSaveScheduler"/>),
/// mapping the sub view models to and from <see cref="SessionState"/>, restoring working sessions
/// and applying saved correction options.
/// </summary>
public partial class MainWindowViewModel
{
    private void ScheduleSave()
    {
        if (_disposed || !_persistenceEnabled || _restoring)
        {
            return;
        }

        _settingsSaver.Schedule();
    }

    /// <summary>Synchronous save (used on flush and close); ordered with background saves.</summary>
    private void Persist() => _settingsSaver.SaveNow();

    /// <summary>Settings to persist now, or null while saving is disabled or a session is restoring.</summary>
    private SessionState? SettingsSnapshot() =>
        !_persistenceEnabled || _restoring ? null : BuildSessionState(includeItems: false);

    private SessionState BuildSessionState(bool includeItems) => new()
    {
        Items = includeItems
            ? Session.Select(item => new SessionItemDto
            {
                Path = item.Path,
                Name = item.Name,
                HandshakeJson = item.Handshake?.Json ?? "",
                ReferenceProfiles = item.ReferenceProfiles.Select(p => p.Snapshot()).ToList(),
                ReferenceAssignments = item.ReferenceAssignments.ToList(),
                ReferencesNeedAssignment = item.ReferencesNeedAssignment,
                CorrectedPath = item.CorrectedPath,
                ReportPath = item.ReportPath,
                ModelPath = item.ModelPath,
                CompletedContext = item.CompletedContext,
                SourceIdentity = item.SourceIdentity,
                FaceChoice = item.FaceChoice,
                UseAllFaces = item.UseAllFaces,
                IncludeInBatch = item.IncludeInBatch,
                PreviewSuppressed = item.PreviewSuppressed,
                PreviewDir = item.PreviewDir,
                FfcPlane = item.FfcPlane is { } plane
                    ? [plane.X, plane.Y, plane.Width, plane.Height]
                    : null,
                FfcWhiteSize = item.FfcPlane is { } sized && sized.WhiteWidth > 0
                    ? [sized.WhiteWidth, sized.WhiteHeight]
                    : null,
                FfcPlaneSource = item.FfcPlane?.Source ?? "",
            }).ToList()
            : [],
        ActivePath = includeItems ? Workspace.Active?.Path ?? "" : "",
        Options = BuildOptions(),
        Crop = Stage.Crop,
        Theme = Layout.IsLightTheme ? "Light" : "Dark",
        TrustedOutputDirs = _trusted.ToList(),
        CatalogFolders = CatalogFolders.Select(folder => folder.Path).ToList(),
        ForceCpu = Compute.ForceCpu,
        AccelPolicy = Compute.SelectedAccelPolicy.Value,
        ShowLattice = Stage.ShowLattice,
        ShowSample = Stage.ShowSample,
        ShowDim = Stage.ShowDim,
        ShowPlane = Stage.ShowPlane,
    };

    private void RestoreSession()
    {
        _settingsSaver.CancelPending();
        _restoring = true;
        try
        {
            var (state, error) = SessionStore.TryLoad();
            if (!string.IsNullOrWhiteSpace(error))
            {
                Log("Session", "Saved session could not be restored.", LogLevel.Warning, [error]);
            }

            foreach (var dir in state.TrustedOutputDirs)
            {
                _trusted.Add(dir);
            }

            CatalogFolders.Clear();
            foreach (var folder in state.CatalogFolders.Distinct(PathIdentity.Comparer))
            {
                if (Directory.Exists(folder))
                {
                    CatalogFolders.Add(new CatalogFolderViewModel(folder));
                }
            }
            Workspace.HasCatalogFolders = CatalogFolders.Count > 0;

            ApplyOptions(state.Options);
            Stage.Crop = state.Crop is > 0.04 and <= 1 ? state.Crop : 0.75;
            Stage.OverlayCrop = Stage.Crop;
            Layout.IsLightTheme = string.Equals(state.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            var policy = !string.IsNullOrWhiteSpace(state.AccelPolicy)
                ? state.AccelPolicy
                : state.ForceCpu ? "cpu" : "auto";
            Compute.SelectedAccelPolicy = Pick(AccelPolicies, policy == "cpu" ? "auto" : policy);
            Compute.ForceCpu = state.ForceCpu || policy == "cpu";
            _worker.Policy = Compute.SelectedAccelPolicy.Value;
            Stage.ShowLattice = state.ShowLattice ?? true;
            Stage.ShowSample = state.ShowSample ?? true;
            Stage.ShowDim = state.ShowDim ?? true;
            Stage.ShowPlane = state.ShowPlane ?? true;

            LoadCatalog();
            RefreshSessionUi();
            EnsureWhiteInSession(activate: false);
            RefreshOverlay();
        }
        finally
        {
            _restoring = false;
            RefreshSessionUi();
            NotifyRunCommands();
        }
    }

    internal int RestoreWorkingItems(SessionState state)
    {
        var wasRestoring = _restoring;
        _restoring = true;
        try
        {
            if (state.HasOptions)
            {
                ApplyOptions(state.Options);
                Stage.Crop = state.Crop is > 0.04 and <= 1 ? state.Crop : 0.75;
                Stage.OverlayCrop = Stage.Crop;
            }
            RestoreWorkingItemsCore(state);
            var skipped = state.Items.Count - Session.Count;
            if (skipped > 0) Job.Status = $"Skipped {skipped} unsupported session item(s); only JPEG, PNG and TIFF stills are supported.";
            return skipped;
        }
        finally
        {
            _restoring = wasRestoring;
            UpdateCcSummary(); NotifyRunCommands(); ScheduleSave();
        }
    }

    private void RestoreWorkingItemsCore(SessionState state)
    {
        _previewGeneration++;
        foreach (var item in Session.ToList())
        {
            item.Dispose();
        }

        Session.Clear();
        Workspace.Active = null;
        _rangeAnchor = null;
        Stage.LastPreviewDir = "";
        SetPreview(null, null);
        Stage.OverlayHandshake = null;
        Workspace.HasImage = false;
        Workspace.HasCorrected = false;
        Stage.ShowFacePicker = false;
        FaceOptions.Clear();
        ClearPicker();
        ApplyReport(null);
        foreach (var dto in state.Items)
        {
            if (!IsImage(dto.Path))
            {
                continue;
            }

            var item = new SessionItemViewModel(dto.Path)
            {
                FaceChoice = dto.FaceChoice,
                UseAllFaces = dto.UseAllFaces,
                IncludeInBatch = dto.IncludeInBatch,
                CorrectedPath = dto.CorrectedPath,
                ReportPath = dto.ReportPath,
                ModelPath = dto.ModelPath,
                PreviewDir = dto.PreviewDir,
                CompletedContext = dto.CompletedContext,
                PreviewSuppressed = dto.PreviewSuppressed,
                SourceIdentity = dto.SourceIdentity ?? dto.CompletedContext?.Source,
                IsCorrected = File.Exists(dto.CorrectedPath)
                    || (Directory.Exists(dto.PreviewDir) && ResolvePreviewFile(dto.PreviewDir, "corrected") is not null),
                IncludeInSave = File.Exists(dto.CorrectedPath) || Directory.Exists(dto.PreviewDir),
                Report = CorrectionReport.TryLoad(dto.ReportPath),
                FfcPlane = PlaneFromDto(dto) ?? CorrectionReport.TryLoad(dto.ReportPath)?.FfcPlane
                           ?? FfcPlane.TryLoad(dto.ModelPath),
            };
            item.NotifyOutputs();
            if (!string.IsNullOrWhiteSpace(dto.HandshakeJson))
            {
                try
                {
                    item.ApplyHandshake(new HandshakeDocument(dto.HandshakeJson));
                }
                catch
                {
                    item.ApplyHandshake(null);
                }
            }

            item.ReferenceProfiles = dto.ReferenceProfiles.Select(p => p.Snapshot()).ToList();
            item.ReferenceAssignments = dto.ReferenceAssignments.Select(a => new ChartReferenceAssignment { ChartId = a.ChartId, ProfileId = a.ProfileId, ProfileRevision = a.ProfileRevision }).ToList();
            item.ReferencesNeedAssignment = dto.ReferencesNeedAssignment;
            ValidateRestoredReferences(item);
            Session.Add(item);
        }

        if (Session.Count > 0)
        {
            var active = Session.FirstOrDefault(item => PathIdentity.Equal(item.Path, state.ActivePath))
                         ?? Session[0];
            Activate(active);
            Layout.LibraryExpanded = false;
        }

        RefreshSessionUi();
        EnsureWhiteInSession(activate: false);
        NotifyRunCommands();

    }

    private static FfcPlane? PlaneFromDto(SessionItemDto dto)
    {
        if (dto.FfcPlane is not { Length: >= 4 } rect || rect[2] <= 0 || rect[3] <= 0)
        {
            return null;
        }

        return new FfcPlane
        {
            X = rect[0],
            Y = rect[1],
            Width = rect[2],
            Height = rect[3],
            WhiteWidth = dto.FfcWhiteSize is { Length: >= 2 } size ? size[0] : 0,
            WhiteHeight = dto.FfcWhiteSize is { Length: >= 2 } size2 ? size2[1] : 0,
            Source = dto.FfcPlaneSource ?? "",
        };
    }

    private void ApplyOptions(CorrectionOptions options)
    {
        Settings.ClearDrafts(notify: false);
        Compute.ForceCpu = options.CpuLut;
        Compute.SelectedProcessingDevice = Pick(ProcessingDevices, options.CpuLut ? "cpu" : options.LutDevice);
        Compute.FastMode = options.FastMode;
        Output.BatchPreviewResults = options.BatchPreviewResults;
        options.Normalize();
        Settings.SelectedMethod = Pick(Methods, options.Method);
        SyncDegreeAndAdvanced();
        Settings.SelectedDegree = DegreeOptions.FirstOrDefault(item => item.Value == options.Degree.ToString()) ?? Settings.SelectedDegree;
        Settings.SelectedInputSpace = Pick(RgbSpaces, options.InputSpace);
        Settings.SelectedInputWhite = Pick(WhitePoints, options.InputWhite);
        Settings.SelectedTargetSpace = Pick(RgbSpaces, options.TargetSpace);
        Settings.SelectedTargetWhite = Pick(WhitePoints, options.TargetWhite);
        Output.SelectedOutputFormat = Pick(OutputFormats, options.OutputFormat);
        Settings.FitSamples = Math.Clamp(options.FitSamples, 1, 200);
        Settings.PlsComponents = Math.Clamp(options.PlsComponents, 0, 64);
        Settings.HiddenLayersText = CorrectionOptions.FormatHiddenLayers(options.HiddenLayers);
        Settings.SelectedNnOptimizer = Pick(NnOptimizers, options.NnOptimizer);
        Settings.NnBatchNormalization = options.NnBatchNormalization;
        Settings.NnDropout = (decimal)Math.Clamp(options.NnDropout, 0, 0.8);
        Settings.NnLr = (decimal)options.NnLr;
        Settings.NnEpochs = Math.Clamp(options.NnEpochs, 50, 20000);
        Settings.NnPatience = Math.Clamp(options.NnPatience, 1, 5000);
        Settings.NnTol = (decimal)ClampTol(options.NnTol, 1e-6);
        Settings.SkipSaturated = options.SkipSaturated;
        Settings.IncludeCombinations = options.IncludeCombinations;
        Output.OutputDir = options.OutputDir;
        Settings.DoFfc = options.DoFfc;
        Settings.DoGc = options.DoGc;
        Settings.DoWb = options.DoWb;
        Settings.DoCc = options.DoCc;
        Settings.WhiteImagePath = options.WhiteImagePath;
        Stage.CropRect = options.CropRect;
        Settings.SelectedFfcFitMethod = Pick(FfcFitMethods, string.IsNullOrWhiteSpace(options.FfcFitMethod) ? "poly" : options.FfcFitMethod);
        Settings.FfcBins = Math.Clamp(options.FfcBins, 4, 200);
        Settings.FfcSmoothWindow = Math.Clamp(options.FfcSmoothWindow, 1, 51);
        Settings.FfcDegree = Math.Clamp(options.FfcDegree, 1, 7);
        Settings.FfcNnMaxIter = Math.Clamp(options.FfcNnMaxIter <= 0 ? 10000 : options.FfcNnMaxIter, 50, 20000);
        Settings.FfcTol = (decimal)ClampTol(options.FfcTol, 1e-8);
        Settings.FfcCutOff = (decimal)ClampFfcCutOff(options.FfcCutOff);
        Settings.FfcMaxVal = (decimal)ClampFfcMaxVal(options.FfcMaxVal, ClampFfcCutOff(options.FfcCutOff));
        Settings.ClipLo = (decimal)ClampClipLo(options.ClipLo);
        Settings.ClipHi = (decimal)ClampClipHi(options.ClipHi, ClampClipLo(options.ClipLo));
        Compute.Phase2Teacher = options.Phase2Teacher;
        var model = File.Exists(options.LoadedModelPath) ? options.LoadedModelPath
            : File.Exists(options.ModelInPath) ? options.ModelInPath : "";
        Output.LoadedModelPath = model;
        Output.UseLoadedModel = options.UseLoadedModel == true && File.Exists(model);
        _loadedModelPlane = FfcPlane.TryLoad(model);
        UpdateCcSummary();
    }
}
