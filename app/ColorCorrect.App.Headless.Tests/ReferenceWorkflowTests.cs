using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.ViewModels;
using ColorCorrect.App.Views;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class ReferenceWorkflowTests
{
    [AvaloniaFact]
    public void SuccessfulRefitClearsReferenceStaleBadgeButFailedFitDoesNot()
    {
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy=false };
        using var item = new SessionItemViewModel("reference-workflow-missing.png");
        item.ApplyHandshake(Handshake("refit"));
        vm.Session.Add(item);
        vm.AssignReference(item,"refit:a",Profile("custom"));
        var apply = typeof(MainWindowViewModel).GetMethod("ApplyOutcome",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        apply.Invoke(vm,[item,new CorrectionOutcome { Ok=false, Error="failed" },true,false]);
        Assert.Contains("refit required",item.DetectLabel);
        apply.Invoke(vm,[item,new CorrectionOutcome { Ok=true },true,false]);
        Assert.Equal(ColorSpaces.ChartLabel(item.Handshake!.ChartType),item.DetectLabel);
        Assert.Single(item.ReferenceAssignments);
    }

    [AvaloniaFact]
    public void BuiltInFallbackCannotReuseImportedCustomProfileIdentity()
    {
        var vm=new MainWindowViewModel(persistenceEnabled:false) {Busy=false};
        using var item=new SessionItemViewModel("reference-workflow-missing.png");
        var node=System.Text.Json.Nodes.JsonNode.Parse(Handshake("collision").Json)!;
        var charts=node["charts"]!.AsArray();
        var third=charts[1]!.DeepClone();third["chart_id"]="collision:c";charts.Add(third);
        item.ApplyHandshake(new HandshakeDocument(node.ToJsonString()));
        var custom=Profile("builtin-classic");custom.Values[0][0]-=10;
        vm.AssignReference(item,"collision:a",custom);
        var method=typeof(MainWindowViewModel).GetMethod("WithItemReferences",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var options=Assert.IsType<CorrectionOptions>(method.Invoke(null,[new CorrectionOptions(),item]));
        var selected=options.ReferenceAssignments.Single(a=>a.ChartId=="collision:a");
        var second=options.ReferenceAssignments.Single(a=>a.ChartId=="collision:b");
        var last=options.ReferenceAssignments.Single(a=>a.ChartId=="collision:c");
        Assert.Equal(custom.Id,selected.ProfileId);Assert.NotEqual(custom.Id,second.ProfileId);
        Assert.Equal(second.ProfileId,last.ProfileId);
        var builtin=options.ReferenceProfiles.Single(p=>p.Id==second.ProfileId&&p.Revision==second.ProfileRevision);
        Assert.Equal(ReferenceCatalog.Get("classic").Lab[0],builtin.Values[0]);
        Assert.Equal(custom.Values[0],options.ReferenceProfiles.Single(p=>p.Id==custom.Id).Values[0]);
    }

    [AvaloniaFact]
    public void ReferenceEditDetachesStaleArtifactsAndPreviewWithoutDeletingSavedFiles()
    {
        var directory=Path.Combine(Path.GetTempPath(),$"ccp-reference-stale-{Guid.NewGuid():N}");Directory.CreateDirectory(directory);
        var corrected=Path.Combine(directory,"corrected.png");File.WriteAllBytes(corrected,[]);
        var model=Path.Combine(directory,"model.json");File.WriteAllText(model,"{}");
        var vm=new MainWindowViewModel(persistenceEnabled:false) {Busy=false};
        using var item=new SessionItemViewModel("reference-workflow-missing.png");item.ApplyHandshake(Handshake("s"));
        vm.Session.Add(item);vm.Active=item;
        item.IsCorrected=true;item.IncludeInSave=true;item.CorrectedPath=corrected;item.ModelPath=model;item.PreviewDir=directory;
        vm.LastPreviewDir=directory;vm.HasCorrected=true;vm.ShowCorrected=true;
        var generation=item.ApplyGeneration;
        try
        {
            Assert.True(item.HasSaveable);
            vm.AssignReference(item,"s:a",Profile("new-reference"));
            Assert.False(item.IsCorrected);Assert.False(item.IncludeInSave);Assert.False(item.HasSaveable);
            Assert.Empty(item.CorrectedPath);Assert.Empty(item.ModelPath);Assert.Empty(item.PreviewDir);
            Assert.False(vm.HasCorrected);Assert.False(vm.ShowCorrected);Assert.Empty(vm.LastPreviewDir);Assert.Null(vm.CorrectedPreview);
            Assert.True(item.ApplyGeneration>generation);
            Assert.True(File.Exists(corrected));Assert.True(File.Exists(model));
        }
        finally {Directory.Delete(directory,recursive:true);}
    }

    [AvaloniaFact]
    public void SameTypeChartsKeepSeparateProfilesAndSharedProfileRevisions()
    {
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-workflow-missing.png");
        item.ApplyHandshake(Handshake("s"));
        var first = Profile("card-a"); var second = Profile("card-b"); second.Values[0][0] -= 5;
        vm.AssignReference(item,"s:a",first); vm.AssignReference(item,"s:b",second);
        Assert.Equal(2,item.ReferenceAssignments.Count);
        Assert.Equal("card-a",item.ReferenceAssignments.Single(a=>a.ChartId=="s:a").ProfileId);
        Assert.Equal("card-b",item.ReferenceAssignments.Single(a=>a.ChartId=="s:b").ProfileId);
        // One reusable profile may be assigned to two charts; editing one keeps
        // the old immutable revision available to the other chart.
        vm.AssignReference(item,"s:b",first);
        var edited = first.Snapshot(); edited.Revision=2; edited.Values[0][0]-=2;
        vm.AssignReference(item,"s:a",edited);
        Assert.Equal(2,item.ReferenceAssignments.Single(a=>a.ChartId=="s:a").ProfileRevision);
        Assert.Equal(1,item.ReferenceAssignments.Single(a=>a.ChartId=="s:b").ProfileRevision);
        Assert.Contains(item.ReferenceProfiles,p=>p.Id==first.Id&&p.Revision==1);
        Assert.Contains(item.ReferenceProfiles,p=>p.Id==first.Id&&p.Revision==2);
        edited.Values[0][0]=0;
        Assert.NotEqual(0,item.ReferenceProfiles.Single(p=>p.Id==first.Id&&p.Revision==2).Values[0][0]);
    }

    [AvaloniaFact]
    public void InvalidProfileDoesNotRemoveAssignmentAndEditsInvalidateFit()
    {
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-workflow-missing.png"); item.ApplyHandshake(Handshake("s"));
        vm.Session.Add(item);
        vm.AssignReference(item,"s:a",Profile("card"));
        item.IsCorrected=true; item.ModelPath="previous.json"; item.ReportPath="report.json";
        var invalid=Profile("invalid");invalid.Values.RemoveAt(0);
        Assert.Throws<ArgumentException>(()=>vm.AssignReference(item,"s:a",invalid));
        Assert.Equal("card",Assert.Single(item.ReferenceAssignments).ProfileId);
        Assert.True(item.IsCorrected);
        var incompatible=new ReferenceProfile {Id="sg",Name="SG",Chart="digital-sg",Values=ReferenceCatalog.Get("digital-sg").Lab.Select(v=>v.ToArray()).ToList()};
        Assert.Throws<ArgumentException>(()=>vm.AssignReference(item,"s:a",incompatible));
        Assert.Equal("card",Assert.Single(item.ReferenceAssignments).ProfileId);Assert.True(item.IsCorrected);
        var conflicting=Profile("card");conflicting.Values[0][0]-=1;
        Assert.Throws<ArgumentException>(()=>vm.AssignReference(item,"s:a",conflicting));
        Assert.Equal("card",Assert.Single(item.ReferenceAssignments).ProfileId);
        Assert.Equal(Profile("card").Values[0],Assert.Single(item.ReferenceProfiles).Values[0]);
        using var other=new SessionItemViewModel("another-reference-workflow-missing.png");other.ApplyHandshake(Handshake("other"));
        Assert.Throws<ArgumentException>(()=>vm.AssignReference(other,"other:a",conflicting));
        Assert.Empty(other.ReferenceAssignments);
        vm.AssignReference(item,"s:a",Profile("replacement"));
        Assert.False(item.IsCorrected);Assert.Equal("",item.ModelPath);Assert.Equal("",item.ReportPath);
        vm.AssignReference(item,"s:a",null);
        Assert.Empty(item.ReferenceAssignments);
    }

    [AvaloniaFact]
    public void NewDetectionInvalidatesAssignmentsButSameSnapshotDoesNot()
    {
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-workflow-missing.png"); item.ApplyHandshake(Handshake("old"));
        vm.AssignReference(item,"old:a",Profile("card"));
        item.ApplyHandshake(Handshake("old"));
        Assert.Single(item.ReferenceAssignments); Assert.False(item.ReferencesNeedAssignment);
        item.ApplyHandshake(Handshake("new"));
        Assert.Empty(item.ReferenceAssignments);Assert.True(item.ReferencesNeedAssignment);
        Assert.NotEmpty(item.ReferenceProfiles);
        Assert.Throws<InvalidOperationException>(()=>vm.AssignReference(item,"old:a",Profile("stale")));
        vm.AssignReference(item,"new:a",Profile("reassigned"));
        Assert.False(item.ReferencesNeedAssignment);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkingSessionRestoresOnlyAssignmentsFromItsDetectionSnapshot(bool stale)
    {
        var path=Path.Combine(Path.GetTempPath(),$"ccp-reference-session-{Guid.NewGuid():N}.png");File.WriteAllBytes(path,[]);
        var oldOutput=path+".corrected.png";File.WriteAllBytes(oldOutput,[]);
        var vm=new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        try
        {
            var state=new SessionState { ActivePath=path,Items=[new SessionItemDto {Path=path,HandshakeJson=Handshake("saved").Json,CorrectedPath=oldOutput,
                ReferenceProfiles=[Profile("card")],ReferenceAssignments=[new ChartReferenceAssignment {ChartId=stale?"old:a":"saved:a",ProfileId="card",ProfileRevision=1}]}]};
            vm.RestoreWorkingItems(state);
            var item=Assert.Single(vm.Session);
            Assert.Equal("saved",item.Handshake!.SnapshotId);
            Assert.Equal(stale,item.ReferencesNeedAssignment);
            if(stale)
            {
                Assert.Empty(item.ReferenceAssignments);Assert.False(item.IsCorrected);Assert.Empty(item.CorrectedPath);Assert.False(item.IncludeInSave);
                Assert.True(File.Exists(oldOutput));
            }
            else Assert.Equal("saved:a",Assert.Single(item.ReferenceAssignments).ChartId);
        }
        finally
        {
            foreach(var item in vm.Session)item.Dispose();
            foreach (var temporary in new[] { path, oldOutput })
            {
                // Disposal invalidates the result but an already-running thumbnail
                // hash may still have this fixture open briefly on Windows.
                var deadline = Environment.TickCount64 + 2000;
                while (true)
                {
                    try { File.Delete(temporary); break; }
                    catch (IOException ex) when (OperatingSystem.IsWindows()
                        && (ex.HResult & 0xffff) is 32 or 33 && Environment.TickCount64 < deadline)
                    { await Task.Delay(20); }
                }
            }
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory]
    [InlineData("classic",24)]
    [InlineData("digital-sg",140)]
    public void ReferenceEditorRendersAllCellsWithScrollableEntryAndNoSessionMutation(string chart,int cells)
    {
        var vm=new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item=new SessionItemViewModel("reference-workflow-missing.png");item.ApplyHandshake(Handshake("render",chart));
        vm.Session.Add(item);vm.Active=item;
        var dialog=new ReferenceEditorDialog(vm) {Width=760,Height=520};
        try
        {
            dialog.Show();Dispatcher.UIThread.RunJobs();
            var texts=dialog.GetVisualDescendants().OfType<TextBox>().ToList();
            Assert.True(texts.Count>=cells*3+1,$"Expected all {cells} cells and profile name.");
            Assert.Contains(dialog.GetVisualDescendants().OfType<ScrollViewer>(),s=>s.Extent.Height>s.Viewport.Height);
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(),b=>Equals(b.Content,"Assign reference"));
            var templatePicker=dialog.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.ItemsSource?.Cast<object>().Contains(chart)==true);
            var allowed=templatePicker.ItemsSource!.Cast<string>().ToArray();
            Assert.Equal(chart=="classic"?new[]{"classic","passport"}:new[]{chart},allowed);
            Assert.Empty(item.ReferenceAssignments);
            using var frame=dialog.CaptureRenderedFrame();Assert.NotNull(frame);
            var directory=Path.Combine(AppContext.BaseDirectory,"TestResults","References");Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory,$"reference-editor-{chart}.png"));
            dialog.Width=1080;dialog.Height=740;Dispatcher.UIThread.RunJobs();
            EditorInvoke(dialog,"CopyBuiltIn");Dispatcher.UIThread.RunJobs();
            using var defaultFrame=dialog.CaptureRenderedFrame();Assert.NotNull(defaultFrame);
            defaultFrame.Save(Path.Combine(directory,$"reference-editor-{chart}-default.png"));
            Assert.Empty(item.ReferenceAssignments);
        }
        finally {dialog.Close();Dispatcher.UIThread.RunJobs();}
    }

    [AvaloniaFact]
    public void ImportedReferenceValuesRoundTripThroughTheEditableGridWithoutRounding()
    {
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-precision-missing.png");
        item.ApplyHandshake(Handshake("precision")); vm.Session.Add(item); vm.Active = item;
        var profile = Profile("precise"); profile.Values[0] = [50.123456789012344, 180.12345678901235, -140.12345678901235];
        var dialog = new ReferenceEditorDialog(vm);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            typeof(ReferenceEditorDialog).GetMethod("LoadProfile", flags)!.Invoke(dialog, [profile]);
            var restored = (ReferenceProfile)typeof(ReferenceEditorDialog).GetMethod("ReadDraft", flags)!.Invoke(dialog, null)!;
            Assert.Equal(profile.Values[0], restored.Values[0]);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTheory]
    [InlineData("row-major", false)]
    [InlineData("column-major", false)]
    [InlineData("row-major", true)]
    [InlineData("column-major", true)]
    public void ReusingAnEditorProfileKeepsItsRevisionUntilValuesChange(string order, bool edit)
    {
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-reuse-missing.png");
        item.ApplyHandshake(Handshake("reuse")); vm.Session.Add(item); vm.Active = item;
        var profile = Profile("shared"); profile.Revision = 4; profile.Name = "  Shared imported reference  ";
        if (order == "column-major")
        {
            var rows = profile.Values;
            profile.Values = Enumerable.Range(0, 24).Select(i => rows[(i % 4) * 6 + i / 4].ToArray()).ToList();
            profile.Order = order;
        }
        vm.AssignReference(item, "reuse:a", profile);
        item.FaceChoice = 1;
        var dialog = new ReferenceEditorDialog(vm);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            typeof(ReferenceEditorDialog).GetMethod("LoadProfile", flags)!.Invoke(dialog, [profile.Snapshot()]);
            if (edit)
            {
                var inputs = (List<TextBox[]>)typeof(ReferenceEditorDialog).GetField("_inputs", flags)!.GetValue(dialog)!;
                inputs[0][0].Text = "40";
            }
            typeof(ReferenceEditorDialog).GetMethod("Assign", flags)!.Invoke(dialog, null);
            Assert.Equal(4, item.ReferenceAssignments.Single(a => a.ChartId == "reuse:a").ProfileRevision);
            Assert.Equal(edit ? 5 : 4, item.ReferenceAssignments.Single(a => a.ChartId == "reuse:b").ProfileRevision);
            var assigned = item.ReferenceProfiles.Single(p => p.Id == profile.Id && p.Revision == (edit ? 5 : 4));
            if (edit) Assert.Equal(40, assigned.RowMajorValues()[0][0]);
            else Assert.Equal(profile.ToJson(), assigned.ToJson());
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task RepeatedExportsAndAssignmentShareImmutableDraftRevisions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ccp-reference-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-export-missing.png");
        item.ApplyHandshake(Handshake("export")); vm.Session.Add(item); vm.Active = item;
        var original = Profile("exported"); original.Revision = 4;
        vm.AssignReference(item, "export:a", original); item.FaceChoice = 1;
        var dialog = new ReferenceEditorDialog(vm);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            EditorInvoke(dialog, "LoadProfile", original.Snapshot());
            EditFirstCell(dialog, "40");
            var first = Path.Combine(directory, "first.json");
            var repeat = Path.Combine(directory, "repeat.json");
            var changed = Path.Combine(directory, "changed.json");
            await ExportDraft(dialog, first);
            await ExportDraft(dialog, repeat);
            Assert.Equal(5, ReferenceProfile.FromJson(File.ReadAllText(first)).Revision);
            Assert.Equal(File.ReadAllText(first), File.ReadAllText(repeat));
            EditFirstCell(dialog, "39");
            await ExportDraft(dialog, changed);
            Assert.Equal(6, ReferenceProfile.FromJson(File.ReadAllText(changed)).Revision);
            EditorInvoke(dialog, "Assign");
            Assert.Equal(4, item.ReferenceAssignments.Single(a => a.ChartId == "export:a").ProfileRevision);
            Assert.Equal(6, item.ReferenceAssignments.Single(a => a.ChartId == "export:b").ProfileRevision);
            Assert.Equal(39, item.ReferenceProfiles.Single(p => p.Id == original.Id && p.Revision == 6).Values[0][0]);
            Assert.Equal(original.ToJson(), item.ReferenceProfiles.Single(p => p.Id == original.Id && p.Revision == 4).ToJson());
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, recursive:true); }
    }

    [AvaloniaFact]
    public async Task UnchangedUnassignedJsonImportExportsItsExactColumnMajorSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ccp-reference-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-import-missing.png");
        item.ApplyHandshake(Handshake("import")); vm.Session.Add(item); vm.Active = item;
        var source = Profile("external"); source.Revision = 7; source.Name = "  External reference  ";
        var rows = source.Values;
        source.Values = Enumerable.Range(0, 24).Select(i => rows[(i % 4) * 6 + i / 4].ToArray()).ToList();
        source.Order = "column-major";
        var dialog = new ReferenceEditorDialog(vm);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            EditorInvoke(dialog, "AcceptImportedProfile", source.Snapshot(), true);
            Assert.Empty(item.ReferenceAssignments); Assert.Empty(item.ReferenceProfiles);
            var path = Path.Combine(directory, "unchanged.json");
            await ExportDraft(dialog, path);
            Assert.Equal(source.ToJson(), File.ReadAllText(path));
            EditorInvoke(dialog, "Assign");
            Assert.Equal(7, Assert.Single(item.ReferenceAssignments).ProfileRevision);
            Assert.Equal(source.ToJson(), Assert.Single(item.ReferenceProfiles).ToJson());
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, recursive:true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingAcceptedUnassignedJsonImportAllocatesANewRevision(bool exportFirst)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ccp-reference-import-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-import-edit-missing.png");
        item.ApplyHandshake(Handshake("import-edit")); vm.Session.Add(item); vm.Active = item;
        var source = Profile("external-edit"); source.Revision = 7;
        var dialog = new ReferenceEditorDialog(vm);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            EditorInvoke(dialog, "AcceptImportedProfile", source.Snapshot(), true);
            EditFirstCell(dialog, "40");
            if (exportFirst)
            {
                var path = Path.Combine(directory, "edited.json");
                await ExportDraft(dialog, path);
                Assert.Equal(8, ReferenceProfile.FromJson(File.ReadAllText(path)).Revision);
            }
            EditorInvoke(dialog, "Assign");
            Assert.Equal(8, Assert.Single(item.ReferenceAssignments).ProfileRevision);
            Assert.Equal(40, Assert.Single(item.ReferenceProfiles).Values[0][0]);
            Assert.Equal(7, source.Revision); Assert.NotEqual(40, source.Values[0][0]);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, recursive:true); }
    }

    [AvaloniaFact]
    public async Task FailedExportDoesNotEstablishOrConsumeADraftRevision()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ccp-reference-export-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-export-fail-missing.png");
        item.ApplyHandshake(Handshake("fail")); vm.Session.Add(item); vm.Active = item;
        var source = Profile("export-failure"); source.Revision = 4;
        vm.AssignReference(item, "fail:a", source);
        var dialog = new ReferenceEditorDialog(vm);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            EditFirstCell(dialog, "40");
            await Assert.ThrowsAnyAsync<Exception>(() => ExportDraft(dialog, directory));
            EditFirstCell(dialog, "39");
            var path = Path.Combine(directory, "successful.json");
            await ExportDraft(dialog, path);
            Assert.Equal(5, ReferenceProfile.FromJson(File.ReadAllText(path)).Revision);
            Assert.Equal(source.ToJson(), Assert.Single(item.ReferenceProfiles).ToJson());
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); Directory.Delete(directory, recursive:true); }
    }

    [AvaloniaFact]
    public void AssigningChartsKeepsTheEditorOpenAndPreservesOtherChartDrafts()
    {
        var vm = new MainWindowViewModel(persistenceEnabled:false) { Busy = false };
        using var item = new SessionItemViewModel("reference-multichart-drafts-missing.png");
        item.ApplyHandshake(Handshake("drafts")); vm.Session.Add(item); vm.Active = item;
        var dialog = new ReferenceEditorDialog(vm);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs();
            var picker = (ComboBox)typeof(ReferenceEditorDialog).GetField("_face", flags)!.GetValue(dialog)!;
            var reusable = (ComboBox)typeof(ReferenceEditorDialog).GetField("_reusable", flags)!.GetValue(dialog)!;
            ReferenceProfile ChoiceProfile(object choice) => (ReferenceProfile)choice.GetType().GetProperty("Profile")!.GetValue(choice)!;
            EditorInvoke(dialog, "LoadProfile", Profile("draft-a"));
            EditFirstCell(dialog, "40");
            picker.SelectedIndex = 1;
            EditorInvoke(dialog, "LoadProfile", Profile("draft-b"));
            EditFirstCell(dialog, "41");
            EditorInvoke(dialog, "Assign");
            Assert.True(dialog.IsVisible);
            Assert.Equal("drafts:b", Assert.Single(item.ReferenceAssignments).ChartId);
            Assert.Contains(reusable.ItemsSource!.Cast<object>(), choice => ChoiceProfile(choice).Id == "draft-b");
            Assert.Equal("draft-b", ((ReferenceProfile)EditorInvoke(dialog, "ReadDraft")!).Id);

            picker.SelectedIndex = 0;
            var first = (ReferenceProfile)EditorInvoke(dialog, "ReadDraft")!;
            Assert.Equal("draft-a", first.Id); Assert.Equal(40, first.Values[0][0]);
            EditorInvoke(dialog, "Assign");
            Assert.True(dialog.IsVisible);
            Assert.Equal(2, item.ReferenceAssignments.Count);
            Assert.Equal(40, item.ReferenceProfiles.Single(p => p.Id == "draft-a").Values[0][0]);
            Assert.Equal(41, item.ReferenceProfiles.Single(p => p.Id == "draft-b").Values[0][0]);
            Assert.Equal(new[] { "draft-a", "draft-b" }, reusable.ItemsSource!.Cast<object>().Select(choice => ChoiceProfile(choice).Id).OrderBy(id => id));
            Assert.Equal("draft-a", ((ReferenceProfile)EditorInvoke(dialog, "ReadDraft")!).Id);
            EditorInvoke(dialog, "Assign");
            Assert.Equal(1, item.ReferenceAssignments.Single(a => a.ChartId == "drafts:a").ProfileRevision);

            EditorInvoke(dialog, "UseBuiltIn");
            Assert.True(dialog.IsVisible);
            Assert.Equal("drafts:b", Assert.Single(item.ReferenceAssignments).ChartId);
            picker.SelectedIndex = 1;
            Assert.Equal(41, ((ReferenceProfile)EditorInvoke(dialog, "ReadDraft")!).Values[0][0]);
            picker.SelectedIndex = 0;
            Assert.Equal(40, ((ReferenceProfile)EditorInvoke(dialog, "ReadDraft")!).Values[0][0]);
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Done"));
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("Assign each edited chart") == true);
            reusable.SelectedItem = reusable.ItemsSource!.Cast<object>().Single(choice => ChoiceProfile(choice).Id == "draft-b");
            Assert.Equal("draft-b", ((ReferenceProfile)EditorInvoke(dialog, "ReadDraft")!).Id);
            Assert.Equal(41, ((ReferenceProfile)EditorInvoke(dialog, "ReadDraft")!).Values[0][0]);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static object? EditorInvoke(ReferenceEditorDialog dialog, string method, params object[] arguments) =>
        typeof(ReferenceEditorDialog).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dialog, arguments);
    private static void EditFirstCell(ReferenceEditorDialog dialog, string value)
    {
        var inputs = (List<TextBox[]>)typeof(ReferenceEditorDialog).GetField("_inputs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;
        inputs[0][0].Text = value;
    }
    private static Task ExportDraft(ReferenceEditorDialog dialog, string path) =>
        (Task)EditorInvoke(dialog, "ExportProfileToPathAsync", path, EditorInvoke(dialog, "ReadDraft")!)!;

    private static ReferenceProfile Profile(string id)=>new() {Id=id,Name=id,Values=ReferenceCatalog.Get("classic").Lab.Select(v=>v.ToArray()).ToList()};
    private static HandshakeDocument Handshake(string snapshot,string chart="classic")
    {
        var t=ReferenceCatalog.Get(chart);
        var quads=Enumerable.Range(0,t.Rows*t.Columns).Select(i=>new[]{new[]{(i%t.Columns)*10.0,(i/t.Columns)*10.0},new[]{(i%t.Columns+1)*10.0,(i/t.Columns)*10.0},new[]{(i%t.Columns+1)*10.0,(i/t.Columns+1)*10.0},new[]{(i%t.Columns)*10.0,(i/t.Columns+1)*10.0}}).ToArray();
        return new HandshakeDocument(System.Text.Json.JsonSerializer.Serialize(new {ok=true,snapshot_id=snapshot,charts=new[]{new {chart_id=$"{snapshot}:a",chart_type=chart,cli_chart=chart,patch_obbs=quads},new {chart_id=$"{snapshot}:b",chart_type=chart,cli_chart=chart,patch_obbs=quads}}}));
    }
}
