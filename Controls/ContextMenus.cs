using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PCOptimizer.Controls;

public sealed record MenuAction(string Label, Action Run, bool Enabled = true);

/// <summary>
/// Right-click menus for cards and list items. A menu offers the buttons and tick boxes that are
/// already on that card or row, and choosing one is the same as clicking it, so every confirmation
/// and safety check still applies.
/// </summary>
public static class ContextMenus
{
    private const int MostControls = 10;

    public static readonly DependencyProperty ExtraProperty = DependencyProperty.RegisterAttached(
        "Extra", typeof(Func<IEnumerable<MenuAction>>), typeof(ContextMenus));

    /// <summary>Adds page-specific entries to a card or list.</summary>
    public static void SetExtra(DependencyObject target, Func<IEnumerable<MenuAction>> actions) =>
        target.SetValue(ExtraProperty, actions);

    public static void Attach(FrameworkElement root, Func<IEnumerable<MenuAction>> pageActions)
    {
        bool Open(DependencyObject source, bool atMouse)
        {
            var target = FindTarget(source);
            if (target == null) return false;
            if (target is ListBoxItem { IsEnabled: true } item) item.IsSelected = true;
            var menu = new ContextMenu();
            foreach (var action in Build(target).Concat(new MenuAction?[] { null }).Concat(pageActions()))
            {
                if (action == null) { menu.Items.Add(new Separator()); continue; }
                var entry = new MenuItem { Header = new TextBlock { Text = action.Label }, IsEnabled = action.Enabled };
                entry.Click += (_, _) => action.Run();
                menu.Items.Add(entry);
            }
            menu.PlacementTarget = (UIElement)target;
            menu.Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Center;
            menu.IsOpen = true;
            return true;
        }

        root.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Open(source, true)) e.Handled = true;
        };
        // The Menu key and Shift+F10 open the same menu for the focused item.
        root.PreviewKeyDown += (_, e) =>
        {
            var menuKey = e.Key == Key.Apps ||
                          (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift);
            if (menuKey && Keyboard.FocusedElement is DependencyObject focused && Open(focused, false)) e.Handled = true;
        };
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                       : LogicalTreeHelper.GetParent(node);

    private static bool IsCard(DependencyObject node) =>
        node is Border border && border.Style != null &&
        ReferenceEquals(border.Style, Application.Current.TryFindResource("GlassCard"));

    /// <summary>The nearest list item or card. Text boxes keep their own Copy and Paste menu.</summary>
    public static DependencyObject? FindTarget(DependencyObject source)
    {
        for (var node = source; node != null; node = Parent(node))
        {
            if (node is TextBoxBase or PasswordBox) return null;
            if (node is FrameworkElement { ContextMenu: not null }) return null;
            if (node is ListBoxItem or TreeViewItem || IsCard(node)) return node;
        }
        return null;
    }

    public static List<MenuAction> Build(DependencyObject target)
    {
        var actions = new List<MenuAction>();
        for (var node = target; node != null; node = Parent(node))
            if (node.GetValue(ExtraProperty) is Func<IEnumerable<MenuAction>> extra) actions.AddRange(extra());

        var text = TextOf(target);
        if (text.Length > 0) actions.Add(new MenuAction("Copy text", () => Copy(text)));

        var controls = Descendants(target).OfType<ButtonBase>()
            .Where(b => b.TemplatedParent == null && b.Visibility == Visibility.Visible &&
                        b is Button or CheckBox or ToggleButton and not RadioButton)
            .ToList();
        // A large card with many switches would make an unusable menu.
        if (controls.Count <= MostControls)
            foreach (var control in controls)
            {
                var name = System.Windows.Automation.AutomationProperties.GetName(control);
                if (string.IsNullOrWhiteSpace(name)) name = TextOf(control.Content).ReplaceLineEndings(" ");
                if (name.Length > 60) name = name[..60] + "...";
                var label = control is ToggleButton toggle
                    ? (toggle.IsChecked == true ? "Turn off: " : "Turn on: ") + (name.Length > 0 ? name : "this item")
                    : name;
                if (label.Length > 0) actions.Add(new MenuAction(label, () => Press(control), control.IsEnabled));
            }
        return actions;
    }

    /// <summary>The same as clicking: toggles tick boxes and raises Click.</summary>
    public static void Press(ButtonBase control)
    {
        if (!control.IsEnabled) return;
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        // The logical tree also covers content inside closed expanders; list items made of data need the visual tree.
        var logical = LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList();
        if (logical.Count == 0 && node is Visual)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) logical.Add(VisualTreeHelper.GetChild(node, i));
        foreach (var child in logical)
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    public static string TextOf(object? content)
    {
        var lines = new List<string>();
        void Add(object? item)
        {
            switch (item)
            {
                case null: return;
                case string s: lines.Add(s); return;
                case TextBlock block:
                    lines.Add(new TextRange(block.ContentStart, block.ContentEnd).Text); return;
                case TextBox box: lines.Add(box.Text); return;
                case ButtonBase { Content: string }: return;   // button captions are not part of the item's text
                case ContentControl control: Add(control.Content); return;
                case DependencyObject node:
                    var children = LogicalTreeHelper.GetChildren(node).Cast<object>().ToList();
                    if (children.Count == 0 && node is Visual)
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                            children.Add(VisualTreeHelper.GetChild(node, i));
                    children.ForEach(Add);
                    return;
                default: lines.Add(item.ToString() ?? ""); return;
            }
        }
        if (content is Button { Content: string caption }) return caption;
        Add(content);
        return string.Join(Environment.NewLine, lines.Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (ExternalException) { }   // another program is holding the clipboard
    }
}
