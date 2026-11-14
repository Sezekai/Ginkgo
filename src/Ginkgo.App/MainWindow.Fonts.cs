using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// A font and size for each part of the app (Appearance → Text): the section name, the window name, buttons, folder switches,
/// pop-outs, Browse's path, the labels on the pictures, the status bar, the pages, the viewer, menus and tooltips. A part left
/// alone uses the theme's font and its usual size.
/// </summary>
public partial class MainWindow
{
    /// <summary>The font for a part, or the theme's font.</summary>
    private FontFamily PartFont(string key)
    {
        var p = _settings.Fonts.Get(key);
        return ThemeKit.Family(p.Font.Length > 0 ? p.Font : _settings.Theme.Font);
    }

    /// <summary>The size for a part: chosen, else its usual size, else the theme's text size.</summary>
    private double PartSize(string key, double usual = 0)
    {
        var p = _settings.Fonts.Get(key);
        double theme = Math.Clamp(_settings.Theme.FontSize, 9, 18);
        return p.Size > 0 ? p.Size : usual > 0 ? usual : theme;
    }

    private void ApplyFonts()
    {
        var R = Application.Current.Resources;
        foreach (var (key, _, usual, _, _, _) in FontsLook.All)
        {
            R["Font." + key] = PartFont(key);
            R["Size." + key] = PartSize(key, usual);
        }
        // the section name and the window name: their sizes live with the top bar's layout
        SectionTitle.FontFamily = PartFont("Section");
        TitleText.FontFamily = PartFont("Window");
        // places that take a part's font as their own text's font
        void Text(FrameworkElement e, string key)
        {
            e.SetResourceReference(TextElement.FontFamilyProperty, "Font." + key);
            e.SetResourceReference(TextElement.FontSizeProperty, "Size." + key);
        }
        Text(StatusBar, "Status");
        Text(Crumbs, "Path");
        Text(PageHost, "Pages");
        Text(InfoText, "Viewer");
        Text(ViewerNote, "Viewer");
        Text(SavedPopup.Child as FrameworkElement ?? PageHost, "Popouts");
        PopCard.SetFonts(PartFont("Popouts"), PartSize("Popouts"));
        FolderPanel.SetSwitchFont(PartFont("Switches"), PartSize("Switches"));
        MasonryView.SetLabelFont(new Typeface(PartFont("Grid"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), PartSize("Grid", 11) / 11.0);
        View.RefreshLayout();
    }
}
