using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QingReminder.Core;
using Forms = System.Windows.Forms;

namespace QingReminder.Tests
{
    internal static class ResourceTests
    {
        private static int passed;
        private static string runDirectory;
        private static readonly List<string> report = new List<string>();

        [STAThread]
        private static int Main(string[] arguments)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            string output = Path.GetFullPath(arguments.Length > 0 ? arguments[0] : ".artifacts/resource-tests");
            double seconds = arguments.Length > 1 ? Double.Parse(arguments[1], CultureInfo.InvariantCulture) : 30;
            if (seconds < 5 || seconds > 60) throw new ArgumentOutOfRangeException("seconds");
            Directory.CreateDirectory(output);
            runDirectory = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            MainWindowController controller = null;
            try
            {
                Program.LoadTheme(app);
                var scheduler = NewScheduler("idle");
                controller = new MainWindowController(scheduler, delegate { }, false);
                app.MainWindow = controller.Window;
                controller.Control<CheckBox>("SoundToggle").IsChecked = false;
                controller.Show();
                for (int index = 0; index < 10; index++)
                    scheduler.AddCountdown("空闲资源验证 " + index, TimeSpan.FromHours(2 + index), DateTimeOffset.Now);
                PumpFor(TimeSpan.FromSeconds(2));
                Record("Environment: .NET Framework " + Environment.Version + ", processors=" + Environment.ProcessorCount
                    + ", OS=" + Environment.OSVersion + ", UTC=" + DateTime.UtcNow.ToString("O"));
                Record("Measurements are this isolated WPF test process, with 10 future alarms and sound disabled; startup is excluded.");
                Record("The test does not run Program.Main, acquire the real single-instance mutex, or read user data.");
                MeasureIdle(controller, "visible", seconds);
                controller.Window.Hide();
                Pump();
                MeasureIdle(controller, "hidden", seconds);
                controller.Show();
                Pump();
                TestHiddenScheduling();
                TestSystemEventWakeup();
                TestActivationListener();
                TestReminderSoundAndShutdown();
                TestEarlyConstructorFailure();
                TestFailedPersistence();
                TestFailedReminderDisplay();
                TestControllerDisposal();
                TestCollectability(controller, scheduler);
                Record("PASS: " + passed + " resource/lifecycle assertions.");
                Record("This short run detects retained tested objects and records resource deltas; it does not prove absence of all long-running leaks.");
                File.WriteAllLines(Path.Combine(output, "resource-report.txt"), report.ToArray());
                Console.WriteLine("Report: " + Path.Combine(output, "resource-report.txt"));
                return 0;
            }
            catch (Exception error)
            {
                Record("FAIL: " + error);
                File.WriteAllLines(Path.Combine(output, "resource-report.txt"), report.ToArray());
                return 1;
            }
            finally
            {
                foreach (ReminderWindow reminder in app.Windows.OfType<ReminderWindow>().ToArray()) reminder.CloseForShutdown();
                if (controller != null) { controller.Dispose(); controller.Window.Close(); }
                app.Shutdown();
            }
        }

        private static void TestHiddenScheduling()
        {
            var scheduler = NewScheduler("hidden");
            var controller = new MainWindowController(scheduler, delegate { }, false);
            try
            {
                controller.Control<CheckBox>("SoundToggle").IsChecked = false;
                controller.Show();
                controller.Window.Hide();
                Pump();
                DispatcherTimer timer = Field<DispatcherTimer>(controller, "timer");
                Check(!timer.IsEnabled, "a hidden controller without scheduled alarms stops its dispatcher timer");
                string clock = controller.Control<TextBlock>("ClockText").Text;
                scheduler.AddCountdown("隐藏期间一秒后提醒", TimeSpan.FromSeconds(1), DateTimeOffset.Now);
                Check(timer.IsEnabled, "adding a countdown restarts the hidden controller's timer");
                bool opened = PumpUntil(delegate { return VisibleReminders().Length == 1; }, TimeSpan.FromSeconds(5));
                Check(opened && !controller.Window.IsVisible, "a hidden one-second alarm opens its reminder without making the editor visible");
                Check(controller.Control<TextBlock>("ClockText").Text == clock, "hidden polling leaves the invisible clock unchanged");
                Click(FindButton(VisibleReminders().Single(), "完成"));
                Check(scheduler.Alarms.Count == 0 && !timer.IsEnabled,
                    "completing the last hidden alarm stops polling again");
                controller.Show();
                Pump();
                Check(timer.IsEnabled, "opening the editor resumes its clock refresh");
                controller.Window.WindowState = WindowState.Minimized;
                Pump();
                Check(!timer.IsEnabled, "a minimized editor without alarms also stops polling");
            }
            finally { controller.Dispose(); controller.Window.Close(); }
        }

        private static void TestReminderSoundAndShutdown()
        {
            var reminder = new ReminderWindow("静音计时器验证", DateTimeOffset.Now, delegate(TimeSpan delay) { }, delegate { }, delegate { });
            reminder.SoundEnabled = false;
            reminder.Show();
            Pump();
            DispatcherTimer soundTimer = Field<DispatcherTimer>(reminder, "_soundTimer");
            Check(!soundTimer.IsEnabled, "a muted reminder does not start a repeating sound timer");
            reminder.SoundEnabled = true;
            Check(soundTimer.IsEnabled, "enabling sound starts the visible reminder timer");
            reminder.SoundEnabled = false;
            Check(!soundTimer.IsEnabled, "disabling sound immediately stops the reminder timer");
            reminder.Close();
            reminder.CloseForShutdown();
            Pump();
            Check(!reminder.IsVisible && !soundTimer.IsEnabled, "shutdown cancels a pending deferred close and leaves the sound timer stopped");
        }

        private static void TestSystemEventWakeup()
        {
            var scheduler = NewScheduler("system-events");
            var controller = new MainWindowController(scheduler, delegate { }, false);
            controller.Control<CheckBox>("SoundToggle").IsChecked = false;
            try
            {
                controller.Show();
                controller.Window.Hide();
                Pump();
                DispatcherTimer timer = Field<DispatcherTimer>(controller, "timer");
                scheduler.AddCountdown("模拟时间变更后补发", TimeSpan.FromSeconds(1), DateTimeOffset.Now);
                timer.Stop();
                PumpFor(TimeSpan.FromMilliseconds(1100));
                InvokeHandler(controller, "OnSystemTimeChanged", EventArgs.Empty);
                Check(PumpUntil(delegate { return VisibleReminders().Length == 1; }, TimeSpan.FromSeconds(1)),
                    "a time-change notification immediately checks a hidden overdue alarm");
                Click(FindButton(VisibleReminders().Single(), "完成"));
                scheduler.AddCountdown("模拟恢复后补发", TimeSpan.FromSeconds(1), DateTimeOffset.Now);
                timer.Stop();
                PumpFor(TimeSpan.FromMilliseconds(1100));
                InvokeHandler(controller, "OnPowerModeChanged", new PowerModeChangedEventArgs(PowerModes.Resume));
                Check(PumpUntil(delegate { return VisibleReminders().Length == 1; }, TimeSpan.FromSeconds(1)),
                    "a resume notification immediately checks a hidden overdue alarm");
                Click(FindButton(VisibleReminders().Single(), "完成"));
                controller.Dispose();
                InvokeHandler(controller, "OnSystemTimeChanged", EventArgs.Empty);
                InvokeHandler(controller, "OnPowerModeChanged", new PowerModeChangedEventArgs(PowerModes.Resume));
                Pump();
                Check(!timer.IsEnabled && VisibleReminders().Length == 0,
                    "system notifications arriving after Dispose cannot restart polling or open reminders");
            }
            finally { controller.Dispose(); controller.Window.Close(); }
        }

        private static void TestEarlyConstructorFailure()
        {
            int initialCount = Application.Current.Windows.Count;
            bool invalidDelay = false;
            try
            {
                new ReminderWindow("非法时间验证", DateTimeOffset.Now, delegate(TimeSpan delay) { },
                    delegate { }, delegate { }, TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException) { invalidDelay = true; }
            Pump();
            Check(invalidDelay && Application.Current.Windows.Count == initialCount,
                "rejecting a zero countdown in the reminder constructor leaves no registered partial window");
            bool missingCallback = false;
            try
            {
                new ReminderWindow("非法回调验证", DateTimeOffset.Now, null, delegate { }, delegate { });
            }
            catch (ArgumentNullException) { missingCallback = true; }
            Pump();
            Check(missingCallback && Application.Current.Windows.Count == initialCount,
                "rejecting a missing reminder callback leaves no registered partial window");
        }

        private static void TestActivationListener()
        {
            int calls = 0;
            using (var request = new EventWaitHandle(false, EventResetMode.AutoReset))
            {
                var listener = new SingleInstanceActivationListener(request, Dispatcher.CurrentDispatcher, delegate { calls++; });
                request.Set();
                Check(PumpUntil(delegate { return calls == 1; }, TimeSpan.FromSeconds(2)),
                    "the single-instance listener dispatches an event request without a polling timer");
                listener.Dispose();
                listener.Dispose();
                request.Set();
                PumpFor(TimeSpan.FromMilliseconds(150));
                Check(calls == 1, "a disposed activation listener is idempotent and ignores later requests");
            }
        }

        private static void TestFailedPersistence()
        {
            string storage = Path.Combine(runDirectory, "blocked-storage");
            string savedStorage = storage + ".saved";
            string path = Path.Combine(storage, "reminders.json");
            var scheduler = new AlarmScheduler(new AlarmStore(path));
            var controller = new MainWindowController(scheduler, delegate { }, false);
            bool blocked = false;
            try
            {
                controller.Control<CheckBox>("SoundToggle").IsChecked = false;
                controller.Show();
                AlarmEntry alarm = scheduler.AddCountdown("磁盘不可写时仍提醒", TimeSpan.FromSeconds(1), DateTimeOffset.Now);
                // A regular file at the expected directory deterministically prevents writes, without touching user data.
                Directory.Move(storage, savedStorage);
                File.WriteAllText(storage, "intentional isolated test write blocker");
                blocked = true;
                bool failed = PumpUntil(delegate
                {
                    return controller.Control<TextBlock>("ErrorText").Visibility == Visibility.Visible;
                }, TimeSpan.FromSeconds(5));
                Check(failed && scheduler.Alarms.Single(a => a.Id == alarm.Id).State == AlarmState.Scheduled,
                    "a failed ringing write keeps the due alarm scheduled and shows the storage error");
                Check(VisibleReminders().Length == 0,
                    "a ringing write failure does not acknowledge or display an unpersisted reminder");
                Check(Field<DateTimeOffset>(controller, "nextPollAttempt") > DateTimeOffset.Now.AddSeconds(10),
                    "a failed write backs off retries instead of repeatedly hitting storage");
                PumpFor(TimeSpan.FromSeconds(2));
                Check(scheduler.Alarms.Single(a => a.Id == alarm.Id).State == AlarmState.Scheduled
                    && VisibleReminders().Length == 0,
                    "the due alarm remains pending throughout the write retry backoff");
                File.Delete(storage);
                Directory.Move(savedStorage, storage);
                blocked = false;
                InvokeHandler(controller, "OnSystemTimeChanged", EventArgs.Empty);
                bool recovered = PumpUntil(delegate
                {
                    return scheduler.Alarms.Single(a => a.Id == alarm.Id).State == AlarmState.Ringing;
                }, TimeSpan.FromSeconds(5));
                Check(recovered && VisibleReminders().Length == 1,
                    "restoring storage lets the ringing state persist without losing or duplicating the alert");
                Click(FindButton(VisibleReminders().Single(), "完成"));
                Check(scheduler.Alarms.Count == 0 && VisibleReminders().Length == 0,
                    "the recovered reminder can be completed and saved normally");
            }
            finally
            {
                controller.Dispose();
                controller.Window.Close();
                if (blocked) { File.Delete(storage); Directory.Move(savedStorage, storage); }
            }
        }

        private static void TestControllerDisposal()
        {
            var scheduler = NewScheduler("dispose");
            var controller = new MainWindowController(scheduler, delegate { }, false);
            controller.Control<CheckBox>("SoundToggle").IsChecked = false;
            controller.Show();
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset dueAt = now.AddMinutes(1);
            for (int index = 0; index < 3; index++) scheduler.AddAbsolute("退出生命周期 " + index, dueAt, now);
            controller.ProcessDue(scheduler.Poll(dueAt.AddMilliseconds(1)));
            Pump();
            ReminderWindow[] reminders = VisibleReminders();
            Check(reminders.Length == 3, "three alarms due together each open one distinct reminder");
            DispatcherTimer timer = Field<DispatcherTimer>(controller, "timer");
            string count = controller.Control<TextBlock>("CountText").Text;
            controller.Dispose();
            controller.Dispose();
            Pump();
            Check(!timer.IsEnabled && reminders.All(reminder => !reminder.IsVisible),
                "controller Dispose is idempotent and stops polling and all simultaneous reminders");
            scheduler.AddCountdown("Dispose 后外部变更", TimeSpan.FromMinutes(2), DateTimeOffset.Now);
            Pump();
            Check(controller.Control<TextBlock>("CountText").Text == count,
                "a disposed controller no longer responds to scheduler change events");
            controller.ProcessDue(scheduler.Alarms);
            Pump();
            Check(VisibleReminders().Length == 0, "a disposed controller ignores later due batches");
            controller.Window.Close();
        }

        private static void TestFailedReminderDisplay()
        {
            var scheduler = NewScheduler("failed-display");
            var controller = new MainWindowController(scheduler, delegate { }, false);
            ResourceDictionary resources = Application.Current.Resources;
            Type resourceKey = typeof(ReminderWindow);
            bool hadStyle = resources.Contains(resourceKey);
            object originalStyle = resources[resourceKey];
            try
            {
                controller.Control<CheckBox>("SoundToggle").IsChecked = false;
                controller.Show();
                controller.Window.Hide();
                Pump();
                DateTimeOffset now = DateTimeOffset.Now;
                for (int index = 0; index < 3; index++)
                    scheduler.AddAbsolute("显示故障后补发 " + index, now.AddSeconds(1), now);
                // An incompatible implicit style models a real WPF initialization/display error.
                // Scope the bad resource to reminder windows so the already-open editor remains healthy.
                resources[resourceKey] = new Style(typeof(Button));
                bool failed = PumpUntil(delegate
                {
                    return controller.Control<TextBlock>("ErrorText").Visibility == Visibility.Visible;
                }, TimeSpan.FromSeconds(5));
                Check(failed && scheduler.Alarms.All(alarm => alarm.State == AlarmState.Ringing),
                    "a reminder display failure leaves the entire due batch persisted as ringing");
                Check(Field<Dictionary<string, AlarmEntry>>(controller, "pendingReminders").Count == 3,
                    "all three failed reminders remain queued for another display attempt");
                Check(Field<Dictionary<string, ReminderWindow>>(controller, "reminders").Count == 0
                    && Application.Current.Windows.OfType<ReminderWindow>().Count() == 0,
                    "failed reminder construction/display leaves no half-created windows or registered dialogs");
                Check(Field<DispatcherTimer>(controller, "timer").IsEnabled,
                    "a hidden controller keeps its retry timer running while reminder displays are pending");
                Check(Field<DateTimeOffset>(controller, "nextPollAttempt") > DateTimeOffset.Now.AddSeconds(10),
                    "failed reminder display backs off repeated construction attempts");
                if (hadStyle) resources[resourceKey] = originalStyle;
                else resources.Remove(resourceKey);
                InvokeHandler(controller, "OnSystemTimeChanged", EventArgs.Empty);
                Check(PumpUntil(delegate { return VisibleReminders().Length == 3; }, TimeSpan.FromSeconds(5)),
                    "restoring valid resources displays all three queued reminders exactly once");
                Check(Field<Dictionary<string, AlarmEntry>>(controller, "pendingReminders").Count == 0,
                    "successful retry clears the entire pending reminder queue");
                foreach (ReminderWindow reminder in VisibleReminders()) Click(FindButton(reminder, "完成"));
                Check(scheduler.Alarms.Count == 0 && VisibleReminders().Length == 0
                    && !Field<DispatcherTimer>(controller, "timer").IsEnabled,
                    "all recovered reminders can be completed and the hidden retry timer stops");
            }
            finally
            {
                if (hadStyle) resources[resourceKey] = originalStyle;
                else resources.Remove(resourceKey);
                controller.Dispose();
                controller.Window.Close();
            }
        }

        private static void TestCollectability(MainWindowController activeController, AlarmScheduler activeScheduler)
        {
            // Weak references plus explicit collections are only for tests; the application never forces a GC.
            var dialogReferences = new List<WeakReference>();
            var lifetimeCases = new List<LifetimeCase>();
            Snapshot before = ReadSnapshot();
            for (int batch = 0; batch < 3; batch++)
            {
                for (int index = 0; index < 30; index++)
                    dialogReferences.AddRange(ReminderCycle(activeController, activeScheduler, index));
                activeController.Show();
                Pump();
                CollectReleasedObjects();
                RecordSnapshot("after reminder batch " + (batch + 1), ReadSnapshot());
            }
            // TextBox input services complete asynchronous cleanup after their last window closes.
            // Allow normal dispatcher time before checking for roots retained beyond that cleanup.
            PumpFor(TimeSpan.FromSeconds(2));
            CollectReleasedObjects();
            if (dialogReferences.Any(reference => reference.IsAlive))
                Record("Uncollected reminder indexes: " + String.Join(",", dialogReferences.Select((reference, index) =>
                    reference.IsAlive ? index.ToString(CultureInfo.InvariantCulture) : null).Where(value => value != null).ToArray()));
            Check(dialogReferences.All(reference => !reference.IsAlive),
                "all 180 manually rescheduled and completed reminder windows are collectible after dispatcher cleanup");
            for (int batch = 0; batch < 3; batch++)
            {
                for (int index = 0; index < 8; index++) lifetimeCases.Add(ControllerCycle(batch * 8 + index, index < 3));
                activeController.Show();
                Pump();
                CollectReleasedObjects();
                RecordSnapshot("after controller batch " + (batch + 1), ReadSnapshot());
            }
            Check(lifetimeCases.All(item => !item.Controller.IsAlive && !item.Window.IsAlive),
                "all 24 disposed controllers and windows are collectible while their schedulers remain alive");
            Check(lifetimeCases.All(item => !item.Timer.IsEnabled),
                "all retained disposed-controller timers remain stopped");
            Check(lifetimeCases.Where(item => item.TrayMenuDisposed.HasValue).All(item => item.TrayMenuDisposed.Value),
                "all 9 independently created tray context menus are explicitly disposed");
            RecordDelta("180 reminders and 24 controller lifecycles, measured after test-only GC", before, ReadSnapshot(), null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] ReminderCycle(MainWindowController controller, AlarmScheduler scheduler, int index)
        {
            AlarmEntry target = scheduler.AddCountdown("重复弹窗资源验证 " + index, TimeSpan.FromMinutes(1), DateTimeOffset.Now);
            controller.ProcessDue(scheduler.Poll(target.DueAt.AddMilliseconds(1)));
            Pump();
            ReminderWindow reminder = VisibleReminders().Single();
            var reference = new WeakReference(reminder);
            SetReminderInput(reminder, "RepeatHours", "00");
            SetReminderInput(reminder, "RepeatMinutes", "70");
            SetReminderInput(reminder, "RepeatSeconds", "23");
            Click(FindButton(reminder, "再次提醒"));
            target = scheduler.Alarms.Single(alarm => alarm.Id == target.Id);
            controller.ProcessDue(scheduler.Poll(target.DueAt.AddMilliseconds(1)));
            Pump();
            reminder = VisibleReminders().Single();
            var repeatedReference = new WeakReference(reminder);
            Click(FindButton(reminder, "完成"));
            return new[] { reference, repeatedReference };
        }

        private static void SetReminderInput(DependencyObject parent, string name, string value)
        {
            TextBox input = parent as TextBox;
            if (input != null && input.Name == name) { input.Text = value; return; }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                SetReminderInput(VisualTreeHelper.GetChild(parent, index), name, value);
        }

        private sealed class LifetimeCase
        {
            internal AlarmScheduler Scheduler;
            internal WeakReference Controller;
            internal WeakReference Window;
            internal DispatcherTimer Timer;
            internal bool? TrayMenuDisposed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LifetimeCase ControllerCycle(int index, bool enableTray)
        {
            var scheduler = NewScheduler("cycle-" + index);
            var controller = new MainWindowController(scheduler, delegate { }, enableTray);
            controller.Show();
            Pump();
            var result = new LifetimeCase
            {
                Scheduler = scheduler,
                Controller = new WeakReference(controller),
                Window = new WeakReference(controller.Window),
                Timer = Field<DispatcherTimer>(controller, "timer")
            };
            Forms.ContextMenuStrip menu = enableTray ? Field<Forms.NotifyIcon>(controller, "tray").ContextMenuStrip : null;
            controller.Dispose();
            if (menu != null) result.TrayMenuDisposed = menu.IsDisposed;
            controller.Window.Close();
            Pump();
            return result;
        }

        private static AlarmScheduler NewScheduler(string name)
        {
            return new AlarmScheduler(new AlarmStore(Path.Combine(runDirectory, name, "reminders.json")));
        }

        private static void MeasureIdle(MainWindowController controller, string state, double seconds)
        {
            PumpFor(TimeSpan.FromSeconds(1));
            Snapshot start = ReadSnapshot();
            Stopwatch elapsed = Stopwatch.StartNew();
            PumpFor(TimeSpan.FromSeconds(seconds));
            elapsed.Stop();
            Snapshot end = ReadSnapshot();
            RecordDelta("idle " + state + " (10 future alarms)", start, end, elapsed.Elapsed);
            Check(VisibleReminders().Length == 0, "idle " + state + " measurement does not create reminders");
        }

        private sealed class Snapshot
        {
            internal TimeSpan Cpu;
            internal long PrivateBytes;
            internal long WorkingSet;
            internal int Handles;
            internal uint Gdi;
            internal uint User;
            internal int Gen0;
            internal int Gen1;
            internal int Gen2;
        }

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr process, uint flags);

        private static Snapshot ReadSnapshot()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                process.Refresh();
                return new Snapshot
                {
                    Cpu = process.TotalProcessorTime, PrivateBytes = process.PrivateMemorySize64,
                    WorkingSet = process.WorkingSet64, Handles = process.HandleCount,
                    Gdi = GetGuiResources(process.Handle, 0), User = GetGuiResources(process.Handle, 1),
                    Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2)
                };
            }
        }

        private static void RecordDelta(string label, Snapshot before, Snapshot after, TimeSpan? elapsed)
        {
            string timing = elapsed.HasValue ? String.Format(CultureInfo.InvariantCulture,
                "; elapsed={0:F3}s; CPU={1:F2}ms; average one-core CPU={2:F4}%; average machine CPU={3:F4}%",
                elapsed.Value.TotalSeconds, (after.Cpu - before.Cpu).TotalMilliseconds,
                (after.Cpu - before.Cpu).TotalSeconds / elapsed.Value.TotalSeconds * 100,
                (after.Cpu - before.Cpu).TotalSeconds / elapsed.Value.TotalSeconds * 100 / Environment.ProcessorCount) : "";
            Record(label + timing);
            RecordSnapshot("  before", before);
            RecordSnapshot("  after", after);
            Record(String.Format(CultureInfo.InvariantCulture,
                "  delta: private={0:+0.000;-0.000;0.000} MiB; working set={1:+0.000;-0.000;0.000} MiB; handles={2:+0;-0;0}; GDI={3:+0;-0;0}; USER={4:+0;-0;0}; collections={5}/{6}/{7}",
                (after.PrivateBytes - before.PrivateBytes) / 1048576.0, (after.WorkingSet - before.WorkingSet) / 1048576.0,
                after.Handles - before.Handles, (long)after.Gdi - before.Gdi, (long)after.User - before.User,
                after.Gen0 - before.Gen0, after.Gen1 - before.Gen1, after.Gen2 - before.Gen2));
        }

        private static void RecordSnapshot(string label, Snapshot value)
        {
            Record(String.Format(CultureInfo.InvariantCulture,
                "{0}: private={1:F3} MiB; working set={2:F3} MiB; handles={3}; GDI={4}; USER={5}; collections={6}/{7}/{8}",
                label, value.PrivateBytes / 1048576.0, value.WorkingSet / 1048576.0, value.Handles,
                value.Gdi, value.User, value.Gen0, value.Gen1, value.Gen2));
        }

        private static void CollectReleasedObjects()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Pump();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Pump();
            }
        }

        private static T Field<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static void InvokeHandler(object target, string name, EventArgs arguments)
        {
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, new object[] { target, arguments });
        }

        private static ReminderWindow[] VisibleReminders()
        {
            return Application.Current.Windows.OfType<ReminderWindow>().Where(window => window.IsVisible).ToArray();
        }

        private static void Click(Button button)
        {
            if (button == null) throw new InvalidOperationException("The required action button was not found.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }

        private static Button FindButton(DependencyObject parent, string label)
        {
            Button match = parent as Button;
            if (match != null && String.Equals(match.Content as string, label, StringComparison.Ordinal)) return match;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                Button child = FindButton(VisualTreeHelper.GetChild(parent, index), label);
                if (child != null) return child;
            }
            return null;
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }

        private static void PumpFor(TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = duration };
            EventHandler stop = delegate { frame.Continue = false; };
            timer.Tick += stop;
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); timer.Tick -= stop; }
        }

        private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var frame = new DispatcherFrame();
            var elapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            bool matched = false;
            EventHandler check = delegate
            {
                matched = condition();
                if (matched || elapsed.Elapsed >= timeout) frame.Continue = false;
            };
            timer.Tick += check;
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); timer.Tick -= check; }
            return matched;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
            Record("PASS " + message);
        }

        private static void Record(string line)
        {
            report.Add(line);
            Console.WriteLine(line);
        }
    }
}
