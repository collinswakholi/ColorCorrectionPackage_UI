namespace ColorCorrect.App.ViewModels;

public sealed class CatalogFolderViewModel
{
    public CatalogFolderViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(
            path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = path;
        }
    }

    public string Path { get; }
    public string Name { get; }
}
