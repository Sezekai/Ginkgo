using Ginkgo.Core;
using Ginkgo.Data;
using SkiaSharp;

int pass = 0, fail = 0;
void Check(bool ok, string name) { if (ok) pass++; else { fail++; Console.WriteLine("::error::FAIL: " + name); } }

try
{
var tmp = Path.Combine(Path.GetTempPath(), "ginkgo-test-" + Guid.NewGuid().ToString("N"));
var lib = Path.Combine(tmp, "IMGs");
void Img(string rel, int w, int h, SKEncodedImageFormat fmt)
{
    var full = Path.Combine(lib, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    using var bmp = new SKBitmap(w, h);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(new SKColor((byte)(w % 255), (byte)(h % 255), 120));
        using var paint = new SKPaint { Color = SKColors.White };
        canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) / 4f, paint);
    }
    using var data = SKImage.FromBitmap(bmp).Encode(fmt, 90);
    File.WriteAllBytes(full, data.ToArray());
}
Img("Artists/A/one.jpg", 3000, 2000, SKEncodedImageFormat.Jpeg);
Img("Artists/A/two.png", 800, 1600, SKEncodedImageFormat.Png);
Img("Artists/NSFW/three.jpg", 400, 400, SKEncodedImageFormat.Jpeg);
Img("Artists/NSFW/Very/four.webp", 640, 480, SKEncodedImageFormat.Webp);
Img("Artists/tall.png", 800, 4000, SKEncodedImageFormat.Png);
Img("Artists/wide.png", 3000, 300, SKEncodedImageFormat.Png);
File.WriteAllBytes(Path.Combine(lib, "Artists/five.gif"), Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==")); // Skia cannot write GIFs, so a tiny hand-made one
Img("Sort/skipme.jpg", 100, 100, SKEncodedImageFormat.Jpeg);          // excluded top folder
File.WriteAllText(Path.Combine(lib, "Artists/notes.txt"), "ignore me");   // not media
File.WriteAllBytes(Path.Combine(lib, "Artists/broken.jpg"), new byte[] { 1, 2, 3, 4 }); // corrupt
File.WriteAllBytes(Path.Combine(lib, "Artists/clip.mp4"), new byte[100]);               // video: indexed, no thumb yet

var db = new IndexDb(Path.Combine(tmp, "data", "index.db"));
var r1 = LibraryScanner.Scan(db, lib, MediaTypes.DefaultExcludedTopFolders, null, CancellationToken.None);
Check(r1.Total == 9 && r1.Added == 9, $"first scan finds 9 media files (got {r1.Total}/{r1.Added})");

using (var c = db.Open())
{
    Check(IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE path LIKE '%skipme%'") == 0, "excluded folder skipped");
    Check(IndexDb.Scalar(c, "SELECT level FROM files WHERE path LIKE '%three.jpg'") == (int)ContentLevel.Nsfw, "nsfw level");
    Check(IndexDb.Scalar(c, "SELECT level FROM files WHERE path LIKE '%four.webp'") == (int)ContentLevel.Very, "very level");
    Check(IndexDb.Scalar(c, "SELECT level FROM files WHERE path LIKE '%one.jpg'") == (int)ContentLevel.General, "general level");
    Check(IndexDb.Scalar(c, "SELECT kind FROM files WHERE path LIKE '%five.gif'") == (int)MediaKind.Gif, "gif kind");
}

VideoTools.Disabled = true;          // first without the video tools: videos wait
var stores = new ThumbStores(Path.Combine(tmp, "cache"));
var store = stores.Full;
var stats = new PipelineStats();
await ThumbnailPipeline.RunAsync(db, stores, stats, 3, CancellationToken.None);
Check(stats.Done == 7, $"7 thumbnails made (got {stats.Done})");
Check(stats.Failed == 1, $"1 corrupt file failed cleanly (got {stats.Failed})");

using (var c = db.Open())
{
    Check(IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state=1") == 7, "7 rows marked done");
    Check(IndexDb.Scalar(c, "SELECT w FROM files WHERE path LIKE '%one.jpg'") == 3000 && IndexDb.Scalar(c, "SELECT h FROM files WHERE path LIKE '%one.jpg'") == 2000, "real size recorded");
    Check(IndexDb.Scalar(c, "SELECT tw FROM files WHERE path LIKE '%one.jpg'") == 320, "thumb width capped at 320");
    Check(IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state=0") == 1, "video still waiting");

    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT thumb_pack, thumb_off, thumb_len, tw, th FROM files WHERE path LIKE '%two.png'";
    using var rd = cmd.ExecuteReader();
    Check(rd.Read(), "two.png row");
    var bytes = store.Read(rd.GetInt32(0), rd.GetInt64(1), rd.GetInt32(2));
    Check(bytes is not null, "thumbnail readable from pack");
    using var bmp = SKBitmap.Decode(bytes!);
    Check(bmp is not null && bmp.Width == rd.GetInt32(3) && bmp.Height == rd.GetInt32(4), "thumb decodes to recorded size");
    Check(bmp is not null && bmp.Height == 640 && bmp.Width == 320, $"tall image fits 320x640 (got {bmp?.Width}x{bmp?.Height})");
}

// second scan: nothing new; then change/remove
var r2 = LibraryScanner.Scan(db, lib, MediaTypes.DefaultExcludedTopFolders, null, CancellationToken.None);
Check(r2.Added == 0 && r2.Changed == 0 && r2.Removed == 0, "rescan sees no changes");
File.Delete(Path.Combine(lib, "Artists/A/two.png"));
Img("Artists/new.jpg", 500, 500, SKEncodedImageFormat.Jpeg);
var r3 = LibraryScanner.Scan(db, lib, MediaTypes.DefaultExcludedTopFolders, null, CancellationToken.None);
Check(r3.Added == 1 && r3.Removed == 1, $"rescan sees 1 new, 1 gone (got +{r3.Added} -{r3.Removed})");
var stats2 = new PipelineStats();
await ThumbnailPipeline.RunAsync(db, stores, stats2, 2, CancellationToken.None);
Check(stats2.Done == 1, "only the new file is thumbnailed");

// small and micro sizes exist, are smaller, and decode to the right width
using (var c = db.Open())
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT thumb_len, s_pack, s_off, s_len, m_pack, m_off, m_len FROM files WHERE path LIKE '%one.jpg'";
    using var rd = cmd.ExecuteReader();
    Check(rd.Read(), "one.jpg row for tiers");
    int fl = rd.GetInt32(0), sl = rd.GetInt32(3), ml = rd.GetInt32(6);
    Check(sl > 0 && ml > 0 && ml < sl && sl < fl, $"micro < small < full ({ml} < {sl} < {fl})");
    using var sb = SKBitmap.Decode(stores.Small.Read(rd.GetInt32(1), rd.GetInt64(2), sl)!);
    using var mb = SKBitmap.Decode(stores.Micro.Read(rd.GetInt32(4), rd.GetInt64(5), ml)!);
    Check(sb.Width == 192, $"small is 192 wide (got {sb.Width})");
    Check(mb.Width == 112, $"micro is 112 wide (got {mb.Width})");
}

// long and wide images: thumbnails follow width, extreme shapes are cropped to 1:4 / 4:1
using (var c = db.Open())
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT w, h, tw, th FROM files WHERE path LIKE '%tall.png'";
    using var rd = cmd.ExecuteReader();
    Check(rd.Read() && rd.GetInt32(0) == 800 && rd.GetInt32(1) == 4000, "tall original size recorded");
    Check(rd.GetInt32(2) == 320 && rd.GetInt32(3) == 1280, $"tall thumbnail is 320x1280, cropped at 1:4 (got {rd.GetInt32(2)}x{rd.GetInt32(3)})");
    rd.Close();
    cmd.CommandText = "SELECT tw, th FROM files WHERE path LIKE '%wide.png'";
    using var rd2 = cmd.ExecuteReader();
    Check(rd2.Read() && rd2.GetInt32(0) == 320 && rd2.GetInt32(1) == 80, "wide thumbnail is 320x80, cropped at 4:1");
}
Check(Math.Abs(ThumbMaker.ClampAspect(800, 4000) - 0.25f) < 1e-6 && Math.Abs(ThumbMaker.ClampAspect(3000, 300) - 4f) < 1e-6 && ThumbMaker.ClampAspect(100, 100) == 1f, "aspect clamp");

// large on demand
{
    var big = ThumbMaker.MakeLarge(Path.Combine(lib, "Artists/A/one.jpg"));
    using var lb = SKBitmap.Decode(big);
    Check(lb.Width == 1280 && lb.Height == 853, $"large is 1280x853 (got {lb.Width}x{lb.Height})");
    var mid = ThumbMaker.MakeLarge(Path.Combine(lib, "Artists/NSFW/Very/four.webp"));
    using var mb2 = SKBitmap.Decode(mid);
    Check(mb2.Width == 640, $"smaller image keeps its size (got {mb2.Width})");
    var loc = stores.Large.Append(big);
    Check(stores.Large.Read(loc.pack, loc.offset, loc.length)!.Length == big.Length && stores.Large.Size >= big.Length, "large store roundtrip and size");
    IndexUpdates.SaveLarge(db, stores, new[] { (1L, loc.pack, loc.offset, loc.length) });
    using var c2 = db.Open();
    Check(IndexDb.Scalar(c2, "SELECT l_len FROM files WHERE id=1") == loc.length, "large location saved in index");
}

// prepare sharp versions ahead of time: PNGs (and files over a size limit) only
{
    using (var c = db.Open()) IndexDb.Exec(c, "UPDATE files SET l_pack=0, l_off=0, l_len=0");
    var (cnt, bytesTotal) = SharpPrebuild.Count(db, long.MaxValue);
    Check(cnt >= 2 && bytesTotal > 0, $"count finds the PNGs (got {cnt})");
    var (cntAll, _) = SharpPrebuild.Count(db, 1);
    Check(cntAll > cnt, "a low size limit adds more files");
    var sp = new PipelineStats();
    await SharpPrebuild.RunAsync(db, stores, sp, long.MaxValue, 1L << 40, 2, CancellationToken.None);
    Check(sp.SharpDone == cnt && sp.SharpFailed == 0, $"all PNGs prepared (done {sp.SharpDone} of {cnt}, failed {sp.SharpFailed})");
    using var c3 = db.Open();
    Check(IndexDb.Scalar(c3, "SELECT COUNT(*) FROM files WHERE path LIKE '%.png' AND w>340 AND state=1 AND missing_since=0 AND l_len=0") == 0, "every wide-enough PNG has a sharp version");
    Check(IndexDb.Scalar(c3, "SELECT l_len FROM files WHERE path LIKE '%three.jpg'") == 0, "small JPEG left alone");
    var (cnt2, _) = SharpPrebuild.Count(db, long.MaxValue);
    Check(cnt2 == 0, "nothing left to prepare");
    using var cmd3 = c3.CreateCommand();
    cmd3.CommandText = "SELECT l_pack, l_off, l_len FROM files WHERE path LIKE '%wide.png'";
    using var rd3 = cmd3.ExecuteReader();
    Check(rd3.Read(), "wide.png row");
    using var wb = SKBitmap.Decode(stores.Large.Read(rd3.GetInt32(0), rd3.GetInt64(1), rd3.GetInt32(2))!);
    Check(wb.Width == 1200 && wb.Height == 300, $"wide image cropped at 4:1 (got {wb.Width}x{wb.Height})");
}

// backfill: forget the small sizes, then rebuild them from the full thumbnails
using (var c = db.Open()) IndexDb.Exec(c, "UPDATE files SET s_len=0, m_len=0");
var bstats = new PipelineStats();
await TierBackfill.RunAsync(db, stores, bstats, 2, CancellationToken.None);
using (var c = db.Open())
{
    long ready = IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state=1 AND missing_since=0");
    Check(IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state=1 AND missing_since=0 AND m_len>0 AND s_len>0") == ready, $"backfill restored all {ready}");
    Check(bstats.BackfillDone == ready, $"backfill count (got {bstats.BackfillDone})");
}
var again = new PipelineStats();
await TierBackfill.RunAsync(db, stores, again, 2, CancellationToken.None);
Check(again.BackfillTotal == 0, "backfill has nothing left");

// old thumbnails of long/wide images get rebuilt once, after the rules change
{
    using (var c = db.Open())
    {
        IndexDb.Exec(c, "UPDATE meta SET value='1'");
        IndexDb.Exec(c, "UPDATE files SET state=1 WHERE state=3");
    }
    var db2 = new IndexDb(Path.Combine(tmp, "data", "index.db"));
    using var c2 = db2.Open();
    long marked = IndexDb.Scalar(c2, "SELECT COUNT(*) FROM files WHERE state=3");
    Check(marked >= 2 && db2.MarkedForRebuild == marked, $"tall and wide thumbnails marked for rebuild (got {marked})");
    Check(IndexDb.Scalar(c2, "SELECT COUNT(*) FROM files WHERE l_len>0") == 0, "old large thumbnails forgotten");
    var st3 = new PipelineStats();
    await ThumbnailPipeline.RunAsync(db2, stores, st3, 2, CancellationToken.None);
    Check(st3.Done == marked && IndexDb.Scalar(c2, "SELECT COUNT(*) FROM files WHERE state=3") == 0, "rebuild clears the marks");
}


// =====================================================================================================================
// Foundations: file IDs, soft deletes, edits, safety check, grace period, watcher, clean-up
// =====================================================================================================================
{
    string lib3 = Path.Combine(tmp, "Lib3"), hold = Path.Combine(tmp, "hold");
    Directory.CreateDirectory(hold);
    void ImgIn(string rel, int w, int h)
    {
        var full = Path.Combine(lib3, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor((byte)(w % 251), (byte)(h % 241), 90));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) / 4f, paint);
        }
        using var data = SKImage.FromBitmap(bmp).Encode(rel.EndsWith(".png") ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(full, data.ToArray());
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(-10));     // "old": not still being copied
    }
    string P(string rel) => Path.Combine(lib3, rel);
    var none = new string[0];
    var fids = new ContentIds();
    ScanOptions Opt(Action<ScanOptionsBuilder>? tweak = null)
    {
        var b = new ScanOptionsBuilder { FileIds = fids };
        tweak?.Invoke(b);
        return b.Build();
    }
    ImgIn("Art/A/a1.jpg", 600, 400); ImgIn("Art/A/a2.jpg", 601, 401); ImgIn("Art/B/b1.jpg", 602, 402);
    ImgIn("Art/NSFW/n1.jpg", 603, 403); ImgIn("Top/c1.png", 604, 404);

    var db3 = new IndexDb(Path.Combine(tmp, "data3", "index.db"));
    var stores3 = new ThumbStores(Path.Combine(tmp, "cache3"));
    var f1 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(f1.Added == 5 && f1.Total == 5, $"foundations: first scan finds 5 (got {f1.Added}/{f1.Total})");
    using (var c = db3.Open())
    {
        Check(IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE file_id IS NOT NULL") == 5, "every new file got an ID");
        Check(IndexDb.Scalar(c, "SELECT COUNT(*) FROM folders") == 5, $"5 folder rows (got {IndexDb.Scalar(c, "SELECT COUNT(*) FROM folders")})");
    }
    var pst = new PipelineStats();
    await ThumbnailPipeline.RunAsync(db3, stores3, pst, 2, CancellationToken.None);
    Check(pst.Done == 5, $"foundations: 5 thumbnails (got {pst.Done})");
    long Q(string sql) { using var c = db3.Open(); return IndexDb.Scalar(c, sql); }

    // ---- hidden, not forgotten; comes back with its thumbnails
    long thumbA1 = Q("SELECT thumb_len FROM files WHERE path LIKE '%a1.jpg'");
    File.Move(P("Art/A/a1.jpg"), Path.Combine(hold, "a1.jpg"));
    var d1 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(d1.Removed == 1 && d1.Purged == 0, $"missing file is hidden, not purged (hid {d1.Removed}, purged {d1.Purged})");
    Check(Q("SELECT COUNT(*) FROM files WHERE path LIKE '%a1.jpg' AND missing_since>0") == 1, "its entry is kept");
    Check(Q("SELECT COUNT(*) FROM files WHERE " + Visible.Shown) == 4, "and it no longer shows");
    File.Move(Path.Combine(hold, "a1.jpg"), P("Art/A/a1.jpg"));
    var d2 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(d2.Restored == 1 && Q("SELECT thumb_len FROM files WHERE path LIKE '%a1.jpg'") == thumbA1 && Q("SELECT missing_since FROM files WHERE path LIKE '%a1.jpg'") == 0, "it returns with its thumbnails");

    // ---- a move keeps the row, thumbnails, first-seen date; content level follows the new place
    long idA2 = Q("SELECT id FROM files WHERE path LIKE '%a2.jpg'"), seenA2 = Q("SELECT first_seen FROM files WHERE path LIKE '%a2.jpg'"), thumbA2 = Q("SELECT thumb_len FROM files WHERE path LIKE '%a2.jpg'");
    File.Move(P("Art/A/a2.jpg"), P("Art/NSFW/a2.jpg"));
    var m1 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(m1.Moved == 1 && m1.Added == 0 && m1.Removed == 0, $"move recognised (moved {m1.Moved}, added {m1.Added}, hid {m1.Removed})");
    Check(Q("SELECT COUNT(*) FROM files WHERE path LIKE '%a2.jpg'") == 1 && Q($"SELECT COUNT(*) FROM files WHERE id={idA2} AND path LIKE '%NSFW%a2.jpg'") == 1, "same row, new path");
    Check(Q($"SELECT thumb_len FROM files WHERE id={idA2}") == thumbA2 && Q($"SELECT first_seen FROM files WHERE id={idA2}") == seenA2, "thumbnails and first-seen date kept");
    Check(Q($"SELECT level FROM files WHERE id={idA2}") == (int)ContentLevel.Nsfw, "content level follows the move");

    // ---- a renamed folder: same folder identity, files keep their rows
    long folderB = Q("SELECT id FROM folders WHERE path LIKE '%B'");
    long idB1 = Q("SELECT id FROM files WHERE path LIKE '%b1.jpg'");
    Directory.Move(P("Art/B"), P("Art/B2"));
    var m2 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(m2.Moved == 1 && m2.Added == 0, $"folder rename: file followed (moved {m2.Moved}, added {m2.Added})");
    Check(Q($"SELECT COUNT(*) FROM folders WHERE id={folderB} AND path LIKE '%B2'") == 1 && Q("SELECT COUNT(*) FROM folders WHERE path LIKE '%B'") == 0, "folder keeps its identity under the new name");
    Check(Q($"SELECT COUNT(*) FROM files WHERE id={idB1} AND path LIKE '%B2%'") == 1, "file row kept");

    // ---- an edited file: still shown, sharp version dropped, rebuilt
    Q("UPDATE files SET l_len=5, l_off=1 WHERE path LIKE '%c1.png'");
    ImgIn("Top/c1.png", 700, 500);
    var e1 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(e1.Changed == 1, $"edit seen (got {e1.Changed})");
    Check(Q("SELECT state FROM files WHERE path LIKE '%c1.png'") == 3 && Q("SELECT l_len FROM files WHERE path LIKE '%c1.png'") == 0, "edited: marked for rebuild, stale sharp version dropped");
    Check(Q("SELECT COUNT(*) FROM files WHERE path LIKE '%c1.png' AND " + Visible.Shown) == 1, "edited file stays visible while it is rebuilt");
    var pst2 = new PipelineStats();
    await ThumbnailPipeline.RunAsync(db3, stores3, pst2, 2, CancellationToken.None);
    Check(pst2.Done == 1 && Q("SELECT w FROM files WHERE path LIKE '%c1.png'") == 700 && Q("SELECT state FROM files WHERE path LIKE '%c1.png'") == 1, "edited file rebuilt with the new picture");

    // ---- safety check: a big sudden disappearance is not acted on
    string[] victims = { "Art/A/a1.jpg", "Art/NSFW/a2.jpg", "Art/NSFW/n1.jpg" };
    foreach (var v in victims) File.Move(P(v), Path.Combine(hold, Path.GetFileName(v)));
    var t1 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt(b => { b.SafetyMinCount = 2; b.SafetyFraction = 0.2; }));
    Check(t1.SafetyTripped && t1.WouldHide == 3 && t1.Removed == 0, $"safety check trips (tripped {t1.SafetyTripped}, would hide {t1.WouldHide})");
    Check(Q("SELECT COUNT(*) FROM files WHERE missing_since>0") == 0, "nothing hidden when it trips");
    var t2 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt(b => { b.SafetyMinCount = 2; b.AcceptLargeRemoval = true; }));
    Check(!t2.SafetyTripped && t2.Removed == 3, $"hidden once confirmed (hid {t2.Removed})");
    foreach (var v in victims) File.Move(Path.Combine(hold, Path.GetFileName(v)), P(v));
    var t3 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(t3.Restored == 3, $"all three come back (got {t3.Restored})");

    // ---- an unplugged library changes nothing
    long visibleBefore = Q("SELECT COUNT(*) FROM files WHERE " + Visible.Shown);
    var off = LibraryScanner.Scan(db3, Path.Combine(tmp, "NoSuchDrive"), none, null, CancellationToken.None, Opt());
    Check(off.RootMissing && Q("SELECT COUNT(*) FROM files WHERE " + Visible.Shown) == visibleBefore && Q("SELECT COUNT(*) FROM files WHERE missing_since>0") == 0, "missing library folder: nothing changes");

    // ---- partial scans and files still being copied
    ImgIn("Art/A/new1.jpg", 610, 410);
    long rowsBefore = Q("SELECT COUNT(*) FROM files");
    var sub = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt(b => { b.SubFolder = Path.Combine("Art", "A"); b.Recursive = false; }));
    Check(sub.Added == 1 && sub.Removed == 0 && Q("SELECT COUNT(*) FROM files") == rowsBefore + 1, $"folder-only scan adds just the new file (added {sub.Added})");
    ImgIn("Art/A/fresh.jpg", 611, 411);
    File.SetLastWriteTimeUtc(P("Art/A/fresh.jpg"), DateTime.UtcNow);
    var fr = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt(b => b.SkipRecentSeconds = 3600));
    Check(fr.Deferred == 1 && fr.Added == 0, $"a file still being copied is left for later (deferred {fr.Deferred}, added {fr.Added})");
    var fr2 = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    Check(fr2.Added == 1, "and picked up once it settles");

    // ---- the live watcher
    {
        int rounds = 0;
        using var watcher = new LibraryWatcher(db3, lib3, none, null, Opt(), 300);
        watcher.Scanned += _ => Interlocked.Increment(ref rounds);
        ImgIn("Art/A/watched.jpg", 612, 412);
        bool Wait(Func<bool> cond) { for (int i = 0; i < 100; i++) { if (cond()) return true; Thread.Sleep(200); } return false; }
        Check(Wait(() => Q("SELECT COUNT(*) FROM files WHERE path LIKE '%watched.jpg'") == 1), "watcher: a new file shows up by itself");
        File.Delete(P("Art/A/watched.jpg"));
        Check(Wait(() => Q("SELECT COUNT(*) FROM files WHERE path LIKE '%watched.jpg' AND missing_since>0") == 1), "watcher: a deleted file is hidden by itself");
        Check(Interlocked.CompareExchange(ref rounds, 0, 0) >= 2, "watcher reported its scans");
    }

    // ---- confirmed deletion after the grace period, then the cache is compacted
    File.Move(P("Art/A/a1.jpg"), Path.Combine(hold, "gone-for-good.jpg"));
    LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt());
    long rowsBeforePurge = Q("SELECT COUNT(*) FROM files");
    var early = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt(b => b.UtcNow = () => DateTime.UtcNow.AddDays(10)));
    Check(early.Purged == 0 && Q("SELECT COUNT(*) FROM files") == rowsBeforePurge, "not purged before the grace period ends");
    var late = LibraryScanner.Scan(db3, lib3, none, null, CancellationToken.None, Opt(b => b.UtcNow = () => DateTime.UtcNow.AddDays(31)));
    Check(late.Purged >= 1 && Q("SELECT COUNT(*) FROM files WHERE path LIKE '%a1.jpg'") == 0, $"purged after the grace period (purged {late.Purged})");

    var usage = CacheCleanup.Measure(db3, stores3);
    var fullTier = usage.First(u => u.Name == "Full");
    Check(fullTier.Wasted > 0, $"purged thumbnails leave dead space ({fullTier.Wasted} bytes)");
    var cr = CacheCleanup.Compact(db3, stores3, 0.0, null, CancellationToken.None);
    Check(cr.BytesAfter < cr.BytesBefore && cr.Entries > 0, $"clean-up gives space back ({cr.BytesBefore} -> {cr.BytesAfter})");
    Check(CacheCleanup.Measure(db3, stores3).First(u => u.Name == "Full").Wasted == 0, "no dead space left");
    using (var c = db3.Open())
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT thumb_pack, thumb_off, thumb_len, tw FROM files WHERE state=1 AND thumb_len>0";
        using var rd = cmd.ExecuteReader();
        int ok = 0, total = 0;
        while (rd.Read())
        {
            total++;
            var b = stores3.Full.Read(rd.GetInt32(0), rd.GetInt64(1), rd.GetInt32(2));
            using var bm = b is null ? null : SKBitmap.Decode(b);
            if (bm is not null && bm.Width == rd.GetInt32(3)) ok++;
        }
        Check(total > 0 && ok == total, $"every thumbnail still reads correctly after clean-up ({ok}/{total})");
    }

    // ---- real NTFS file IDs (only on Windows)
    if (OperatingSystem.IsWindows())
    {
        var real = new WindowsFileIds();
        var a = P("Top/c1.png");
        string? id1 = real.GetFileId(a, false);
        Check(id1 is not null, "windows: file ID readable");
        var moved = P("Top/c1-moved.png"); File.Move(a, moved);
        Check(real.GetFileId(moved, false) == id1, "windows: ID survives a rename");
        var copy = P("Top/c1-copy.png"); File.Copy(moved, copy);
        Check(real.GetFileId(copy, false) != id1, "windows: a copy has its own ID");
        Check(real.GetFileId(P("Top"), true) is not null, "windows: folder ID readable");
        Check(real.GetFileId(P("Top/nothing.png"), false) is null, "windows: missing file gives no ID");
        // backfill on an index that predates IDs
        Q("UPDATE files SET file_id=NULL");
        int filled = FileIdBackfill.Run(db3, lib3, real, null, CancellationToken.None);
        Check(filled > 0 && Q("SELECT COUNT(*) FROM files WHERE file_id IS NULL AND missing_since=0 AND path NOT LIKE '%c1.png'") == 0, $"windows: backfill fills IDs ({filled})");
    }
    stores3.Dispose();
}

// ---- packs held in memory ----
{
    var mdir = Path.Combine(tmp, "mem");
    var ms = new ThumbStore(mdir, "micro");
    var r = new Random(9);
    var blobs = Enumerable.Range(0, 50).Select(i => { var b = new byte[100 + i * 37]; r.NextBytes(b); return b; }).ToList();
    var locs = blobs.Select(b => ms.Append(b)).ToList();
    ms.Flush();
    var packs = ms.Packs();
    Check(packs.Count == 1 && packs[0].size == blobs.Sum(b => b.Length), "packs listed with sizes");
    Check(!ms.IsInMemory(locs[0].pack) && ms.MemoryBytes == 0, "nothing in memory at first");
    Check(ms.LoadPack(locs[0].pack, null, CancellationToken.None) && ms.IsInMemory(locs[0].pack) && ms.MemoryBytes == packs[0].size, "pack loaded into memory");
    bool same = true;
    for (int i = 0; i < blobs.Count; i++) { var got = ms.Read(locs[i].pack, locs[i].offset, locs[i].length); if (got is null || !got.AsSpan().SequenceEqual(blobs[i])) same = false; }
    Check(same, "reads from memory match what was written");
    var extra = new byte[777]; r.NextBytes(extra);
    var le = ms.Append(extra); ms.Flush();
    var gotExtra = ms.Read(le.pack, le.offset, le.length);
    Check(gotExtra is not null && gotExtra.AsSpan().SequenceEqual(extra), "a thumbnail added after loading is read from the drive");
    ms.DropPack(locs[0].pack);
    Check(!ms.IsInMemory(locs[0].pack) && ms.MemoryBytes == 0, "pack let go");
    var backFromDrive = ms.Read(locs[5].pack, locs[5].offset, locs[5].length);
    Check(backFromDrive is not null && backFromDrive.AsSpan().SequenceEqual(blobs[5]), "after letting go, reads come from the drive again");
    ms.Dispose();
}

// ---- pieces across a 32 MB boundary ----
{
    var bdir = Path.Combine(tmp, "mem2");
    var bs = new ThumbStore(bdir, "small");
    var filler = new byte[ThumbStore.SegSize - 1000];
    bs.Append(filler);
    var cross = new byte[5000]; new Random(4).NextBytes(cross);
    var lc = bs.Append(cross); bs.Flush();
    Check(bs.Segments().Count == 2, "a pack over 32 MB has two pieces");
    bs.LoadSegment(lc.pack, 0, CancellationToken.None);
    var crossA = bs.Read(lc.pack, lc.offset, lc.length);
    Check(crossA is not null && crossA.AsSpan().SequenceEqual(cross), "a thumbnail across two pieces, one in memory, is read correctly");
    bs.LoadSegment(lc.pack, 1, CancellationToken.None);
    var crossB = bs.Read(lc.pack, lc.offset, lc.length);
    Check(crossB is not null && crossB.AsSpan().SequenceEqual(cross) && bs.IsInMemory(lc.pack, 1), "a thumbnail across two pieces in memory is joined correctly");
    Check(bs.Hits(lc.pack, 0) >= 2, "lookups are counted per piece");
    bs.DropSegment(lc.pack, 0);
    Check(!bs.IsInMemory(lc.pack, 0) && bs.IsInMemory(lc.pack, 1), "one piece let go");
    bs.Dispose();
}

// ---- a second home for pieces (the graphics card's memory in the app; a dictionary here) ----
{
    var sdir = Path.Combine(tmp, "mem3");
    var ss = new ThumbStore(sdir, "micro");
    var home = new DictHome();
    ss.Second = home;
    var rnd3 = new Random(9);
    var items = new List<(byte[] data, (int pack, long offset, int length) at)>();
    for (int i = 0; i < 40; i++) { var b = new byte[rnd3.Next(100, 20000)]; rnd3.NextBytes(b); items.Add((b, ss.Append(b))); }
    ss.Flush();
    int pk = items[0].at.pack;
    ss.LoadSegment(pk, 0, CancellationToken.None);
    Check(ss.MoveToSecond(pk, 0) && !ss.IsInMemory(pk, 0) && ss.InSecond(pk, 0) && ss.MemoryBytes == 0, "a piece moves from memory to the second home");
    int before = home.Reads;
    bool all = items.All(it => ss.Read(it.at.pack, it.at.offset, it.at.length) is { } g && g.AsSpan().SequenceEqual(it.data));
    Check(all && home.Reads - before == items.Count, "reads come from the second home and match");
    ss.DropSecond(pk, 0);
    Check(!ss.InSecond(pk, 0) && ss.Read(items[3].at.pack, items[3].at.offset, items[3].at.length) is { } g3 && g3.AsSpan().SequenceEqual(items[3].data), "after leaving the second home, reads come from the drive");
    Check(ss.LoadToSecond(pk, 0, CancellationToken.None) && ss.InSecond(pk, 0) && !ss.IsInMemory(pk, 0), "a piece loads from the drive straight into the second home");
    home.Full = true; ss.DropSecond(pk, 0); ss.LoadSegment(pk, 0, CancellationToken.None);
    Check(!ss.MoveToSecond(pk, 0) && ss.IsInMemory(pk, 0), "no room in the second home: the piece stays in memory");
    ss.Dispose();
}

// ---- moving the small thumbnails to a fast drive ----
{
    var from = Path.Combine(tmp, "mv-old"); var to = Path.Combine(tmp, "mv-fast");
    Directory.CreateDirectory(from);
    var rnd = new Random(3);
    byte[] Blob(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
    File.WriteAllBytes(Path.Combine(from, "micro-00000.bin"), Blob(1000));
    File.WriteAllBytes(Path.Combine(from, "small-00000.bin"), Blob(5_000_000));
    File.WriteAllBytes(Path.Combine(from, "pack-00000.bin"), Blob(300));
    File.WriteAllBytes(Path.Combine(from, "pack-00001.bin"), Blob(7));
    File.WriteAllBytes(Path.Combine(from, "large-00000.bin"), Blob(50));   // sharp versions stay where they are
    var plan = CacheMigration.Plan(from);
    Check(plan.Items.Count == 4 && plan.TotalBytes == 5_001_307, $"move plan counts the small packs only ({plan.Items.Count}, {plan.TotalBytes})");
    Check(!CacheMigration.IsDone(to), "not done before the move");
    long lastDone = 0; bool ok = true;
    CacheMigration.Run(plan, to, (d, t) => { if (d < lastDone || t != plan.TotalBytes) ok = false; lastDone = d; }, CancellationToken.None);
    Check(ok && lastDone == plan.TotalBytes, "progress only goes forward and ends at the total");
    Check(CacheMigration.IsDone(to), "marker written");
    Check(File.Exists(Path.Combine(to, "small-00000.bin")) && new FileInfo(Path.Combine(to, "small-00000.bin")).Length == 5_000_000, "files arrived whole");
    Check(!File.Exists(Path.Combine(from, "micro-00000.bin")) && File.Exists(Path.Combine(from, "large-00000.bin")), "originals removed, large pack untouched");
    Check(!Directory.EnumerateFiles(to, "*.part").Any(), "no half-copied files left");
    // an interrupted move resumes: a file already there in full is skipped, the originals stay until the end
    var from2 = Path.Combine(tmp, "mv-old2"); var to2 = Path.Combine(tmp, "mv-fast2");
    Directory.CreateDirectory(from2); Directory.CreateDirectory(to2);
    File.WriteAllBytes(Path.Combine(from2, "micro-00000.bin"), Blob(2000));
    File.WriteAllBytes(Path.Combine(to2, "micro-00000.bin"), File.ReadAllBytes(Path.Combine(from2, "micro-00000.bin")));
    File.WriteAllBytes(Path.Combine(from2, "small-00000.bin"), Blob(3000));
    using var cts = new CancellationTokenSource(); cts.Cancel();
    bool cancelled = false;
    try { CacheMigration.Run(CacheMigration.Plan(from2), to2, null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && File.Exists(Path.Combine(from2, "small-00000.bin")) && !CacheMigration.IsDone(to2), "cancelled move keeps the originals and writes no marker");
    CacheMigration.Run(CacheMigration.Plan(from2), to2, null, CancellationToken.None);
    Check(CacheMigration.IsDone(to2) && new FileInfo(Path.Combine(to2, "small-00000.bin")).Length == 3000, "resumed move completes");
    var empty = Path.Combine(tmp, "mv-empty");
    Check(CacheMigration.Plan(Path.Combine(tmp, "does-not-exist")).Items.Count == 0, "missing old folder plans nothing");
    CacheMigration.MarkEmpty(empty);
    Check(CacheMigration.IsDone(empty), "empty move is marked");
}

// ---- videos (only where ffmpeg and ffprobe are available, as on the build machine) ----
VideoTools.Disabled = false;
if (VideoTools.Available)
{
    var vdir = Path.Combine(tmp, "vids"); Directory.CreateDirectory(vdir);
    string mp4 = Path.Combine(vdir, "clip one.mp4"), webm = Path.Combine(vdir, "tall.webm");
    void Make(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(VideoTools.FfmpegPath!, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)!; p.StandardError.ReadToEnd(); p.WaitForExit();
    }
    Make($"-y -v error -f lavfi -i testsrc=duration=4:size=640x360:rate=25 -pix_fmt yuv420p \"{mp4}\"");
    Make($"-y -v error -f lavfi -i testsrc=duration=2:size=240x480:rate=25 -c:v libvpx-vp9 \"{webm}\"");
    var i1 = VideoTools.Probe(mp4);
    Check(i1.Width == 640 && i1.Height == 360 && Math.Abs(i1.Seconds - 4) < 0.2, $"video size and length read ({i1.Width}x{i1.Height}, {i1.Seconds:F2}s, {i1.Codec})");
    using (var fr = VideoTools.Frame(mp4, i1.Seconds, 320))
    {
        Check(fr.Width == 320 && fr.Height == 180, $"a frame is grabbed at the asked width ({fr.Width}x{fr.Height})");
        var th = ThumbMaker.MakeFrom(fr, i1.Width, i1.Height);
        Check(th.Width == 640 && th.ThumbWidth == 320 && th.Micro.Length > 0 && th.Small.Length > 0, "video thumbnails in three sizes");
    }
    if (File.Exists(webm))
    {
        var i2 = VideoTools.Probe(webm);
        Check(i2.Width == 240 && i2.Height == 480 && i2.Seconds > 1.5, $"webm read ({i2.Width}x{i2.Height}, {i2.Seconds:F2}s)");
    }
    bool threw = false;
    try { VideoTools.Probe(Path.Combine(lib, "Artists/clip.mp4")); } catch { threw = true; }
    Check(threw, "a broken video is reported, not hung on");
    Console.WriteLine("::notice::video tools found and tested");
}
else Console.WriteLine("::notice::video tools not found here: video tests skipped");

stores.Dispose();
try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine($"::notice::{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine("::error::EXCEPTION " + ex.GetType().Name + ": " + ex.Message.Replace("\n", " ") + " @ " + (ex.StackTrace ?? "").Replace("\r", "").Replace("\n", " | "));
    return 2;
}


/// <summary>Test stand-in for NTFS file IDs: a file is identified by its content, a folder by what is directly inside it.</summary>
class ContentIds : IFileIdProvider
{
    public string? GetFileId(string path, bool isDirectory)
    {
        try
        {
            if (!isDirectory)
                return File.Exists(path) ? "f:" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(path))) : null;
            if (!Directory.Exists(path)) return null;
            var parts = Directory.GetFiles(path).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length).Order().ToList();
            if (parts.Count == 0) parts = Directory.GetDirectories(path).Select(d => "d" + Path.GetFileName(d)).Order().ToList();
            return "d:" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts))));
        }
        catch { return null; }
    }
}

/// <summary>A second home for pieces kept in a dictionary (the app uses the graphics card's memory).</summary>
class DictHome : ISecondHome
{
    private readonly Dictionary<(ThumbStore, long), byte[]> _d = new();
    public int Reads; public bool Full;
    public bool Has(ThumbStore s, long key) => _d.ContainsKey((s, key));
    public bool Read(ThumbStore s, long key, int inSeg, Span<byte> dst)
    {
        if (!_d.TryGetValue((s, key), out var b) || inSeg + dst.Length > b.Length) return false;
        b.AsSpan(inSeg, dst.Length).CopyTo(dst); Reads++; return true;
    }
    public unsafe bool Put(ThumbStore s, long key, IntPtr data, int length)
    {
        if (Full) return false;
        _d[(s, key)] = new ReadOnlySpan<byte>((void*)data, length).ToArray(); return true;
    }
    public void Remove(ThumbStore s, long key) => _d.Remove((s, key));
    public void RemovePack(ThumbStore s, int pack) { foreach (var k in _d.Keys.Where(k => k.Item1 == s && (int)(k.Item2 >> 20) == pack).ToList()) _d.Remove(k); }
}

/// <summary>ScanOptions is immutable (init-only); tests tweak it through this.</summary>
class ScanOptionsBuilder
{
    public IFileIdProvider? FileIds; public double SafetyFraction = 0.2; public int SafetyMinCount = 200; public bool AcceptLargeRemoval;
    public string? SubFolder; public bool Recursive = true; public int SkipRecentSeconds; public Func<DateTime> UtcNow = () => DateTime.UtcNow;
    public ScanOptions Build() => new()
    {
        FileIds = FileIds, SafetyFraction = SafetyFraction, SafetyMinCount = SafetyMinCount, AcceptLargeRemoval = AcceptLargeRemoval,
        SubFolder = SubFolder, Recursive = Recursive, SkipRecentSeconds = SkipRecentSeconds, UtcNow = UtcNow,
    };
}
