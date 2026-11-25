using System;
using System.IO;
using System.Text.RegularExpressions;
using ColorCorrect.Engine;

namespace ColorCorrect.App;

/// <summary>
/// Turns a missing native library at start-up into a readable message.
/// On Linux, Avalonia and .NET load X11, GTK, ICU and OpenSSL at runtime; without
/// them the process used to abort with an unhandled exception and a core dump.
/// </summary>
public static partial class StartupFailure
{
    [GeneratedRegex(@"shared library '([^']+)'")]
    private static partial Regex SharedLibrary();

    /// <summary>The missing library behind <paramref name="error"/>, or null if it is not a missing-library failure.</summary>
    public static string? MissingLibrary(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException)
            {
                var match = SharedLibrary().Match(current.Message);
                return match.Success ? match.Groups[1].Value : "a required system library";
            }
        }
        return null;
    }

    public static string Message(string library) =>
        $"ColorCorrect Studio could not start: {library} is missing from this system.\n" +
        (OperatingSystem.IsLinux()
            ? "Run ./check-system in the application folder (/opt/colorcorrect-studio for the .deb) to list " +
              "everything that is missing, then ./install-dependencies --install to install it.\n"
            : "Reinstall ColorCorrect Studio to restore its files.\n");

    /// <summary>Report the failure on stderr and in startup-error.txt; returns the process exit code.</summary>
    public static int Report(string library)
    {
        var message = Message(library);
        Console.Error.Write(message);
        try
        {
            // Desktop-menu launches have no terminal; leave the message where support can find it.
            File.WriteAllText(Path.Combine(SessionStore.AppDataDir(), "startup-error.txt"),
                $"{DateTimeOffset.Now:O}\n{message}");
        }
        catch (Exception)
        {
            // The message is already on stderr; a read-only profile must not hide it.
        }
        return 1;
    }
}
