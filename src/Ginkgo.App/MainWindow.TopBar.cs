using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Ginkgo.App;

/// <summary>
/// The big logo: spread over the window's title bar and the top bar together, with the window's name and its line starting after
/// it. It fades with the top bar when the bar slides away, and in full screen (no title bar) the logo goes back into the bar at
/// its own size.
/// </summary>
public partial class MainWindow
{
    private bool _bigLogoOn;
    private bool _bigHooked;

    private void HookBigLogo()
    {
        if (_bigHooked) return;
        _bigHooked = true;
        BigLogo.MouseLeftButtonDown += (_, e) => OnTitleDown(TitleBar, e);      // drags the window like the title bar
        TopBar.SizeChanged += (_, _) => { UpdateBigLogo(); PlacePage(); };
        TitleBar.SizeChanged += (_, _) => UpdateBigLogo();
        ViewerRoot.IsVisibleChanged += (_, _) => UpdateBigLogo();
        KeysLayer.IsVisibleChanged += (_, _) => UpdateBigLogo();
        MoveLayer.IsVisibleChanged += (_, _) => UpdateBigLogo();
        TitleBar.IsVisibleChanged += (_, _) => UpdateBigLogo();
        BarHost.IsVisibleChanged += (_, _) => UpdateBigLogo();
    }

    /// <summary>Big or in the bar, by the setting and the window's state.</summary>
    private void UpdateBigLogo()
    {
        HookBigLogo();
        bool big = _settings.Logo.Big && !_fullscreen && TitleBar.Visibility == Visibility.Visible && BarHost.Visibility == Visibility.Visible
                   && TopBar.ActualHeight > 10 && !ViewerOpen && KeysLayer.Visibility != Visibility.Visible && MoveLayer.Visibility != Visibility.Visible;
        _bigLogoOn = big;
        if (big)
        {
            double h = Math.Round(TitleBar.ActualHeight + TopBar.ActualHeight - 14);
            var look = _settings.Logo.Copy(); look.Size = h;
            BigLogo.Apply(look);
            BigLogo.SetButtonColours(_settings.Look);
            BigLogo.Margin = new Thickness(20, 7, 0, 0);
            BigLogo.Visibility = Visibility.Visible;
            BigLogo.BeginAnimation(OpacityProperty, null);
            BigLogo.Opacity = _barShown ? 1 : 0;
            BigLogo.IsHitTestVisible = _barShown;
            // the logo in the bar keeps its place empty, so the switch starts after the big one
            Logo.Visibility = Visibility.Hidden;
            Logo.Width = h; Logo.Height = 10;
            TitleText.Margin = new Thickness(20 + h + 12, 0, 0, 1);
            TitleLine.Margin = new Thickness(20 + h + 8, 0, 0, 0);
        }
        else
        {
            BigLogo.Visibility = Visibility.Collapsed;
            Logo.Visibility = Visibility.Visible;
            Logo.Apply(_settings.Logo);          // its own size again
            Logo.SetButtonColours(_settings.Look);
            TitleText.Margin = new Thickness(14, 0, 0, 1);
            TitleLine.Margin = new Thickness(0);
        }
    }

    /// <summary>The big logo comes and goes with the top bar.</summary>
    private void FadeBigLogo(bool shown)
    {
        if (!_bigLogoOn) return;
        BigLogo.IsHitTestVisible = shown;
        BigLogo.BeginAnimation(OpacityProperty, new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(shown ? 140 : 160)));
    }
}
