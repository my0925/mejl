using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace MuYu.DailyTracker
{
    public static class SettingsTests
    {
        private static int passed;
        private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; }
        private static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }
        private static object Field(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
        private static object Call(object instance, string name, params object[] args)
        { return instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, args); }
        private static void Prepare(Control control)
        { IntPtr handle = control.Handle; foreach (Control child in control.Controls) Prepare(child); control.PerformLayout(); }
        public static int Run(string directory)
        {
            directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
            string reportPath = Path.Combine(directory, "settings-test-results.txt");
            try
            {
                string root = Path.Combine(directory, "settings-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                var store = new RecordStore(Path.Combine(root, "records.json"));
                File.WriteAllText(store.FilePath, "{\"Version\":1,\"Records\":[{\"Date\":\"2026-09-25\",\"Complex\":10,\"Regular\":20,\"Simple\":30,\"Equivalent\":114}]}");
                var db = store.Load();
                Check(db.Settings.ComplexFactor == 5.6m && db.Settings.WeeklyRestDays == 1 && db.Records[0].Complex == 10, "Old data migrates with default rules and retained quantities");
                var custom = new TrackerSettings { ComplexFactor = 6.25m, RegularFactor = 2.15m, WeeklyRestDays = 2,
                    GoodMinimum = 40, NormalMinimum = 30, SomeMinimum = 20 };
                custom.Validate(); db.Settings = custom; store.Save(db); db = store.Load();
                Check(db.Settings.ComplexFactor == 6.25m && db.Settings.RegularFactor == 2.15m && db.Settings.SomeMinimum == 20, "Custom rules survive saving and reopening");
                var day = new DateTime(2026, 9, 25); var week = Calculator.Calculate(db, day, false); var month = Calculator.Calculate(db, day, true);
                Check(week.Equivalent == 135.5m && week.EffectiveDays == 5 && week.Rating == "有点果效", "Partial week waits until Sunday before deduction");
                Check(month.EffectiveDays == 19 && month.Rating == "没果效", "Completed weekly deductions drive monthly calculation");
                Check(Calculator.Rate(40, custom) == "有果效" && Calculator.Rate(39.99m, custom) == "一般果效"
                    && Calculator.Rate(30, custom) == "一般果效" && Calculator.Rate(29.99m, custom) == "有点果效"
                    && Calculator.Rate(20, custom) == "有点果效" && Calculator.Rate(19.99m, custom) == "没果效", "Custom boundaries have no gaps or overlaps");
                Check(RecordStore.Csv(db).Contains("2026-09-25,10,20,30,135.5"), "CSV uses current rules");
                string report = SettlementWindow.ReportText(week, false, true);
                Check(report.Contains("复杂 × 6.25 + 常规 × 2.15") && report.Contains("有果效：≥40") && report.Contains("当前扣减暂不计入"), "Copied settlement explains deferred weekly deduction");
                db.Settings.GoodMinimum = 99; Check(week.Rules.GoodMinimum == 40, "Open settlement retains snapshot rules");
                var invalid = custom.Copy(); invalid.NormalMinimum = invalid.GoodMinimum;
                Check(Rejects(invalid.Validate), "Overlapping rating boundaries rejected");
                invalid = custom.Copy(); invalid.ComplexFactor = 0; Check(Rejects(invalid.Validate), "Zero conversion factor rejected");
                invalid = custom.Copy(); invalid.WeeklyRestDays = -1; Check(Rejects(invalid.Validate), "Negative rest days rejected");
                invalid = custom.Copy(); invalid.WeeklyRestDays = 8; Check(Rejects(invalid.Validate), "Out-of-range rest days rejected");
                invalid = custom.Copy(); invalid.RegularFactor = 1.234m; Check(Rejects(invalid.Validate), "Unsupported precision rejected instead of silently rounding");
                db.Settings = custom.Copy(); db.Settings.WeeklyRestDays = 7;
                Check(Calculator.Calculate(db, new DateTime(2026, 9, 27), false).Average == null, "Large rest deduction never divides by zero on Sunday");
                db.Settings.WeeklyRestDays = 0; Check(Calculator.Calculate(db, day, false).EffectiveDays == 5, "Zero rest deduction supported");
                db.Settings = custom.Copy(); store.Save(db);
                using (var main = new MainWindow(store, db, true))
                {
                    Check(main.Controls.Find("settingsButton", true).Length == 1, "Settings entry in main window");
                    var dates = (DateTimePicker)Field(main, "editDate"); dates.Value = day;
                    var changed = custom.Copy(); changed.ComplexFactor = 7; changed.RegularFactor = 2;
                    Call(main, "ApplySettings", changed);
                    Check(((Label)Field(main, "total")).Text.Contains("140.0") && ((Label)Field(main, "complexHint")).Text.Contains("7"), "Rule edits refresh daily total and conversion label");
                    var grid = (DataGridView)Field(main, "history"); bool found = false;
                    foreach (DataGridViewRow row in grid.Rows) if ((string)row.Tag == "2026-09-25") found = Convert.ToString(row.Cells[4].Value) == "140.0";
                    Check(found && store.Load().Settings.ComplexFactor == 7, "Historical totals and stored settings update together");
                    Check(db.Records.Find(r => r.Date == "2026-09-25").Complex == 10, "Rule edits preserve raw historical counts");
                }
                TrackerSettings saved = null;
                using (var settings = new SettingsWindow(custom, value => saved = value))
                {
                    ((NumericUpDown)Field(settings, "complex")).Value = 8.5m;
                    Call(settings, "SaveSettings");
                    Check(saved != null && saved.ComplexFactor == 8.5m && custom.ComplexFactor == 6.25m, "Settings editor saves a new candidate without mutating input");
                }
                saved = null;
                using (var settings = new SettingsWindow(custom, value => saved = value))
                {
                    ((NumericUpDown)Field(settings, "normal")).Value = 100;
                    Call(settings, "SaveSettings");
                    Check(saved == null && settings.DialogResult != DialogResult.OK && ((Label)Field(settings, "error")).Text.Contains("未保存"), "Invalid settings stay open and never save");
                }
                using (var settings = new SettingsWindow(custom, value => { throw new IOException("test write failure"); }))
                {
                    Call(settings, "SaveSettings");
                    Check(settings.DialogResult != DialogResult.OK && ((Label)Field(settings, "error")).Text.Contains("test write failure"), "Write failure reported without closing editor");
                }
                using (var settings = new SettingsWindow(new TrackerSettings(), value => { }))
                {
                    Prepare(settings);
                    using (var bitmap = new Bitmap(settings.Width, settings.Height))
                    { settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, settings.Size)); bitmap.Save(Path.Combine(directory, "设置预览.png")); }
                }
                File.WriteAllText(reportPath, "PASS: " + passed + " settings checks\r\n" + DateTime.Now.ToString("o"), Encoding.UTF8); return 0;
            }
            catch (Exception ex) { File.WriteAllText(reportPath, ex.ToString(), Encoding.UTF8); return 1; }
        }
    }
}
