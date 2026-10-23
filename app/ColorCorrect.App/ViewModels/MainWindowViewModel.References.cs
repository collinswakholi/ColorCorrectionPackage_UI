using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ColorCorrect.Engine;

namespace ColorCorrect.App.ViewModels;

public partial class MainWindowViewModel
{
    public string ReferenceSummary => Active is { ReferencesNeedAssignment: true }
        ? "Custom references need reassignment after detection."
        : Active is { ReferenceAssignments.Count: > 0 } item
            ? $"Custom references · {item.ReferenceAssignments.Count} chart(s)"
            : "Built-in chart references";

    public void AssignReference(SessionItemViewModel item, string chartId, ReferenceProfile? profile)
    {
        if (Busy || IsSaving) throw new InvalidOperationException("Wait for the current job before editing chart references.");
        var faces = item.Handshake?.Faces().Where(f => f.TryGetProperty("chart_id", out var id) && id.GetString() == chartId).ToArray();
        if (faces is not { Length: 1 })
            throw new InvalidOperationException("The detection changed. Reopen the reference editor and select the chart again.");
        profile?.Validate();
        var snapshot = profile?.Snapshot();
        if (snapshot is not null)
        {
            var detected = faces[0].TryGetProperty("cli_chart", out var chart) ? chart.GetString()! : faces[0].GetProperty("chart_type").GetString()!;
            if (!ReferenceCatalog.Compatible(detected, snapshot.Chart))
                throw new ArgumentException("The reference template does not match the selected chart.");
            var canonical = snapshot.ToJson();
            var existingProfiles = Session.SelectMany(entry => entry.ReferenceProfiles).Concat(item.ReferenceProfiles);
            if (existingProfiles.Any(p => p.Id == snapshot.Id && p.Revision == snapshot.Revision && p.ToJson() != canonical))
                throw new ArgumentException("This profile revision already has different values or metadata. Increment the revision before assigning the edited profile.");
        }
        item.ReferenceAssignments.RemoveAll(a => a.ChartId == chartId);
        if (snapshot is not null)
        {
            item.ReferenceProfiles.RemoveAll(p => p.Id == snapshot.Id && p.Revision == snapshot.Revision);
            item.ReferenceProfiles.Add(snapshot);
            item.ReferenceAssignments.Add(new ChartReferenceAssignment { ChartId = chartId, ProfileId = snapshot.Id, ProfileRevision = snapshot.Revision });
        }
        // Explicit built-in assignments for remaining faces keep multi-chart custom requests complete.
        item.ReferencesNeedAssignment = false;
        InvalidateCalibration(item);
        if (item == Active)
        {
            UseLoadedModel = false;
            ApplyReport(null);
            OnPropertyChanged(nameof(ReferenceSummary));
        }
        item.DetectLabel = "Reference changed · refit required";
        Status = "Chart reference updated. Fit again to use it; the previous saved model is unchanged.";
        ScheduleSave();
        NotifyRunCommands();
    }

    private void InvalidateCalibration(SessionItemViewModel item)
    {
        item.InvalidateCalibration();
        if (item == Active)
        {
            // A bitmap decode scheduled before the edit must never reattach the
            // old result after the current result associations have been cleared.
            _previewGeneration++;
            LastPreviewDir = "";
            HasCorrected = false;
            ShowCorrected = false;
            StageName = "Original";
            SetPreview(OriginalPreview, null);
            ApplyReport(null);
            NotifyStageRadios();
            RefreshOverlay();
            if (OriginalPreview is null && System.IO.File.Exists(item.Path))
                _ = LoadPreviewsAsync(item, _previewGeneration);
        }
        RefreshSessionUi();
    }

    private static CorrectionOptions WithItemReferences(CorrectionOptions options, SessionItemViewModel item)
    {
        var copy = JsonSerializer.Deserialize<CorrectionOptions>(JsonSerializer.Serialize(options))!;
        if (item.ReferencesNeedAssignment) throw new InvalidOperationException("Assign custom references again after detecting this image, or choose built-in references.");
        copy.ReferenceProfiles = item.ReferenceProfiles.Select(p => p.Snapshot()).ToList();
        copy.ReferenceAssignments = item.ReferenceAssignments.Select(a => new ChartReferenceAssignment { ChartId = a.ChartId, ProfileId = a.ProfileId, ProfileRevision = a.ProfileRevision }).ToList();
        if (copy.ReferenceAssignments.Count == 0)
        {
            copy.ReferenceProfiles.Clear();
            return copy;
        }
        copy.ReferenceProfiles.RemoveAll(p => !copy.ReferenceAssignments.Any(a => a.ProfileId == p.Id && a.ProfileRevision == p.Revision));
        if (copy.ReferenceAssignments.Count > 0 && item.Handshake is { } handshake)
        {
            var builtIns = new Dictionary<string, ReferenceProfile>(StringComparer.Ordinal);
            foreach (var (face, index) in handshake.Faces().Select((face, index) => (face, index)))
            {
                var id = handshake.ChartId(index);
                if (copy.ReferenceAssignments.Any(a => a.ChartId == id)) continue;
                var type = face.TryGetProperty("cli_chart", out var chart) ? chart.GetString()! : handshake.ChartType;
                var template = ReferenceCatalog.Get(type);
                if (!builtIns.TryGetValue(template.Chart, out var builtin))
                {
                    string syntheticId;
                    do { syntheticId = $"builtin-{template.Chart}-{Guid.NewGuid():N}"; } while (copy.ReferenceProfiles.Any(p => p.Id == syntheticId));
                    builtin = new ReferenceProfile { Id = syntheticId, Name = $"Built-in {template.Chart}", Chart = template.Chart, Values = template.Lab.Select(v => v.ToArray()).ToList() };
                    builtIns.Add(template.Chart, builtin);
                    copy.ReferenceProfiles.Add(builtin);
                }
                copy.ReferenceAssignments.Add(new ChartReferenceAssignment { ChartId = id, ProfileId = builtin.Id, ProfileRevision = 1 });
            }
        }
        return copy;
    }

    private static void ValidateRestoredReferences(SessionItemViewModel item)
    {
        if (item.ReferencesNeedAssignment)
        {
            item.ReferenceAssignments.Clear();
            item.InvalidateCalibration();
            return;
        }
        if (item.ReferenceAssignments.Count == 0) return;
        var handshake = item.Handshake;
        var valid = handshake is not null && item.ReferenceAssignments.Select(a => a.ChartId).Distinct().Count() == item.ReferenceAssignments.Count;
        if (valid)
        {
            foreach (var assignment in item.ReferenceAssignments)
            {
                var profile = item.ReferenceProfiles.FirstOrDefault(p => p.Id == assignment.ProfileId && p.Revision == assignment.ProfileRevision);
                var index = Enumerable.Range(0, handshake!.ChartCount).FirstOrDefault(i => handshake.ChartId(i) == assignment.ChartId, -1);
                if (profile is null || index < 0) { valid = false; break; }
                var face = handshake.ActiveFace(index);
                var type = face.TryGetProperty("cli_chart", out var chart) ? chart.GetString()! : face.GetProperty("chart_type").GetString()!;
                try { profile.Validate(); valid &= ReferenceCatalog.Compatible(type, profile.Chart); }
                catch (ArgumentException) { valid = false; }
            }
        }
        if (!valid)
        {
            item.ReferenceAssignments.Clear();
            item.ReferencesNeedAssignment = true;
            item.InvalidateCalibration();
        }
    }
}
