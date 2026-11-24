using System.ComponentModel;
using Avalonia.Headless.XUnit;
using ColorCorrect.App.Models;
using ColorCorrect.App.ViewModels;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>Numeric drafts and validation rules live on CorrectionSettingsViewModel.</summary>
public sealed class SettingsValidationTests
{
    private static string? Validate(CorrectionSettingsViewModel s, bool fit = true, bool modelChanged = false,
        bool whiteChanged = false, bool whiteReadable = true, bool showPls = false, bool showIter = false, bool showNn = false) =>
        s.ValidationError(fit, modelChanged, whiteChanged, () => whiteReadable, showPls, showIter, showNn);

    [AvaloniaFact]
    public void DraftsUpdateValidValuesKeepInvalidTextAndRaiseDraftEdited()
    {
        var s = new CorrectionSettingsViewModel();
        var edits = 0;
        s.DraftEdited += (_, _) => edits++;

        s.FitSamplesText = "12";
        Assert.Equal(12m, s.FitSamples);
        s.FitSamplesText = "abc";
        Assert.Equal(12m, s.FitSamples); // unchanged
        Assert.Equal("abc", s.FitSamplesText); // the draft is kept for the field
        Assert.Equal(2, edits);
        Assert.Equal("Samples per side: enter a number.", Validate(s));

        s.FitSamplesText = "1.5";
        Assert.Equal("Samples per side: enter a whole number from 1 to 200.", Validate(s));
    }

    [AvaloniaFact]
    public void ClearDraftsRestoresSettingTextAndOptionallyNotifies()
    {
        var s = new CorrectionSettingsViewModel();
        s.NnEpochsText = "oops";
        var changed = new List<string?>();
        ((INotifyPropertyChanged)s).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        s.ClearDrafts(notify: false);
        Assert.Empty(changed);
        Assert.Equal(s.NnEpochs.ToString(System.Globalization.CultureInfo.CurrentCulture), s.NnEpochsText);

        s.NnEpochsText = "oops";
        s.ClearDrafts();
        Assert.Contains("NnEpochsText", changed);
    }

    [AvaloniaFact]
    public void RulesKeepTheirOrderAndScope()
    {
        var s = new CorrectionSettingsViewModel { DoFfc = true };
        // Apply-only checks come first and only for applies.
        Assert.StartsWith("The loaded model changed", Validate(s, fit: false, modelChanged: true));
        Assert.StartsWith("Choose a readable white reference", Validate(s, whiteReadable: false));
        Assert.StartsWith("The model's white reference changed", Validate(s, fit: false, whiteChanged: true));
        Assert.Null(Validate(s, fit: true, whiteChanged: true)); // ignored when fitting

        s.FfcSmoothWindow = 4;
        Assert.Equal("FFC smooth window must be an odd whole number.", Validate(s));
        s.FfcSmoothWindow = 5;

        s.SelectedInputSpace = new NamedValue("p3", "Display P3");
        Assert.StartsWith("FFC, tone response and white balance require sRGB / D65", Validate(s));
        Assert.Null(Validate(s, fit: false)); // input space only matters for fits
        s.SelectedInputSpace = new NamedValue("srgb", "sRGB");

        s.HiddenLayersText = "not layers";
        Assert.Null(Validate(s)); // hidden layers only matter for the NN method
        Assert.NotNull(Validate(s, showNn: true));

        s.DoCc = false;
        Assert.Null(Validate(s, showNn: true)); // CC parameters are skipped when CC is off
    }
}
