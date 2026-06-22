using System;
using System.Globalization;
using Avalonia.Media;
using ScriptDock.Converters;
using ScriptDock.Models;
using Xunit;

namespace ScriptDock.Tests.Converters;

/// <summary>
/// The Scripts-row foreground mapping. The actual decision is the flag/hidden → palette-key choice
/// (with removed taking precedence over hidden); the key→brush lookup is <see cref="ScriptDock.Palette"/>'s
/// job and needs a running Application, so it is exercised live in the app rather than here.
/// </summary>
public sealed class ScriptItemColorConverterTests
{
    private static ScriptItem Item(ScriptFlag flag = ScriptFlag.None, bool hidden = false) =>
        new("/x/run.command") { Flag = flag, IsHidden = hidden };

    [Theory]
    [InlineData(ScriptFlag.Removed, false, "DangerTextBrush")] // removed → danger
    [InlineData(ScriptFlag.Removed, true, "DangerTextBrush")]  // removed wins over hidden
    [InlineData(ScriptFlag.None, true, "ScriptHiddenBrush")]   // hidden → dimmed
    [InlineData(ScriptFlag.New, false, "TextPrimaryBrush")]    // new is a dot, not a colour
    [InlineData(ScriptFlag.None, false, "TextPrimaryBrush")]   // ordinary row
    public void PaletteKeyFor_MapsStateToKey(ScriptFlag flag, bool hidden, string expectedKey)
    {
        Assert.Equal(expectedKey, ScriptItemColorConverter.PaletteKeyFor(Item(flag, hidden)));
    }

    [Fact]
    public void Convert_NonScriptItem_IsTransparent()
    {
        var converter = new ScriptItemColorConverter();

        var result = converter.Convert("not a script item", typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        var converter = new ScriptItemColorConverter();

        Assert.Throws<NotSupportedException>(
            () => converter.ConvertBack(null, typeof(ScriptItem), null, CultureInfo.InvariantCulture));
    }
}
