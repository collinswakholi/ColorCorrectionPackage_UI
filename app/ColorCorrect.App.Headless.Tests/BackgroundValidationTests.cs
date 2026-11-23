using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>
/// UI-polled validation (ValidationMessage, CanExecute) must not hash files on the UI thread,
/// yet still reports a changed model or white reference once the background check finishes.
/// </summary>
public sealed class BackgroundValidationTests
{
    private static (MainWindowViewModel Vm, string Dir, string White, string Model) LoadModelWithWhite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccp-bg-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var white = Path.Combine(dir, "white.png"); File.WriteAllText(white, "white A");
        var model = Path.Combine(dir, "model.json");
        File.WriteAllText(model, System.Text.Json.JsonSerializer.Serialize(new { do_ffc = true, do_gc = false, do_wb = true, do_cc = true,
            ffc_params = new { method = "linear", bins = 17, smooth_window = 3, degree = 4, nn_max_iter = 90, tol = 1e-7 },
            python_api = new { white, white_sha256 = AssetIdentity.Capture(white).Sha256.ToLowerInvariant(), crop_rect = "1,2,30,40" } }));
        var vm = new MainWindowViewModel(persistenceEnabled: false) { Output = { LoadedModelPath = model }};
        vm.RestoreLoadedModelSettings(model);
        vm.Output.UseLoadedModel = true; // apply mode: validation checks the loaded files
        return (vm, dir, white, model);
    }

    private static void Refresh(MainWindowViewModel vm) =>
        typeof(MainWindowViewModel).GetMethod("RefreshValidation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);

    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task PolledValidationUsesBackgroundCheckForChangedWhiteAndModel()
    {
        var (vm, dir, white, model) = LoadModelWithWhite();
        try
        {
            Refresh(vm);
            await Settle(); // let the initial background check finish
            Assert.DoesNotContain("changed", vm.ValidationMessage);

            File.WriteAllText(white, "white B");
            // Reading the property does not hash: it still shows the last background result.
            Assert.DoesNotContain("changed", vm.ValidationMessage);
            Refresh(vm);
            await WaitFor(() => vm.ValidationMessage.Contains("white reference changed"));
            Assert.Contains("white reference changed", vm.ValidationMessage);

            File.WriteAllText(white, "white A");
            File.WriteAllText(model, "{}");
            Refresh(vm);
            await WaitFor(() => vm.ValidationMessage.Contains("model changed"));
            Assert.Contains("model changed", vm.ValidationMessage);
            // The direct, authoritative check still hashes synchronously.
            Assert.Contains("model changed", vm.OperationError(false));
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public async Task ApplyStartVerifiesFilesEvenWhenTheCachedResultIsStale()
    {
        var (vm, dir, white, _) = LoadModelWithWhite();
        try
        {
            Refresh(vm);
            await Settle();
            File.WriteAllText(white, "white B"); // no refresh: the cached state is stale
            var verify = typeof(MainWindowViewModel).GetMethod("VerifyLoadedFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var error = await (Task<string?>)verify.Invoke(vm, [false])!;
            Assert.Contains("white reference changed", error);
            Assert.Contains("white reference changed", vm.ValidationMessage); // cache updated too
            Assert.Null(await (Task<string?>)verify.Invoke(vm, [true])!); // fitting does not use the loaded files
        }
        finally { Directory.Delete(dir, true); }
    }
}
