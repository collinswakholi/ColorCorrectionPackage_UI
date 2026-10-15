using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorCorrect.App.Views;
using ColorCorrect.App.ViewModels;
using Xunit;

namespace ColorCorrect.App.Headless.Tests;

public sealed class HelpWindowTests
{
    [AvaloniaFact]
    public void MainWindow_F1OpensGuide_EscapeReturnsFocus()
    {
        var window = new MainWindow
        {
            Width = 1002, Height = 672,
            DataContext = new MainWindowViewModel(persistenceEnabled: false) { Busy = false }
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var guide = Assert.Single(window.OwnedWindows.OfType<HelpWindow>());
            Assert.True(guide.SearchBox.IsFocused);
            Assert.True(window.ModalScrim.IsVisible);
            guide.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(guide.IsVisible);
            Assert.False(window.ModalScrim.IsVisible);
            Assert.True(window.HelpButton.IsFocused);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Guide_SearchesBody_HandlesNoResults_AndClosesWithEscape()
    {
        var owner = new Window { Width = 1002, Height = 672 };
        owner.Show();
        var guide = new HelpWindow();
        var closed = guide.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(guide.SearchBox.IsFocused);
            Assert.Equal(10, guide.TopicList.ItemCount);
            guide.SearchBox.Text = "Blank.JPG";
            Dispatcher.UIThread.RunJobs();
            Assert.True(guide.TopicList.ItemCount >= 1);
            Assert.Contains("Blank.JPG", Article(guide));
            guide.SearchBox.Text = "not-a-topic-12345";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, guide.TopicList.ItemCount);
            Assert.Contains("No matching topics", Article(guide));
            guide.SearchBox.Text = "";
            Dispatcher.UIThread.RunJobs();
            guide.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed.IsCompleted);
        }
        finally { guide.Close(); owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guide_AllTopicsRenderWithinOwner_InBothThemes(bool light)
    {
        foreach (var theme in new[] { light ? ThemeVariant.Light : ThemeVariant.Dark })
        {
            var owner = new Window { Width = 1002, Height = 672, RequestedThemeVariant = theme };
            owner.Show();
            var guide = new HelpWindow { RequestedThemeVariant = theme };
            _ = guide.ShowDialog(owner);
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.True(guide.Bounds.Width <= owner.ClientSize.Width);
                Assert.True(guide.Bounds.Height <= owner.ClientSize.Height);
                for (var i = 0; i < guide.TopicList.ItemCount; i++)
                {
                    guide.TopicList.SelectedIndex = i;
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(guide.GuideScroll.Viewport.Height > 200);
                    Assert.True(guide.GuideScroll.Extent.Width <= guide.GuideScroll.Viewport.Width + .5);
                    Assert.NotEmpty(Article(guide));
                }
                guide.TopicList.SelectedIndex = 0;
                Dispatcher.UIThread.RunJobs();
                VisualRegression.Match(guide, $"guide-{theme.Key.ToString()!.ToLowerInvariant()}");
            }
            finally { guide.Close(); owner.Close(); Dispatcher.UIThread.RunJobs(); }
        }
    }

    [AvaloniaFact]
    public void Guide_ContainsResearchAndNumericalLimitations()
    {
        var topics = HelpWindow.LoadTopics();
        Assert.Equal(10, topics.Select(t => t.Title).Distinct().Count());
        Assert.All(topics, t => Assert.NotEmpty(t.Paragraphs));
        Assert.Equal("https://acsess.onlinelibrary.wiley.com/doi/full/10.1002/ppj2.70067", HelpWindow.ResearchUrl);
        var all = string.Join("\n", topics.SelectMany(t => t.Paragraphs));
        Assert.Contains("CIEDE2000", all);
        Assert.Contains("ICC", all);
        Assert.Contains("cannot certify your individual chart", all);
        Assert.Contains("Blank.JPG", all);
        Assert.Contains("not an internet server", all);
    }

    private static string Article(HelpWindow guide) => string.Join("\n",
        guide.ArticlePanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
}
