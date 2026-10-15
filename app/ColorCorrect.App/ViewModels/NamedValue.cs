namespace ColorCorrect.App.ViewModels;

public sealed record NamedValue(string Value, string Label)
{
    public override string ToString() => Label;
}
