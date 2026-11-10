using System.Windows;
using System.Windows.Media;
using Ginkgo.Core;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>Favourites: the star on tiles and in the viewer, the Favourites section, and the progress of checks and copies.</summary>
public partial class MainWindow
{
    private FavouritesService _fav = null!;
    private LibraryModel? _libModel, _favModel;
    /// <summary>The model the current section shows: the Favourites folder in Favourites, everything else elsewhere.</summary>
    /// <summary>The list the tiles on screen come from. It changes together with _order (when a view has been built), so a tile's
    /// row always means the same list, even while a section switch or a library reload is still building the next view.</summary>
    private LibraryModel? _model => _shownModel ?? TargetModel;
    private LibraryModel? _shownModel;
    /// <summary>The list this section shows (the next view is built from it).</summary>
    private LibraryModel? TargetModel => InFavourites ? _favModel : _libModel;
    private bool InFavourites => _section == "Favourites";
    private int _favCountLoaded = -1;

    private void InitFavourites()
    {
        _fav = new FavouritesService(_engine, _settings);
        _fav.Changed += () => Dispatcher.BeginInvoke(OnFavouritesChanged);
        _fav.Message += text => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = text;
            _favMessageUntil = System.Diagnostics.Stopwatch.GetTimestamp() + 6 * System.Diagnostics.Stopwatch.Frequency;
            if (ViewerOpen) Osd(text.Length > 60 ? (text.StartsWith("Added") ? "Added to Favourites" : text.StartsWith("Removed") ? "Removed from Favourites" : "Favourites: see the status bar") : text);
        });
        View.Starred = i => RowOfTile(i) is int r and >= 0 && _model is { } m && r < m.Id.Length && _fav.IsStarred(m.Id[r]);
        View.StarClicked += i => { if (RowOfTile(i) is int r and >= 0) ToggleFavourite(r); };
        // every change in the library (a new file, a copy made in Explorer, a favourite deleted elsewhere) is checked again
        _engine.DataChanged += () => _fav.RequestSync();
    }

    private long _favMessageUntil;

    private int? RowOfTile(int i) => i >= _firstFile && i < _order.Length ? _order[i] : null;

    private void ToggleFavourite(int row)
    {
        var m = _model;
        if (m is null || row < 0 || row >= m.Count) return;
        if (!_fav.Ready) { StatusText.Text = "Favourites are still being checked. Try again in a moment."; return; }
        _fav.Toggle(m.Id[row], m.Path[row], (MediaKind)m.Kind[row], (ContentLevel)m.Level[row]);
    }

    private void OnFavouritesChanged()
    {
        View.Refresh();
        UpdateViewerStar();
        // the Favourites section follows the folder (a copy added, one removed or deleted in Explorer)
        // (also while some favourites have no thumbnail yet: they join as their thumbnails are made)
        if (_fav.FavCount != _favCountLoaded || (_favModel is { } fm && fm.Count + fm.VideoCount < _fav.FavCount)) _ = ReloadFavourites();
    }

    private async Task ReloadFavourites()
    {
        var db = _engine.Db;
        if (db is null) return;
        _favCountLoaded = _fav.FavCount;
        string root = _settings.FavRoot;
        var m = await Task.Run(() => LibraryModel.Load(db, root, favourites: true));
        _favModel = m;
        if (InFavourites && !ViewerOpen) await BuildViewAsync(true);
    }

    /// <summary>The star button in the viewer: filled when the open file is a favourite.</summary>
    private void UpdateViewerStar()
    {
        if (!ViewerOpen || _model is not { } m || _vIndex < _firstFile || _vIndex >= _order.Length) return;
        int row = _order[_vIndex];
        bool on = row >= 0 && _fav.IsStarred(m.Id[row]);
        FavBtn.Content = on ? "" : "";
        FavBtn.Foreground = on ? StarGold : Brushes.White;
        FavBtn.ToolTip = on ? "Remove from Favourites (the copy goes to the Recycle Bin)" : "Add to Favourites (copies the file into your Favourites folder)";
    }

    private static readonly Brush StarGold = MasonryView.StarFill;

    private void OnViewerFav(object s, RoutedEventArgs e)
    {
        if (_vIndex >= _firstFile && _vIndex < _order.Length) ToggleFavourite(_order[_vIndex]);
        ViewerRoot.Focus();
    }

    /// <summary>The favourites check or copy, for the status bar: what, how far, how long so far and how long left.</summary>
    private string FavStatus()
    {
        var p = _fav.Progress;
        if (!p.Running) return "";
        string left = p.Remaining is { } r ? $", about {Clock(r.TotalSeconds)} left" : "";
        long bt = Interlocked.Read(ref p.BytesTotal);
        string amount = bt > 0 ? $"{Interlocked.Read(ref p.BytesDone) / 1048576.0:N1} of {bt / 1048576.0:N1} MB" : $"{Interlocked.Read(ref p.FilesDone):N0} of {Interlocked.Read(ref p.FilesTotal):N0} files";
        string file = p.Current.Length > 0 ? "  (" + System.IO.Path.GetFileName(p.Current) + ")" : "";
        return $"{p.What}: {p.Fraction * 100:F0}%, {amount}, {Clock(p.Elapsed.TotalSeconds)} so far{left}{file}";
    }

    private void TickFavourites()
    {
        var p = _fav.Progress;
        FavBar.Visibility = p.Running ? Visibility.Visible : Visibility.Collapsed;
        if (p.Running) FavBar.Value = p.Fraction;
    }

    private string FavDiag() => $" | favourites {_fav.FavCount:N0} ({_fav.Linked:N0} linked to originals){(_fav.Progress.Running ? ", " + FavStatus() : "")}{(_fav.LastProblem.Length > 0 ? ", problem: " + _fav.LastProblem : "")}";
}
