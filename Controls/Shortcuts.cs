using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PCOptimizer.Controls;

/// <summary>What a page's button does, so one key can mean the same thing on every page.</summary>
public enum ButtonRole { None, Refresh, Main, Cancel }

public sealed record Shortcut(Key Key, ModifierKeys Modifiers, string Keys, string Description, Func<bool> Run);

public static class Shortcuts
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(ButtonRole), typeof(Shortcuts), new PropertyMetadata(ButtonRole.None, (target, e) =>
        {
            if (target is not Button button) return;
            var keys = (ButtonRole)e.NewValue switch
            { ButtonRole.Refresh => "F5", ButtonRole.Main => "Ctrl+Enter", ButtonRole.Cancel => "Esc", _ => null };
            if (keys != null && button.ToolTip == null) button.ToolTip = "Keyboard: " + keys;
        }));

    public static Button Role(this Button button, ButtonRole role)
    {
        button.SetValue(RoleProperty, role);
        return button;
    }

    /// <summary>The first button with this role that is on screen and enabled.</summary>
    public static Button? Find(DependencyObject page, ButtonRole role)
    {
        if (page is Button button && (ButtonRole)button.GetValue(RoleProperty) == role &&
            button.IsVisible && button.IsEnabled) return button;
        if (page is UIElement { Visibility: not Visibility.Visible }) return null;
        foreach (var child in LogicalTreeHelper.GetChildren(page).OfType<DependencyObject>())
            if (Find(child, role) is { } found) return found;
        return null;
    }

    public static bool Press(DependencyObject page, ButtonRole role)
    {
        if (Find(page, role) is not { } button) return false;
        ContextMenus.Press(button);
        return true;
    }
}
