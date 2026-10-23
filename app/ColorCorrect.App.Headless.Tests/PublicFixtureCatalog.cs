using System.Reflection;
using ColorCorrect.App.ViewModels;
using ColorCorrect.Engine;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

/// <summary>Visual fixtures must not depend on private sample folders or junctions.</summary>
internal static class PublicFixtureCatalog
{
    internal static void Install(MainWindowViewModel vm)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var catalog = (List<CatalogItemViewModel>)typeof(MainWindowViewModel)
            .GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        foreach (var old in catalog) old.Dispose();
        catalog.Clear();
        foreach (var name in new[] { "Blank.JPG", "ColorCard_Big.JPG", "ColorCard_Combo.JPG", "Sample_3572.JPG", "Sample_3573.JPG" })
        {
            var path = Path.Combine(root, "test_images", name); Assert.True(File.Exists(path), $"Missing public visual fixture: {path}");
            var white = SampleCatalog.LooksLikeWhite(name);
            catalog.Add(new(new(name, path, white ? "Whites" : "Samples", white)));
        }
        vm.SampleGroups.Clear(); foreach (var group in new[] { "All", "Whites", "Samples" }) vm.SampleGroups.Add(group);
        vm.SelectedGroup = "All"; vm.FilteredSamples.Clear();
        foreach (var item in catalog) vm.FilteredSamples.Add(item);
        vm.SampleCountLabel = catalog.Count.ToString();
    }
}
