using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MuYu.DailyTracker
{
    public sealed class WeeklyDeductionsWindow : Form
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly Label error = new Label();
        private readonly Database db;
        private readonly Action<Dictionary<string, decimal>> save;
        private readonly Action<List<string>> saveCollapsed;
        public WeeklyDeductionsWindow(Database db, DateTime cutoff, Action<Dictionary<string, decimal>> save)
            : this(db, cutoff, save, null) { }
        public WeeklyDeductionsWindow(Database db, DateTime cutoff, Action<Dictionary<string, decimal>> save, Action<List<string>> saveCollapsed)
        {
            this.db = db; this.save = save; this.saveCollapsed = saveCollapsed;
            Text = "每周扣减 · " + cutoff.ToString("yyyy年MM月");
            Font = new Font("Microsoft YaHei UI", 10); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(850, 500); MinimumSize = new Size(780, 480); StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); Controls.Add(layout);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "双击月份可折叠/展开；点击“扣减时间”直接输入，支持 0.92、1、3（0～7，最多两位小数）。\n每周默认扣 1 天（可在设置中修改）；本周到周日结算时才扣减。\n月结算只累计已经到周日的周；跨月的一周整笔归周日所在月。" }, 0, 0);
            grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.BackgroundColor = Color.White;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "周一至周日", ReadOnly = true, FillWeight = 200 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "扣减时间（天）", Name = "deduction", FillWeight = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "扣减归属月份", ReadOnly = true, FillWeight = 100 });
            PopulateWeeks(cutoff);
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs args)
            {
                if (args.RowIndex < 0) return;
                string tag = grid.Rows[args.RowIndex].Tag as string;
                if (tag != null && tag.StartsWith("month:", StringComparison.Ordinal)) ToggleMonth(tag.Substring(6));
            };
            layout.Controls.Add(grid, 0, 1); error.Dock = DockStyle.Fill; error.ForeColor = Color.Firebrick; layout.Controls.Add(error, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var confirm = new Button { Text = "保存扣减", Width = 120, Height = 36 };
            confirm.Click += delegate { SaveValues(); };
            var cancel = new Button { Text = "取消", Width = 100, Height = 36, DialogResult = DialogResult.Cancel };
            actions.Controls.Add(confirm); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 3); CancelButton = cancel;
        }
        private void PopulateWeeks(DateTime cutoff)
        {
            DateTime firstMonth = new DateTime(cutoff.Year, cutoff.Month, 1);
            foreach (var record in db.Records)
            {
                DateTime day = Calculator.Parse(record.Date);
                DateTime month = new DateTime(day.Year, day.Month, 1);
                if (month < firstMonth) firstMonth = month;
            }
            foreach (string key in db.WeeklyDeductions.Keys)
            {
                DateTime sunday = Calculator.Parse(key).AddDays(6);
                DateTime month = new DateTime(sunday.Year, sunday.Month, 1);
                if (month < firstMonth) firstMonth = month;
            }
            DateTime last = new DateTime(cutoff.Year, cutoff.Month, 1).AddMonths(1).AddDays(-1);
            var mondays = new List<DateTime>();
            for (DateTime monday = Calculator.WeekStart(firstMonth); monday <= last; monday = monday.AddDays(7)) mondays.Add(monday);
            foreach (var group in mondays.GroupBy(monday => monday.AddDays(6).ToString("yyyy-MM", CultureInfo.InvariantCulture)))
            {
                string month = group.Key;
                bool collapsed = db.CollapsedDeductionMonths.Contains(month);
                int header = grid.Rows.Add((collapsed ? "▶ " : "▼ ") + month.Replace("-", "年") + "月（" + group.Count() + " 周）", "", "");
                grid.Rows[header].Tag = "month:" + month; grid.Rows[header].Height = 38; grid.Rows[header].ReadOnly = true;
                grid.Rows[header].DefaultCellStyle.BackColor = Color.FromArgb(218, 232, 227);
                grid.Rows[header].DefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
                foreach (DateTime monday in group)
                {
                    DateTime sunday = monday.AddDays(6);
                    int row = grid.Rows.Add("    " + monday.ToString("yyyy-MM-dd") + " 至 " + sunday.ToString("yyyy-MM-dd"),
                        Calculator.WeeklyDeduction(db, monday).ToString("0.##", CultureInfo.InvariantCulture), sunday.ToString("yyyy年MM月"));
                    grid.Rows[row].Tag = Calculator.Key(monday); grid.Rows[row].Cells[0].Tag = month;
                    grid.Rows[row].Height = 36; grid.Rows[row].Visible = !collapsed;
                }
            }
        }
        private void ToggleMonth(string month)
        {
            grid.EndEdit();
            bool wasCollapsed = db.CollapsedDeductionMonths.Contains(month);
            var candidate = new List<string>(db.CollapsedDeductionMonths);
            if (wasCollapsed) candidate.Remove(month); else candidate.Add(month);
            try
            {
                if (saveCollapsed == null) db.CollapsedDeductionMonths = candidate;
                else saveCollapsed(candidate);
                grid.ClearSelection(); grid.CurrentCell = null;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    string tag = row.Tag as string;
                    if (tag == "month:" + month) row.Cells[0].Value = (wasCollapsed ? "▼ " : "▶ ") + month.Replace("-", "年") + "月（" + CountWeeks(month) + " 周）";
                    else if (Convert.ToString(row.Cells[0].Tag) == month) row.Visible = wasCollapsed;
                }
                error.Text = month.Replace("-", "年") + "月已" + (wasCollapsed ? "展开" : "折叠") + "，状态已保存。";
                error.ForeColor = Color.DimGray;
            }
            catch (Exception ex)
            {
                error.ForeColor = Color.Firebrick; error.Text = "折叠状态未保存：" + ex.Message;
            }
        }
        private int CountWeeks(string month)
        {
            int count = 0;
            foreach (DataGridViewRow row in grid.Rows) if (Convert.ToString(row.Cells[0].Tag) == month) count++;
            return count;
        }
        private void SaveValues()
        {
            try
            {
                grid.EndEdit();
                var values = new Dictionary<string, decimal>(db.WeeklyDeductions);
                foreach (DataGridViewRow row in grid.Rows)
                {
                    string key = row.Tag as string;
                    if (key == null || key.StartsWith("month:", StringComparison.Ordinal)) continue;
                    decimal value;
                    if (!decimal.TryParse(Convert.ToString(row.Cells[1].Value), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out value) || value < 0 || value > 7 || decimal.Round(value, 2) != value)
                        throw new InvalidDataException(Convert.ToString(row.Cells[0].Value) + "：请输入 0～7 天，最多两位小数，例如 0.92。");
                    // Only changed values become overrides; untouched defaults keep following settings.
                    if (value != Calculator.WeeklyDeduction(db, Calculator.Parse(key))) values[key] = value;
                }
                save(values); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { error.Text = "未保存：" + ex.Message; }
        }
    }

    public sealed class TrackerSettings
    {
        public decimal ComplexFactor { get; set; }
        public decimal RegularFactor { get; set; }
        public decimal WeeklyRestDays { get; set; }
        public decimal GoodMinimum { get; set; }
        public decimal NormalMinimum { get; set; }
        public decimal SomeMinimum { get; set; }
        public TrackerSettings()
        {
            ComplexFactor = 5.6m; RegularFactor = 1.4m;
            WeeklyRestDays = 1;
            GoodMinimum = 28m; NormalMinimum = 23m; SomeMinimum = 18m;
        }
        public TrackerSettings Copy() { return (TrackerSettings)MemberwiseClone(); }
        public void Validate()
        {
            if (ComplexFactor <= 0 || ComplexFactor > 1000 || RegularFactor <= 0 || RegularFactor > 1000)
                throw new InvalidDataException("换算系数必须大于 0，且不超过 1000。");
            if (WeeklyRestDays < 0 || WeeklyRestDays > 7 || decimal.Round(WeeklyRestDays, 2) != WeeklyRestDays)
                throw new InvalidDataException("每周默认扣减时间须为 0～7 天，最多两位小数。");
            if (SomeMinimum < 0 || GoodMinimum > 1000000 || GoodMinimum <= NormalMinimum || NormalMinimum <= SomeMinimum)
                throw new InvalidDataException("日均分界值须满足：有果效 > 一般果效 > 有点果效 ≥ 0，最高不超过 1000000。");
            foreach (decimal value in new[] { ComplexFactor, RegularFactor, GoodMinimum, NormalMinimum, SomeMinimum })
                if (decimal.Round(value, 2) != value) throw new InvalidDataException("换算系数与日均分界值最多保留两位小数。");
        }
        public string ThresholdSummary()
        {
            return "≥" + GoodMinimum.ToString("0.##") + " 有果效  ·  ≥" + NormalMinimum.ToString("0.##")
                + " 一般果效  ·  ≥" + SomeMinimum.ToString("0.##") + " 有点果效  ·  <" + SomeMinimum.ToString("0.##") + " 没果效";
        }
        public string ThresholdDetails()
        {
            return "有果效：≥" + GoodMinimum.ToString("0.##") + " /天\r\n一般果效：≥" + NormalMinimum.ToString("0.##")
                + " 且 <" + GoodMinimum.ToString("0.##") + " /天\r\n有点果效：≥" + SomeMinimum.ToString("0.##")
                + " 且 <" + NormalMinimum.ToString("0.##") + " /天\r\n没果效：<" + SomeMinimum.ToString("0.##") + " /天";
        }
    }

    public sealed class SettingsWindow : Form
    {
        private readonly NumericUpDown complex, regular, weekly, good, normal, some;
        private readonly Label ranges = new Label(), error = new Label();
        private readonly Action<TrackerSettings> save;
        public SettingsWindow(TrackerSettings initial, Action<TrackerSettings> save)
        {
            this.save = save;
            Text = "设置 · 换算与结算规则"; Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(243, 247, 246); ClientSize = new Size(720, 700);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 2, RowCount = 13 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);
            var heading = new Label { Text = "自定义结算规则", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Dock = DockStyle.Fill };
            layout.Controls.Add(heading, 0, 0); layout.SetColumnSpan(heading, 2);
            complex = AddInput(layout, 1, "1 个复杂折合几个简单", 0.01m, 1000, 2);
            regular = AddInput(layout, 2, "1 个常规（中等）折合几个简单", 0.01m, 1000, 2);
            weekly = AddInput(layout, 3, "每周默认扣减时间（天）", 0, 7, 2);
            var monthlyHelp = new Label { Text = "每周扣减到周日才计入；每月累计已到周日的各周输入。\n跨月周归周日所在月；主界面可逐周填写，例如 0.92 天。", Dock = DockStyle.Fill, ForeColor = Color.DimGray };
            layout.Controls.Add(monthlyHelp, 0, 4); layout.SetColumnSpan(monthlyHelp, 2);
            good = AddInput(layout, 5, "有果效：日均至少达到", 0, 1000000, 2);
            normal = AddInput(layout, 6, "一般果效：日均至少达到", 0, 1000000, 2);
            some = AddInput(layout, 7, "有点果效：日均至少达到", 0, 1000000, 2);
            ranges.Dock = DockStyle.Fill; ranges.ForeColor = Color.FromArgb(18, 113, 99); ranges.Padding = new Padding(0, 8, 0, 0);
            layout.Controls.Add(ranges, 0, 8); layout.SetColumnSpan(ranges, 2);
            var help = new Label { Text = "简单固定为 1；已单独填写的周不受默认扣减值修改影响。\n保存后，历史数量保持原值，结算按新规则计算。\n有效时间大于 0 即可评价；等于 0 时暂不能评价。", Dock = DockStyle.Fill, ForeColor = Color.DimGray };
            layout.Controls.Add(help, 0, 9); layout.SetColumnSpan(help, 2);
            error.Dock = DockStyle.Fill; error.ForeColor = Color.Firebrick; layout.Controls.Add(error, 0, 10); layout.SetColumnSpan(error, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var confirm = MakeButton("保存设置", true); confirm.Click += delegate { SaveSettings(); };
            var cancel = MakeButton("取消", false); cancel.DialogResult = DialogResult.Cancel;
            var reset = MakeButton("恢复默认", false); reset.Click += delegate { SetValues(new TrackerSettings()); error.Text = "已填入默认值，点击“保存设置”后生效。"; };
            buttons.Controls.Add(confirm); buttons.Controls.Add(cancel); buttons.Controls.Add(reset);
            layout.Controls.Add(buttons, 0, 11); layout.SetColumnSpan(buttons, 2);
            AcceptButton = confirm; CancelButton = cancel;
            foreach (var input in new[] { complex, regular, weekly, good, normal, some }) input.ValueChanged += delegate { UpdateRanges(); };
            SetValues(initial);
        }
        private NumericUpDown AddInput(TableLayoutPanel layout, int row, string text, decimal min, decimal max, int places)
        {
            layout.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var input = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = places, Increment = places > 0 ? 0.1m : 1m,
                Width = 180, Anchor = AnchorStyles.Left, Name = "rule" + row };
            layout.Controls.Add(input, 1, row); return input;
        }
        private Button MakeButton(string text, bool primary)
        { return new Button { Text = text, Width = 112, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(18, 113, 99) : Color.White, ForeColor = primary ? Color.White : Color.Black }; }
        private void SetValues(TrackerSettings value)
        {
            complex.Value = value.ComplexFactor; regular.Value = value.RegularFactor; weekly.Value = value.WeeklyRestDays;
            good.Value = value.GoodMinimum; normal.Value = value.NormalMinimum; some.Value = value.SomeMinimum; UpdateRanges();
        }
        private TrackerSettings ReadValues()
        {
            return new TrackerSettings { ComplexFactor = complex.Value, RegularFactor = regular.Value, WeeklyRestDays = weekly.Value,
                GoodMinimum = good.Value, NormalMinimum = normal.Value, SomeMinimum = some.Value };
        }
        private void UpdateRanges()
        {
            if (some == null) return;
            var value = ReadValues();
            try { value.Validate(); ranges.Text = value.ThresholdDetails(); error.Text = ""; }
            catch (Exception ex) { ranges.Text = "请保持分界值从高到低递减，避免评价区间重叠。"; error.Text = ex.Message; }
        }
        private void SaveSettings()
        {
            try { var value = ReadValues(); value.Validate(); save(value); DialogResult = DialogResult.OK; Close(); }
            catch (Exception ex) { error.Text = "设置未保存：" + ex.Message; }
        }
    }
}
