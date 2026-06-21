using System.Collections.Generic;
using System.Windows.Input;

namespace PixSnap.Services;

internal static class HotkeyDisplayFormatter
{
    public static string FormatForDisplay(ModifierKeys modifiers, Key key)
        => Format(modifiers, key, " + ");

    public static string FormatCompact(ModifierKeys modifiers, Key key)
        => Format(modifiers, key, "+");

    private static string Format(ModifierKeys modifiers, Key key, string separator)
    {
        if (key == Key.None)
            return "无";

        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToLabel(key));
        return string.Join(separator, parts);
    }

    private static string KeyToLabel(Key key) => key switch
    {
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemBackslash => "\\",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.Space => "Space",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        Key.Prior => "PgUp",
        Key.Next => "PgDn",
        _ => key.ToString()
    };
}
