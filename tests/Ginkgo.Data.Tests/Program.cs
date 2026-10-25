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

// backfill: forget the small sizes, then rebuild them from the full thumbnails
using (var c = db.Open()) IndexDb.Exec(c, "UPDATE files SET s_len=0, m_len=0");
var bstats = new PipelineStats();
await TierBackfill.RunAsync(db, stores, bstats, 2, CancellationToken.None);
using (var c = db.Open())
{
    long ready = IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state=1");
    Check(IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state=1 AND m_len>0 AND s_len>0") == ready, $"backfill restored all {ready}");
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
