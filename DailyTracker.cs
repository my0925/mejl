using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MuYu.DailyTracker
{
    public sealed class DayRecord
    {
        public string Date { get; set; }
        public int Complex { get; set; }
        public int Regular { get; set; }
        public int Simple { get; set; }
        public string UpdatedAt { get; set; }
    }

    public sealed class Database
    {
        public int Version { get; set; }
        public List<DayRecord> Records { get; set; }
        public TrackerSettings Settings { get; set; }
        public Dictionary<string, decimal> WeeklyDeductions { get; set; }
        public List<string> CollapsedMonths { get; set; }
        public List<string> CollapsedDeductionMonths { get; set; }
        public Database() { Version = 1; Records = new List<DayRecord>(); Settings = new TrackerSettings(); WeeklyDeductions = new Dictionary<string, decimal>(); CollapsedMonths = new List<string>(); CollapsedDeductionMonths = new List<string>(); }
    }

    public sealed class Settlement
    {
        public DateTime Start, End;
        public int CalendarDays, RecordedDays;
        public decimal RestDays, EffectiveDays;
        public string DeductionDetails;
        public decimal? Target, Shortfall, FullWeekEffectiveDays, FullWeekTarget, FullWeekShortfall;
        public long Complex, Regular, Simple;
        public decimal Equivalent;
        public decimal? Average;
        public string Rating;
        public TrackerSettings Rules = new TrackerSettings();
    }

    public static class Calculator
    {
        public static string Key(DateTime date) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static DateTime WeekStart(DateTime date) { return date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); }
        public static decimal WeeklyDeduction(Database db, DateTime date)
        {
            decimal value;
            return db.WeeklyDeductions != null && db.WeeklyDeductions.TryGetValue(Key(WeekStart(date)), out value) ? value : db.Settings.WeeklyRestDays;
        }
        public static DateTime Parse(string key) { return DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static string Rate(decimal average)
        { return Rate(average, new TrackerSettings()); }
        public static decimal Equivalent(long complex, long regular, long simple, TrackerSettings rules)
        { return complex * rules.ComplexFactor + regular * rules.RegularFactor + simple; }
        public static decimal Equivalent(DayRecord row, TrackerSettings rules)
        { return Equivalent(row.Complex, row.Regular, row.Simple, rules); }
        public static string Rate(decimal average, TrackerSettings rules)
        {
            if (average >= rules.GoodMinimum) return "有果效";
            if (average >= rules.NormalMinimum) return "一般果效";
            if (average >= rules.SomeMinimum) return "有点果效";
            return "没果效";
        }
        public static Settlement Calculate(Database db, DateTime end, bool monthly)
        {
            end = end.Date;
            DateTime start = monthly ? new DateTime(end.Year, end.Month, 1)
                : end.AddDays(-(((int)end.DayOfWeek + 6) % 7));
            var rules = db.Settings ?? new TrackerSettings(); rules.Validate();
            var result = new Settlement { Start = start, End = end, Rules = rules.Copy() };
            var deductions = new StringBuilder();
            for (DateTime monday = WeekStart(start); monday <= end; monday = monday.AddDays(7))
            {
                DateTime sunday = monday.AddDays(6);
                // Apply a week's deduction only after that week reaches Sunday.
                // A cross-month week belongs wholly to its Sunday month.
                if (sunday > end) continue;
                if (monthly && (sunday.Year != end.Year || sunday.Month != end.Month)) continue;
                decimal deduction = WeeklyDeduction(db, monday);
                result.RestDays += deduction;
                deductions.AppendLine(monday.ToString("yyyy-MM-dd") + " 至 " + sunday.ToString("yyyy-MM-dd") + "：扣减 " + deduction.ToString("0.##") + " 天");
            }
            result.DeductionDetails = deductions.ToString();
            result.CalendarDays = (end - start).Days + 1;
            result.EffectiveDays = Math.Max(0, result.CalendarDays - result.RestDays);
            foreach (var row in db.Records)
            {
                DateTime day = Parse(row.Date);
                if (day < start || day > end) continue;
                result.RecordedDays++;
                result.Complex += row.Complex;
                result.Regular += row.Regular;
                result.Simple += row.Simple;
            }
            result.Equivalent = Equivalent(result.Complex, result.Regular, result.Simple, rules);
            result.Average = result.EffectiveDays > 0 ? (decimal?)(result.Equivalent / result.EffectiveDays) : null;
            result.Rating = result.Average.HasValue ? Rate(result.Average.Value, rules) : "暂不能评价";
            if (!monthly)
            {
                result.FullWeekEffectiveDays = Math.Max(0m, 7m - WeeklyDeduction(db, start));
                result.FullWeekTarget = result.FullWeekEffectiveDays.Value * rules.GoodMinimum;
                result.FullWeekShortfall = Math.Max(0m, result.FullWeekTarget.Value - result.Equivalent);
            }
            if (result.Average.HasValue)
            {
                result.Target = result.EffectiveDays * rules.GoodMinimum;
                result.Shortfall = Math.Max(0m, result.Target.Value - result.Equivalent);
            }
            return result;
        }
    }

    public sealed class RecordStore
    {
        public const int MaxQuantity = 1000000;
        public const int MaxDataBytes = 16 * 1024 * 1024;
        public const int MaxRecords = 20000;
        public readonly string FilePath;
        public RecordStore(string filePath) { FilePath = filePath; }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 16000000 }; }
        public static void Validate(Database db)
        {
            if (db == null || db.Version != 1 || db.Records == null || db.Records.Count > MaxRecords)
                throw new InvalidDataException("数据文件格式、版本或记录数量不受支持。");
            if (db.Settings == null) db.Settings = new TrackerSettings();
            db.Settings.Validate();
            if (db.WeeklyDeductions == null) db.WeeklyDeductions = new Dictionary<string, decimal>();
            if (db.WeeklyDeductions.Count > 10000) throw new InvalidDataException("每周扣减记录过多。");
            if (db.CollapsedMonths == null) db.CollapsedMonths = new List<string>();
            if (db.CollapsedDeductionMonths == null) db.CollapsedDeductionMonths = new List<string>();
            if (!ValidMonthList(db.CollapsedMonths) || !ValidMonthList(db.CollapsedDeductionMonths))
                throw new InvalidDataException("折叠月份记录格式无效。");
            foreach (var item in db.WeeklyDeductions)
            {
                DateTime monday;
                if (!DateTime.TryParseExact(item.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out monday)
                    || monday.DayOfWeek != DayOfWeek.Monday || item.Value < 0 || item.Value > 7 || decimal.Round(item.Value, 2) != item.Value)
                    throw new InvalidDataException("每周扣减记录须使用周一日期，时间为 0～7 天，最多两位小数。");
            }
            var dates = new HashSet<string>();
            foreach (var row in db.Records)
            {
                DateTime parsed;
                if (row == null || !DateTime.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                    || !dates.Add(row.Date)) throw new InvalidDataException("记录中存在无效或重复日期。");
                if (row.Complex < 0 || row.Regular < 0 || row.Simple < 0 || row.Complex > MaxQuantity
                    || row.Regular > MaxQuantity || row.Simple > MaxQuantity) throw new InvalidDataException("记录数量超出允许范围。");
            }
        }
        private static bool ValidMonthList(List<string> values)
        {
            return !values.Any(value =>
            {
                DateTime month;
                return !DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
            }) && values.Distinct().Count() == values.Count;
        }
        public Database Load()
        {
            if (!File.Exists(FilePath))
            {
                if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("主数据文件缺失，但检测到备份。请先恢复备份，避免覆盖已有数据。");
                return new Database();
            }
            if (new FileInfo(FilePath).Length > MaxDataBytes) throw new InvalidDataException("数据文件超过 16 MB 安全限制。");
            Database db = Serializer().Deserialize<Database>(File.ReadAllText(FilePath, Encoding.UTF8));
            Validate(db);
            return db;
        }
        public void Save(Database db)
        {
            Validate(db);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(Serializer().Serialize(db));
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        public static DayRecord Find(Database db, DateTime day)
        {
            string key = Calculator.Key(day);
            return db.Records.FirstOrDefault(r => r.Date == key);
        }
        public static DayRecord Put(Database db, DateTime day, int complex, int regular, int simple)
        {
            var row = Find(db, day);
            if (row == null) { row = new DayRecord { Date = Calculator.Key(day) }; db.Records.Add(row); }
            row.Complex = complex;
            row.Regular = regular;
            row.Simple = simple;
            row.UpdatedAt = DateTime.Now.ToString("o");
            return row;
        }
        public static bool EnsureDay(Database db, DateTime day)
        {
            if (Find(db, day) != null) return false;
            Put(db, day, 0, 0, 0);
            return true;
        }
        public static string Csv(Database db)
        {
            var csv = new StringBuilder("日期,复杂数量,常规数量,简单数量,折合简单数量\r\n");
            foreach (var row in db.Records.OrderBy(r => r.Date))
                csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:0.0#}\r\n", row.Date, row.Complex, row.Regular, row.Simple, Calculator.Equivalent(row, db.Settings));
            return csv.ToString();
        }
    }

    public sealed class QuantityBox : TextBox
    {
        public decimal Value
        {
            get { return int.Parse(Text, CultureInfo.InvariantCulture); }
            set
            {
                if (value < 0 || value > RecordStore.MaxQuantity || value != decimal.Truncate(value)) throw new ArgumentOutOfRangeException("value");
                Text = value.ToString("0", CultureInfo.InvariantCulture);
            }
        }
        public QuantityBox()
        {
            Text = "0"; MaxLength = 7;
            Font = new Font("Microsoft YaHei UI", 24, FontStyle.Bold);
            BorderStyle = BorderStyle.FixedSingle;
            Dock = DockStyle.Fill;
        }
        public bool CommitText()
        {
            int number;
            if (!int.TryParse(Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number)
                || number < 0 || number > RecordStore.MaxQuantity) return false;
            Value = number;
            return true;
        }
    }

    public sealed class MainWindow : Form
    {
        private readonly RecordStore store;
        private readonly Database db;
        private readonly Color ink = Color.FromArgb(29, 47, 54), muted = Color.FromArgb(101, 117, 122);
        private readonly Color accent = Color.FromArgb(18, 113, 99);
        private DateTime selectedDay = DateTime.Today, observedToday = DateTime.Today;
        private DateTime lastSettlementDate = DateTime.Today;
        private bool loading, dirty, closing;
        private readonly bool previewMode;
        private readonly DateTimePicker editDate = new DateTimePicker(), settlementDate = new DateTimePicker();
        private readonly QuantityBox complex = new QuantityBox(), regular = new QuantityBox(), simple = new QuantityBox();
        private readonly Label saveState = new Label(), total = new Label(), todayHint = new Label();
        private Label complexHint, regularHint, restHint, thresholdsHint;
        private readonly DataGridView history = new DataGridView();
        private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly System.Windows.Forms.Timer dayTimer = new System.Windows.Forms.Timer { Interval = 1000 };

        public MainWindow(RecordStore store, Database db, bool preview)
        {
            this.store = store; this.db = db; this.previewMode = preview;
            Text = "每日果效记录";
            Font = new Font("Microsoft YaHei UI", 10);
            ForeColor = ink; BackColor = Color.FromArgb(243, 247, 246);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1080, 820); MinimumSize = new Size(960, 780);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            if (RecordStore.EnsureDay(db, DateTime.Today)) { dirty = true; if (!preview) SaveNow(); }
            LoadDay(selectedDay); RefreshHistory();
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow(); };
            dayTimer.Tick += delegate { CheckNewDay(); };
            Activated += delegate { CheckNewDay(); };
            FormClosing += OnClosing;
            if (!preview) dayTimer.Start();
        }
        private Label LabelOf(string text, float size, Color color, bool bold)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = color,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(0, 0, 0, 0), Anchor = AnchorStyles.Left };
        }
        private Button ButtonOf(string text, bool primary, int width)
        {
            var button = new Button { Text = text, Width = width, Height = 38, FlatStyle = FlatStyle.Flat,
                BackColor = primary ? accent : Color.White, ForeColor = primary ? Color.White : ink,
                Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0) };
            button.FlatAppearance.BorderColor = primary ? accent : Color.FromArgb(211, 224, 220);
            return button;
        }
        private void ConfigureDate(DateTimePicker picker)
        {
            picker.Format = DateTimePickerFormat.Custom; picker.CustomFormat = "yyyy年MM月dd日";
            picker.Width = 182; picker.MaxDate = DateTime.Today;
            picker.Margin = new Padding(8, 5, 16, 0);
        }
        private Control Card(string name, string conversion, QuantityBox input, int rightMargin)
        {
            var card = new TableLayoutPanel { BackColor = Color.White, Dock = DockStyle.Fill,
                Padding = new Padding(20, 12, 20, 12), Margin = new Padding(0, 0, rightMargin, 0), RowCount = 3, ColumnCount = 1 };
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(LabelOf(name + " · 数量", 12, ink, true), 0, 0);
            card.Controls.Add(input, 0, 1);
            var conversionLabel = LabelOf(conversion, 9, muted, false);
            if (input == complex) complexHint = conversionLabel;
            if (input == regular) regularHint = conversionLabel;
            card.Controls.Add(conversionLabel, 0, 2);
            input.TextChanged += OnQuantityChanged;
            return card;
        }
        private void BuildUi()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 18), ColumnCount = 1, RowCount = 9 };
            foreach (int height in new[] { 76, 48, 148, 54, 92, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(layout);

            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            heading.Controls.Add(LabelOf("每日果效记录", 24, ink, true), 0, 0);
            heading.Controls.Add(LabelOf("记录每日完成量，让每周、每月的成果清晰可见。", 10, muted, false), 0, 1);
            layout.Controls.Add(heading, 0, 0);

            var dateBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var dateLabel = LabelOf("记录日期", 10, ink, true); dateLabel.Margin = new Padding(0, 9, 0, 0);
            dateBar.Controls.Add(dateLabel); ConfigureDate(editDate); dateBar.Controls.Add(editDate);
            var today = ButtonOf("回到今天", false, 108);
            today.Click += delegate { CheckNewDay(); editDate.Value = DateTime.Today; };
            dateBar.Controls.Add(today);
            saveState.AutoSize = true; saveState.Margin = new Padding(10, 10, 0, 0); saveState.ForeColor = accent;
            saveState.Text = "已自动保存"; dateBar.Controls.Add(saveState); layout.Controls.Add(dateBar, 0, 1);
            editDate.ValueChanged += OnDateChanged;

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            cards.Controls.Add(Card("复杂", "每个折合 5.6 个简单", complex, 14), 0, 0);
            cards.Controls.Add(Card("常规", "每个折合 1.4 个简单", regular, 14), 1, 0);
            cards.Controls.Add(Card("简单", "每个折合 1 个简单", simple, 0), 2, 0);
            layout.Controls.Add(cards, 0, 2);

            var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            total.AutoSize = true; total.Font = new Font(Font.FontFamily, 13, FontStyle.Bold); total.ForeColor = accent; total.Anchor = AnchorStyles.Left;
            todayHint.Text = "修改后自动保存 · 支持补填、修改历史日期"; todayHint.AutoSize = true; todayHint.ForeColor = muted; todayHint.Anchor = AnchorStyles.Right;
            summary.Controls.Add(total, 0, 0); summary.Controls.Add(todayHint, 1, 0); layout.Controls.Add(summary, 0, 3);

            var settlementPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(229, 240, 235),
                Padding = new Padding(16, 12, 12, 6), Margin = new Padding(0, 0, 0, 6), ColumnCount = 1, RowCount = 2 };
            settlementPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var endLabel = LabelOf("结算截止", 10, ink, true); endLabel.Margin = new Padding(0, 8, 0, 0); actions.Controls.Add(endLabel);
            ConfigureDate(settlementDate); actions.Controls.Add(settlementDate);
            var weekly = ButtonOf("星期结算", true, 120); var monthly = ButtonOf("月结算", true, 120);
            weekly.Click += delegate { ShowSettlement(false); }; monthly.Click += delegate { ShowSettlement(true); };
            actions.Controls.Add(weekly); actions.Controls.Add(monthly);
            var deduction = ButtonOf("每周扣减", false, 120); deduction.Name = "weeklyDeductionButton";
            deduction.Click += delegate { ShowWeeklyDeductions(); }; actions.Controls.Add(deduction);
            settlementPanel.Controls.Add(actions, 0, 0);
            restHint = LabelOf("", 9, muted, false); settlementPanel.Controls.Add(restHint, 0, 1);
            layout.Controls.Add(settlementPanel, 0, 4);

            var historyTitle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            historyTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); historyTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            historyTitle.Controls.Add(LabelOf("历史记录", 12, ink, true), 0, 0);
            var hint = LabelOf("双击月份可折叠/展开 · 双击日期可修改 · 未填按 0 计入结算", 9, muted, false); hint.Anchor = AnchorStyles.Right;
            historyTitle.Controls.Add(hint, 1, 0); layout.Controls.Add(historyTitle, 0, 5);
            history.Dock = DockStyle.Fill; history.Margin = new Padding(0, 0, 0, 8); history.ReadOnly = true;
            history.AllowUserToAddRows = false; history.AllowUserToDeleteRows = false; history.AllowUserToResizeRows = false;
            history.MultiSelect = false; history.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            history.RowHeadersVisible = false; history.BackgroundColor = Color.White; history.BorderStyle = BorderStyle.None;
            history.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; history.EnableHeadersVisualStyles = false;
            history.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(230, 237, 234);
            history.ColumnHeadersDefaultCellStyle.ForeColor = ink; history.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            history.ColumnHeadersHeight = 38; history.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            history.RowTemplate.Height = 34; history.GridColor = Color.FromArgb(235, 240, 238);
            history.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            history.DefaultCellStyle.SelectionBackColor = Color.FromArgb(213, 235, 226); history.DefaultCellStyle.SelectionForeColor = ink;
            history.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 250);
            foreach (string name in new[] { "日期", "复杂", "常规", "简单", "折合简单" })
                history.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, SortMode = DataGridViewColumnSortMode.NotSortable });
            history.Columns[0].FillWeight = 150;
            history.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                string tag = history.Rows[e.RowIndex].Tag as string;
                if (tag != null && tag.StartsWith("month:", StringComparison.Ordinal)) { ToggleMonth(tag.Substring(6)); return; }
                DateTime day = Calculator.Parse(tag);
                if (day > DateTime.Today) { MessageBox.Show(this, "这条记录晚于电脑当前日期，请先检查系统时间。", "日期提示"); return; }
                editDate.Value = day;
            };
            layout.Controls.Add(history, 0, 6);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var export = ButtonOf("导出 CSV", false, 118); export.Click += delegate { Export(false); };
            var backup = ButtonOf("备份数据", false, 118); backup.Click += delegate { Export(true); };
            var photos = ButtonOf("图片附件", true, 118); photos.Click += delegate { ShowPhotos(); };
            var settings = ButtonOf("设置", false, 92); settings.Name = "settingsButton"; settings.Click += delegate { ShowSettings(); };
            var folder = ButtonOf("打开数据文件夹", false, 150); folder.Click += delegate { System.Diagnostics.Process.Start(Path.GetDirectoryName(store.FilePath)); };
            footer.Controls.Add(photos); footer.Controls.Add(settings); footer.Controls.Add(export); footer.Controls.Add(backup); footer.Controls.Add(folder); layout.Controls.Add(footer, 0, 7);
            thresholdsHint = LabelOf("", 9, muted, false); layout.Controls.Add(thresholdsHint, 0, 8);
            RefreshRules();
        }
        private void RefreshRules()
        {
            complexHint.Text = "每个折合 " + db.Settings.ComplexFactor.ToString("0.##") + " 个简单";
            regularHint.Text = "每个折合 " + db.Settings.RegularFactor.ToString("0.##") + " 个简单";
            restHint.Text = "每周默认扣 " + db.Settings.WeeklyRestDays.ToString("0.##") + " 天，可逐周调整；周一至周六先按已过天数计算，到周日才扣减。";
            thresholdsHint.Text = "日均标准：" + db.Settings.ThresholdSummary(); UpdateTotal();
        }
        private void ApplySettings(TrackerSettings candidate)
        {
            candidate.Validate(); var previous = db.Settings;
            db.Settings = candidate.Copy();
            try { store.Save(db); } catch { db.Settings = previous; throw; }
            RefreshRules(); RefreshHistory();
            saveState.Text = "设置已保存 · 所有日期按新规则计算"; saveState.ForeColor = accent;
        }
        private void ShowSettings()
        {
            if (!SaveNow()) { MessageBox.Show(this, "请先修正当前输入或解决保存错误。", "暂未打开设置"); return; }
            using (var dialog = new SettingsWindow(db.Settings, ApplySettings)) dialog.ShowDialog(this);
        }
        private void ShowWeeklyDeductions()
        {
            if (!SaveNow()) return;
            using (var dialog = new WeeklyDeductionsWindow(db, settlementDate.Value.Date, delegate(Dictionary<string, decimal> values)
            {
                var previous = db.WeeklyDeductions; db.WeeklyDeductions = values;
                try { store.Save(db); } catch { db.WeeklyDeductions = previous; throw; }
                saveState.Text = "每周扣减已保存 · 重新结算即可查看结果";
            }, delegate(List<string> months)
            {
                var previous = db.CollapsedDeductionMonths; db.CollapsedDeductionMonths = new List<string>(months);
                try { store.Save(db); } catch { db.CollapsedDeductionMonths = previous; throw; }
            })) dialog.ShowDialog(this);
        }
        private void OnQuantityChanged(object sender, EventArgs args)
        {
            if (loading) return;
            dirty = true; saveState.Text = "正在编辑，稍后自动保存…"; saveState.ForeColor = muted;
            UpdateTotal(); saveTimer.Stop(); saveTimer.Start();
        }
        private void UpdateTotal()
        {
            // Do not normalize or overwrite partially entered text while the user is typing.
            int c, r, s;
            if (int.TryParse(complex.Text, NumberStyles.None, CultureInfo.InvariantCulture, out c)
                && int.TryParse(regular.Text, NumberStyles.None, CultureInfo.InvariantCulture, out r)
                && int.TryParse(simple.Text, NumberStyles.None, CultureInfo.InvariantCulture, out s)
                && c <= RecordStore.MaxQuantity && r <= RecordStore.MaxQuantity && s <= RecordStore.MaxQuantity)
                total.Text = "当日折合简单：" + Calculator.Equivalent(c, r, s, db.Settings).ToString("0.0#") + " 个";
            else total.Text = "当日折合简单：请填写有效数量";
        }
        private void LoadDay(DateTime day)
        {
            loading = true;
            selectedDay = day.Date;
            var row = RecordStore.Find(db, selectedDay);
            complex.Value = row == null ? 0 : row.Complex; complex.Text = complex.Value.ToString("0");
            regular.Value = row == null ? 0 : row.Regular; regular.Text = regular.Value.ToString("0");
            simple.Value = row == null ? 0 : row.Simple; simple.Text = simple.Value.ToString("0");
            editDate.Value = selectedDay;
            loading = false;
            UpdateTotal();
            if (!dirty) { saveState.Text = row == null ? "尚未填写，输入数量即可保存" : "已自动保存"; saveState.ForeColor = accent; }
        }
        private bool SaveNow()
        {
            saveTimer.Stop();
            if (!dirty) return true;
            loading = true;
            bool valid = complex.CommitText() & regular.CommitText() & simple.CommitText();
            loading = false;
            if (!valid)
            {
                saveState.Text = "未保存：请填写 0～1000000 的整数"; saveState.ForeColor = Color.Firebrick;
                return false;
            }
            RecordStore.Put(db, selectedDay, (int)complex.Value, (int)regular.Value, (int)simple.Value);
            UpdateTotal();
            try
            {
                store.Save(db); dirty = false;
                saveState.Text = "已自动保存 · " + DateTime.Now.ToString("HH:mm:ss"); saveState.ForeColor = accent;
                RefreshHistory();
                return true;
            }
            catch (Exception ex)
            {
                saveState.Text = "保存失败，请检查数据文件夹后重试"; saveState.ForeColor = Color.Firebrick;
                saveState.Tag = ex.Message;
                return false;
            }
        }
        private void OnDateChanged(object sender, EventArgs args)
        {
            if (loading) return;
            DateTime next = editDate.Value.Date;
            if (!SaveNow())
            {
                loading = true; editDate.Value = selectedDay; loading = false;
                MessageBox.Show(this, "当前输入尚未保存，请先检查数量和数据文件夹。", "未切换日期", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            LoadDay(next);
        }
        private void RefreshHistory()
        {
            string selection = history.SelectedRows.Count > 0 ? history.SelectedRows[0].Tag as string : null;
            int scroll = history.FirstDisplayedScrollingRowIndex;
            history.Rows.Clear();
            foreach (var group in db.Records.OrderByDescending(r => r.Date).GroupBy(r => r.Date.Substring(0, 7)))
            {
                string month = group.Key; bool collapsed = db.CollapsedMonths.Contains(month);
                decimal total = group.Sum(row => Calculator.Equivalent(row, db.Settings));
                int header = history.Rows.Add((collapsed ? "▶ " : "▼ ") + month.Replace("-", "年") + "月（" + group.Count() + " 天）", "", "", "", total.ToString("0.0#"));
                history.Rows[header].Tag = "month:" + month;
                history.Rows[header].Height = 38;
                history.Rows[header].DefaultCellStyle.BackColor = Color.FromArgb(218, 232, 227);
                history.Rows[header].DefaultCellStyle.ForeColor = ink;
                history.Rows[header].DefaultCellStyle.Font = new Font(history.Font, FontStyle.Bold);
                if (collapsed) continue;
                foreach (var row in group)
                {
                    int i = history.Rows.Add("    " + row.Date, row.Complex, row.Regular, row.Simple, Calculator.Equivalent(row, db.Settings).ToString("0.0#"));
                    history.Rows[i].Tag = row.Date;
                }
            }
            history.ClearSelection();
            foreach (DataGridViewRow row in history.Rows) if (row.Tag as string == selection) row.Selected = true;
            if (scroll >= 0 && scroll < history.Rows.Count) history.FirstDisplayedScrollingRowIndex = scroll;
        }
        private void ToggleMonth(string month)
        {
            bool wasCollapsed = db.CollapsedMonths.Contains(month);
            if (wasCollapsed) db.CollapsedMonths.Remove(month); else db.CollapsedMonths.Add(month);
            try { store.Save(db); RefreshHistory(); saveState.Text = wasCollapsed ? "月份已展开" : "月份已折叠"; }
            catch (Exception ex)
            {
                if (wasCollapsed) db.CollapsedMonths.Add(month); else db.CollapsedMonths.Remove(month);
                MessageBox.Show(this, "折叠状态未保存。\n" + ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private void CheckNewDay()
        {
            if (closing || loading || DateTime.Today == observedToday) return;
            if (!SaveNow()) return;
            DateTime today = DateTime.Today;
            // Expand the limit before setting a new value; shrink only after changing it.
            if (today > editDate.MaxDate) editDate.MaxDate = today;
            if (today > settlementDate.MaxDate) settlementDate.MaxDate = today;
            if (settlementDate.Value.Date == observedToday || settlementDate.Value.Date > today) settlementDate.Value = today;
            observedToday = today;
            bool added = RecordStore.EnsureDay(db, today);
            LoadDay(today);
            editDate.MaxDate = today; settlementDate.MaxDate = today;
            if (added) { dirty = true; SaveNow(); }
        }
        private void ShowSettlement(bool monthly)
        {
            CheckNewDay();
            if (!SaveNow()) { MessageBox.Show(this, "请先修正当前输入或解决保存错误，再进行结算。", "暂未结算"); return; }
            lastSettlementDate = settlementDate.Value.Date;
            Settlement result = Calculator.Calculate(db, lastSettlementDate, monthly);
            using (var dialog = new SettlementWindow(result, monthly)) dialog.ShowDialog(this);
        }
        private void ShowPhotos()
        {
            if (!SaveNow()) { MessageBox.Show(this, "请先修正当前输入或解决保存错误。", "暂未打开图片"); return; }
            DateTime photoDay = selectedDay;
            try
            {
                using (var dialog = new PhotoWindow(new PhotoStore(Path.GetDirectoryName(store.FilePath)), photoDay)) dialog.ShowDialog(this);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "图片操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void Export(bool backup)
        {
            if (!SaveNow()) { MessageBox.Show(this, "请先修正当前输入或解决保存错误。", "暂未导出"); return; }
            using (var picker = new SaveFileDialog())
            {
                picker.Filter = backup ? "完整备份，含图片 (*.zip)|*.zip" : "CSV 表格 (*.csv)|*.csv";
                picker.FileName = "每日果效记录_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + (backup ? ".zip" : ".csv");
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string target = Path.GetFullPath(picker.FileName);
                    DataPaths.RequireOutside(Path.GetDirectoryName(store.FilePath), target);
                    if (backup) PhotoStore.Backup(store.FilePath, target);
                    else File.WriteAllText(target, RecordStore.Csv(db), new UTF8Encoding(true));
                    MessageBox.Show(this, "已保存到：\n" + target, backup ? "备份完成" : "导出完成");
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
        private void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (SaveNow()) { closing = true; dayTimer.Stop(); saveTimer.Stop(); return; }
            args.Cancel = true;
            MessageBox.Show(this, "当前输入尚未保存，软件暂不关闭。\n请检查数量（0～1000000 的整数）或数据文件夹写入权限。\n" + Convert.ToString(saveState.Tag),
                "请先保存数据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { saveTimer.Dispose(); dayTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public sealed class SettlementWindow : Form
    {
        private readonly RichTextBox report = new RichTextBox();
        public static string ReportText(Settlement result, bool monthly, bool details = false)
        {
            string average = result.Average.HasValue ? result.Average.Value.ToString("0.00") + " 个简单 / 天" : "—（有效时间为 0）";
            var text = new StringBuilder();
            if (details)
            {
            text.AppendLine((monthly ? "月结算" : "星期结算") + " · " + result.End.ToString("yyyy年MM月dd日"));
            text.AppendLine(result.Start.ToString("yyyy-MM-dd") + " 至 " + result.End.ToString("yyyy-MM-dd") + "（含首尾当天）");
            text.AppendLine();
            }
            text.AppendLine("复杂：" + result.Complex.ToString("N0") + " 个");
            text.AppendLine("常规：" + result.Regular.ToString("N0") + " 个");
            text.AppendLine("简单：" + result.Simple.ToString("N0") + " 个");
            text.AppendLine();
            text.AppendLine((monthly ? "本月累计总量：" : "本周累计总量：") + result.Equivalent.ToString("0.##") + " 个简单（折合）");
            text.AppendLine("日均：" + average);
            text.AppendLine("评价：" + result.Rating);
            text.AppendLine();
            if (!result.Shortfall.HasValue) text.AppendLine("距有果效：暂不能计算（有效时间为 0）");
            else if (result.Shortfall.Value == 0) text.AppendLine("距有果效：已达标，还差 0 个简单");
            else
            {
                text.AppendLine("距有果效：还差 " + result.Shortfall.Value.ToString("0.####") + " 个简单（折合量）");
                text.AppendLine("若只补简单：至少还需 " + decimal.Ceiling(result.Shortfall.Value).ToString("0") + " 个");
            }
            if (!monthly && result.FullWeekShortfall.HasValue)
            {
                text.AppendLine();
                text.AppendLine("本周有果效总目标：" + result.FullWeekTarget.Value.ToString("0.####") + " 个简单（"
                    + result.FullWeekEffectiveDays.Value.ToString("0.##") + " 天 × " + result.Rules.GoodMinimum.ToString("0.##") + "）");
                if (result.FullWeekShortfall.Value == 0) text.AppendLine("本周目标剩余：已完成，还差 0 个简单");
                else
                {
                    text.AppendLine("本周目标剩余：还差 " + result.FullWeekShortfall.Value.ToString("0.####") + " 个简单（折合量）");
                    text.AppendLine("若只补简单完成整周目标：至少还需 " + decimal.Ceiling(result.FullWeekShortfall.Value).ToString("0") + " 个");
                }
            }
            if (!details) return text.ToString();
            text.AppendLine();
            text.AppendLine("折合简单总量：" + result.Equivalent.ToString("0.0#") + " 个");
            text.AppendLine("复杂 × " + result.Rules.ComplexFactor.ToString("0.##") + " + 常规 × " + result.Rules.RegularFactor.ToString("0.##") + " + 简单");
            text.AppendLine();
            text.AppendLine(string.Format("有效时间：{0} 个日历天 − {1:0.##} 天扣减 = {2:0.##} 天", result.CalendarDays, result.RestDays, result.EffectiveDays));
            if (result.RestDays > result.CalendarDays) text.AppendLine("累计扣减超过日历时间时，有效时间按 0 计算。");
            if (monthly) text.AppendLine("每月只累计已经到周日、且周日归属当月的各周扣减；跨月周整笔归周日所在月，不按日折算。");
            else if (result.End.DayOfWeek != DayOfWeek.Sunday) text.AppendLine("本周尚未到周日，当前扣减暂不计入；日均按实际已过日历天数计算。");
            text.Append(result.DeductionDetails);
            text.AppendLine(string.Format("已填 {0} 天；未填日期数量按 0 计算。", result.RecordedDays));
            text.AppendLine();
            if (result.Average.HasValue)
            {
                text.AppendLine("有果效目标：" + result.EffectiveDays.ToString("0.##") + " × " + result.Rules.GoodMinimum.ToString("0.##") + " = " + result.Target.Value.ToString("0.####") + " 个简单");
                text.AppendLine("差额 = 目标 − 当前折合总量，最低为 0；按所选截止日期的有效时间计算。");
                text.AppendLine("日均 = 折合简单总量 ÷ 有效天数。评价按未四舍五入值判断。");
                text.Append(result.Rules.ThresholdDetails());
            }
            else text.Append("当前范围的日历天数不大于扣除的休息天数，暂不能评价。\r\n数量汇总仍然有效，请选择更晚的截止日期后再结算。");
            return text.ToString();
        }
        public SettlementWindow(Settlement result, bool monthly)
        {
            Text = (monthly ? "月结算" : "星期结算") + " · " + result.Start.ToString("yyyy-MM-dd") + " 至 " + result.End.ToString("yyyy-MM-dd"); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 11); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 700); MinimumSize = new Size(660, 520);
            MinimizeBox = false; BackColor = Color.FromArgb(243, 247, 246);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "拖动选择文字后按 Ctrl+C 复制，也可以按 Ctrl+A 全选。", Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(101, 117, 122), Font = new Font(Font.FontFamily, 10) }, 0, 0);
            report.ReadOnly = true; report.DetectUrls = false; report.ShortcutsEnabled = true;
            report.HideSelection = false; report.BorderStyle = BorderStyle.None; report.Dock = DockStyle.Fill;
            report.BackColor = Color.White; report.ForeColor = Color.FromArgb(29, 47, 54);
            report.Font = new Font(Font.FontFamily, 11); report.ScrollBars = RichTextBoxScrollBars.Vertical;
            SetReport(result, monthly, false); report.Name = "settlementReport";
            report.Select(0, 0); layout.Controls.Add(report, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            var close = new Button { Text = "完成", BackColor = Color.FromArgb(18, 113, 99), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Width = 105, Height = 36, DialogResult = DialogResult.OK };
            var copyAll = new Button { Text = "复制全部", Width = 105, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            var copySelected = new Button { Text = "复制所选", Width = 105, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Enabled = false };
            var details = new Button { Text = "查看详情", Name = "detailsButton", Width = 105, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            bool expanded = false;
            details.Click += delegate { expanded = !expanded; SetReport(result, monthly, expanded); details.Text = expanded ? "收起详情" : "查看详情"; };
            copyAll.Click += delegate { CopyText(report.Text); };
            copySelected.Click += delegate { CopyText(report.SelectedText); };
            report.SelectionChanged += delegate { copySelected.Enabled = report.SelectionLength > 0; };
            report.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.A) { report.SelectAll(); e.SuppressKeyPress = true; }
                if (e.Control && e.KeyCode == Keys.C) { CopyText(report.SelectedText); e.SuppressKeyPress = true; }
            };
            var menu = new ContextMenuStrip();
            var copyItem = menu.Items.Add("复制所选", null, delegate { CopyText(report.SelectedText); });
            menu.Items.Add("全选", null, delegate { report.SelectAll(); });
            menu.Opening += delegate { copyItem.Enabled = report.SelectionLength > 0; }; report.ContextMenuStrip = menu;
            FormClosed += delegate { menu.Dispose(); };
            actions.Controls.Add(close); actions.Controls.Add(copyAll); actions.Controls.Add(copySelected); actions.Controls.Add(details);
            layout.Controls.Add(actions, 0, 2); AcceptButton = close; CancelButton = close;
            Shown += delegate { report.Focus(); report.Select(0, 0); };
        }
        private void SetReport(Settlement result, bool monthly, bool details)
        {
            report.Clear(); report.Font = new Font(Font.FontFamily, 11);
            report.Text = ReportText(result, monthly, details);
            report.SelectAll(); report.SelectionFont = report.Font; report.SelectionColor = Color.FromArgb(29, 47, 54);
            foreach (string prefix in new[] { "复杂：", "常规：", "简单：", "本周累计总量：", "本月累计总量：", "日均：", "评价：", "距有果效：", "本周有果效总目标：", "本周目标剩余：" })
            {
                int start = report.Text.IndexOf(prefix, StringComparison.Ordinal); if (start < 0) continue;
                int end = report.Text.IndexOf('\n', start); if (end < 0) end = report.Text.Length;
                report.Select(start, end - start);
                bool highlighted = prefix == "本周累计总量：" || prefix == "本月累计总量：" || prefix == "日均：" || prefix == "评价：";
                using (var font = new Font(Font.FontFamily, highlighted ? 14 : 12, FontStyle.Bold)) report.SelectionFont = font;
                if (highlighted) report.SelectionColor = Color.FromArgb(18, 113, 99);
            }
            report.Select(0, 0); report.ScrollToCaret();
        }
        private void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (Exception ex) { MessageBox.Show(this, "暂时无法复制，请重试。\n" + ex.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    public static class Program
    {
        private static void PreparePreview(Control control)
        {
            IntPtr handle = control.Handle;
            foreach (Control child in control.Controls) PreparePreview(child);
            control.PerformLayout();
        }
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : ".");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--photo-self-test") return PhotoTests.Run(args[1]);
            if (args.Length > 0 && args[0] == "--settings-self-test") return SettingsTests.Run(args[1]);
            if (args.Length > 0 && args[0] == "--settlement-self-test") return SettlementTests.Run(args[1]);
            if (args.Length > 0 && args[0] == "--ui-self-test") return SelfTests.RunUi(args[1]);
            if (args.Length > 0 && args[0] == "--render-preview")
            {
                string directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
                var db = new Database();
                RecordStore.Put(db, DateTime.Today.AddDays(-2), 8, 12, 15);
                RecordStore.Put(db, DateTime.Today.AddDays(-1), 6, 14, 20);
                RecordStore.Put(db, DateTime.Today, 10, 15, 24);
                using (var window = new MainWindow(new RecordStore(Path.Combine(directory, "preview-only.json")), db, true))
                {
                    PreparePreview(window);
                    using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save(Path.Combine(directory, "界面预览.png")); }
                }
                using (var dialog = new SettlementWindow(Calculator.Calculate(db, DateTime.Today, false), false))
                {
                    PreparePreview(dialog);
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(directory, "结算预览.png")); }
                }
                return 0;
            }
            bool first;
            using (var mutex = new Mutex(true, "Local\\MuYuDailyTracker_v1", out first))
            {
                if (!first) { MessageBox.Show("软件已经打开，请在任务栏切换到“每日果效记录”。", "每日果效记录"); return 0; }
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuYuDailyTracker", "records.json");
                try
                {
                    var store = new RecordStore(path);
                    using (var main = new MainWindow(store, store.Load(), false)) Application.Run(main);
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法打开软件，原数据不会被覆盖。\n\n" + ex.Message + "\n\n数据位置：" + path
                        + "\n如主文件损坏，请保留原文件，然后将 records.json.bak 的副本恢复为 records.json。", "每日果效记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public static class SelfTests
    {
        private static int passed;
        private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; }
        private static object Field(object instance, string name)
        {
            return instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(instance);
        }
        private static object Invoke(object instance, string name)
        {
            return instance.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(instance, null);
        }
        public static int RunUi(string directory)
        {
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string report = Path.Combine(directory, "ui-test-results.txt");
            try
            {
                DateTime today = DateTime.Today;
                var db = new Database();
                RecordStore.Put(db, today.AddDays(-1), 3, 4, 5);
                RecordStore.Put(db, today, 10, 15, 24);
                var store = new RecordStore(Path.Combine(directory, "ui-records-" + Guid.NewGuid().ToString("N") + ".json"));
                store.Save(db);
                using (var form = new MainWindow(store, db, true))
                {
                    var complex = (QuantityBox)Field(form, "complex");
                    var regular = (QuantityBox)Field(form, "regular");
                    var simple = (QuantityBox)Field(form, "simple");
                    var date = (DateTimePicker)Field(form, "editDate");
                    var grid = (DataGridView)Field(form, "history");
                    Check(complex.Value == 10 && regular.Value == 15 && simple.Value == 24, "UI restores saved numbers");
                    Check(grid.Rows.Cast<DataGridViewRow>().Count(row => !(row.Tag as string).StartsWith("month:")) == 2, "History populated with month grouping");
                    string currentMonth = today.ToString("yyyy-MM");
                    Check(grid.Rows.Cast<DataGridViewRow>().Any(row => row.Tag as string == "month:" + currentMonth), "Current month has collapsible header");
                    typeof(MainWindow).GetMethod("ToggleMonth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { currentMonth });
                    Check(grid.Rows.Cast<DataGridViewRow>().Count(row => (row.Tag as string) != null && (row.Tag as string).StartsWith(currentMonth)) == 0
                        && store.Load().CollapsedMonths.Contains(currentMonth), "Month collapse hides days and persists");
                    typeof(MainWindow).GetMethod("ToggleMonth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { currentMonth });
                    Check(grid.Rows.Cast<DataGridViewRow>().Count(row => (row.Tag as string) != null && (row.Tag as string).StartsWith(currentMonth)) == 2
                        && !store.Load().CollapsedMonths.Contains(currentMonth), "Month expands and persists");
                    complex.Text = "19";
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < 750) { Application.DoEvents(); Thread.Sleep(15); }
                    Check(RecordStore.Find(store.Load(), today).Complex == 19, "Autosave timer writes without manual save");
                    complex.Text = "";
                    Check(!(bool)Invoke(form, "SaveNow") && RecordStore.Find(store.Load(), today).Complex == 19, "Blank input preserves prior saved value");
                    complex.Text = "1.5";
                    Check(!(bool)Invoke(form, "SaveNow"), "Fractional quantity rejected");
                    complex.Text = "1000001";
                    Check(!(bool)Invoke(form, "SaveNow"), "Oversized quantity rejected");
                    complex.Text = "12"; regular.Text = "16"; simple.Text = "30";
                    Check((bool)Invoke(form, "SaveNow"), "Typed values save");
                    var saved = RecordStore.Find(store.Load(), today);
                    Check(saved.Complex == 12 && saved.Regular == 16 && saved.Simple == 30, "Typed text persists exactly");
                    complex.Value = 14;
                    date.Value = today.AddDays(-1);
                    Check(RecordStore.Find(store.Load(), today).Complex == 14, "Date switch flushes pending input");
                    Check(complex.Value == 3 && regular.Value == 4 && simple.Value == 5, "Historical date restores its own numbers");
                    simple.Text = "20"; Invoke(form, "SaveNow");
                    Check(RecordStore.Find(store.Load(), today.AddDays(-1)).Simple == 20, "Historical edit saved");
                    date.Value = today.AddDays(-5);
                    Check(RecordStore.Find(db, today.AddDays(-5)) == null, "Browsing empty date creates no record");
                    complex.Text = "2"; Invoke(form, "SaveNow");
                    Check(RecordStore.Find(store.Load(), today.AddDays(-5)).Complex == 2, "Backfilled date saved");
                    date.Value = today;
                    Check(complex.Value == 14 && regular.Value == 16 && simple.Value == 30, "Return to today preserves changes");
                    simple.Text = "41";
                    typeof(MainWindow).GetField("observedToday", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(form, today.AddDays(-1));
                    Invoke(form, "CheckNewDay");
                    Check(RecordStore.Find(store.Load(), today).Simple == 41, "Rollover check flushes pending input");
                    Check(date.Value.Date == today && grid.Rows.Cast<DataGridViewRow>().Count(row => !(row.Tag as string).StartsWith("month:")) == 3, "Rollover selects today without duplicates");
                }
                using (var reopened = new MainWindow(store, store.Load(), true))
                    Check(((QuantityBox)Field(reopened, "simple")).Value == 41, "Closing and reopening restores data");
                var fresh = new RecordStore(Path.Combine(directory, "first-run-" + Guid.NewGuid().ToString("N") + ".json"));
                using (var firstRun = new MainWindow(fresh, fresh.Load(), false))
                    Check(Calculator.Equivalent(RecordStore.Find(fresh.Load(), today), db.Settings) == 0, "First launch persists today's zero record");
                var previous = new Database(); RecordStore.Put(previous, today.AddDays(-1), 11, 22, 33);
                fresh.Save(previous);
                using (var newDay = new MainWindow(fresh, fresh.Load(), false))
                    Check(RecordStore.Find(fresh.Load(), today.AddDays(-1)).Simple == 33 && Calculator.Equivalent(RecordStore.Find(fresh.Load(), today), db.Settings) == 0,
                        "Next launch creates today while retaining yesterday");
                File.WriteAllText(report, "PASS: " + passed + " UI checks\r\n" + DateTime.Now.ToString("o"), Encoding.UTF8);
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString(), Encoding.UTF8); return 1; }
        }
        public static int Run(string directory)
        {
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string report = Path.Combine(directory, "test-results.txt");
            try
            {
                DateTime end = new DateTime(2026, 9, 25);
                var db = new Database();
                RecordStore.Put(db, new DateTime(2026, 8, 31), 999, 999, 999);
                RecordStore.Put(db, new DateTime(2026, 9, 1), 2, 3, 4);
                RecordStore.Put(db, new DateTime(2026, 9, 20), 1, 2, 3);
                RecordStore.Put(db, new DateTime(2026, 9, 21), 10, 20, 30);
                RecordStore.Put(db, end, 5, 10, 15);
                RecordStore.Put(db, end.AddDays(1), 999, 999, 999);
                var week = Calculator.Calculate(db, end, false);
                Check(week.Start == new DateTime(2026, 9, 21) && week.End == end, "Monday-to-Friday inclusive");
                Check(week.CalendarDays == 5 && week.RestDays == 0 && week.EffectiveDays == 5, "Partial week uses actually elapsed days");
                Check(week.Complex == 15 && week.Regular == 30 && week.Simple == 45, "Weekly totals and exclusions");
                Check(week.Equivalent == 171m && week.Average == 34.2m && week.Rating == "有果效", "Weighted weekly average");
                var month = Calculator.Calculate(db, end, true);
                Check(month.Start == new DateTime(2026, 9, 1) && month.EffectiveDays == 22, "Monthly range and completed weekly deductions");
                Check(month.Complex == 18 && month.Regular == 35 && month.Simple == 52, "Monthly totals inclusive");
                Check(month.Equivalent == 201.8m && month.Average == 201.8m / 22m, "Monthly weighted average");
                Check(month.RecordedDays == 4 && month.CalendarDays == 25, "Missing dates retain denominator");
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 21), false).EffectiveDays == 1, "Monday uses one elapsed day without early deduction");
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 5), true).EffectiveDays == 5, "Partial month waits for Sunday deduction");
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 1), true).EffectiveDays == 1, "First day remains one effective day");
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 6), true).EffectiveDays == 5, "Sixth day denominator");
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 27), false).EffectiveDays == 6, "Full week six days");
                Check(Calculator.Calculate(db, new DateTime(2027, 1, 1), false).Start == new DateTime(2026, 12, 28), "Week crosses year boundary");
                Check(Calculator.Calculate(db, new DateTime(2024, 2, 29), true).EffectiveDays == 25, "Leap February and Sunday month attribution");
                Check(Calculator.Rate(28m) == "有果效" && Calculator.Rate(100m) == "有果效", "Good threshold");
                Check(Calculator.Rate(23m) == "一般果效" && Calculator.Rate(27.999m) == "一般果效", "Normal fractional threshold");
                Check(Calculator.Rate(18m) == "有点果效" && Calculator.Rate(22.999m) == "有点果效", "Some fractional threshold");
                Check(Calculator.Rate(17.999m) == "没果效" && Calculator.Rate(0m) == "没果效", "Low fractional threshold");
                RecordStore.Put(db, end, 7, 8, 9);
                Check(db.Records.Count(r => r.Date == "2026-09-25") == 1 && RecordStore.Find(db, end).Complex == 7, "Edit replaces, never duplicates");
                Check(RecordStore.EnsureDay(db, end.AddDays(2)), "New day creates record");
                Check(!RecordStore.EnsureDay(db, end.AddDays(2)) && Calculator.Equivalent(RecordStore.Find(db, end.AddDays(2)), db.Settings) == 0, "Reopen preserves zero record");
                Check(RecordStore.Find(db, end).Complex == 7, "Rollover preserves prior quantities");
                var store = new RecordStore(Path.Combine(directory, "test-records-" + Guid.NewGuid().ToString("N") + ".json"));
                store.Save(db);
                var loaded = store.Load();
                Check(loaded.Records.Count == db.Records.Count && RecordStore.Find(loaded, end).Regular == 8, "Persistent JSON round trip");
                RecordStore.Put(db, end, 11, 12, 13); store.Save(db);
                Check(RecordStore.Find(store.Load(), end).Complex == 11, "Atomic update persists");
                Check(File.Exists(store.FilePath + ".bak") && RecordStore.Find(new RecordStore(store.FilePath + ".bak").Load(), end).Complex == 7, "Backup preserves previous save");
                store.Save(db); Check(store.Load().Records.Count == db.Records.Count, "Repeated replacement with backup");
                string csv = RecordStore.Csv(db);
                Check(csv.StartsWith("日期,复杂数量") && csv.Contains("2026-09-25,11,12,13,91.4"), "CSV consistent with weighted data");
                File.WriteAllText(store.FilePath, "{broken");
                bool rejected = false; try { store.Load(); } catch { rejected = true; }
                Check(rejected && File.ReadAllText(store.FilePath) == "{broken", "Corrupt file rejected without overwrite");
                var invalid = new Database(); RecordStore.Put(invalid, end, -1, 0, 0);
                rejected = false; try { RecordStore.Validate(invalid); } catch { rejected = true; }
                Check(rejected, "Negative quantities rejected");
                invalid.Records[0].Complex = 0; invalid.Records.Add(invalid.Records[0]);
                rejected = false; try { RecordStore.Validate(invalid); } catch { rejected = true; }
                Check(rejected, "Duplicate date rejected");
                File.WriteAllText(report, "PASS: " + passed + " checks\r\n" + DateTime.Now.ToString("o"), Encoding.UTF8);
                Console.WriteLine("PASS: " + passed + " checks"); return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString(), Encoding.UTF8); Console.Error.WriteLine(ex); return 1; }
        }
    }
}
