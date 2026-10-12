using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace MuYu.DailyTracker
{
    public static class SettlementTests
    {
        private static int passed;
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; }
        private static object Field(object obj, string name) { return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj); }
        private static void Call(object obj, string name) { obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, null); }
        private static void Prepare(Control control) { IntPtr handle = control.Handle; foreach (Control child in control.Controls) Prepare(child); control.PerformLayout(); }
        private static void Capture(Form form, string path)
        {
            form.Show(); Application.DoEvents(); Prepare(form); Application.DoEvents();
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path); }
            form.Hide();
        }
        public static int Run(string directory)
        {
            Directory.CreateDirectory(directory);
            string report = Path.Combine(directory, "settlement-test-results.txt");
            try
            {
                var db = new Database(); db.Settings.GoodMinimum = 26.5m; db.Settings.NormalMinimum = 21.5m; db.Settings.SomeMinimum = 16;
                var sunday = new DateTime(2026, 9, 27);
                RecordStore.Put(db, sunday, 15, 38, 29);
                db.WeeklyDeductions["2026-09-21"] = 0.92m;
                var week = Calculator.Calculate(db, sunday, false);
                Check(week.RestDays == 0.92m && week.EffectiveDays == 6.08m && week.Equivalent == 166.2m, "Fractional weekly deduction remains exact");
                Check(week.Average == 166.2m / 6.08m && week.Shortfall == 0 && week.Target == 161.12m
                    && week.FullWeekTarget == 161.12m && week.FullWeekShortfall == 0, "Screenshot counts and custom target calculate correctly");
                db.WeeklyDeductions["2026-08-31"] = 0.92m; db.WeeklyDeductions["2026-09-07"] = 3m;
                db.WeeklyDeductions["2026-09-14"] = 0.25m; db.WeeklyDeductions["2026-09-28"] = 2.1m;
                var month = Calculator.Calculate(db, new DateTime(2026, 9, 30), true);
                Check(month.RestDays == 5.09m && month.EffectiveDays == 24.91m, "Monthly deduction sums actual weekly values without prorating");
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 5), true).RestDays == 0m, "Weekly deduction waits until Sunday");
                Check(Calculator.Calculate(db, new DateTime(2026, 10, 1), true).RestDays == 0m, "Cross-month deduction waits for its Sunday");
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 30), false).RestDays == 0m, "Current partial week has no early deduction");
                Check(month.Shortfall == 493.915m && SettlementWindow.ReportText(month, true).Contains("至少还需 494"), "Exact deficit and whole-simple ceiling");
                db.WeeklyDeductions["2026-09-21"] = 3;
                Check(Calculator.Calculate(db, sunday, false).EffectiveDays == 4, "Three-day manual deduction supported");
                db.WeeklyDeductions["2026-09-21"] = 0.92m;
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 21), false).EffectiveDays == 1m
                    && Calculator.Calculate(db, new DateTime(2026, 9, 21), false).Average.HasValue, "Monday average uses one actually elapsed day");
                var currentMonday = new Database(); currentMonday.Settings.GoodMinimum = 26.5m;
                currentMonday.WeeklyDeductions["2026-09-28"] = 0.71m;
                RecordStore.Put(currentMonday, new DateTime(2026, 9, 28), 0, 10, 18);
                var currentResult = Calculator.Calculate(currentMonday, new DateTime(2026, 9, 28), false);
                Check(currentResult.Equivalent == 32m && currentResult.RestDays == 0m && currentResult.EffectiveDays == 1m
                    && currentResult.Average == 32m, "User case shows 32 total and 32 daily average on Monday");
                Check(currentResult.FullWeekEffectiveDays == 6.29m && currentResult.FullWeekTarget == 166.685m
                    && currentResult.FullWeekShortfall == 134.685m, "Whole-week good-performance remainder uses weekly deduction and current total");
                string currentReport = SettlementWindow.ReportText(currentResult, false);
                Check(currentReport.Contains("距有果效：已达标，还差 0") && currentReport.Contains("本周目标剩余：还差 134.685")
                    && currentReport.Contains("至少还需 135"), "Existing elapsed target remains while whole-week remainder is added");
                Check(!SettlementWindow.ReportText(month, true).Contains("本周目标剩余") && !month.FullWeekTarget.HasValue,
                    "Monthly settlement remains unchanged by weekly remainder");
                db.WeeklyDeductions["2026-09-21"] = 7;
                Check(Calculator.Calculate(db, sunday, false).Shortfall == null, "Zero effective time has no misleading target");
                db.WeeklyDeductions["2026-09-21"] = 0.92m;
                string root = Path.Combine(directory, "settlement-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                var store = new RecordStore(Path.Combine(root, "records.json")); store.Save(db);
                var reloaded = store.Load();
                Check(reloaded.WeeklyDeductions["2026-09-21"] == 0.92m && reloaded.Settings.GoodMinimum == 26.5m, "Decimal inputs and custom thresholds persist");
                File.WriteAllText(store.FilePath, "{\"Version\":1,\"Records\":[],\"Settings\":{\"WeeklyRestDays\":1,\"MonthlyRestDays\":3,\"GoodMinimum\":26.5,\"NormalMinimum\":21.5,\"SomeMinimum\":16}}");
                var legacy = store.Load();
                Check(legacy.Settings.GoodMinimum == 26.5m && Calculator.Calculate(legacy, sunday, true).RestDays == 4m,
                    "Legacy fixed monthly three days ignored while custom thresholds survive");
                var year = new Database(); year.WeeklyDeductions["2026-12-28"] = 0.92m;
                Check(Calculator.Calculate(year, new DateTime(2026, 12, 31), true).RestDays == 4m
                    && Calculator.Calculate(year, new DateTime(2027, 1, 3), true).RestDays == 0.92m, "Cross-year week belongs to January only");
                foreach (bool monthly in new[] { false, true })
                using (var window = new SettlementWindow(monthly ? month : week, monthly))
                {
                    var box = (RichTextBox)Field(window, "report");
                    Check(box.Text.Contains("距有果效") && box.Text.Contains((monthly ? "本月累计总量：" : "本周累计总量：") + "166.2 个简单（折合）") && box.Text.Contains("日均：" + (monthly ? month : week).Average.Value.ToString("0.00") + " 个简单 / 天") && !box.Text.Contains("折合简单总量")
                        && !box.Text.Contains("有效时间：") && !box.Text.Contains("有果效：≥"), "Default summary hides non-selected explanation");
                    Capture(window, Path.Combine(directory, monthly ? "月结算_简洁.png" : "周结算_简洁.png"));
                    var button = (Button)window.Controls.Find("detailsButton", true)[0];
                    typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(button, new object[] { EventArgs.Empty });
                    Check(box.Text.Contains("折合简单总量") && box.Text.Contains("日均：") && box.Text.Contains("有效时间：") && box.Text.Contains("有果效：≥26.5") && button.Text == "收起详情", "Details click restores full calculation");
                    Capture(window, Path.Combine(directory, monthly ? "月结算_详情.png" : "周结算_详情.png"));
                    typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(button, new object[] { EventArgs.Empty });
                    Check(!box.Text.Contains("折合简单总量") && button.Text == "查看详情", "Details collapse restores summary");
                }
                Dictionary<string, decimal> saved = null;
                using (var editor = new WeeklyDeductionsWindow(db, sunday, values => saved = values))
                {
                    var grid = (DataGridView)Field(editor, "grid");
                    DataGridViewRow aug31 = grid.Rows.Cast<DataGridViewRow>().First(row => Convert.ToString(row.Tag) == "2026-08-31");
                    DataGridViewRow sep07 = grid.Rows.Cast<DataGridViewRow>().First(row => Convert.ToString(row.Tag) == "2026-09-07");
                    DataGridViewRow sep14 = grid.Rows.Cast<DataGridViewRow>().First(row => Convert.ToString(row.Tag) == "2026-09-14");
                    aug31.Cells[1].Value = "0.92"; sep07.Cells[1].Value = "3"; sep14.Cells[1].Value = "0.99";
                    Capture(editor, Path.Combine(directory, "每周扣减_输入.png"));
                    Call(editor, "SaveValues");
                    Check(saved != null && saved["2026-09-07"] == 3 && saved["2026-09-14"] == 0.99m
                        && db.WeeklyDeductions["2026-09-14"] == 0.25m, "Editor saves decimals without mutating input database");
                }
                foreach (string bad in new[] { "", "-1", "7.01", "0.921", "abc", "1,2" })
                using (var editor = new WeeklyDeductionsWindow(db, sunday, values => { throw new Exception("Should not save"); }))
                {
                    var grid = (DataGridView)Field(editor, "grid");
                    grid.Rows.Cast<DataGridViewRow>().First(row => !(Convert.ToString(row.Tag)).StartsWith("month:")).Cells[1].Value = bad;
                    Call(editor, "SaveValues");
                    Check(editor.DialogResult != DialogResult.OK && ((Label)Field(editor, "error")).Text.Contains("请输入"), "Invalid manual input rejected: " + bad);
                }
                using (var editor = new WeeklyDeductionsWindow(db, sunday, values => { throw new IOException("test write failure"); }))
                {
                    Call(editor, "SaveValues");
                    Check(editor.DialogResult != DialogResult.OK && ((Label)Field(editor, "error")).Text.Contains("test write failure"), "Save error keeps editor open");
                }
                List<string> folded = null;
                using (var editor = new WeeklyDeductionsWindow(db, sunday, values => { }, months => { folded = months; db.CollapsedDeductionMonths = months; }))
                {
                    var grid = (DataGridView)Field(editor, "grid");
                    typeof(WeeklyDeductionsWindow).GetMethod("ToggleMonth", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { "2026-09" });
                    Check(folded != null && folded.Contains("2026-09")
                        && grid.Rows.Cast<DataGridViewRow>().Where(row => Convert.ToString(row.Cells[0].Tag) == "2026-09").All(row => !row.Visible),
                        "Weekly deductions month collapses and persists independently");
                }
                var below = new Database(); below.Settings.GoodMinimum = 26.5m; below.Settings.ComplexFactor = 1.99m;
                RecordStore.Put(below, sunday, 1, 0, 157);
                var rounded = Calculator.Calculate(below, sunday, false);
                Check(rounded.Average.Value.ToString("0.00") == "26.50" && rounded.Rating != "有果效" && rounded.Shortfall == 0.01m,
                    "Rounded display never hides positive shortfall or changes rating");
                RecordStore.Put(below, sunday, 0, 0, 159);
                Check(Calculator.Calculate(below, sunday, false).Shortfall == 0 && Calculator.Calculate(below, sunday, false).Rating == "有果效", "Exact threshold counts as reached");
                File.WriteAllText(report, "PASS: " + passed + " settlement checks\r\n" + DateTime.Now.ToString("o"), Encoding.UTF8); return 0;
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString(), Encoding.UTF8); return 1; }
        }
    }
}
