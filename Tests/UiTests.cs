using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingReminder.Core;

namespace QingReminder.Tests
{
    internal static class UiTests
    {
        private static int passed;
        private static string outputDirectory;

        [STAThread]
        private static int Main(string[] arguments)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            outputDirectory = arguments.Length == 0 ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots") : Path.GetFullPath(arguments[0]);
            Directory.CreateDirectory(outputDirectory);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            MainWindowController controller = null;
            try
            {
                Program.LoadTheme(app);
                string dataPath = Path.Combine(outputDirectory, "run-" + Guid.NewGuid().ToString("N"), "reminders.json");
                var scheduler = new AlarmScheduler(new AlarmStore(dataPath));
                controller = new MainWindowController(scheduler, delegate { app.Shutdown(); }, false);
                app.MainWindow = controller.Window;
                controller.Show();
                controller.Control<CheckBox>("SoundToggle").IsChecked = false;
                Pump();
                TestQuickCountdown(controller, scheduler);
                TestRememberedCountdown(controller, scheduler);
                TestCountdownNormalization(controller, scheduler);
                TestManualReminderCountdown();
                TestCreation(controller, scheduler);
                TestReminderWorkflow(controller, scheduler);
                TestRealTimer(controller, scheduler);
                TestDueWhileEditing(controller, scheduler);
                TestClosingAndShutdown();
                TestControllerShutdown(controller, scheduler);
                Console.WriteLine("PASS: " + passed + " WPF integration assertions.");
                Console.WriteLine("Screenshots: " + outputDirectory);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error);
                return 1;
            }
            finally
            {
                foreach (ReminderWindow window in app.Windows.OfType<ReminderWindow>().ToArray())
                    window.CloseForShutdown();
                if (controller != null)
                {
                    controller.Dispose();
                    controller.Window.Close();
                }
                app.Shutdown();
            }
        }

        private static void TestQuickCountdown(MainWindowController controller, AlarmScheduler scheduler)
        {
            Check(controller.Control<RadioButton>("CountdownMode").IsChecked == true,
                "the main window starts with the countdown editor and its shortcuts visible");
            Capture(controller.Window, "main-default.png");

            Click(controller.Control<Button>("Preset48"));
            Check(controller.Control<ComboBox>("DelayHours").Text == "00"
                && controller.Control<ComboBox>("DelayMinutes").Text == "48"
                && controller.Control<ComboBox>("DelaySeconds").Text == "00",
                "the 48-minute shortcut fills an exact 48-minute countdown");
            controller.Control<TextBox>("TitleInput").Text = "48 分钟后休息一下";
            Pump();
            AssertTextFits(controller.Control<TextBox>("TitleInput"), "reminder text fits its field without clipping");
            Capture(controller.Window, "main-48-minutes.png");
            DateTimeOffset before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry preset = scheduler.Alarms.Single();
            Check(preset.Mode == AlarmMode.Countdown && preset.DueAt >= before.AddMinutes(48)
                && preset.DueAt <= DateTimeOffset.Now.AddMinutes(48),
                "saving the shortcut schedules the reminder 48 minutes from the actual save time");
            Click(FindButtonByTooltip(controller.Control<StackPanel>("AlarmList").Children[0], "删除提醒"));
            Check(scheduler.Alarms.Count == 0, "the shortcut reminder can be removed with its list action");

            SetCountdown(controller, "00", "15", "00");
            ComboBox minutes = controller.Control<ComboBox>("DelayMinutes");
            minutes.ApplyTemplate();
            var editable = minutes.Template.FindName("PART_EditableTextBox", minutes) as TextBox;
            Check(editable != null && editable.IsVisible, "the minute field exposes a usable editable text box");
            editable.Focus();
            editable.SelectAll();
            editable.Text = "48";
            Pump();
            Check(minutes.Text == "48", "typing into the styled minute field updates its countdown value");
            controller.Control<TextBox>("TitleInput").Text = "手动输入 48 分钟";
            before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry typed = scheduler.Alarms.Single();
            Check(typed.Mode == AlarmMode.Countdown && typed.DueAt >= before.AddMinutes(48)
                && typed.DueAt <= DateTimeOffset.Now.AddMinutes(48),
                "the edited minute text is used when saving a reminder");
            Click(FindButtonByTooltip(controller.Control<StackPanel>("AlarmList").Children[0], "删除提醒"));
            minutes.IsDropDownOpen = true;
            Pump();
            var popup = minutes.Template.FindName("PART_Popup", minutes) as Popup;
            Check(popup != null && popup.IsOpen && popup.Child.IsVisible,
                "the styled countdown dropdown opens a visible list");
            var minuteItem = minutes.ItemContainerGenerator.ContainerFromIndex(7) as ComboBoxItem;
            Check(minuteItem != null, "the open dropdown generates selectable minute items");
            minuteItem.IsSelected = true;
            Pump();
            Check(minutes.SelectedIndex == 7 && minutes.Text == "07",
                "selecting a dropdown item updates the countdown text");
            minutes.IsDropDownOpen = false;
            editable.Focus();
            Pump();
            Check(minutes.IsKeyboardFocusWithin, "the styled countdown editor accepts keyboard focus");
            SetCountdown(controller, "00", "15", "00");
        }

        private static void TestRememberedCountdown(MainWindowController controller, AlarmScheduler scheduler)
        {
            Click(controller.Control<Button>("Preset48"));
            controller.Control<TextBox>("TitleInput").Text = "每隔 48 分钟休息一下";
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry target = scheduler.Alarms.Single();
            string originalId = target.Id;
            Check(target.CountdownDuration == TimeSpan.FromMinutes(48),
                "the saved alarm retains its original 48-minute countdown");

            ReminderWindow reminder = Ring(controller, scheduler, target);
            AssertSelectedDelay(reminder, TimeSpan.FromMinutes(48),
                "the alert defaults to the alarm's original 48-minute interval");
            AssertFits(FindButton(reminder, "再次提醒"), reminder,
                "the repeat command fits the alert with its original interval selected");
            Capture(reminder, "reminder-48-minutes.png");
            DateTimeOffset before = DateTimeOffset.Now;
            Click(FindButton(reminder, "再次提醒"));
            target = scheduler.Alarms.Single();
            Check(target.Id == originalId && target.State == AlarmState.Scheduled
                && target.Title == "每隔 48 分钟休息一下",
                "repeating the countdown keeps the alarm and its reminder title");
            Check(target.DueAt >= before.AddMinutes(48) && target.DueAt <= DateTimeOffset.Now.AddMinutes(48),
                "accepting the alert's default repeats 48 minutes from the click time");

            reminder = Ring(controller, scheduler, target);
            Click(FindButton(reminder, "更多设置"));
            AssertCountdownFields(controller, "00", "48", "00",
                "rescheduling a triggered countdown pre-fills its full original 48 minutes");
            Check(controller.Control<TextBox>("TitleInput").Text == target.Title,
                "the countdown editor also pre-fills the existing reminder title");
            Capture(controller.Window, "reschedule-48-minutes.png");
            before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            target = scheduler.Alarms.Single();
            Check(target.Id == originalId && target.CountdownDuration == TimeSpan.FromMinutes(48)
                && target.DueAt >= before.AddMinutes(48) && target.DueAt <= DateTimeOffset.Now.AddMinutes(48),
                "saving the unchanged editor repeats the same alarm 48 minutes from saving");

            reminder = Ring(controller, scheduler, target);
            before = DateTimeOffset.Now;
            reminder.Close();
            Pump();
            target = scheduler.Alarms.Single();
            Check(target.CountdownDuration == TimeSpan.FromMinutes(48)
                && target.DueAt >= before.AddMinutes(5) && target.DueAt <= DateTimeOffset.Now.AddMinutes(5),
                "a temporary five-minute delay preserves the original 48-minute interval");
            reminder = Ring(controller, scheduler, target);
            AssertSelectedDelay(reminder, TimeSpan.FromMinutes(48),
                "the next alert restores 48 minutes after a temporary five-minute delay");
            Click(FindButton(reminder, "更多设置"));
            AssertCountdownFields(controller, "00", "48", "00",
                "rescheduling after a temporary delay still fills the original 48 minutes");
            Click(controller.Control<Button>("CancelEditButton"));
            reminder = OnlyReminder();
            AssertSelectedDelay(reminder, TimeSpan.FromMinutes(48),
                "cancelling the editor reopens the alert with its original interval");
            Click(FindButton(reminder, "完成"));
            Check(scheduler.Alarms.Count == 0, "the repeated 48-minute alarm can be completed");

            SetCountdown(controller, "00", "07", "23");
            controller.Control<TextBox>("TitleInput").Text = "自定义倒计时间隔";
            Click(controller.Control<Button>("SaveButton"));
            target = scheduler.Alarms.Single();
            TimeSpan customDuration = TimeSpan.FromMinutes(7).Add(TimeSpan.FromSeconds(23));
            reminder = Ring(controller, scheduler, target);
            AssertSelectedDelay(reminder, customDuration,
                "an arbitrary countdown with seconds is selected exactly in the alert");
            before = DateTimeOffset.Now;
            Click(FindButton(reminder, "再次提醒"));
            target = scheduler.Alarms.Single();
            Check(target.DueAt >= before.Add(customDuration) && target.DueAt <= DateTimeOffset.Now.Add(customDuration),
                "repeating a custom countdown retains its seconds without rounding");
            reminder = Ring(controller, scheduler, target);
            Click(FindButton(reminder, "更多设置"));
            AssertCountdownFields(controller, "00", "07", "23",
                "the editor restores the exact custom minutes and seconds");
            before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            target = scheduler.Alarms.Single();
            Check(target.CountdownDuration == customDuration && target.DueAt >= before.Add(customDuration)
                && target.DueAt <= DateTimeOffset.Now.Add(customDuration),
                "saving an unchanged custom countdown preserves its exact duration");
            scheduler.Complete(target.Id);
        }

        private static void TestCountdownNormalization(MainWindowController controller, AlarmScheduler scheduler)
        {
            SetCountdown(controller, "00", "70", "00");
            controller.Control<TextBox>("TitleInput").Text = "70 分钟自动换算";
            DateTimeOffset before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry target = scheduler.Alarms.Single();
            Check(target.CountdownDuration == TimeSpan.FromMinutes(70)
                && target.DueAt >= before.AddMinutes(70) && target.DueAt <= DateTimeOffset.Now.AddMinutes(70),
                "seventy entered minutes save the full seventy-minute countdown");
            AssertCountdownFields(controller, "01", "10", "00",
                "saving seventy minutes normalizes the visible fields to one hour and ten minutes");
            Capture(controller.Window, "countdown-70-minutes.png");
            Click(FindButtonByTooltip(controller.Control<StackPanel>("AlarmList").Children[0], "重新设定"));
            AssertCountdownFields(controller, "01", "10", "00",
                "reopening a seventy-minute alarm restores its normalized countdown");

            string originalId = target.Id;
            SetCountdown(controller, "01", "130", "130");
            TimeSpan mixedDuration = new TimeSpan(3, 12, 10);
            before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            target = scheduler.Alarms.Single();
            Check(target.Id == originalId && target.CountdownDuration == mixedDuration
                && target.DueAt >= before.Add(mixedDuration) && target.DueAt <= DateTimeOffset.Now.Add(mixedDuration),
                "saving mixed overflowing fields adds all units without creating another alarm");
            AssertCountdownFields(controller, "03", "12", "10",
                "saving mixed hours, minutes and seconds carries both overflowing units");
            ReminderWindow reminder = Ring(controller, scheduler, target);
            AssertSelectedDelay(reminder, mixedDuration,
                "a normalized countdown becomes the reminder's exact default repeat duration");
            Click(FindButton(reminder, "更多设置"));
            AssertCountdownFields(controller, "03", "12", "10",
                "the ringing alarm's editor restores its normalized hours, minutes and seconds");
            Click(controller.Control<Button>("CancelEditButton"));
            Click(FindButton(OnlyReminder(), "完成"));

            SetCountdown(controller, "00", "00", "70");
            controller.Control<TextBox>("TitleInput").Text = "70 秒自动换算";
            before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            target = scheduler.Alarms.Single();
            Check(target.CountdownDuration == TimeSpan.FromSeconds(70)
                && target.DueAt >= before.AddSeconds(70) && target.DueAt <= DateTimeOffset.Now.AddSeconds(70),
                "seventy entered seconds save the full seventy-second countdown");
            AssertCountdownFields(controller, "00", "01", "10",
                "saving seventy seconds normalizes the visible fields to one minute and ten seconds");
            scheduler.Complete(target.Id);

            SetCountdown(controller, "00", "525600", "00");
            Click(controller.Control<Button>("SaveButton"));
            target = scheduler.Alarms.Single();
            Check(target.CountdownDuration == AlarmScheduler.MaxDelay,
                "large minute input accepts the existing exact 365-day duration boundary");
            AssertCountdownFields(controller, "8760", "00", "00",
                "the maximum duration converts its minutes to total hours without wrapping at one day");
            scheduler.Complete(target.Id);

            AssertRejectedCountdown(controller, scheduler, "00", "1.5", "00", "fractional minutes");
            AssertRejectedCountdown(controller, scheduler, "00", "00", "1.5", "fractional seconds");
            AssertRejectedCountdown(controller, scheduler, "00", "-1", "00", "negative minutes");
            AssertRejectedCountdown(controller, scheduler, "00", "00", "-1", "negative seconds");
            AssertRejectedCountdown(controller, scheduler, "00", "525600", "01", "a combined duration one second over 365 days");
            AssertRejectedCountdown(controller, scheduler, "00", "00", "31536001", "a seconds-only duration over 365 days");
            AssertRejectedCountdown(controller, scheduler, "00", "2147483647", "00", "huge minutes without integer multiplication overflow");
            AssertRejectedCountdown(controller, scheduler, "00", "00", "9223372036854775807", "huge seconds without integer overflow");
            AssertRejectedCountdown(controller, scheduler, "00", "00", "9999999999999999999999999999999999999999", "an integer beyond the parser's numeric range");

            ComboBox absoluteMinutes = controller.Control<ComboBox>("MinuteInput");
            ComboBox absoluteSeconds = controller.Control<ComboBox>("SecondInput");
            string[] absoluteChoices = Enumerable.Range(0, 60).Select(value => value.ToString("00")).ToArray();
            Check(!absoluteMinutes.IsEditable && !absoluteSeconds.IsEditable
                && absoluteMinutes.Items.Cast<string>().SequenceEqual(absoluteChoices)
                && absoluteSeconds.Items.Cast<string>().SequenceEqual(absoluteChoices),
                "absolute-time minute and second choices remain restricted to zero through fifty-nine");
            SetCountdown(controller, "00", "15", "00");
        }

        private static void TestManualReminderCountdown()
        {
            string path = Path.Combine(outputDirectory, "manual-" + Guid.NewGuid().ToString("N"), "reminders.json");
            var scheduler = new AlarmScheduler(new AlarmStore(path));
            MainWindowController controller = null;
            try
            {
                controller = new MainWindowController(scheduler, delegate { }, false);
                controller.Control<CheckBox>("SoundToggle").IsChecked = false;
                controller.Show();
                AlarmEntry target = scheduler.AddCountdown("弹窗中自行输入时分秒", TimeSpan.FromMinutes(48), DateTimeOffset.Now);
                string originalId = target.Id;
                ReminderWindow reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, TimeSpan.FromMinutes(48),
                    "the alert exposes independently editable hours, minutes and seconds with the original countdown filled");
                Check(new[] { "RepeatHours", "RepeatMinutes", "RepeatSeconds" }.All(name =>
                    !ReminderControl<TextBox>(reminder, name).IsReadOnly
                    && ReminderControl<TextBox>(reminder, name).IsEnabled
                    && ReminderControl<TextBox>(reminder, name).IsVisible),
                    "all three alert duration fields are visible, enabled and accept manual text input");
                SetReminderCountdown(reminder, "01", "70", "70");
                Capture(reminder, "reminder-manual-hms.png");
                TimeSpan mixed = new TimeSpan(2, 11, 10);
                DateTimeOffset before = DateTimeOffset.Now;
                Click(FindButton(reminder, "再次提醒"));
                target = scheduler.Alarms.Single();
                Check(!reminder.IsVisible && target.Id == originalId && target.Title == "弹窗中自行输入时分秒"
                    && target.State == AlarmState.Scheduled && target.CountdownDuration == mixed
                    && target.DueAt >= before.Add(mixed) && target.DueAt <= DateTimeOffset.Now.Add(mixed),
                    "typing one hour, seventy minutes and seventy seconds reschedules the existing alarm for two hours, eleven minutes and ten seconds");
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, mixed,
                    "the next alert fills the newly entered interval in normalized hours, minutes and seconds");
                Capture(reminder, "reminder-normalized-hms.png");
                SetReminderCountdown(reminder, "00", "00", "70");
                before = DateTimeOffset.Now;
                Click(FindButton(reminder, "再次提醒"));
                target = scheduler.Alarms.Single();
                Check(target.CountdownDuration == TimeSpan.FromSeconds(70)
                    && target.DueAt >= before.AddSeconds(70) && target.DueAt <= DateTimeOffset.Now.AddSeconds(70),
                    "typing seventy seconds directly in the alert saves exactly seventy seconds");
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, TimeSpan.FromSeconds(70),
                    "the next alert converts seventy entered seconds to one minute and ten seconds");
                SetReminderCountdown(reminder, "00", "48", "23");
                TimeSpan exact = new TimeSpan(0, 48, 23);
                before = DateTimeOffset.Now;
                Click(FindButton(reminder, "再次提醒"));
                target = scheduler.Alarms.Single();
                Check(target.CountdownDuration == exact && target.DueAt >= before.Add(exact)
                    && target.DueAt <= DateTimeOffset.Now.Add(exact),
                    "a manually entered forty-eight-minute interval preserves all twenty-three custom seconds");
                controller.Dispose();
                controller.Window.Close();
                controller = null;
                scheduler = new AlarmScheduler(new AlarmStore(path));
                target = scheduler.Alarms.Single();
                Check(target.Id == originalId && target.CountdownDuration == exact,
                    "the explicitly edited popup interval persists when the scheduler reloads its saved data");
                controller = new MainWindowController(scheduler, delegate { }, false);
                controller.Control<CheckBox>("SoundToggle").IsChecked = false;
                controller.Show();
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, exact,
                    "the first popup after restart restores the manually entered hours, minutes and seconds");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "bad", "00", "nonnumeric text");
                Capture(reminder, "reminder-inline-error.png");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "00", "00", "a zero interval");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "-1", "00", "negative minutes");
                AssertRejectedReminderCountdown(reminder, scheduler, "-1", "00", "00", "negative hours");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "00", "-1", "negative seconds");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "1.5", "00", "fractional minutes");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "00", "1.5", "fractional seconds");
                AssertRejectedReminderCountdown(reminder, scheduler, "", "00", "00", "an empty hours field");
                AssertRejectedReminderCountdown(reminder, scheduler, "8760", "00", "01", "a combined interval beyond 365 days");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "2147483647", "00", "integer multiplication overflow input");
                AssertRejectedReminderCountdown(reminder, scheduler, "00", "00", "9223372036854775807", "huge seconds");
                AssertRejectedReminderCountdown(reminder, scheduler, "999999999999999999999999999999", "00", "00", "an integer beyond the numeric parser range");
                SetReminderCountdown(reminder, "00", "48", "00");
                before = DateTimeOffset.Now;
                Click(FindButton(reminder, "再次提醒"));
                target = scheduler.Alarms.Single();
                Check(!reminder.IsVisible && target.CountdownDuration == TimeSpan.FromMinutes(48)
                    && target.DueAt >= before.AddMinutes(48) && target.DueAt <= DateTimeOffset.Now.AddMinutes(48),
                    "correcting invalid input allows retry and saves the exact new forty-eight-minute interval");
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, TimeSpan.FromMinutes(48),
                    "the alert remembers the corrected forty-eight-minute interval");
                SetReminderCountdown(reminder, "00", "03", "17");
                before = DateTimeOffset.Now;
                reminder.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(reminder),
                    Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump();
                target = scheduler.Alarms.Single();
                Check(!reminder.IsVisible && target.CountdownDuration == TimeSpan.FromMinutes(48)
                    && target.DueAt >= before.AddMinutes(5) && target.DueAt <= DateTimeOffset.Now.AddMinutes(5),
                    "Escape temporarily delays five minutes and discards unconfirmed popup edits");
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, TimeSpan.FromMinutes(48),
                    "the original forty-eight minutes return after Escape's temporary five-minute delay");
                SetReminderCountdown(reminder, "00", "525600", "00");
                before = DateTimeOffset.Now;
                Click(FindButton(reminder, "再次提醒"));
                target = scheduler.Alarms.Single();
                Check(target.CountdownDuration == AlarmScheduler.MaxDelay
                    && target.DueAt >= before.Add(AlarmScheduler.MaxDelay)
                    && target.DueAt <= DateTimeOffset.Now.Add(AlarmScheduler.MaxDelay),
                    "the popup accepts large minute input at the exact 365-day boundary");
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, AlarmScheduler.MaxDelay,
                    "the maximum interval fills total hours without wrapping after twenty-four hours");
                AssertFits(ReminderControl<TextBox>(reminder, "RepeatHours"), reminder,
                    "a four-digit total-hour field fits the reminder window");
                Click(FindButton(reminder, "完成"));
                target = scheduler.AddAbsolute("指定时间提醒也可输入间隔", DateTimeOffset.Now.AddMinutes(1), DateTimeOffset.Now);
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, TimeSpan.FromMinutes(5),
                    "an absolute-time alarm initially fills the existing five-minute delay default");
                SetReminderCountdown(reminder, "00", "00", "70");
                before = DateTimeOffset.Now;
                Click(FindButton(reminder, "延后提醒"));
                target = scheduler.Alarms.Single();
                Check(target.Mode == AlarmMode.Countdown && target.CountdownDuration == TimeSpan.FromSeconds(70)
                    && target.DueAt >= before.AddSeconds(70) && target.DueAt <= DateTimeOffset.Now.AddSeconds(70),
                    "the absolute-time alarm's entered interval becomes its saved countdown for subsequent alerts");
                reminder = Ring(controller, scheduler, target);
                AssertSelectedDelay(reminder, TimeSpan.FromSeconds(70),
                    "the converted absolute-time alarm restores the exact entered interval on its next alert");
                Click(FindButton(reminder, "完成"));
            }
            finally
            {
                if (controller != null) { controller.Dispose(); controller.Window.Close(); }
            }
        }

        private static void AssertRejectedReminderCountdown(ReminderWindow reminder, AlarmScheduler scheduler,
            string hours, string minutes, string seconds, string description)
        {
            AlarmEntry previous = scheduler.Alarms.Single();
            SetReminderCountdown(reminder, hours, minutes, seconds);
            Click(FindButton(reminder, "再次提醒"));
            AlarmEntry current = scheduler.Alarms.Single();
            TextBlock error = ReminderControl<TextBlock>(reminder, "RepeatError");
            Check(reminder.IsVisible && current.Id == previous.Id && current.State == AlarmState.Ringing
                && current.DueAt == previous.DueAt && current.CountdownDuration == previous.CountdownDuration
                && error.IsVisible && !String.IsNullOrWhiteSpace(error.Text),
                "the popup keeps its ringing alarm unchanged and shows an inline error for " + description);
        }

        private static void AssertRejectedCountdown(MainWindowController controller, AlarmScheduler scheduler,
            string hours, string minutes, string seconds, string description)
        {
            int previousCount = scheduler.Alarms.Count;
            SetCountdown(controller, hours, minutes, seconds);
            Click(controller.Control<Button>("SaveButton"));
            TextBlock error = controller.Control<TextBlock>("ErrorText");
            Check(scheduler.Alarms.Count == previousCount && error.Visibility == Visibility.Visible
                && (error.Text.Contains("整数") || error.Text.Contains("365 天")),
                description + " is rejected with a readable validation error and no added alarm");
        }

        private static void TestCreation(MainWindowController controller, AlarmScheduler scheduler)
        {
            controller.Control<RadioButton>("AbsoluteMode").IsChecked = true;
            DateTime expected = DateTime.Now.AddHours(2);
            expected = new DateTime(expected.Year, expected.Month, expected.Day, expected.Hour, expected.Minute, expected.Second);
            controller.Control<TextBox>("TitleInput").Text = "下午会议 · 项目进度";
            controller.Control<DatePicker>("DateInput").SelectedDate = expected.Date;
            controller.Control<ComboBox>("HourInput").SelectedIndex = expected.Hour;
            controller.Control<ComboBox>("MinuteInput").SelectedIndex = expected.Minute;
            controller.Control<ComboBox>("SecondInput").SelectedIndex = expected.Second;
            Click(controller.Control<Button>("SaveButton"));
            Check(scheduler.Alarms.Count == 1, "absolute mode adds one alarm");
            AlarmEntry absolute = scheduler.Alarms[0];
            Check(absolute.Mode == AlarmMode.Absolute && absolute.DueAt.LocalDateTime == expected,
                "absolute alarm retains the selected date, hour, minute and second");
            Check(controller.Control<TextBlock>("CountText").Text == "1", "list count updates after saving");
            controller.Control<TextBox>("TitleInput").Text = "准备下一次提醒";
            Capture(controller.Window, "main-absolute.png");

            SetCountdown(controller, "00", "15", "00");
            controller.Control<TextBox>("TitleInput").Text = "站起来活动一下";
            Capture(controller.Window, "main-countdown.png");
            DateTimeOffset before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry countdown = scheduler.Alarms.Single(a => a.Title == "站起来活动一下");
            Check(scheduler.Alarms.Count == 2 && countdown.Mode == AlarmMode.Countdown,
                "editable countdown controls create a countdown alarm");
            Check(countdown.DueAt >= before.AddMinutes(15) && countdown.DueAt <= DateTimeOffset.Now.AddMinutes(15),
                "countdown is calculated from the actual save time");

            SetCountdown(controller, "00", "00", "00");
            Click(controller.Control<Button>("SaveButton"));
            Check(scheduler.Alarms.Count == 2, "zero countdown does not create an alarm");
            Check(controller.Control<TextBlock>("ErrorText").Visibility == Visibility.Visible
                && controller.Control<TextBlock>("ErrorText").Text.Contains("0 秒"),
                "zero countdown displays a readable validation message");

            SetCountdown(controller, "00", "30", "00");
            controller.Control<TextBox>("TitleInput").Text = "检查本周项目计划，整理会议记录并确认所有待办事项都已分配给负责人，最后回顾交付时间。";
            Click(controller.Control<Button>("SaveButton"));
            Check(scheduler.Alarms.Count == 3, "a long reminder title can be saved");
            double originalWidth = controller.Window.Width;
            double originalHeight = controller.Window.Height;
            double originalMinHeight = controller.Window.MinHeight;
            controller.Window.Width = 800;
            controller.Window.Height = 680;
            Pump();
            Capture(controller.Window, "compact.png");
            var pageScroll = controller.Control<ScrollViewer>("PageScroll");
            AssertFitsViewport(controller.Control<Button>("SaveButton"), pageScroll, "save command fits the compact viewport");
            AssertFitsViewport(controller.Control<ComboBox>("DelaySeconds"), pageScroll, "countdown fields fit the compact viewport");
            AssertFitsViewport(controller.Control<TextBlock>("ClockText"), pageScroll, "the clock remains fully visible in the compact viewport");
            foreach (string name in new[] { "Preset5", "Preset15", "Preset30", "Preset48", "Preset60" })
                AssertFitsViewport(controller.Control<Button>(name), pageScroll, name + " fits the compact viewport");
            controller.Window.MinHeight = 500;
            controller.Window.Height = 540;
            Pump();
            Check(pageScroll.ScrollableHeight > 0, "a short work area provides scrolling instead of clipping the editor");
            pageScroll.ScrollToTop();
            Pump();
            AssertFitsViewport(controller.Control<Button>("SaveButton"), pageScroll, "the save command remains reachable in a short work area");
            pageScroll.ScrollToBottom();
            Pump();
            Check(pageScroll.VerticalOffset > 0, "the short window can scroll to its lower content");
            AssertFitsViewport(controller.Control<TextBlock>("StatusText"), pageScroll, "scrolling reaches the status footer in a short work area");
            Capture(controller.Window, "small-height.png");
            pageScroll.ScrollToTop();
            controller.Window.MinHeight = originalMinHeight;
            controller.Window.Width = originalWidth;
            controller.Window.Height = originalHeight;
        }

        private static void TestReminderWorkflow(MainWindowController controller, AlarmScheduler scheduler)
        {
            AlarmEntry target = scheduler.Alarms.Single(a => a.Title == "站起来活动一下");
            ReminderWindow reminder = Ring(controller, scheduler, target);
            Check(reminder.IsVisible && !reminder.SoundEnabled, "due alarm opens a dialog and respects the sound toggle");
            Check(scheduler.Alarms.Single(a => a.Id == target.Id).State == AlarmState.Ringing,
                "the triggered alarm is recorded as ringing");
            Capture(reminder, "reminder.png");
            DateTimeOffset before = DateTimeOffset.Now;
            Click(FindButton(reminder, "再次提醒"));
            AlarmEntry snoozed = scheduler.Alarms.Single(a => a.Id == target.Id);
            Check(!reminder.IsVisible && snoozed.State == AlarmState.Scheduled,
                "snooze closes the dialog and schedules the same alarm");
            Check(snoozed.DueAt >= before.AddMinutes(15) && snoozed.DueAt <= DateTimeOffset.Now.AddMinutes(15),
                "the default repeat uses this alarm's original 15-minute countdown");

            reminder = Ring(controller, scheduler, snoozed);
            Click(FindButton(reminder, "更多设置"));
            Check(!reminder.IsVisible && controller.Window.IsVisible,
                "reschedule closes the alert and opens the main editor");
            Check(controller.Control<TextBlock>("FormHeading").Text == "重新设定"
                && controller.Control<TextBlock>("SaveLabel").Text == "保存提醒",
                "reschedule editor shows the correct heading and command");
            AssertCountdownFields(controller, "00", "15", "00",
                "the 15-minute alarm opens its original countdown in the editor");
            SetCountdown(controller, "00", "02", "00");
            controller.Control<TextBox>("TitleInput").Text = "活动结束，继续工作";
            int originalCount = scheduler.Alarms.Count;
            before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry rescheduled = scheduler.Alarms.Single(a => a.Id == target.Id);
            Check(scheduler.Alarms.Count == originalCount && rescheduled.Title == "活动结束，继续工作"
                && rescheduled.State == AlarmState.Scheduled,
                "reschedule updates the original identifier and title without adding an alarm");
            Check(rescheduled.DueAt >= before.AddMinutes(2) && rescheduled.DueAt <= DateTimeOffset.Now.AddMinutes(2),
                "reschedule applies the newly selected countdown");

            reminder = Ring(controller, scheduler, rescheduled);
            AssertSelectedDelay(reminder, TimeSpan.FromMinutes(2),
                "a newly saved countdown becomes the alert's new default repeat interval");
            Click(FindButton(reminder, "更多设置"));
            AssertCountdownFields(controller, "00", "02", "00",
                "the editor uses the newly saved two-minute interval");
            Click(controller.Control<Button>("CancelEditButton"));
            reminder = OnlyReminder();
            Check(reminder.IsVisible && scheduler.Alarms.Single(a => a.Id == target.Id).State == AlarmState.Ringing,
                "cancelling the editor reopens the unacknowledged reminder");
            Click(FindButton(reminder, "完成"));
            Check(!reminder.IsVisible && scheduler.Alarms.All(a => a.Id != target.Id)
                && scheduler.Alarms.Count == originalCount - 1,
                "complete closes the dialog and removes the alarm");
        }

        private static void TestClosingAndShutdown()
        {
            int postponeCalls = 0;
            TimeSpan postponedBy = TimeSpan.Zero;
            var closeReminder = new ReminderWindow("关闭窗口仍保留提醒", DateTimeOffset.Now,
                delegate(TimeSpan delay) { postponeCalls++; postponedBy = delay; }, delegate { }, delegate { });
            closeReminder.SoundEnabled = false;
            closeReminder.Show();
            Pump();
            closeReminder.Close();
            Pump();
            Check(postponeCalls == 1 && postponedBy == TimeSpan.FromMinutes(5) && !closeReminder.IsVisible,
                "closing an alert postpones it exactly once for five minutes");

            var legacyReminder = new ReminderWindow("兼容旧调用方式", DateTimeOffset.Now,
                delegate(TimeSpan delay) { postponeCalls++; postponedBy = delay; }, delegate { }, delegate { },
                TimeSpan.FromMinutes(48));
            legacyReminder.SoundEnabled = false;
            legacyReminder.Show();
            Pump();
            AssertSelectedDelay(legacyReminder, TimeSpan.FromMinutes(48),
                "the compatible six-argument reminder constructor also pre-fills all countdown fields");
            SetReminderCountdown(legacyReminder, "00", "00", "70");
            Click(FindButton(legacyReminder, "再次提醒"));
            Check(postponeCalls == 2 && postponedBy == TimeSpan.FromSeconds(70) && !legacyReminder.IsVisible,
                "without the new save callback a manually entered interval falls back to the existing postpone callback");

            var longReminder = new ReminderWindow(
                "这是一条较长的提醒：请整理完整的项目计划、检查所有截止日期、回顾会议记录，并与相关负责人确认后续安排。"
                + "这是一条较长的提醒：请整理完整的项目计划、检查所有截止日期、回顾会议记录，并与相关负责人确认后续安排。",
                DateTimeOffset.Now, delegate(TimeSpan delay) { postponeCalls++; }, delegate { }, delegate { });
            longReminder.SoundEnabled = false;
            longReminder.Show();
            Pump();
            AssertFits(FindButton(longReminder, "完成"), longReminder, "long titles preserve access to reminder commands");
            Capture(longReminder, "reminder-long-title.png");
            int callsBeforeShutdown = postponeCalls;
            longReminder.CloseForShutdown();
            Pump();
            Check(!longReminder.IsVisible && postponeCalls == callsBeforeShutdown,
                "application shutdown closes alerts without postponing them");
        }

        private static void TestDueWhileEditing(MainWindowController controller, AlarmScheduler scheduler)
        {
            SetCountdown(controller, "00", "05", "00");
            controller.Control<TextBox>("TitleInput").Text = "编辑期间到期";
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry target = scheduler.Alarms.Single(a => a.Title == "编辑期间到期");
            var firstRow = controller.Control<StackPanel>("AlarmList").Children[0];
            Click(FindButtonByTooltip(firstRow, "重新设定"));
            Check(controller.Control<TextBlock>("FormHeading").Text == "重新设定",
                "a scheduled alarm can be edited from its list action");
            ReminderWindow reminder = Ring(controller, scheduler, target);
            SetCountdown(controller, "00", "03", "00");
            DateTimeOffset before = DateTimeOffset.Now;
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry updated = scheduler.Alarms.Single(a => a.Id == target.Id);
            Check(!reminder.IsVisible && updated.State == AlarmState.Scheduled,
                "saving an editor closes the old alert when its alarm became due during editing");
            Check(updated.DueAt >= before.AddMinutes(3) && updated.DueAt <= DateTimeOffset.Now.AddMinutes(3),
                "closing the stale alert preserves the newly saved schedule");
            scheduler.Complete(target.Id);
        }

        private static void TestRealTimer(MainWindowController controller, AlarmScheduler scheduler)
        {
            int previousCount = scheduler.Alarms.Count;
            SetCountdown(controller, "00", "00", "01");
            controller.Control<TextBox>("TitleInput").Text = "一秒倒计时验证";
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry target = scheduler.Alarms.Single(a => a.Title == "一秒倒计时验证");
            bool opened = PumpUntil(delegate
            {
                return Application.Current.Windows.OfType<ReminderWindow>().Any(w => w.IsVisible);
            }, TimeSpan.FromSeconds(5));
            Check(opened, "the real dispatcher timer opens a one-second countdown without manual polling");
            Check(scheduler.Alarms.Single(a => a.Id == target.Id).State == AlarmState.Ringing,
                "the real timer persists the ringing state before showing the alert");
            Click(FindButton(OnlyReminder(), "完成"));
            Check(scheduler.Alarms.Count == previousCount && scheduler.Alarms.All(a => a.Id != target.Id),
                "the automatically triggered reminder can be completed normally");
        }

        private static void TestControllerShutdown(MainWindowController controller, AlarmScheduler scheduler)
        {
            SetCountdown(controller, "00", "01", "00");
            controller.Control<TextBox>("TitleInput").Text = "退出时保持提醒计划";
            Click(controller.Control<Button>("SaveButton"));
            AlarmEntry target = scheduler.Alarms.Single(a => a.Title == "退出时保持提醒计划");
            ReminderWindow reminder = Ring(controller, scheduler, target);
            controller.Dispose();
            Pump();
            AlarmEntry stored = scheduler.Alarms.Single(a => a.Id == target.Id);
            Check(!reminder.IsVisible && stored.DueAt == target.DueAt && stored.State == AlarmState.Ringing,
                "controller disposal closes its alerts without changing their saved schedule");
        }

        private static ReminderWindow Ring(MainWindowController controller, AlarmScheduler scheduler, AlarmEntry target)
        {
            IList<AlarmEntry> due = scheduler.Poll(target.DueAt.AddMilliseconds(1));
            Check(due.Count == 1 && due[0].Id == target.Id, "poll triggers the expected alarm once");
            controller.ProcessDue(due);
            Pump();
            return OnlyReminder();
        }

        private static ReminderWindow OnlyReminder()
        {
            return Application.Current.Windows.OfType<ReminderWindow>().Single(w => w.IsVisible);
        }

        private static void SetCountdown(MainWindowController controller, string hours, string minutes, string seconds)
        {
            controller.Control<RadioButton>("CountdownMode").IsChecked = true;
            controller.Control<ComboBox>("DelayHours").Text = hours;
            controller.Control<ComboBox>("DelayMinutes").Text = minutes;
            controller.Control<ComboBox>("DelaySeconds").Text = seconds;
            Pump();
        }

        private static void AssertCountdownFields(MainWindowController controller, string hours,
            string minutes, string seconds, string message)
        {
            Check(controller.Control<RadioButton>("CountdownMode").IsChecked == true
                && controller.Control<ComboBox>("DelayHours").Text == hours
                && controller.Control<ComboBox>("DelayMinutes").Text == minutes
                && controller.Control<ComboBox>("DelaySeconds").Text == seconds, message);
        }

        private static void AssertSelectedDelay(ReminderWindow reminder, TimeSpan duration, string message)
        {
            Check(ReminderControl<TextBox>(reminder, "RepeatHours").Text == ((long)duration.TotalHours).ToString("00")
                && ReminderControl<TextBox>(reminder, "RepeatMinutes").Text == duration.Minutes.ToString("00")
                && ReminderControl<TextBox>(reminder, "RepeatSeconds").Text == duration.Seconds.ToString("00"), message);
        }

        private static T ReminderControl<T>(ReminderWindow reminder, string name) where T : FrameworkElement
        {
            T match = FindNamed<T>(reminder, name);
            if (match == null) throw new InvalidOperationException("The reminder control was not found: " + name);
            return match;
        }

        private static T FindNamed<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            T match = parent as T;
            if (match != null && match.Name == name) return match;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                T child = FindNamed<T>(VisualTreeHelper.GetChild(parent, index), name);
                if (child != null) return child;
            }
            return null;
        }

        private static void SetReminderCountdown(ReminderWindow reminder, string hours, string minutes, string seconds)
        {
            ReminderControl<TextBox>(reminder, "RepeatHours").Text = hours;
            ReminderControl<TextBox>(reminder, "RepeatMinutes").Text = minutes;
            ReminderControl<TextBox>(reminder, "RepeatSeconds").Text = seconds;
            Pump();
        }

        private static void Click(Button button)
        {
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

        private static Button FindButtonByTooltip(DependencyObject parent, string tooltip)
        {
            Button match = parent as Button;
            if (match != null && String.Equals(match.ToolTip as string, tooltip, StringComparison.Ordinal)) return match;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                Button child = FindButtonByTooltip(VisualTreeHelper.GetChild(parent, index), tooltip);
                if (child != null) return child;
            }
            return null;
        }

        private static void AssertFits(FrameworkElement element, Window window, string message)
        {
            var content = (FrameworkElement)window.Content;
            window.UpdateLayout();
            Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
            Check(element.IsVisible && bounds.Left >= -1 && bounds.Top >= -1
                && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1, message);
        }

        private static void AssertFitsViewport(FrameworkElement element, ScrollViewer scroll, string message)
        {
            var viewport = FindVisual<ScrollContentPresenter>(scroll);
            Rect bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
            Rect visible = viewport.TransformToAncestor(scroll).TransformBounds(new Rect(viewport.RenderSize));
            visible.Inflate(1, 1);
            Check(element.IsVisible && visible.Contains(bounds), message);
        }

        private static void AssertTextFits(TextBox input, string message)
        {
            input.ApplyTemplate();
            input.UpdateLayout();
            var host = input.Template.FindName("PART_ContentHost", input) as ScrollViewer;
            var viewport = FindVisual<ScrollContentPresenter>(host);
            Rect text = input.GetRectFromCharacterIndex(0);
            Rect visible = viewport.TransformToAncestor(input).TransformBounds(new Rect(viewport.RenderSize));
            visible.Inflate(1, 1);
            Check(!text.IsEmpty && visible.Contains(text), message);
        }

        private static T FindVisual<T>(DependencyObject parent) where T : DependencyObject
        {
            T match = parent as T;
            if (match != null) return match;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                T child = FindVisual<T>(VisualTreeHelper.GetChild(parent, index));
                if (child != null) return child;
            }
            return null;
        }

        private static void Capture(Window window, string filename)
        {
            Pump();
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
                (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (DrawingContext drawing = background.RenderOpen())
                drawing.DrawRectangle(window.Background, null, new Rect(content.RenderSize));
            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory, filename))) encoder.Save(stream);
            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            int visiblePixels = 0;
            for (int index = 0; index < pixels.Length; index += 4)
                if (pixels[index + 3] > 0 && (pixels[index] < 220 || pixels[index + 1] < 220 || pixels[index + 2] < 220)) visiblePixels++;
            Check(visiblePixels > 500, filename + " contains rendered controls and text");
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }

        private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var frame = new DispatcherFrame();
            var elapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            bool matched = false;
            timer.Tick += delegate
            {
                matched = condition();
                if (matched || elapsed.Elapsed >= timeout) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            return matched;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
            Console.WriteLine("PASS " + message);
        }
    }
}
