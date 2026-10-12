using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MuYu.DailyTracker
{
    public sealed class PhotoPeriod
    {
        public DateTime Start, End;
        public bool Monthly;
        public string Caption { get { return Monthly ? "本月图片" : "本周图片"; } }
        public string Range { get { return Calculator.Key(Start) + " 至 " + Calculator.Key(End); } }
        public static PhotoPeriod ForDate(DateTime day, bool monthly)
        {
            day = day.Date;
            DateTime start = monthly ? new DateTime(day.Year, day.Month, 1) : day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
            return new PhotoPeriod { Start = start, End = monthly ? new DateTime(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month)) : start.AddDays(6), Monthly = monthly };
        }
    }
    public sealed class PhotoDownloadResult
    {
        public string Directory;
        public int Saved;
        public readonly List<string> Errors = new List<string>();
    }
    public sealed class PhotoImportResult
    {
        public int Saved;
        public readonly List<string> Errors = new List<string>();
    }
    public sealed class SavedPhoto
    {
        public DateTime Day { get; set; }
        public string FilePath { get; set; }
        public string Name { get { return Path.GetFileName(FilePath).Substring(33); } }
        public override string ToString() { return Name; }
    }

    public static class DataPaths
    {
        public static void RequireOutside(string directory, string destination)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(destination);
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path == root.TrimEnd(Path.DirectorySeparatorChar))
                throw new IOException("请选择数据文件夹以外的位置，避免覆盖软件保存的记录或图片。");
        }
        public static void Publish(string temporary, string target)
        {
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
    }

    public sealed class PhotoStore
    {
        public const string Filter = "图片 (*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.tif;*.tiff";
        public readonly string DataDirectory;
        public string Root { get { return Path.Combine(DataDirectory, "images"); } }
        private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff" };
        public PhotoStore(string dataDirectory) { DataDirectory = Path.GetFullPath(dataDirectory); }
        public static bool CanImport(string path)
        { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()); }
        private string DayDirectory(DateTime day) { return Path.Combine(Root, Calculator.Key(day)); }
        private static bool ManagedName(string name)
        {
            Guid id;
            return name.Length > 33 && name[32] == '_' && Guid.TryParseExact(name.Substring(0, 32), "N", out id)
                && Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());
        }
        private string RequireManaged(SavedPhoto photo)
        {
            if (photo == null) throw new IOException("请先选择图片。");
            string path = Path.GetFullPath(photo.FilePath);
            if (!string.Equals(Path.GetDirectoryName(path), DayDirectory(photo.Day), StringComparison.OrdinalIgnoreCase)
                || !ManagedName(Path.GetFileName(path))) throw new IOException("图片不属于当前数据文件夹。");
            return path;
        }
        public List<SavedPhoto> List(DateTime day)
        {
            string directory = DayDirectory(day);
            if (!Directory.Exists(directory)) return new List<SavedPhoto>();
            return Directory.GetFiles(directory).Where(p => ManagedName(Path.GetFileName(p)))
                .OrderBy(File.GetLastWriteTimeUtc).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new SavedPhoto { Day = day.Date, FilePath = p }).ToList();
        }
        public List<SavedPhoto> ListPeriod(PhotoPeriod period)
        {
            var photos = new List<SavedPhoto>();
            for (DateTime day = period.Start; day <= period.End; day = day.AddDays(1))
            {
                photos.AddRange(List(day));
                if (day == period.End) break;
            }
            return photos;
        }
        public PhotoDownloadResult DownloadPeriod(PhotoPeriod period, string destinationDirectory)
        {
            DataPaths.RequireOutside(DataDirectory, destinationDirectory);
            var result = new PhotoDownloadResult();
            var photos = ListPeriod(period);
            if (photos.Count == 0) return result;
            result.Directory = AvailableName(destinationDirectory, period.Caption + "_" + Calculator.Key(period.Start) + "_至_" + Calculator.Key(period.End));
            Directory.CreateDirectory(result.Directory);
            foreach (var photo in photos)
            {
                try
                {
                    string folder = Path.Combine(result.Directory, Calculator.Key(photo.Day));
                    Directory.CreateDirectory(folder);
                    Download(photo, AvailableName(folder, photo.Name));
                    result.Saved++;
                }
                catch (Exception ex) { result.Errors.Add(Calculator.Key(photo.Day) + " / " + photo.Name + "：" + ex.Message); }
            }
            return result;
        }
        private static void ValidateImage(Image image)
        {
            Guid format = image.RawFormat.Guid;
            if (format != ImageFormat.Jpeg.Guid && format != ImageFormat.Png.Guid && format != ImageFormat.Gif.Guid
                && format != ImageFormat.Bmp.Guid && format != ImageFormat.Tiff.Guid)
                throw new InvalidDataException("请选择 JPG、PNG、GIF、BMP 或 TIFF 图片。");
            if ((long)image.Width * image.Height > 40000000L) throw new InvalidDataException("图片超过 4000 万像素，请缩小后再添加。");
        }
        public SavedPhoto Add(DateTime day, string sourcePath)
        {
            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!Extensions.Contains(extension)) throw new InvalidDataException("不支持此图片格式，请选择 JPG、PNG、GIF、BMP 或 TIFF。");
            string original = Path.GetFileName(sourcePath);
            if (original.Length > 100) original = Path.GetFileNameWithoutExtension(original).Substring(0, 90) + extension;
            string directory = DayDirectory(day);
            string destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + "_" + original);
            string temporary = destination + ".upload";
            try
            {
                using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (source.Length > 50L * 1024 * 1024) throw new InvalidDataException("单张图片不能超过 50 MB。");
                    using (var image = Image.FromStream(source, true, true))
                    {
                        ValidateImage(image);
                        // Decode a small preview now so malformed data is rejected before publication.
                        using (var probe = CreatePreview(image)) { }
                    }
                    source.Position = 0;
                    Directory.CreateDirectory(directory);
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { source.CopyTo(output); output.Flush(true); }
                }
                File.Move(temporary, destination);
                return new SavedPhoto { Day = day.Date, FilePath = destination };
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static Bitmap CreatePreview(Image image)
        {
            double scale = Math.Min(1.0, 1600.0 / Math.Max(image.Width, image.Height));
            var bitmap = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                { graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; graphics.DrawImage(image, 0, 0, bitmap.Width, bitmap.Height); }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        public Bitmap Preview(SavedPhoto photo)
        {
            using (var stream = new FileStream(RequireManaged(photo), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var image = Image.FromStream(stream, true, true))
            { ValidateImage(image); return CreatePreview(image); }
        }
        public void Download(SavedPhoto photo, string destination)
        {
            string source = RequireManaged(photo);
            DataPaths.RequireOutside(DataDirectory, destination);
            string target = Path.GetFullPath(destination);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(source, temporary, false); DataPaths.Publish(temporary, target); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static string AvailableName(string directory, string name)
        {
            string result = Path.Combine(directory, name);
            int i = 2;
            while (File.Exists(result) || Directory.Exists(result))
                result = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + " (" + i++ + ")" + Path.GetExtension(name));
            return result;
        }
        public void Delete(SavedPhoto photo) { File.Delete(RequireManaged(photo)); }
        public static void Backup(string recordsPath, string destination)
        {
            string root = Path.GetDirectoryName(Path.GetFullPath(recordsPath));
            DataPaths.RequireOutside(root, destination);
            string target = Path.GetFullPath(destination);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                    {
                        zip.CreateEntryFromFile(recordsPath, "records.json", CompressionLevel.Optimal);
                        var photos = new PhotoStore(root);
                        if (Directory.Exists(photos.Root))
                            foreach (string folder in Directory.GetDirectories(photos.Root))
                            {
                                DateTime day;
                                if (!DateTime.TryParseExact(Path.GetFileName(folder), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.None, out day)) continue;
                                foreach (var photo in photos.List(day))
                                    zip.CreateEntryFromFile(photo.FilePath, "images/" + Calculator.Key(day) + "/" + Path.GetFileName(photo.FilePath), CompressionLevel.NoCompression);
                            }
                    }
                    output.Flush(true);
                }
                DataPaths.Publish(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public sealed class PhotoWindow : Form
    {
        private readonly PhotoStore store;
        private readonly DateTime day;
        private readonly ListBox list = new ListBox();
        private readonly PictureBox preview = new PictureBox();
        private readonly Label empty = new Label(), status = new Label();
        private readonly Label dragHint = new Label();
        private readonly Button download, delete;
        private bool importing;
        public PhotoWindow(PhotoStore store, DateTime day)
        {
            this.store = store; this.day = day.Date;
            Text = "图片附件 · " + Calculator.Key(day);
            Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(243, 247, 246);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(960, 668); MinimumSize = new Size(820, 588); StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 6, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = day.ToString("yyyy年MM月dd日") + " · 图片附件", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            dragHint.Dock = DockStyle.Fill; dragHint.TextAlign = ContentAlignment.MiddleLeft;
            ResetDragHint(); layout.Controls.Add(dragHint, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            download = MakeButton("下载原图", false); delete = MakeButton("删除图片", false);
            download.Click += delegate { Download(); }; delete.Click += delegate { DeleteSelected(); };
            actions.Controls.Add(download); actions.Controls.Add(delete);
            actions.Controls.Add(new Label { Text = "按 Ctrl 或 Shift 可多选", AutoSize = true, Margin = new Padding(12, 10, 0, 0), ForeColor = Color.DimGray });
            layout.Controls.Add(actions, 0, 2);
            var periodActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var weekDownload = MakeButton("下载本周图片", false); weekDownload.Width = 128; weekDownload.Name = "weekDownload";
            var monthDownload = MakeButton("下载本月图片", false); monthDownload.Width = 128; monthDownload.Name = "monthDownload";
            weekDownload.Click += delegate { DownloadPeriod(false); }; monthDownload.Click += delegate { DownloadPeriod(true); };
            periodActions.Controls.Add(weekDownload); periodActions.Controls.Add(monthDownload);
            periodActions.Controls.Add(new Label { Text = "按窗口日期所在整周 / 整月导出，按日期分文件夹", AutoSize = true,
                Margin = new Padding(0, 10, 0, 0), ForeColor = Color.DimGray });
            layout.Controls.Add(periodActions, 0, 3);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 67));
            list.Dock = DockStyle.Fill; list.SelectionMode = SelectionMode.MultiExtended; list.IntegralHeight = false;
            list.HorizontalScrollbar = true; list.BorderStyle = BorderStyle.FixedSingle; list.Margin = new Padding(0, 0, 14, 0);
            list.SelectedIndexChanged += delegate { SelectionChanged(); }; body.Controls.Add(list, 0, 0);
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(229, 237, 233) };
            preview.Dock = DockStyle.Fill; preview.SizeMode = PictureBoxSizeMode.Zoom;
            empty.Dock = DockStyle.Fill; empty.TextAlign = ContentAlignment.MiddleCenter; empty.ForeColor = Color.DimGray;
            panel.Controls.Add(preview); panel.Controls.Add(empty); body.Controls.Add(panel, 1, 0); layout.Controls.Add(body, 0, 4);
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(status, 0, 5);
            Reload(null);
            EnableDrop(this);
        }
        private Button MakeButton(string text, bool primary)
        {
            var button = new Button { Text = text, Width = 112, Height = 36, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(18, 113, 99) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(29, 47, 54), Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand };
            return button;
        }
        private List<SavedPhoto> Selected() { return list.SelectedItems.Cast<SavedPhoto>().ToList(); }
        private void ReleasePreview()
        {
            Image old = preview.Image; preview.Image = null; if (old != null) old.Dispose();
        }
        private void Reload(string selectPath)
        {
            ReleasePreview(); list.Items.Clear();
            foreach (var photo in store.List(day)) list.Items.Add(photo);
            if (list.Items.Count > 0)
            {
                int index = 0;
                for (int i = 0; i < list.Items.Count; i++) if (((SavedPhoto)list.Items[i]).FilePath == selectPath) index = i;
                list.SelectedIndex = index;
            }
            SelectionChanged();
        }
        private void SelectionChanged()
        {
            ReleasePreview();
            var selected = Selected(); download.Enabled = delete.Enabled = selected.Count > 0;
            empty.Visible = true; empty.Text = list.Items.Count == 0 ? "把图片拖到这里\n松开鼠标，自动保存到这一天\n支持一次拖入多张图片" : "请选择图片以预览\n也可以继续拖入图片";
            status.ForeColor = Color.FromArgb(29, 47, 54);
            status.Text = "共 " + list.Items.Count + " 张图片，已选 " + selected.Count + " 张。";
            if (selected.Count == 0) return;
            try
            {
                preview.Image = store.Preview(selected[0]); empty.Visible = false;
                status.Text += "  预览：" + selected[0].Name;
            }
            catch (Exception ex) { empty.Text = "图片无法预览\n" + ex.Message; }
        }
        private void ResetDragHint()
        {
            dragHint.Text = "把图片拖入此窗口即可保存 · 支持多张 · JPG / PNG / GIF / BMP / TIFF · 单张 ≤50 MB";
            dragHint.ForeColor = Color.FromArgb(18, 113, 99); dragHint.BackColor = Color.Transparent;
        }
        private void EnableDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += HandleDragEnter;
            control.DragOver += HandleDragEnter;
            control.DragLeave += delegate { ResetDragHint(); };
            control.DragDrop += HandleDragDrop;
            foreach (Control child in control.Controls) EnableDrop(child);
        }
        private string[] DragFiles(DragEventArgs e)
        {
            if (importing || e.Data == null || (e.AllowedEffect & DragDropEffects.Copy) == 0 || !e.Data.GetDataPresent(DataFormats.FileDrop)) return new string[0];
            return e.Data.GetData(DataFormats.FileDrop) as string[] ?? new string[0];
        }
        private void HandleDragEnter(object sender, DragEventArgs e)
        {
            bool accepted = DragFiles(e).Any(PhotoStore.CanImport);
            e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            if (accepted)
            {
                dragHint.Text = "松开鼠标，将图片保存到 " + Calculator.Key(day) + "（保留电脑上的原图）";
                dragHint.BackColor = Color.FromArgb(206, 232, 216);
            }
            else ResetDragHint();
        }
        private void HandleDragDrop(object sender, DragEventArgs e)
        {
            string[] paths = DragFiles(e); ResetDragHint();
            if (!paths.Any(PhotoStore.CanImport)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            try
            {
                var result = ImportDroppedFiles(paths);
                if (result.Errors.Count > 0) MessageBox.Show(this, "已保存 " + result.Saved + " 张图片。以下文件未添加：\n" + string.Join("\n", result.Errors),
                    "部分文件未添加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "图片添加失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private PhotoImportResult ImportDroppedFiles(string[] paths)
        {
            var result = new PhotoImportResult(); string last = null;
            importing = true; UseWaitCursor = true;
            try
            {
                foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                    try { last = store.Add(day, path).FilePath; result.Saved++; }
                    catch (Exception ex) { result.Errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
                Reload(last);
                status.Text = "已拖入并保存 " + result.Saved + " 张图片。当前日期共有 " + list.Items.Count + " 张。";
                return result;
            }
            finally { importing = false; UseWaitCursor = false; ResetDragHint(); }
        }
        private void Download()
        {
            var selected = Selected(); if (selected.Count == 0) return;
            try
            {
                if (selected.Count == 1)
                {
                    using (var picker = new SaveFileDialog { Title = "下载原图", FileName = selected[0].Name,
                        Filter = "原图格式 (*" + Path.GetExtension(selected[0].Name) + ")|*" + Path.GetExtension(selected[0].Name), AddExtension = true })
                    { if (picker.ShowDialog(this) != DialogResult.OK) return; store.Download(selected[0], picker.FileName); }
                    status.Text = "原图已下载到所选位置。";
                }
                else
                {
                    using (var picker = new FolderBrowserDialog { Description = "选择原图下载文件夹（同名文件自动编号）" })
                    {
                        if (picker.ShowDialog(this) != DialogResult.OK) return;
                        int count = 0; var errors = new List<string>();
                        foreach (var photo in selected)
                            try { store.Download(photo, PhotoStore.AvailableName(picker.SelectedPath, photo.Name)); count++; }
                            catch (Exception ex) { errors.Add(photo.Name + "：" + ex.Message); }
                        status.Text = "已下载 " + count + " 张原图。";
                        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "部分图片未下载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void DeleteSelected()
        {
            var selected = Selected(); if (selected.Count == 0) return;
            if (MessageBox.Show(this, "删除这一天选中的 " + selected.Count + " 张图片？\n只删除软件保存的副本，原始图片和已下载的文件不受影响。",
                "删除图片", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            ReleasePreview(); int count = 0; var errors = new List<string>();
            foreach (var photo in selected)
                try { store.Delete(photo); count++; } catch (Exception ex) { errors.Add(photo.Name + "：" + ex.Message); }
            Reload(null); status.Text = "已删除 " + count + " 张图片。";
            if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "部分图片未删除", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        private void DownloadPeriod(bool monthly)
        {
            PhotoPeriod period = PhotoPeriod.ForDate(day, monthly);
            try
            {
                int count = store.ListPeriod(period).Count;
                if (count == 0) { MessageBox.Show(this, period.Range + "\n这个范围还没有保存图片。", "没有可下载的图片"); return; }
                using (var picker = new FolderBrowserDialog { Description = "下载" + period.Caption + "（" + period.Range + "），共 " + count + " 张。请选择保存位置。" })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    PhotoDownloadResult result;
                    UseWaitCursor = true;
                    try { result = store.DownloadPeriod(period, picker.SelectedPath); }
                    finally { UseWaitCursor = false; }
                    status.Text = period.Range + "：已下载 " + result.Saved + " 张原图。";
                    string message = "已下载 " + result.Saved + " 张原图。\n保存位置：" + result.Directory;
                    if (result.Errors.Count > 0) message += "\n\n未下载：\n" + string.Join("\n", result.Errors);
                    MessageBox.Show(this, message, result.Errors.Count == 0 ? "下载完成" : "部分图片未下载",
                        MessageBoxButtons.OK, result.Errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) ReleasePreview(); base.Dispose(disposing); }
    }
}
