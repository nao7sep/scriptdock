namespace ScriptDock.Views;

/// <summary>The script chip's sizing: a chip is as wide as its name up to this cap, inside the narrowest
/// Scripts pane, so a long name never sizes the list (interface-styling-conventions, Labels).</summary>
public static class ScriptChip
{
    public const double MaxWidth = 280;
}
