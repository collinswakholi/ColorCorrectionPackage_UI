using System.Text.RegularExpressions;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class BackgroundToUiTests
{
    /// <summary>
    /// Background work must return to the UI through the dispatcher its owner captured
    /// on the UI thread. <c>Dispatcher.UIThread</c> creates a dispatcher for the calling
    /// thread when none exists, so a late thumbnail or worker callback that read it
    /// between two headless tests became the next test's "UI thread" and Avalonia's
    /// setup then threw, ending the test loop (the intermittent CI hang).
    /// </summary>
    [Fact]
    public void ViewModelsAndServicesCaptureTheirDispatcherInsteadOfLookingItUp()
    {
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ColorCorrect.App"));
        Assert.True(Directory.Exists(app), $"App sources not found: {app}");
        var captured = new Regex(@"^\s*private readonly Dispatcher _ui = Dispatcher\.UIThread;\s*$");
        var offenders = new List<string>();
        foreach (var folder in new[] { "ViewModels", "Services" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(app, folder), "*.cs", SearchOption.AllDirectories))
            {
                // Installed once on the UI thread at startup.
                if (Path.GetFileName(file) == "UnexpectedErrors.cs") continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.Contains("Dispatcher.UIThread", StringComparison.Ordinal)) continue;
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || captured.IsMatch(line)) continue;
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Use the dispatcher captured on the UI thread (a `_ui` field or the window's Dispatcher):\n" + string.Join("\n", offenders));
    }
}
