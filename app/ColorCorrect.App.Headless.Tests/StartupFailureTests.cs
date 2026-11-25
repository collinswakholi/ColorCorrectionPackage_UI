using System;
using ColorCorrect.App;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class StartupFailureTests
{
    [Fact]
    public void NamesTheMissingLibraryEvenWhenWrapped()
    {
        // The exact text .NET produced on a fresh Ubuntu 24.04 without libICE.
        var missing = new DllNotFoundException(
            "Unable to load shared library 'libICE.so.6' or one of its dependencies. In order to help diagnose loading problems, consider using a tool like strace.");
        Assert.Equal("libICE.so.6", StartupFailure.MissingLibrary(missing));
        Assert.Equal("libICE.so.6", StartupFailure.MissingLibrary(new TypeInitializationException("Avalonia.X11.ICELib", missing)));
    }

    [Fact]
    public void IgnoresOtherFailures()
    {
        Assert.Null(StartupFailure.MissingLibrary(new InvalidOperationException("Unable to load shared library 'x'")));
        Assert.Equal("a required system library", StartupFailure.MissingLibrary(new DllNotFoundException("no name here")));
    }

    [Fact]
    public void MessageSaysWhatToDo()
    {
        var message = StartupFailure.Message("libICE.so.6");
        Assert.Contains("libICE.so.6 is missing", message);
        Assert.Contains(OperatingSystem.IsLinux() ? "./check-system" : "Reinstall", message);
    }
}
