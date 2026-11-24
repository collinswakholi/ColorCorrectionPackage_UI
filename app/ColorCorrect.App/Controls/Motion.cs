using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;

namespace ColorCorrect.App.Controls;

/// <summary>
/// Decides once, at startup, whether the UI animates. Every transition and keyframe
/// animation lives in Resources/Motion.axaml, which is merged only when motion is on, so
/// reduced-motion users and the headless test platform both get settled frames with no
/// in-between states. The static theme in Theme.axaml always describes the final look.
/// </summary>
public static class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool IsEnabled { get; private set; }

    public static void Initialize(Application app)
    {
        IsEnabled = Detect(app);
        if (!IsEnabled)
        {
            return;
        }

        var baseUri = new Uri("avares://ColorCorrect.App/");
        app.Styles.Add(new StyleInclude(baseUri)
        {
            Source = new Uri("avares://ColorCorrect.App/Resources/Motion.axaml"),
        });
    }

    private static bool Detect(Application app)
    {
        switch (Environment.GetEnvironmentVariable("CCP_REDUCED_MOTION"))
        {
            case "1": return false;
            case "0": return true;
        }

        // Only an interactive desktop session animates. The headless test platform and the
        // XAML previewer have no classic lifetime and must render deterministic frames.
        if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime)
        {
            return false;
        }

        // Windows "Show animations in Windows" (Settings > Accessibility > Visual effects).
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var animate, 0) && !animate)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }

        return true;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, [MarshalAs(UnmanagedType.Bool)] out bool pvParam, uint fWinIni);
}
