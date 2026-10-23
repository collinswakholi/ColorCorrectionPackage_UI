using System;
using System.IO;
using Avalonia;
using Avalonia.Dialogs;

namespace ColorCorrect.App.Services;

internal static class DesktopDialogPolicy
{
    internal static AppBuilder ConfigureManagedPicker(AppBuilder builder)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return builder;
        // Avalonia 12 applies the generic window factory only when its options
        // service exists before the storage-provider AfterSetup callback.
        return builder.With(new ManagedFileDialogOptions())
            .UseManagedSystemDialogs<ManagedFilePickerWindow>();
    }

    internal static bool RequiresManagedPicker()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var distribution = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        if (IsWsl(true, distribution, null)) return true;
        try { return IsWsl(true, distribution, File.ReadAllText("/proc/sys/kernel/osrelease")); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // The kernel signal also covers launches with a cleared WSL environment.
    internal static bool IsWsl(bool isLinux, string? distribution, string? kernelRelease)
        => isLinux && (!string.IsNullOrWhiteSpace(distribution)
            || kernelRelease?.Contains("microsoft", StringComparison.OrdinalIgnoreCase) == true);
}
