using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace MuYu.DailyTracker
{
    public static class PhotoTests
    {
        private static int passed;
        private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; }
        private static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }
        private static object Field(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
        private static void Prepare(Control control)
        { IntPtr handle = control.Handle; foreach (Control child in control.Controls) Prepare(child); control.PerformLayout(); }
        private static bool AllDropEnabled(Control control)
        { return control.AllowDrop && control.Controls.Cast<Control>().All(AllDropEnabled); }
        private static bool HasUploadButton(Control control)
        { return (control is Button && control.Text == "上传图片") || control.Controls.Cast<Control>().Any(HasUploadButton); }
        private static void RaiseDrag(Control control, string eventName, DragEventArgs args)
        { typeof(Control).GetMethod(eventName, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { args }); }
        public static int Run(string directory)
        {
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string report = Path.Combine(directory, "photo-test-results.txt");
            try
            {
                string testRoot = Path.Combine(directory, "photo-tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string data = Path.Combine(testRoot, "data");
                string original = Path.Combine(testRoot, "示例图片.png");
                using (var bitmap = new Bitmap(960, 600))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.FromArgb(224, 239, 230));
                    using (var brush = new SolidBrush(Color.FromArgb(18, 113, 99))) graphics.FillEllipse(brush, 320, 130, 320, 320);
                    using (var font = new Font("Microsoft YaHei UI", 32, FontStyle.Bold)) graphics.DrawString("图片保存与预览", font, Brushes.White, 280, 255);
                    bitmap.Save(original, ImageFormat.Png);
                }
                byte[] bytes = File.ReadAllBytes(original);
                DateTime day = new DateTime(2026, 9, 25);
                var store = new PhotoStore(data);
                Check(store.List(day).Count == 0, "Empty photo directory is supported");
                var first = store.Add(day, original);
                Check(File.ReadAllBytes(first.FilePath).SequenceEqual(bytes), "Import preserves original bytes");
                Check(first.Name == "示例图片.png", "Original filename preserved");
                Check(File.Exists(original), "Import retains source");
                var second = store.Add(day, original);
                Check(first.FilePath != second.FilePath && store.List(day).Count == 2, "Same-name images never overwrite");
                var historical = store.Add(day.AddDays(-1), original);
                Check(store.List(day.AddDays(-1)).Count == 1 && store.List(day).Count == 2, "Date isolation");
                Check(new PhotoStore(data).List(day).Count == 2, "Reopening retains saved images");
                using (var preview = store.Preview(first)) Check(preview.Width == 960 && preview.Height == 600, "Preview decoded independently");
                string downloaded = Path.Combine(testRoot, "下载.png"); store.Download(first, downloaded);
                Check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Download preserves original bytes");
                File.WriteAllText(downloaded, "replace me"); store.Download(first, downloaded);
                Check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Explicit overwrite exports safely");
                Check(PhotoStore.AvailableName(testRoot, "下载.png").EndsWith("下载 (2).png"), "Batch download resolves duplicate names");
                Check(Rejects(delegate { store.Download(first, Path.Combine(data, "records.json")); }), "Download cannot overwrite managed data");
                Check(Rejects(delegate { store.Delete(new SavedPhoto { Day = day, FilePath = original }); }) && File.Exists(original), "Deletion restricted to managed photo paths");
                string invalid = Path.Combine(testRoot, "损坏.png"); File.WriteAllText(invalid, "not an image");
                Check(Rejects(delegate { store.Add(day, invalid); }) && store.List(day).Count == 2, "Invalid image rejected without saved attachment");
                string text = Path.Combine(testRoot, "document.txt"); File.WriteAllText(text, "text");
                Check(Rejects(delegate { store.Add(day, text); }), "Unsupported extension rejected");
                Check(!Directory.GetFiles(store.Root, "*.upload", SearchOption.AllDirectories).Any(), "No partial uploads left behind");
                var dragStore = new PhotoStore(Path.Combine(testRoot, "drag-data"));
                using (var gallery = new PhotoWindow(dragStore, day))
                {
                    Check(AllDropEnabled(gallery), "Entire gallery including preview, list and empty hint accepts drag events");
                    Check(!HasUploadButton(gallery), "Click-to-upload button removed");
                    var files = new DataObject(DataFormats.FileDrop, new[] { original });
                    var drag = new DragEventArgs(files, 0, 0, 0, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None);
                    RaiseDrag(gallery, "OnDragEnter", drag);
                    Check(drag.Effect == DragDropEffects.Copy && ((Label)Field(gallery, "dragHint")).Text.Contains("2026-09-25"), "File drag shows copy effect and correct target date");
                    Check(dragStore.List(day).Count == 0, "Drag hover does not import before release");
                    RaiseDrag(gallery, "OnDragDrop", drag);
                    Check(dragStore.List(day).Count == 1 && File.Exists(original), "Dropping file saves copy and retains source");
                    Check(((PictureBox)Field(gallery, "preview")).Image != null, "Dropped image immediately previews");
                    var reopenedPhotos = new PhotoStore(dragStore.DataDirectory);
                    Check(reopenedPhotos.List(day).Count == 1 && reopenedPhotos.List(day.AddDays(-1)).Count == 0, "Drop persists only under selected date");
                    string otherImage = Path.Combine(testRoot, "另一张.PNG"); File.Copy(original, otherImage);
                    var multi = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { original, otherImage }), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
                    RaiseDrag((PictureBox)Field(gallery, "preview"), "OnDragEnter", multi);
                    RaiseDrag((PictureBox)Field(gallery, "preview"), "OnDragDrop", multi);
                    Check(dragStore.List(day).Count == 3, "Multiple files drop onto existing preview");
                    RaiseDrag((ListBox)Field(gallery, "list"), "OnDragDrop", drag);
                    Check(dragStore.List(day).Count == 4, "File list is also a drop target");
                    var words = new DragEventArgs(new DataObject(DataFormats.Text, "text"), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
                    RaiseDrag(gallery, "OnDragEnter", words); RaiseDrag(gallery, "OnDragDrop", words);
                    Check(words.Effect == DragDropEffects.None && dragStore.List(day).Count == 4, "Text drops rejected without adding files");
                    var folderDrag = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { testRoot }), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
                    RaiseDrag(gallery, "OnDragEnter", folderDrag);
                    Check(folderDrag.Effect == DragDropEffects.None, "Folders rejected as image drops");
                    var moveOnly = new DragEventArgs(files, 0, 0, 0, DragDropEffects.Move, DragDropEffects.None);
                    RaiseDrag(gallery, "OnDragEnter", moveOnly);
                    Check(moveOnly.Effect == DragDropEffects.None, "Move-only drag cannot delete original source");
                    var mixed = (PhotoImportResult)typeof(PhotoWindow).GetMethod("ImportDroppedFiles", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(gallery, new object[] { new[] { original, invalid, text } });
                    Check(mixed.Saved == 1 && mixed.Errors.Count == 2 && dragStore.List(day).Count == 5, "Mixed drop saves valid image and reports invalid files");
                    Check(((Label)Field(gallery, "status")).Text.Contains("已拖入并保存 1") && !gallery.UseWaitCursor, "Drop completion reports saved count and clears busy state");
                }
                var rangeStore = new PhotoStore(Path.Combine(testRoot, "range-data"));
                foreach (var date in new[] { new DateTime(2026, 8, 31), new DateTime(2026, 9, 1), new DateTime(2026, 9, 20),
                    new DateTime(2026, 9, 21), day, new DateTime(2026, 9, 27), new DateTime(2026, 9, 28), new DateTime(2026, 9, 30), new DateTime(2026, 10, 1) })
                    rangeStore.Add(date, original);
                rangeStore.Add(day, original);
                var week = PhotoPeriod.ForDate(day, false); var month = PhotoPeriod.ForDate(day, true);
                Check(week.Start == new DateTime(2026, 9, 21) && week.End == new DateTime(2026, 9, 27), "Download full selected week Monday to Sunday");
                Check(month.Start == new DateTime(2026, 9, 1) && month.End == new DateTime(2026, 9, 30), "Download full selected month");
                Check(rangeStore.ListPeriod(week).Count == 4 && rangeStore.ListPeriod(month).Count == 8, "Week and month include boundaries and exclude neighbors");
                Check(PhotoPeriod.ForDate(new DateTime(2026, 9, 27), false).Start == week.Start, "Sunday belongs to current Monday week");
                var newYear = PhotoPeriod.ForDate(new DateTime(2027, 1, 1), false);
                Check(newYear.Start == new DateTime(2026, 12, 28) && newYear.End == new DateTime(2027, 1, 3), "Photo week crosses year correctly");
                Check(PhotoPeriod.ForDate(new DateTime(2024, 2, 15), true).End == new DateTime(2024, 2, 29), "Photo month includes leap day");
                string exports = Path.Combine(testRoot, "period-exports");
                var exported = rangeStore.DownloadPeriod(week, exports);
                Check(exported.Saved == 4 && exported.Errors.Count == 0, "Weekly batch download completes");
                Check(Directory.GetDirectories(exported.Directory).Length == 3 && Directory.GetFiles(exported.Directory, "*.png", SearchOption.AllDirectories).Length == 4,
                    "Period downloads organized by record date");
                Check(File.Exists(Path.Combine(exported.Directory, "2026-09-25", "示例图片 (2).png")), "Period download preserves duplicate names with suffix");
                Check(Directory.GetFiles(exported.Directory, "*.png", SearchOption.AllDirectories).All(p => File.ReadAllBytes(p).SequenceEqual(bytes)), "Period download preserves every original");
                var again = rangeStore.DownloadPeriod(week, exports);
                Check(again.Directory != exported.Directory && Directory.GetFiles(exported.Directory, "*.png", SearchOption.AllDirectories).Length == 4, "Repeated batch keeps existing export untouched");
                Check(rangeStore.DownloadPeriod(month, exports).Saved == 8, "Monthly batch downloads all month images");
                var noPhotos = rangeStore.DownloadPeriod(PhotoPeriod.ForDate(new DateTime(2020, 1, 1), true), exports);
                Check(noPhotos.Saved == 0 && noPhotos.Directory == null, "Empty period creates no output folder");
                Check(Rejects(delegate { rangeStore.DownloadPeriod(week, rangeStore.Root); }), "Period export rejects application data destination");
                using (var window = new PhotoWindow(store, day))
                {
                    var list = (ListBox)Field(window, "list"); var picture = (PictureBox)Field(window, "preview");
                    Check(list.Items.Count == 2 && picture.Image != null, "Gallery opens with saved image preview");
                    Check(window.Controls.Find("weekDownload", true).Length == 1 && window.Controls.Find("monthDownload", true).Length == 1, "Week and month download buttons visible in gallery");
                    list.ClearSelected();
                    Check(!((Button)Field(window, "download")).Enabled && !((Button)Field(window, "delete")).Enabled, "Actions disabled without selection");
                    list.SetSelected(0, true); list.SetSelected(1, true);
                    Check(list.SelectedItems.Count == 2 && ((Button)Field(window, "download")).Enabled, "Multi-selection enables image actions");
                    Prepare(window);
                    using (var screenshot = new Bitmap(window.Width, window.Height))
                    { window.DrawToBitmap(screenshot, new Rectangle(Point.Empty, window.Size)); screenshot.Save(Path.Combine(directory, "图片功能预览.png")); }
                }
                var records = new RecordStore(Path.Combine(data, "records.json")); var db = new Database();
                RecordStore.Put(db, day, 3, 4, 5); records.Save(db);
                string backup = Path.Combine(testRoot, "完整备份.zip"); PhotoStore.Backup(records.FilePath, backup);
                using (var archive = ZipFile.OpenRead(backup))
                {
                    Check(archive.Entries.Count == 4 && archive.GetEntry("records.json") != null, "Full backup contains records and all dates' photos");
                    using (var stream = archive.GetEntry("images/2026-09-25/" + Path.GetFileName(first.FilePath)).Open())
                    using (var memory = new MemoryStream())
                    { stream.CopyTo(memory); Check(memory.ToArray().SequenceEqual(bytes), "Backup preserves photo bytes"); }
                }
                string restored = Path.Combine(testRoot, "restored"); ZipFile.ExtractToDirectory(backup, restored);
                Check(new RecordStore(Path.Combine(restored, "records.json")).Load().Records[0].Complex == 3
                    && new PhotoStore(restored).List(day).Count == 2, "Full backup can restore records and photos");
                byte[] backupBytes = File.ReadAllBytes(backup);
                Check(Rejects(delegate { PhotoStore.Backup(Path.Combine(data, "missing.json"), backup); })
                    && File.ReadAllBytes(backup).SequenceEqual(backupBytes), "Backup failure preserves previous export");
                Check(Rejects(delegate { PhotoStore.Backup(records.FilePath, Path.Combine(data, "backup.zip")); }), "Backup cannot target managed data folder");
                using (var preview = store.Preview(first)) store.Delete(first);
                Check(!File.Exists(first.FilePath) && File.Exists(original) && File.Exists(downloaded), "Deletion works with open preview and leaves source/download alone");
                Check(store.List(day).Count == 1 && store.List(day.AddDays(-1)).Count == 1, "Deletion affects only selected photo");
                store.Delete(second);
                using (var empty = new PhotoWindow(store, day))
                {
                    Check(((ListBox)Field(empty, "list")).Items.Count == 0 && ((PictureBox)Field(empty, "preview")).Image == null, "Empty gallery after deleting last photo");
                    Check(empty.Controls.Find("weekDownload", true)[0].Enabled && empty.Controls.Find("monthDownload", true)[0].Enabled, "Period download available even with no images on selected day");
                }
                var settlement = Calculator.Calculate(db, day, false);
                using (var panel = new SettlementWindow(settlement, false))
                {
                    var reportBox = (RichTextBox)Field(panel, "report");
                    Check(reportBox.ReadOnly && reportBox.ShortcutsEnabled, "Settlement is selectable read-only text");
                    string normalized = reportBox.Text.Replace("\r\n", "\n");
                    Check(normalized.Contains("复杂：3 个\n常规：4 个\n简单：5 个"), "Settlement quantities are three vertical ordered lines");
                    int start = reportBox.Text.IndexOf("复杂：", StringComparison.Ordinal);
                    int end = reportBox.Text.IndexOf("本周累计总量：", StringComparison.Ordinal);
                    reportBox.Select(start, end - start);
                    Check(reportBox.SelectedText.Contains("复杂：3 个") && reportBox.SelectedText.Contains("简单：5 个"), "Arbitrary multi-line report selection works");
                    reportBox.SelectAll(); Check(reportBox.SelectedText == reportBox.Text, "Whole report selectable without losing lines");
                    Check(reportBox.ContextMenuStrip != null && reportBox.ContextMenuStrip.Items.Count == 2, "Copy and select-all context actions provided");
                }
                var zeroDb = new Database(); zeroDb.Settings.WeeklyRestDays = 7;
                Check(SettlementWindow.ReportText(Calculator.Calculate(zeroDb, new DateTime(2026, 9, 7), true), true).Contains("暂不能评价"), "Copyable report retains zero-divisor explanation");
                File.Delete(original);
                Check(File.ReadAllBytes(historical.FilePath).SequenceEqual(bytes), "Deleting original source does not remove stored copy");
                File.WriteAllText(report, "PASS: " + passed + " photo and settlement checks\r\n" + DateTime.Now.ToString("o"), Encoding.UTF8); return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString(), Encoding.UTF8); return 1; }
        }
    }
}
