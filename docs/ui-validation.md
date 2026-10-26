# UI validation checklist

This is an acceptance ledger, not a declaration that every control has passed.
The inventory covers the shipped Avalonia views, their conditional panels, and
the reference editor created in code. A successful API operation does not count
as an interactive test of the corresponding control.

## Builds and evidence

- **Public baseline:** downloaded GitHub v0.2.0 Windows/Linux x64 distributions;
  original payloads were independently checked against their published manifests.
- **Initial UI repair candidates (v1-v3):** application 0.2.1 built from the working source, combined
  explicitly with the unchanged public 0.2.0 worker, models, GPU runtimes, and
  SDK 2.2.1 wheel. This local development candidate is not a published release or
  a full-source rebuild of the engine. Its own manifest records that distinction.
- **Component coverage:** 138 Windows-host headless test cases passed with zero
  skips on 26 October 2026. These cases include realized layout/brush assertions,
  workflow state tests, and parameterized cases; they do not establish native
  desktop, driver, clipboard, or display-scaling coverage.
- **Windows final UI:** fresh Sandbox with vGPU; neither the source checkout nor
  development dependencies are mapped into the installed application.
- **Linux final UI:** dedicated Ubuntu 24.04 WSL2/WSLg guest. Hardware inference
  requires the separately inventoried experimental Mesa Dozen add-on. Results
  with that add-on do not qualify the pristine Linux download for WSL GPU use.

Raw screenshots, saved sessions, verification JSON, logs, and package checksums
are retained locally under `_implementation/ui-docs-20261026/`. Private test
photographs and machine-specific paths are not published. The public
[demonstration gallery](images/demo/README.md) is illustrative evidence of the
states it shows, not evidence for all rows below.

## Acceptance matrix

**Observed** means the specified interaction was performed in a guest and its
result checked. **Component** means the linked automated assertions passed.
**Pending** means the interactive case has not yet been established for the
repair candidate. Template controls and conditional branches must be exercised
in their relevant state; opening a settings dialog alone is insufficient.

| Area and required controls/scenarios | Component assertions | Windows guest evidence | WSL guest evidence | Repair-candidate acceptance remaining |
| --- | --- | --- | --- | --- |
| Launch, installed/portable layout, startup failure/retry and normal exit | `PortableComputeTests`, `DesktopDialogPolicyTests` | Public installation/launch and SDK tests passed; candidate v2 launched with DirectML | Candidate v2 launched with a verified fresh profile and separate add-on | Final v3 launch/restart, worker discovery and unsupported-startup recovery |
| Auto/CPU/GPU correction, Advanced, compatibility processing, inference choice, Apply engine change, device details | `ProcessingSettings_DefaultsAreSimpleAndDevicesKeepFastProcessing`, `RepeatedInferenceAndLutChangesRemainIndependentAndPersist`, observed-provider tests | Candidate v3 reviewed the redesigned Processing menu in both themes; public SDK separately exercised DirectML/WebGPU/Vulkan | Candidate v2 used device/backend choices; v3 checked all Processing disclosures at minimum size in both themes | Verify every available provider on appropriate adapters; missing adapters remain explicit failures |
| Image/folder import, drag/drop, catalog add/remove/search/filter, white-reference identification | still-only import tests; sidebar/layout and batch-default tests | Ten source photographs imported for the observed fit queue | Public chart and blank-field photographs loaded for FFC fitting | Each import route, catalog action, filter, drag/drop and exclusion rule |
| Thumbnails, selection/range selection, remove, previous/next, missing-image entry/context menu | session navigation/Enter tests; `EightyFirstImportCannotEvictCompletedResultsAndRestoredSessionsKeepAllEntries` | Ten-image queue; missing source retained and selected for relinking | Prior public-package acceptance exercised ten-image queues and retained sessions | Candidate navigation, selection modifiers, remove and 80→81 boundary; forwarding of desktop modifier keys remains a harness limitation |
| Detect/re-detect, chart face, patch-sampling inset and read-only dim/lattice/sample/plane overlay | auto-detection eligibility and reference snapshot tests | Public ten-image fit queue produced ten distinct captured models | Public Classic photograph detected and corrected with FFC | Candidate buttons, multi-face selection, sampling inset and remaining overlay toggles; no manual geometry editor exists |
| FFC enable, choose/clear/drop white image, automatic plane, method and numeric fields | operation-specific validation; FFC model-reload cases for all families; dialog layout | Observed ten-image fit with FFC; original white identity retained through relinking | Public FFC fit; invalid numeric input displayed in the current campaign | Remaining FFC methods/numeric branches, clear/reselect white and completed-stage evidence; manual white crop is SDK-only |
| GC enable/details and WB enable; active/inactive stage combinations | `NumericDraftsAreValidatedOnlyForTheRequestedOperation`, stage availability/progress tests | Uncached GC regenerated after relinking; Original/Corrected remained viewable | Public FFC→GC→WB→CC fit completed | Candidate toggles/details and saved-model stage inheritance; unavailable fitted stages must not be silently invented |
| CC model, degree/combinations, samples, PLS components, NN layers/optimizer/normalization/dropout/training fields, encodings/output format, teacher export and Reset | CC/NN dialog baselines, architecture validation, operation-specific validation | Candidate v2 opened the NN conditional panel, scrolled controls and used Done | Candidate v2 opened all five families, rejected bad NN layers and restored a valid value | All six method choices from five families; remaining numeric branches, Reset scope and fit/apply behavior |
| References: face/template/profile name, Lab/XYZ/RGB, white/scale/order, copy built-in, paste/import/preview/export/assign/reuse/reset | `ReferenceWorkflowTests` covers all grid sizes, precision, import order, revisions, per-chart drafts and stale assignments | Public SDK custom references passed | Public SDK custom references passed | Candidate editor entry/paste/clipboard and file dialogs; malformed import must preserve the draft; verify multi-chart assignments and linked highlighting |
| Model fit/use-loaded, load/save model, LUT export; A→B→C ownership and deferred regeneration | `ApplyOnlyCapturesModelBAndFailureCannotReplaceItWithModelC`, owned-model/FFC snapshot tests | Public queue captured ten distinct models; relink preserved the completed model | Public completed FFC model/session saved before switching to candidate | Candidate fit A→apply B→load C→preview/export/restart must remain attributable to B; missing/changed model/white asset recovery |
| Batch selection, fit/apply actions, Preview results, existing FFC/GC/WB/CC/Save result indicators | `BatchProgressTests`, `BatchPresentationTests`, `BatchSnapshotTests` | Public fit10 with previews; candidate v1 apply10 with previews off, cancellation and partial failure verified | Candidate v2 completed four-image FFC apply with previews off and independently verified owned outputs | Final candidate fit/apply with both preview choices; per-image applicable-stage verification |
| Cancellation during detection, fit, apply and batch; partial failures followed by recovery | cancellation/result-retention tests; malformed-output and saved-result recovery tests | Candidate v1 cancelled a repeated batch without changing ten prior outputs; good/bad/good finished 2/1 and Ready | Candidate v2 cancelled during FFC, retained the earlier result, then completed a new fit | Final candidate cancellation at additional stages, unwritable output and worker restart |
| Stage Fit/Original/FFC/GC/WB/CC/Corrected, explicit Show preview, zoom/pan/picker/loupe and RGB/Lab/hex clipboard actions | preview suppression/reload and bitmap-lifetime regressions; decoder/orientation tests | Missing-source identical-content relink regenerated GC without a crash, then Original/Corrected displayed | Candidate v3 reopened the Unicode relink session after restart with GC preview, model, white and metrics retained | Remaining stage radios, zoom/pan/pixel copy and explicit preview variants |
| ΔE/Color/Rendition tabs, analysis/sidebar splitters, hover, patch table and three figure exports | plot minimum-height/scroll tests, plot codec export tests, ΔE theme refresh | Public completed result available for figure inspection | Candidate v2 expanded all three figure tabs; v3 confirmed light plot labels and improvement contrast | Remaining hover/table and exported image contents |
| Save menu: each artifact/stage choice, format, destination/browse/save, Report/Output links, overwrite and partial write failure | `SaveWorkflowTests` covers codecs, stage names, overwrite threading, missing assets and output failures | Session/owned assets saved for independent verification | Completed FFC session/owned assets saved | Candidate full-resolution exports, each artifact choice, overwrite decline/accept and preserved successful result after failure |
| Save/load session, identical/changed/missing source relink, unresolved-session save/reopen | session I/O/options, missing-source preservation and bitmap-lifetime tests | Public Unicode identical-content relink retained corrected cache and regenerated GC, verified after save | Candidate v2 identical-content relink and uncached GC passed; changed replacement invalidated dependent state, verified independently | Final v3 retest, unresolved save/reopen and disconnected model/white assets |
| Clear selected/working set, delete generated runs and confirmation Cancel/Confirm | completed-result retention and session tests | Not established for every destructive-control branch | Not established for every destructive-control branch | Use disposable guest data; verify each limited scope and cancellation before any deletion |
| Guide search/topics/links, settings Close/Done, dialog focus/tab cycle/Escape and confirmation focus | `HelpWindowTests`, settings-dialog focus tests, managed picker Escape/placement tests | Buttons used where modifier forwarding was unreliable | Managed picker and normal buttons used | Candidate guide interactions and each dialog; distinguish unavailable network/PDF from a successful open |
| Light↔dark with existing log rows and completed metrics | `ThemeRefreshTests` retains rows/cards across repeated switches | Candidate v2 old logs/cards refreshed; v3 reviewed both Processing themes | Candidate v3 confirmed readable light plot labels/improvement and both-theme minimum layout | Remaining theme states across all result/control branches |
| Minimum 1000×640, wide viewport, 100/150/200% display scale, focus rings and scrollability | minimum/wide theme baselines, batch geometry, managed picker placement at three scales | Native scale permutations not yet established | Candidate v2 checked native 1000×640 in both themes at available 100%; 150/200% unavailable | Record actual guest scaling availability; headless geometry is not a substitute for native display validation |

The named component tests live in
[the headless test project](../app/ColorCorrect.App.Headless.Tests). A test family
listed beside an area establishes only its explicit assertions; it does not
certify all controls in that area.

## Confirmed defects and repairs

The first full-source 0.2.1 Windows package exposed delayed worker cleanup after
the GUI had exited successfully: its owned worker/launcher remained alive after
30 seconds, then exited later. Cleanup awaited from `Closed` could lose the
desktop dispatcher before it finished. The repair holds the initial close until
one owned cleanup task completes, cancels outstanding worker requests and drains
running UI operations before disposing their state. Early splash closure also
cancels startup rather than creating a later workspace. Real child-process
regressions check process exit before the window's `Closed` event, repeated close,
tokenless deferred-request cancellation and UI-finally cleanup. Renewed fixed-build
guest observations are recorded in the local handoff separately from the earlier
candidate campaign; this is delayed cleanup, not evidence of a permanent orphan.

A private Linux shutdown-repair overlay was tested separately: normal
WM_DELETE_WINDOW left the worker absent by 0.132 seconds and application absent
by 0.236 seconds; early splash closure removed both by 3.18 seconds with no late
workspace or respawn during the ten-second observation. Both app exits returned
zero, and owned session/export integrity survived normal close. This overlay used
managed production content from the intermediate 85358c9 repair, embedded
pre-commit 48669d5 metadata and the unchanged actual-CI 48669d5 worker/runtime.
It is not an official repaired Actions package; final downloaded-package cleanup
qualification remains a separate gate.

In the public 0.2.0 app, log foregrounds resolved a theme brush once from an
immutable log level. Switching the theme updated the background and new entries
but left existing rows using the previous foreground. The repair makes these
bindings observe the containing window's theme. The same repair applies to ΔE
card foregrounds/backgrounds that otherwise retained their initial colors.
Existing entries, figures, results, and fitted data are retained.

The focused regressions switch light→dark→light→dark while retaining the same log
controls/entries and metric cards. They passed, followed by all 136 headless cases.
Fresh guest observations of this repaired build are recorded separately above.

Resizing the correction-settings dialog exposed a second issue: the opening-time
maximum settings-scroll height remained fixed after the window grew. This left
large blank margins while still clipping available options. The repair lets the
DockPanel allocate the remaining height to a stretched scroll viewer, shared by
CC, FFC and GC dialogs. A realized regression enlarges CC/FFC dialogs from 650 to
800 logical pixels (DIPs) and shrinks them to 320, checking viewport growth, scroll limits and
accessible Done buttons. Two affected Windows visual references were reviewed
and refreshed; comparison thresholds were not relaxed.

Attempts to send certain desktop modifier combinations through guest automation
did not reliably deliver the intended keys. The current handler already
distinguishes Ctrl+O, Ctrl+Shift+O and Ctrl+Alt+O. This is a test-transport limitation,
not a confirmed shortcut defect. A separate realized headless routed-keyboard
regression verified these three operations, Ctrl+Shift+S session save, and Ctrl+S
opening the Save menu, including consumed key events and distinct dialog calls.
Native guest modifier-key acceptance remains pending.

Light-theme plot labels used fixed pastel colors even though the axes correctly
followed the theme. The repair gives before/after labels dark, contrasting light
theme colors and refreshes improvement percentages with the theme. A rendered
drawing regression checks the actual glyph brushes and at least 4.5:1 contrast;
dark-theme plot colors and fitted data are unchanged.

The PLS and neural-network implementations always include polynomial interaction
terms, so their checkbox was misleading. It is now visible only for Polynomial,
which honors it. Polynomial degree remains available for PLS and neural networks.
The sampling field now says **Samples per side** and explains that 3 means a
3×3 grid. Conditional-control assertions and the reviewed NN reference cover the
changed layout.

Processing settings now use full-width selectors/actions and plain disclosure
headers. Device details scroll inside the minimum window size. A realized
dark/light regression opens the conditional panels with the keyboard, checks the
rounded focus indicator, and verifies that long device details remain contained.
Native final-candidate verification of the redesigned popover remains pending.

## Additional candidate observations

### Windows candidate v1

The first UI-repair candidate completed an observed ten-image apply-only batch
with FFC and **Preview results disabled**: ten completed, zero failed. Independent
verification of `candidate-apply10.json` checked ten owned completed contexts,
full-resolution outputs, suppressed previews and the same captured model hash
`afe36991f96c1e19cebf44edcf7052e2baca2b44865eb9446d509a6056b0b028`.

A repeated batch was cancelled after three completed images with zero failures
at cancellation. Independent before/after verification of
`candidate-cancel10.json` confirmed that all ten previous successful JPEG files
were byte-identical and retained their source/model/white identities and rendering
options. The next good/malformed/good batch continued past the bad input, finished
with two completed and one failed, and returned to Ready. Explicit **Show preview**
on its first completed image worked (`candidate-partial3.json`,
`candidate-ui-cancel.png`, `candidate-ui-partial.png`). The v1 application closed
normally before v2 validation.

These results establish the specified v1 batch/cancellation/recovery behaviors;
they do not certify every control in v2. The earlier missing-source relink run
used the public 0.2.0 application, not this candidate.

### WSL candidate v1

The first UI-repair candidate was observed in WSL before the settings-resize fix:

- A real completed public-package FFC session restored its owned model, white
  image and metrics (`candidate-session-restored.png`).
- Clear working session and Upload files led to automatic GPU detection of the
  public Classic photograph; lattice/focus/sample overlay controls were used
  (`final-01-loaded.png`, `final-01-photo.png`).
- A fresh fit using polynomial FFC degree 3, GC, WB and RPCC2 completed and exposed
  all four stage cards (`final-04-processing.png`, `final-11-deltae.png`).
- The analysis splitter expanded the figures; ΔE, Color and Rendition tabs,
  sidebar scrolling, the model expander, CC dialog open/Escape, guide open/close,
  and pixel hover/Escape were exercised (`final-11/12/13` figure captures,
  `process-controls-main.png`, `final-help-start-here.png`).
- The enlarged CC dialog exposed the fixed-viewport defect described above.

This first candidate inherited a validation profile and restored session before
the fresh fit; it must not be described as a fresh-profile acceptance run. A file
picker interaction that typed a complete path into its filename filter did not
select an existing file; navigating the folder and selecting the file worked.
That interaction did not establish an application crash. Final-candidate checks
use explicit guest configuration/data directories. Broad method, custom-reference,
backend, scaling, cancellation, batch and relink coverage remains pending unless
specifically recorded in the acceptance matrix.

### Windows candidate v2

The rebuilt candidate launched with DirectML, restored the previously saved owned
result in a new process, and displayed its corrected image and saved metrics.
The analysis splitter expanded the figure. Switching dark→light refreshed old
log rows and metric cards. Neural-network conditional controls, scrolling and
Done were exercised. A native resize attempt did not move the border, so it does
not establish Windows resize acceptance. The light plot-label defect described
above was found in this run and is repaired only in v3.

### WSL candidate v2

The guest used explicit fresh configuration/data directories; its environment
and initial state are recorded in `final-profile-proof.json`. Observed cases:

- Restored an owned completed session and expanded the repaired CC dialog to
  460×800 with its full available viewport.
- Opened all five CC method families and their conditional controls, rejected an
  invalid NN hidden-layer value and restored a valid value.
- Rejected an empty custom-reference assignment, copied built-in values into a
  draft, then closed it without assigning it. This does not test CSV import.
- Used Auto/CPU/GPU correction choices and Advanced Vulkan Apply, then restored
  Automatic. Checked both themes at the native 1000×640 minimum.
- Relinked identical source content, regenerated uncached GC and saved the
  retained context. Changed-content relinking cleared prior result/reference
  state and produced a new detection snapshot; the previous corrected RGB cache
  remained byte-identical.
- Completed a four-image FFC apply batch with previews disabled: four completed,
  zero failed, one captured model, reported Vulkan execution for every image.
- Cancelled during FFC without replacing the earlier result, then completed a
  new fit successfully.

Independent verification files are `ui-v2-relink-verification.json`,
`ui-v2-changed-relink-verification.json` and `ui-v2-batch-verification.json`.
The copied guest evidence has its own inventory and checksums. Native 150/200%
scaling, UI CSV import, all export dialogs/keyboard controls and Linux preview-on
batch cancellation/partial failure remain gaps.

### Final v3 candidate

The final private build contains the light plot-label, conditional-checkbox and
processing-popover repairs. Windows Sandbox v3 observations:

- Processing settings was reviewed in both themes, with full-width Advanced and
  Device details controls (`candidate-v3-menu-light.jpg`,
  `candidate-v3-menu-dark.jpg`, `candidate-v3-device-details.jpg`).
- Changed-content relinking completed without a crash. The replacement 6000×4000
  photograph received fresh DirectML detections: two charts, 48 cells. Prior
  stage previews became unavailable and prior metrics cleared. The resulting
  session is `candidate-v3-changed-relink.json`. Independent
  `changed-relink-verification.json` confirmed the new source hash/path and
  detection identity, cleared prior context/output/model/report/stages, and
  byte-identical preservation of all nine pre-existing artifacts. The fixture
  started with zero custom assignments, so it does not prove removal of a
  nonempty assignment. Its original full corrected JPEG was already missing;
  that absence is not evidence of asset loss during this test.

WSL v3 targeted observations:

- Simple/Advanced Processing settings and scrollable Device details fit inside
  the actual 1000×640 client area in both themes.
- PLS/NN retained polynomial degree and hid the ignored interaction checkbox;
  Polynomial still exposed the applicable checkbox.
- Expanded light-theme ΔE plot labels and improvement percentages were readable.
- The saved Unicode identical-content relink session reopened after a WSL restart
  with its GC preview, owned model, white image and metrics retained.

The screenshots are retained under `wsl/candidate-v3-captures/`. The completed
14-scene demonstration lasts 50.64 seconds; its APNG holds and lossless gallery
match their source pixels, and local Chromium/Firefox playback checks passed.
The additional preview-on partial batch remains pending. Earlier v1/v2 observations
are not relabelled as v3 acceptance. Windows guest automation intercepted Escape
at the remote-desktop layer in this run, so it does not establish popup keyboard
dismissal behavior.

## Conditional and programmatic controls

The overlay is read-only; it has no manual corner/plane geometry controls. The
chart Crop slider changes patch-interior sampling, not the white-image rectangle.
The SDK supports explicit white-image `crop_rect` values and saved-model
inheritance; the desktop has no manual FFC crop editor. This is an unavailable UI
feature, not a failed interaction test.

The reference editor creates controls at runtime, outside AXAML. Its checklist is:

- Detected face, profile name, template, encoding, white point, numeric scale,
  import order and reusable profile selection.
- Every canonical cell's three numeric channels: 24×3 for Classic/Passport/Spyder,
  and 140×3 for Digital SG. Check scrolling, keyboard entry, orientation cues and
  linked photograph highlighting for each layout.
- Copy built-in; paste; import CSV/JSON; mapping preview Cancel/Use imported
  values; export JSON; Assign reference; Use built-in for this chart; Done.
- Draft retention while changing faces, assignment to two same-type charts,
  immutable revisions, incompatible/malformed imports and stale-fit indication.

The platform file picker, confirmation dialog, transient toast Close, plot hover
and image overlay also have runtime behavior. The source inventory lists declared
input controls and pointer surfaces once per template; it does not multiply
controls by photograph count, patch count, popup realization or menu item data.

## Remaining support limits

Windows/Linux ARM64 GPU and interactive acceptance require appropriate native
hardware; hosted build/install tests do not supply it. Apple-silicon macOS remains
source-only with its existing source-validation instructions and no downloadable
app. Native Linux desktop/GPU acceptance remains distinct from WSL/WSLg evidence.
These documentation and x64 UI checks alone do not promote a release to stable.

<!-- CONTROL_INVENTORY -->

## Declared control inventory

The source inventory contains **165** declared input elements and pointer surfaces. Each template declaration appears once. These rows establish scope; their presence is not a passed test. “Conditional” includes inherited visibility, such as NN/PLS panels or loaded-result states. Controls inside unopened menus/tabs/dialogs still require their relevant native interaction.

<details>
<summary>CcSettingsDialog: 24 declarations</summary>

| ID | Element | Identity or label | Visibility |
| --- | --- | --- | --- |
| CcSettingsDialog:001 | Button | `CcSettingsCloseBtn` | Containing view/menu |
| CcSettingsDialog:002 | Button | `CcSettingsDoneBtn` | Containing view/menu |
| CcSettingsDialog:003 | ScrollViewer | `SettingsScroll` | Containing view/menu |
| CcSettingsDialog:004 | ComboBox | `ModelSelect` | Containing view/menu |
| CcSettingsDialog:005 | ComboBox | `DegreeSelect` | Conditional |
| CcSettingsDialog:006 | CheckBox | `PolyCombinationsToggle` | Conditional |
| CcSettingsDialog:007 | TextBox | `FitSamples` | Containing view/menu |
| CcSettingsDialog:008 | TextBox | `PLS components` | Conditional |
| CcSettingsDialog:009 | TextBox | `HiddenLayersInput` | Conditional |
| CcSettingsDialog:010 | ComboBox | `NnOptimizerSelect` | Conditional |
| CcSettingsDialog:011 | CheckBox | `Use batch normalization` | Conditional |
| CcSettingsDialog:012 | TextBox | `Dropout rate` | Conditional |
| CcSettingsDialog:013 | TextBox | `Learning rate` | Conditional |
| CcSettingsDialog:014 | TextBox | `Epochs` | Conditional |
| CcSettingsDialog:015 | TextBox | `Patience` | Conditional |
| CcSettingsDialog:016 | TextBox | `Convergence tolerance` | Conditional |
| CcSettingsDialog:017 | ComboBox | `InputSpaceSelect` | Containing view/menu |
| CcSettingsDialog:018 | ComboBox | `InputWhiteSelect` | Containing view/menu |
| CcSettingsDialog:019 | ComboBox | `TargetSpaceSelect` | Containing view/menu |
| CcSettingsDialog:020 | ComboBox | `TargetWhiteSelect` | Containing view/menu |
| CcSettingsDialog:021 | ComboBox | `OutputFormatSelect` | Containing view/menu |
| CcSettingsDialog:022 | CheckBox | `Export training data` | Containing view/menu |
| CcSettingsDialog:023 | Button | `ChartReferencesBtn` | Containing view/menu |
| CcSettingsDialog:024 | Button | `CcSettingsResetBtn` | Containing view/menu |

</details>

<details>
<summary>ConfirmDialog: 2 declarations</summary>

| ID | Element | Identity or label | Visibility |
| --- | --- | --- | --- |
| ConfirmDialog:001 | Button | `Cancel` | Containing view/menu |
| ConfirmDialog:002 | Button | `ConfirmBtn` | Containing view/menu |

</details>

<details>
<summary>FfcSettingsDialog: 9 declarations</summary>

| ID | Element | Identity or label | Visibility |
| --- | --- | --- | --- |
| FfcSettingsDialog:001 | Button | `Close FFC settings` | Containing view/menu |
| FfcSettingsDialog:002 | Button | `Done` | Containing view/menu |
| FfcSettingsDialog:003 | ScrollViewer | `SettingsScroll` | Containing view/menu |
| FfcSettingsDialog:004 | ComboBox | `FfcMethodSelect` | Containing view/menu |
| FfcSettingsDialog:005 | TextBox | `FFC bins` | Containing view/menu |
| FfcSettingsDialog:006 | TextBox | `FFC smooth window` | Containing view/menu |
| FfcSettingsDialog:007 | TextBox | `FFC degree` | Conditional |
| FfcSettingsDialog:008 | TextBox | `FFC tolerance` | Conditional |
| FfcSettingsDialog:009 | TextBox | `FFC NN max iterations` | Conditional |

</details>

<details>
<summary>GcSettingsDialog: 3 declarations</summary>

| ID | Element | Identity or label | Visibility |
| --- | --- | --- | --- |
| GcSettingsDialog:001 | Button | `Close automatic GC details` | Containing view/menu |
| GcSettingsDialog:002 | Button | `DoneButton` | Containing view/menu |
| GcSettingsDialog:003 | ScrollViewer | `SettingsScroll` | Containing view/menu |

</details>

<details>
<summary>HelpWindow: 7 declarations</summary>

| ID | Element | Identity or label | Visibility |
| --- | --- | --- | --- |
| HelpWindow:001 | Button | `Close guide` | Containing view/menu |
| HelpWindow:002 | TextBox | `SearchBox` | Containing view/menu |
| HelpWindow:003 | ListBox | `TopicList` | Containing view/menu |
| HelpWindow:004 | ScrollViewer | `GuideScroll` | Containing view/menu |
| HelpWindow:005 | Button | `Open documentation folder` | Containing view/menu |
| HelpWindow:006 | Button | `Open original research in browser` | Containing view/menu |
| HelpWindow:007 | Button | `Open offline study PDF` | Containing view/menu |

</details>

<details>
<summary>MainWindow: 120 declarations</summary>

| ID | Element | Identity or label | Visibility |
| --- | --- | --- | --- |
| MainWindow:001 | ToggleButton | `ExpandCollapseButton` | Containing view/menu |
| MainWindow:002 | Button | `Open processing settings` | Containing view/menu |
| MainWindow:003 | ScrollViewer | `ProcessingSettingsScroll` | Containing view/menu |
| MainWindow:004 | ComboBox | `ProcessingDevice` | Containing view/menu |
| MainWindow:005 | Expander | `AdvancedAcceleration` | Containing view/menu |
| MainWindow:006 | ComboBox | `DetectionEngine` | Containing view/menu |
| MainWindow:007 | Button | `Apply engine change` | Containing view/menu |
| MainWindow:008 | CheckBox | `CompatibilityProcessing` | Containing view/menu |
| MainWindow:009 | Expander | `ProcessingDeviceDetails` | Containing view/menu |
| MainWindow:010 | Button | `HelpButton` | Containing view/menu |
| MainWindow:011 | Button | `ThemeToggle` | Containing view/menu |
| MainWindow:012 | Button | `SaveLink` | Containing view/menu |
| MainWindow:013 | ScrollViewer | `{Binding FileName}` | Containing view/menu |
| MainWindow:014 | CheckBox | `{Binding FileName}` | Containing view/menu |
| MainWindow:015 | CheckBox | `Corrected (full size)` | Containing view/menu |
| MainWindow:016 | CheckBox | `FFC (preview)` | Containing view/menu |
| MainWindow:017 | CheckBox | `GC (preview)` | Containing view/menu |
| MainWindow:018 | CheckBox | `WB (preview)` | Containing view/menu |
| MainWindow:019 | CheckBox | `CC (preview)` | Containing view/menu |
| MainWindow:020 | CheckBox | `Report (JSON)` | Containing view/menu |
| MainWindow:021 | ComboBox | `Save image format` | Containing view/menu |
| MainWindow:022 | Button | `Browse save folder` | Containing view/menu |
| MainWindow:023 | TextBox | `{Binding OutputDir}` | Containing view/menu |
| MainWindow:024 | Button | `Save selected` | Containing view/menu |
| MainWindow:025 | Button | `ReportLink` | Containing view/menu |
| MainWindow:026 | Button | `OutputLink` | Containing view/menu |
| MainWindow:027 | Button | `ExitLink` | Containing view/menu |
| MainWindow:028 | GridSplitter | `SidebarResize` | Containing view/menu |
| MainWindow:029 | ScrollViewer | `SidebarScroll` | Containing view/menu |
| MainWindow:030 | Border | `DropZone` | Containing view/menu |
| MainWindow:031 | Button | `ImageUpload` | Containing view/menu |
| MainWindow:032 | Button | `FolderUpload` | Containing view/menu |
| MainWindow:033 | Button | `SaveSessionBtn` | Containing view/menu |
| MainWindow:034 | Button | `LoadSessionBtn` | Containing view/menu |
| MainWindow:035 | Button | `ClearSelectedBtn` | Conditional |
| MainWindow:036 | Button | `ClearWorkingBtn` | Containing view/menu |
| MainWindow:037 | Button | `DeleteRunsBtn` | Containing view/menu |
| MainWindow:038 | Button | `RelinkSelectedImage` | Conditional |
| MainWindow:039 | ScrollViewer | `SessionScroll` | Conditional |
| MainWindow:040 | StackPanel | `{Binding SessionAutomationName}` | Conditional |
| MainWindow:041 | MenuItem | `Relink image…` | Conditional |
| MainWindow:042 | Button | `{Binding RemoveAutomationName}` | Conditional |
| MainWindow:043 | Expander | `AvailableToggle` | Containing view/menu |
| MainWindow:044 | Button | `Add catalog folder` | Containing view/menu |
| MainWindow:045 | Button | `Remove catalog folder` | Conditional |
| MainWindow:046 | TextBox | `SampleSearch` | Containing view/menu |
| MainWindow:047 | ComboBox | `SampleGroupFilter` | Containing view/menu |
| MainWindow:048 | ScrollViewer | `CatalogScroll` | Containing view/menu |
| MainWindow:049 | StackPanel | `{Binding Name}` | Containing view/menu |
| MainWindow:050 | Button | `Use as white reference` | Conditional |
| MainWindow:051 | Button | `DetectBtn` | Conditional |
| MainWindow:052 | ComboBox | `ChartFaceSelect` | Conditional |
| MainWindow:053 | Slider | `CropInset` | Containing view/menu |
| MainWindow:054 | Expander | `ProcessToggle` | Containing view/menu |
| MainWindow:055 | Button | `FfcSettingsButton` | Containing view/menu |
| MainWindow:056 | CheckBox | `Enable FFC` | Containing view/menu |
| MainWindow:057 | Button | `Clear white reference` | Conditional |
| MainWindow:058 | Button | `Choose white reference` | Conditional |
| MainWindow:059 | Button | `GcSettingsButton` | Containing view/menu |
| MainWindow:060 | CheckBox | `Enable GC` | Containing view/menu |
| MainWindow:061 | CheckBox | `Enable WB` | Containing view/menu |
| MainWindow:062 | Button | `CcSettingsButton` | Containing view/menu |
| MainWindow:063 | CheckBox | `Enable CC` | Containing view/menu |
| MainWindow:064 | CheckBox | `NoSatToggle` | Containing view/menu |
| MainWindow:065 | Expander | `ModelToggle` | Containing view/menu |
| MainWindow:066 | RadioButton | `UseLoadedModelRadio` | Containing view/menu |
| MainWindow:067 | RadioButton | `FitFromImageRadio` | Containing view/menu |
| MainWindow:068 | Button | `LoadModelInput` | Containing view/menu |
| MainWindow:069 | Button | `SaveModelBtn` | Containing view/menu |
| MainWindow:070 | Button | `Export LUT` | Containing view/menu |
| MainWindow:071 | Button | `Retry engine startup` | Conditional |
| MainWindow:072 | Button | `RunButton` | Containing view/menu |
| MainWindow:073 | Button | `BatchButton` | Containing view/menu |
| MainWindow:074 | Button | `Cancel processing` | Conditional |
| MainWindow:075 | Button | `StageFitBtn` | Conditional |
| MainWindow:076 | RadioButton | `OriginalStageRadio` | Conditional |
| MainWindow:077 | RadioButton | `FfcStageRadio` | Conditional |
| MainWindow:078 | RadioButton | `GcStageRadio` | Conditional |
| MainWindow:079 | RadioButton | `WbStageRadio` | Conditional |
| MainWindow:080 | RadioButton | `CcStageRadio` | Conditional |
| MainWindow:081 | RadioButton | `CorrectedStageRadio` | Conditional |
| MainWindow:082 | Grid | `Grid` | Conditional |
| MainWindow:083 | MenuItem | `Copy RGB` | Conditional |
| MainWindow:084 | MenuItem | `Copy Lab` | Conditional |
| MainWindow:085 | MenuItem | `Copy hex` | Conditional |
| MainWindow:086 | MenuItem | `Copy pixel color` | Conditional |
| MainWindow:087 | ChartOverlay | `QuadOverlay` | Conditional |
| MainWindow:088 | Button | `PreviousImageButton` | Conditional |
| MainWindow:089 | Button | `NextImageButton` | Conditional |
| MainWindow:090 | ToggleButton | `ToggleDim` | Conditional |
| MainWindow:091 | ToggleButton | `ToggleLattice` | Conditional |
| MainWindow:092 | ToggleButton | `ToggleSample` | Conditional |
| MainWindow:093 | ToggleButton | `TogglePlane` | Conditional |
| MainWindow:094 | PickerLoupeControl | `PickerLoupeControl` | Conditional |
| MainWindow:095 | Button | `ShowResultPreview` | Conditional |
| MainWindow:096 | GridSplitter | `AnalysisResize` | Containing view/menu |
| MainWindow:097 | TabItem | `TabLog` | Containing view/menu |
| MainWindow:098 | ScrollViewer | `LogScrollViewer` | Containing view/menu |
| MainWindow:099 | TabItem | `TabDeltaE` | Containing view/menu |
| MainWindow:100 | ScrollViewer | `DeltaEPlotScroll` | Containing view/menu |
| MainWindow:101 | ScrollViewer | `{Binding Name}` | Conditional |
| MainWindow:102 | Button | `Export Delta E plot` | Conditional |
| MainWindow:103 | PatchPlotControl | `PlotDeltaE` | Conditional |
| MainWindow:104 | TabItem | `TabColor` | Containing view/menu |
| MainWindow:105 | Button | `Export Color plots` | Conditional |
| MainWindow:106 | PatchPlotControl | `PlotRgb` | Conditional |
| MainWindow:107 | PatchPlotControl | `PlotResidual` | Conditional |
| MainWindow:108 | TabItem | `TabPatches` | Containing view/menu |
| MainWindow:109 | Button | `Export Rendition plots` | Conditional |
| MainWindow:110 | PatchPlotControl | `PlotRenditionOriginal` | Conditional |
| MainWindow:111 | PatchPlotControl | `PlotRenditionCorrected` | Conditional |
| MainWindow:112 | Expander | `Patch ΔE table` | Conditional |
| MainWindow:113 | ScrollViewer | `Patch` | Conditional |
| MainWindow:114 | ScrollViewer | `{Binding Name}` | Conditional |
| MainWindow:115 | ScrollViewer | `BatchMenuScroll` | Containing view/menu |
| MainWindow:116 | ScrollViewer | `{Binding FileName}` | Conditional |
| MainWindow:117 | CheckBox | `{Binding FileName}` | Conditional |
| MainWindow:118 | CheckBox | `BatchPreviewResults` | Containing view/menu |
| MainWindow:119 | Button | `BatchProcessAll` | Containing view/menu |
| MainWindow:120 | Button | `BatchFromModel` | Containing view/menu |

</details>
