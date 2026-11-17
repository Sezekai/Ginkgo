using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ginkgo.App;

/// <summary>
/// The top bar's layout (Appearance → Top bar) and the big logo. The logo spreads over the window's title bar and the top bar
/// together, with the window's name and the title bar's line starting after it. When the top bar slides away it shrinks into the
/// title bar's top-left corner and the window's name slides along beside it; when the bar comes back, so does the logo. In full
/// screen (no title bar) the logo goes back into the bar at its own size.
/// </summary>
public partial class MainWindow
{
    private bool _bigLogoOn;
    private bool _bigHooked;
    private readonly ScaleTransform _logoScale = new(1, 1);
    private readonly TranslateTransform _logoMove = new();

    private void HookBigLogo()
    {
        if (_bigHooked) return;
        _bigHooked = true;
        BigLogo.RenderTransformOrigin = new Point(0, 0);
        BigLogo.RenderTransform = new TransformGroup { Children = { _logoScale, _logoMove } };
        BigLogo.MouseLeftButtonDown += (_, e) => OnTitleDown(TitleBar, e);      // drags the window like the title bar
        TopBar.SizeChanged += (_, _) => { UpdateBigLogo(); PlacePage(); };
        TitleBar.SizeChanged += (_, _) => UpdateBigLogo();
        ViewerRoot.IsVisibleChanged += (_, _) => UpdateBigLogo();
        KeysLayer.IsVisibleChanged += (_, _) => UpdateBigLogo();
        MoveLayer.IsVisibleChanged += (_, _) => UpdateBigLogo();
        TitleBar.IsVisibleChanged += (_, _) => UpdateBigLogo();
        BarHost.IsVisibleChanged += (_, _) => UpdateBigLogo();
        AppearancePage.PreviewSmallLogo = PreviewSmallLogo;
    }

    /// <summary>The switch, the section's name and the window's name: sizes, gaps, nudges and colours.</summary>
    private void ApplyTopBarLook()
    {
        var t = _settings.TopBarStyle;
        Sections.Height = Math.Clamp(t.SwitchHeight, 20, 80);
        Sections.Margin = new Thickness(0, 0, Math.Clamp(t.TitleGap, 0, 80), 0);
        Sections.RenderTransform = new TranslateTransform(0, t.SwitchY);
        Sections.SetIconLook(BrushOrNull(t.IconColour), BrushOrNull(t.IconHoverColour), BrushOrNull(t.IconOnColour), t.IconScale);
        Sections.SetShape(t.SwitchShape == "Square", t.SwitchRadius, t.SwitchWidth);
        SectionTitleBox.Height = Sections.Height;
        SectionTitleBox.RenderTransform = new TranslateTransform(0, t.TitleY);
        SectionTitle.FontSize = Math.Clamp(t.TitleSize, 10, 48);
        SectionTitle.FontWeight = t.TitleBold ? FontWeights.SemiBold : FontWeights.Normal;
        if (BrushOrNull(t.TitleColour) is { } tc) SectionTitle.Foreground = tc; else SectionTitle.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text");
        TitleText.FontSize = Math.Clamp(t.NameSize, 8, 24);
        // the title bar, its line and the window buttons
        TitleBar.Height = Math.Clamp(t.TitleHeight, 20, 64);
        TitleLine.Visibility = t.Separator == "None" ? Visibility.Collapsed : Visibility.Visible;
        if (BrushOrNull(t.LineColour) is { } lc) TitleLine.Background = lc; else TitleLine.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Edge");
        // the buttons always keep at least 8 px: the space from the corner gives way first in a short title bar
        double inset = Math.Clamp(t.CaptionInset, 0, Math.Max(0, (TitleBar.Height - 8) / 2));
        double bh = t.CaptionHeight > 0 ? Math.Max(8, Math.Min(t.CaptionHeight, TitleBar.Height - inset * 2)) : double.NaN;
        CaptionFrame.Margin = new Thickness(0, inset, inset, inset);
        CaptionFrame.VerticalAlignment = double.IsNaN(bh) ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        CaptionFrame.BorderThickness = new Thickness(t.CaptionOutline ? Math.Clamp(t.CaptionOutlineWidth, 0.5, 4) : 0);
        CaptionFrame.CornerRadius = new CornerRadius(t.CaptionOutline ? Math.Clamp(t.CaptionRadius, 0, 20) : 0);
        if (BrushOrNull(t.CaptionOutlineColour) is { } cc) CaptionFrame.BorderBrush = cc; else CaptionFrame.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Edge");
        foreach (var b in new[] { MinBtn, MaxBtn, CloseBtn })
        {
            b.Width = Math.Clamp(t.CaptionWidth, 24, 100);
            b.Height = bh;
            b.FontSize = Math.Clamp(t.CaptionIcon, 6, 20);
        }
        // the hover colour stays inside the rounded outline
        CaptionBox.SizeChanged -= ClipCaptions; CaptionBox.SizeChanged += ClipCaptions;
        ClipCaptions(null, null);
        // the controls under the title bar: space above and below, and nudged up or down
        BarRow.Margin = new Thickness(14, Math.Clamp(t.SpaceAbove, 0, 60), 10, Math.Clamp(t.SpaceBelow, 0, 60));
        BarRow.RenderTransform = new TranslateTransform(0, Math.Clamp(t.ControlsY, -80, 80));
        if (BrushOrNull(t.NameColour) is { } nc) TitleText.Foreground = nc; else TitleText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text");
    }

    private void ClipCaptions(object? s, SizeChangedEventArgs? e)
    {
        var t = _settings.TopBarStyle;
        double r = t.CaptionOutline ? Math.Max(0, Math.Clamp(t.CaptionRadius, 0, 20) - CaptionFrame.BorderThickness.Left) : 0;
        CaptionBox.Clip = r > 0 ? new RectangleGeometry(new Rect(CaptionBox.RenderSize), r, r) : null;
    }

    private static Brush? BrushOrNull(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var b = new SolidColorBrush(ButtonLook.C(hex)); b.Freeze();
        return b;
    }

    // ---- where the big logo and the window's name go, big and small ----

    private double BigSize => _settings.TopBarStyle.LogoSize > 0 ? _settings.TopBarStyle.LogoSize : Math.Round(TitleBar.ActualHeight + TopBar.ActualHeight - 14);
    private double SmallSize => _settings.TopBarStyle.SmallSize > 0 ? Math.Min(_settings.TopBarStyle.SmallSize, BigSize) : Math.Max(12, TitleBar.ActualHeight - 8);
    private Point SmallAt => new(_settings.TopBarStyle.SmallX, (TitleBar.ActualHeight - SmallSize) / 2 + _settings.TopBarStyle.SmallY);

    /// <summary>Big or in the bar, by the setting and the window's state.</summary>
    private void UpdateBigLogo()
    {
        HookBigLogo();
        ApplyTopBarLook();
        var t = _settings.TopBarStyle;
        bool big = _settings.Logo.Big && !t.MatchLogo && !_fullscreen && TitleBar.Visibility == Visibility.Visible && BarHost.Visibility == Visibility.Visible
                   && TopBar.ActualHeight > 10 && !ViewerOpen && KeysLayer.Visibility != Visibility.Visible && MoveLayer.Visibility != Visibility.Visible;
        _bigLogoOn = big;
        if (big)
        {
            double h = BigSize;
            var look = _settings.Logo.Copy(); look.Size = h;
            BigLogo.Apply(look);
            BigLogo.SetButtonColours(_settings.Look);
            BigLogo.Margin = new Thickness(t.LogoX, t.LogoY, 0, 0);
            BigLogo.Visibility = Visibility.Visible;
            BigLogo.BeginAnimation(OpacityProperty, null);
            BigLogo.Opacity = 1;
            BigLogo.IsHitTestVisible = true;
            // the logo in the bar keeps its place empty, so the switch starts after the big one
            Logo.Visibility = Visibility.Hidden;
            Logo.RenderTransform = null;
            Logo.Width = Math.Max(0, t.LogoX + h - 20); Logo.Height = 10;
            Logo.Margin = new Thickness(6, 0, Math.Clamp(t.SwitchGap, 0, 80), 0);
            SetLogoSmall(!_barShown, animate: false);
        }
        else
        {
            BigLogo.Visibility = Visibility.Collapsed;
            Logo.Visibility = Visibility.Visible;
            // its own size in the bar (or the switch's, to match), and moved by the same left/top settings as the big one
            var own = _settings.Logo.Copy();
            if (t.MatchLogo) own.Size = Math.Clamp(t.SwitchHeight, 16, 160);
            Logo.Apply(own);
            Logo.SetButtonColours(_settings.Look);
            double dx = t.LogoX - 20, dy = t.LogoY - 7;
            Logo.Margin = new Thickness(Math.Max(-14, 6 + dx), 0, Math.Clamp(t.SwitchGap, 0, 80), 0);
            Logo.RenderTransform = new TranslateTransform(0, dy);
            StopLogoAnimations();
            TitleText.Margin = new Thickness(14 + t.NameX, t.NameY, 0, 1 - t.NameY);
            TitleLine.Margin = new Thickness(0);
        }
    }

    /// <summary>The top bar slid away (or came back): the logo shrinks into the title bar's corner, or grows back.</summary>
    private void FadeBigLogo(bool shown)
    {
        if (!_bigLogoOn) return;
        SetLogoSmall(!shown, animate: true);
    }

    private void SetLogoSmall(bool small, bool animate)
    {
        var t = _settings.TopBarStyle;
        double h = BigSize, s = SmallSize;
        var at = SmallAt;
        double k = small ? s / Math.Max(1, h) : 1;
        double dx = small ? at.X - t.LogoX : 0, dy = small ? at.Y - t.LogoY : 0;
        double gap = Math.Clamp(t.NameGap, 0, 60);
        // the window name: after the logo, then nudged by its own amounts for each state
        double nx = small ? t.SmallNameX : t.NameX, ny = small ? t.SmallNameY : t.NameY;
        var name = new Thickness((small ? at.X + s + gap : t.LogoX + h + gap) + nx, ny, 0, 1 - ny);
        var line = small ? new Thickness(0) : new Thickness(t.LogoX + h + Math.Clamp(t.LineGap, -200, 400), 0, 0, 0);
        if (!animate)
        {
            StopLogoAnimations();
            _logoScale.ScaleX = _logoScale.ScaleY = k; _logoMove.X = dx; _logoMove.Y = dy;
            TitleText.Margin = name; TitleLine.Margin = line;
            return;
        }
        var d = TimeSpan.FromMilliseconds(Math.Clamp(t.Speed, 60, 1500));
        IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation A(double to) => new(to, d) { EasingFunction = ease };
        _logoScale.BeginAnimation(ScaleTransform.ScaleXProperty, A(k));
        _logoScale.BeginAnimation(ScaleTransform.ScaleYProperty, A(k));
        _logoMove.BeginAnimation(TranslateTransform.XProperty, A(dx));
        _logoMove.BeginAnimation(TranslateTransform.YProperty, A(dy));
        TitleText.BeginAnimation(MarginProperty, new ThicknessAnimation(name, d) { EasingFunction = ease });
        TitleLine.BeginAnimation(MarginProperty, new ThicknessAnimation(line, d) { EasingFunction = ease });
    }

    private void StopLogoAnimations()
    {
        _logoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _logoScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _logoMove.BeginAnimation(TranslateTransform.XProperty, null);
        _logoMove.BeginAnimation(TranslateTransform.YProperty, null);
        TitleText.BeginAnimation(MarginProperty, null);
        TitleLine.BeginAnimation(MarginProperty, null);
        _logoScale.ScaleX = _logoScale.ScaleY = 1; _logoMove.X = _logoMove.Y = 0;
    }

    /// <summary>Appearance → Top bar's "Try it": the logo goes small for two seconds, then back.</summary>
    private async void PreviewSmallLogo()
    {
        if (!_bigLogoOn) return;
        SetLogoSmall(true, animate: true);
        await Task.Delay(2000 + (int)_settings.TopBarStyle.Speed);
        if (_bigLogoOn) SetLogoSmall(!_barShown, animate: true);
    }
}
