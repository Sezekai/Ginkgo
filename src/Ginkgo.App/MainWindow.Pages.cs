using System.Windows;
using System.Windows.Input;

namespace Ginkgo.App;

/// <summary>
/// Appearance and Settings: sections of their own in the section switch. They fill the window under the top bar (which keeps only
/// the logo, the switch and the section's name); leaving them goes back to the pictures exactly as they were.
/// </summary>
public partial class MainWindow
{
    /// <summary>"Appearance" or "Settings" while one is open, else null.</summary>
    private string? _pageKey;
    private AppearancePage? _appearance;
    private SettingsPage? _settingsPage;
    private string? _appearanceAt;
    private bool InPage => _pageKey is not null;

    private void ShowPageSection(string key, string? sub = null)
    {
        if (ViewerOpen) CloseViewer(fromUser: false);
        CloseSticky();
        if (_pageKey != key)
        {
            _pageKey = key;
            // each page is built the first time it is opened and kept, so opening it again is instant
            if (key == "Appearance")
            {
                if (_appearance is null)
                {
                    _appearance = new AppearancePage(_settings, ThemeApplied, LooksApplied);
                    _appearance.PageShown += k => _appearanceAt = k;
                    if (_appearanceAt is not null) _appearance.ShowPage(_appearanceAt);
                }
                PageHost.Child = _appearance;
            }
            else
            {
                _settingsPage ??= new SettingsPage(this, _settings, _engine);
                PageHost.Child = _settingsPage;
            }
        }
        if (sub is not null) _appearance?.ShowPage(sub);
        PageHost.Visibility = Visibility.Visible;
        BarRow.PinnedOnly = true;
        _barManual = false; _barAuto = false; ApplyBar();
        PlacePage();
        UpdateNavUi();
    }

    /// <summary>Back to the pictures (Done, Esc, the mouse's back button, or another section in the switch).</summary>
    public void LeavePage()
    {
        if (!InPage) return;
        _pageKey = null;
        PageHost.Visibility = Visibility.Collapsed;
        PageHost.Child = null;
        BarRow.PinnedOnly = false;
        SaveSettings();
        UpdateNavUi();
        ApplyBar();
        View.Focus();
    }

    /// <summary>The page starts just under the top bar.</summary>
    private void PlacePage()
    {
        if (!InPage) return;
        PageHost.Margin = new Thickness(0, TopBar.ActualHeight, 0, 0);
    }

    /// <summary>Keys while Appearance or Settings is open: Esc (or the back keys) leaves, Ctrl+1-6 and Ctrl+B move between sections;
    /// everything else goes to the page's own controls.</summary>
    private bool PageKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox;
        bool dropped = Keyboard.FocusedElement is System.Windows.Controls.ComboBox { IsDropDownOpen: true } || Keyboard.FocusedElement is System.Windows.Controls.ComboBoxItem;
        if (key == Key.Escape && !dropped) { LeavePage(); return true; }
        if (alt && key == Key.Left) { LeavePage(); return true; }
        if (ctrl && key == Key.B) { FocusSections(); return true; }
        if (ctrl && key == Key.W) { Close(); return true; }
        if (ctrl && SectionForDigit(key) is string where) { Navigate(where); return true; }
        if (!typing && Keyboard.Modifiers == ModifierKeys.None && key == Key.F11) { ToggleFullscreen(); return true; }
        return false;
    }

    private static string? SectionForDigit(Key k)
    {
        int i = k switch
        {
            Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1, Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3, Key.D5 or Key.NumPad5 => 4, Key.D6 or Key.NumPad6 => 5, _ => -1,
        };
        return i >= 0 && i < SectionSwitch.Sections.Length ? SectionSwitch.Sections[i].key : null;
    }
}
