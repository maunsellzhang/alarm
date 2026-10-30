using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QingReminder.Core;
using Forms = System.Windows.Forms;

namespace QingReminder
{
    internal sealed class MainWindowController : IDisposable
    {
        private readonly AlarmScheduler scheduler;
        private readonly Action quit;
        private readonly DispatcherTimer timer;
        private readonly Dictionary<string, ReminderWindow> reminders = new Dictionary<string, ReminderWindow>();
        private readonly Dictionary<string, AlarmEntry> pendingReminders = new Dictionary<string, AlarmEntry>();
        private readonly Dictionary<string, TextBlock> remainingLabels = new Dictionary<string, TextBlock>();
        private static readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();
        private static readonly int[] presetMinutes = { 5, 15, 30, 48, 60 };
        private IList<AlarmEntry> displayedAlarms;
        private Forms.NotifyIcon tray;
        private string editingId;
        private volatile bool disposed;
        private bool changingForm;
        private bool notifiedHidden;
        private bool listDirty;
        private bool timeEventAttached;
        private bool powerEventAttached;
        private TimeSpan? previewCountdown;
        private DateTimeOffset? previewAbsolute;
        private DateTimeOffset nextPollAttempt = DateTimeOffset.MinValue;
        private DateTimeOffset lastErrorNotice = DateTimeOffset.MinValue;

        internal Window Window { get; private set; }

        internal MainWindowController(AlarmScheduler scheduler, Action quit, bool enableTray)
        {
            this.scheduler = scheduler;
            this.quit = quit;
            using (Stream stream = Program.Resource("QingReminder.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            Rect workArea = SystemParameters.WorkArea;
            Window.MinHeight = Math.Min(Window.MinHeight, workArea.Height);
            Window.MinWidth = Math.Min(Window.MinWidth, workArea.Width);
            Window.Height = Math.Min(Window.Height, workArea.Height);
            Window.Width = Math.Min(Window.Width, workArea.Width);
            Window.Icon = Program.WindowIcon();
            Window.SourceInitialized += delegate { RoundCorners(Window); };
            Window.Closing += OnClosing;
            FillNumbers(Control<ComboBox>("HourInput"), 24);
            FillNumbers(Control<ComboBox>("MinuteInput"), 60);
            FillNumbers(Control<ComboBox>("SecondInput"), 60);
            FillNumbers(Control<ComboBox>("DelayHours"), 25);
            FillNumbers(Control<ComboBox>("DelayMinutes"), 60);
            FillNumbers(Control<ComboBox>("DelaySeconds"), 60);
            Control<TextBox>("TitleInput").TextChanged += delegate { ClearError(); };
            Control<RadioButton>("AbsoluteMode").Checked += delegate { ChangeMode(); };
            Control<RadioButton>("CountdownMode").Checked += delegate { ChangeMode(); };
            foreach (string name in new[] { "HourInput", "MinuteInput", "SecondInput", "DelayHours", "DelayMinutes", "DelaySeconds" })
            {
                var combo = Control<ComboBox>(name);
                combo.SelectionChanged += delegate { ClearError(); UpdatePreview(); };
                combo.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(delegate { ClearError(); UpdatePreview(); }));
            }
            Control<DatePicker>("DateInput").SelectedDateChanged += delegate { ClearError(); UpdatePreview(); };
            Control<Button>("SaveButton").Click += delegate { Save(); };
            Control<Button>("CancelEditButton").Click += delegate { CancelEditing(); };
            Control<Button>("Preset5").Click += delegate { SetDelay(TimeSpan.FromMinutes(5)); };
            Control<Button>("Preset15").Click += delegate { SetDelay(TimeSpan.FromMinutes(15)); };
            Control<Button>("Preset30").Click += delegate { SetDelay(TimeSpan.FromMinutes(30)); };
            Control<Button>("Preset48").Click += delegate { SetDelay(TimeSpan.FromMinutes(48)); };
            Control<Button>("Preset60").Click += delegate { SetDelay(TimeSpan.FromHours(1)); };
            Control<CheckBox>("SoundToggle").Checked += delegate { UpdateSound(); };
            Control<CheckBox>("SoundToggle").Unchecked += delegate { UpdateSound(); };
            var menu = new ContextMenu();
            var hide = new MenuItem { Header = "最小化到托盘" };
            hide.Click += delegate { HideToTray(); };
            var exit = new MenuItem { Header = "退出轻提醒" };
            exit.Click += delegate { quit(); };
            menu.Items.Add(hide);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            Control<Button>("AppMenuButton").Click += delegate
            {
                menu.PlacementTarget = Control<Button>("AppMenuButton");
                menu.IsOpen = true;
            };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && editingId != null) { CancelEditing(); e.Handled = true; }
            };
            try
            {
                if (enableTray) CreateTray();
                SetDefaultTime();
                SetDelay(TimeSpan.FromMinutes(15));
                ChangeMode();
                scheduler.Changed += OnSchedulerChanged;
                RenderAlarms();
                timer = new DispatcherTimer(DispatcherPriority.Background);
                timer.Tick += OnTimerTick;
                Window.IsVisibleChanged += OnVisibilityChanged;
                Window.StateChanged += OnWindowStateChanged;
                SystemEvents.TimeChanged += OnSystemTimeChanged;
                timeEventAttached = true;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                powerEventAttached = true;
                Tick();
            }
            catch
            {
                Dispose();
                Window.Close();
                throw;
            }
        }

        internal T Control<T>(string name) where T : FrameworkElement
        {
            return (T)Window.FindName(name);
        }

        internal void Show()
        {
            if (disposed) return;
            Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
        }

        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "轻提醒 · 后台运行中", Visible = true };
            using (Stream stream = Program.Resource("QingReminder.Icon"))
            using (var icon = new System.Drawing.Icon(stream))
                tray.Icon = (System.Drawing.Icon)icon.Clone();
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开轻提醒", null, delegate { Window.Dispatcher.Invoke(new Action(Show)); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Window.Dispatcher.Invoke(quit); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Show(); };
            tray.BalloonTipClicked += delegate { Show(); };
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (disposed) return;
            e.Cancel = true;
            HideToTray();
        }

        private void HideToTray()
        {
            if (editingId != null) CancelEditing();
            if (tray == null) { Window.WindowState = WindowState.Minimized; return; }
            Window.Hide();
            if (!notifiedHidden)
            {
                notifiedHidden = true;
                tray.ShowBalloonTip(3500, "轻提醒正在后台运行", "提醒仍会按时触发。可从托盘打开或退出。", Forms.ToolTipIcon.Info);
            }
        }

        private void ChangeMode()
        {
            bool absolute = Control<RadioButton>("AbsoluteMode").IsChecked == true;
            Control<StackPanel>("AbsolutePanel").Visibility = absolute ? Visibility.Visible : Visibility.Collapsed;
            Control<StackPanel>("CountdownPanel").Visibility = absolute ? Visibility.Collapsed : Visibility.Visible;
            Control<StackPanel>("PresetPanel").Visibility = absolute ? Visibility.Collapsed : Visibility.Visible;
            ClearError();
            UpdatePreview();
        }

        private void SetDefaultTime()
        {
            DateTime future = DateTime.Now.AddMinutes(5);
            changingForm = true;
            Control<DatePicker>("DateInput").SelectedDate = future.Date;
            Control<ComboBox>("HourInput").SelectedIndex = future.Hour;
            Control<ComboBox>("MinuteInput").SelectedIndex = future.Minute;
            Control<ComboBox>("SecondInput").SelectedIndex = 0;
            changingForm = false;
        }

        private void SetDelay(TimeSpan delay)
        {
            changingForm = true;
            Control<ComboBox>("DelayHours").Text = ((int)delay.TotalHours).ToString("00");
            Control<ComboBox>("DelayMinutes").Text = delay.Minutes.ToString("00");
            Control<ComboBox>("DelaySeconds").Text = delay.Seconds.ToString("00");
            changingForm = false;
            UpdatePreview();
        }

        private DateTimeOffset ReadDue(DateTimeOffset now)
        {
            if (Control<RadioButton>("CountdownMode").IsChecked == true)
            {
                return now.Add(ReadCountdownDelay());
            }
            DateTime? date = Control<DatePicker>("DateInput").SelectedDate;
            if (!date.HasValue) throw new ArgumentException("请选择有效日期。");
            DateTime local = DateTime.SpecifyKind(date.Value.Date.AddHours(ReadNumber("HourInput", 23))
                .AddMinutes(ReadNumber("MinuteInput", 59)).AddSeconds(ReadNumber("SecondInput", 59)), DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local)) throw new ArgumentException("该时间处于夏令时切换区间，请选择其他时间。");
            if (TimeZoneInfo.Local.IsAmbiguousTime(local)) throw new ArgumentException("该时间在夏令时切换时出现两次，请选择其他时间。");
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        private int ReadNumber(string name, int maximum)
        {
            int value;
            if (!Int32.TryParse(Control<ComboBox>(name).Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0 || value > maximum)
                throw new ArgumentException("指定时间请输入有效整数：小时 0–23，分钟和秒钟 0–59。");
            return value;
        }

        private TimeSpan ReadCountdownDelay()
        {
            return CountdownInput.Parse(Control<ComboBox>("DelayHours").Text,
                Control<ComboBox>("DelayMinutes").Text, Control<ComboBox>("DelaySeconds").Text);
        }

        private void UpdatePreview()
        {
            if (changingForm) return;
            TimeSpan? delay = null;
            try { delay = ReadCountdownDelay(); }
            catch (ArgumentException) { }
            UpdatePresetSelection(delay);
            previewCountdown = null;
            previewAbsolute = null;
            if (Control<RadioButton>("CountdownMode").IsChecked == true)
            {
                previewCountdown = delay;
            }
            else
            {
                try { previewAbsolute = ReadDue(DateTimeOffset.Now); }
                catch (ArgumentException) { }
            }
            RefreshPreview(DateTimeOffset.Now);
        }

        private void RefreshPreview(DateTimeOffset now)
        {
            DateTimeOffset? due = previewCountdown.HasValue ? now.Add(previewCountdown.Value) : previewAbsolute;
            SetText(Control<TextBlock>("PreviewText"), !due.HasValue ? "等待有效时间"
                : due.Value <= now ? "请选择未来的提醒时间" : "将于 " + FormatDue(due.Value) + " 提醒");
        }

        private void UpdatePresetSelection(TimeSpan? delay)
        {
            foreach (int preset in presetMinutes)
            {
                Button button = Control<Button>("Preset" + preset);
                bool selected = delay == TimeSpan.FromMinutes(preset);
                button.Background = Brush(selected ? "#E8F3EE" : "#F5F7F6");
                button.Foreground = Brush(selected ? "#18795E" : "#6F7D74");
                button.BorderBrush = Brush(selected ? "#CDE4D8" : "#F5F7F6");
            }
        }

        private void Save()
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                DateTimeOffset due = ReadDue(now);
                if (due <= now) throw new ArgumentException("提醒时间已经过去，请选择未来的时间。");
                string title = Control<TextBox>("TitleInput").Text;
                AlarmMode mode = Control<RadioButton>("CountdownMode").IsChecked == true ? AlarmMode.Countdown : AlarmMode.Absolute;
                string id = editingId;
                if (id != null)
                {
                    scheduler.Reschedule(id, title, due, mode, now);
                    ReminderWindow staleReminder;
                    if (reminders.TryGetValue(id, out staleReminder)) staleReminder.CloseForShutdown();
                }
                else if (mode == AlarmMode.Countdown) scheduler.AddCountdown(title, due - now, now);
                else scheduler.AddAbsolute(title, due, now);
                CancelEditing();
                Control<TextBox>("TitleInput").Clear();
                if (mode == AlarmMode.Countdown) SetDelay(due - now);
                else SetDefaultTime();
                SetStatus(id == null ? "已添加提醒 · " + FormatDue(due) : "已重新设定 · " + FormatDue(due));
                UpdatePreview();
            }
            catch (Exception error) { ShowError(FriendlyError(error)); }
        }

        private void StartEditing(AlarmEntry alarm)
        {
            Show();
            if (editingId != null && editingId != alarm.Id) CancelEditing();
            editingId = alarm.Id;
            changingForm = true;
            Control<TextBox>("TitleInput").Text = alarm.Title;
            DateTime local = alarm.DueAt > DateTimeOffset.Now ? alarm.DueAt.LocalDateTime : DateTime.Now.AddMinutes(5);
            Control<DatePicker>("DateInput").SelectedDate = local.Date;
            Control<ComboBox>("HourInput").SelectedIndex = local.Hour;
            Control<ComboBox>("MinuteInput").SelectedIndex = local.Minute;
            Control<ComboBox>("SecondInput").SelectedIndex = local.Second;
            if (alarm.Mode == AlarmMode.Countdown)
            {
                Control<RadioButton>("CountdownMode").IsChecked = true;
                SetDelay(alarm.CountdownDuration ?? TimeSpan.FromMinutes(5));
            }
            else
            {
                Control<RadioButton>("AbsoluteMode").IsChecked = true;
            }
            changingForm = false;
            Control<TextBlock>("FormHeading").Text = "重新设定";
            Control<TextBlock>("SaveLabel").Text = "保存提醒";
            Control<TextBlock>("SaveIcon").Text = "\uE73E";
            Control<Button>("CancelEditButton").Visibility = Visibility.Visible;
            Control<TextBox>("TitleInput").Focus();
            Control<TextBox>("TitleInput").SelectAll();
            ChangeMode();
        }

        private void CancelEditing()
        {
            string id = editingId;
            editingId = null;
            Control<TextBlock>("FormHeading").Text = "新建提醒";
            Control<TextBlock>("SaveLabel").Text = "添加提醒";
            Control<TextBlock>("SaveIcon").Text = "\uE710";
            Control<Button>("CancelEditButton").Visibility = Visibility.Collapsed;
            ClearError();
            if (id == null) return;
            Control<TextBox>("TitleInput").Clear();
            // Reopen an unacknowledged ringing alarm when its editor is cancelled.
            AlarmEntry alarm = scheduler.Alarms.FirstOrDefault(a => a.Id == id);
            if (alarm != null && alarm.State == AlarmState.Ringing && !reminders.ContainsKey(id))
            {
                pendingReminders[id] = alarm;
                nextPollAttempt = DateTimeOffset.MinValue;
                Tick();
            }
        }

        private void Tick()
        {
            if (disposed) return;
            DateTimeOffset now = DateTimeOffset.Now;
            if (now >= nextPollAttempt)
            {
                try
                {
                    ProcessDue(scheduler.Poll(now));
                    nextPollAttempt = DateTimeOffset.MinValue;
                }
                catch (Exception error)
                {
                    nextPollAttempt = now.AddSeconds(15);
                    SetStatus(pendingReminders.Count == 0 ? "提醒数据保存失败，请检查磁盘和文件权限。" : "提醒窗口未能打开，稍后将自动重试。");
                    if (now - lastErrorNotice > TimeSpan.FromMinutes(1))
                    {
                        lastErrorNotice = now;
                        ShowError(FriendlyError(error));
                        if (!Window.IsVisible && tray != null)
                            tray.ShowBalloonTip(5000, "提醒暂时无法触发", "请打开应用查看错误，程序会自动重试。", Forms.ToolTipIcon.Error);
                    }
                }
            }
            if (IsPageVisible)
            {
                if (listDirty) TryRenderAlarms();
                SetText(Control<TextBlock>("ClockText"), now.ToString("HH:mm:ss"));
                SetText(Control<TextBlock>("TodayText"), now.ToString("yyyy年M月d日 · dddd"));
                RefreshPreview(now);
                foreach (AlarmEntry alarm in displayedAlarms)
                {
                    TextBlock label;
                    if (remainingLabels.TryGetValue(alarm.Id, out label))
                        SetText(label, alarm.State == AlarmState.Ringing ? (editingId == alarm.Id ? "重新设定中" : "正在提醒") : FormatRemaining(alarm.DueAt - now));
                }
            }
            UpdateTimer(now);
        }

        private bool IsPageVisible { get { return Window.IsVisible && Window.WindowState != WindowState.Minimized; } }

        private void OnTimerTick(object sender, EventArgs args) { Tick(); }
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { Tick(); }
        private void OnWindowStateChanged(object sender, EventArgs args) { Tick(); }

        private void UpdateTimer(DateTimeOffset now)
        {
            if (disposed || timer == null) return;
            DateTimeOffset? next = scheduler.NextScheduledAt;
            if (!IsPageVisible && !next.HasValue && pendingReminders.Count == 0) { timer.Stop(); return; }
            TimeSpan interval = IsPageVisible ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(60);
            if (pendingReminders.Count != 0 && nextPollAttempt - now < interval) interval = nextPollAttempt - now;
            if (next.HasValue)
            {
                DateTimeOffset wakeAt = next.Value < nextPollAttempt ? nextPollAttempt : next.Value;
                TimeSpan untilDue = wakeAt - now;
                if (untilDue < interval) interval = untilDue;
            }
            if (interval < TimeSpan.FromMilliseconds(100)) interval = TimeSpan.FromMilliseconds(100);
            if (timer.Interval != interval) timer.Interval = interval;
            if (!timer.IsEnabled) timer.Start();
        }

        private void OnSystemTimeChanged(object sender, EventArgs args) { QueueSystemRefresh(); }
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Resume) QueueSystemRefresh();
        }

        private void QueueSystemRefresh()
        {
            if (disposed || Window.Dispatcher.HasShutdownStarted) return;
            try
            {
                Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (disposed) return;
                    nextPollAttempt = DateTimeOffset.MinValue;
                    UpdatePreview();
                    Tick();
                }));
            }
            catch (InvalidOperationException) { /* The dispatcher may finish shutting down after the check. */ }
        }

        internal void ProcessDue(IList<AlarmEntry> due)
        {
            if (disposed) return;
            foreach (AlarmEntry alarm in due) pendingReminders[alarm.Id] = alarm;
            if (pendingReminders.Count == 0) return;
            IList<AlarmEntry> current = scheduler.Alarms;
            Exception lastError = null;
            foreach (AlarmEntry queued in pendingReminders.Values.ToArray())
            {
                AlarmEntry alarm = current.FirstOrDefault(a => a.Id == queued.Id);
                if (alarm == null || alarm.State != AlarmState.Ringing)
                {
                    pendingReminders.Remove(queued.Id);
                    continue;
                }
                try
                {
                    ShowReminder(alarm);
                    pendingReminders.Remove(alarm.Id);
                }
                catch (Exception error) { lastError = error; }
            }
            if (lastError != null) throw new InvalidOperationException("提醒窗口未能打开，程序会自动重试。", lastError);
        }

        private void ShowReminder(AlarmEntry alarm)
        {
            if (reminders.ContainsKey(alarm.Id)) { reminders[alarm.Id].Activate(); return; }
            var reminder = new ReminderWindow(alarm.Title, alarm.DueAt,
                delegate(TimeSpan delay)
                {
                    scheduler.Postpone(alarm.Id, delay, DateTimeOffset.Now);
                    if (editingId == alarm.Id) CancelEditing();
                    SetStatus("再次提醒已设置 · " + ReminderWindow.FormatDuration(delay) + "后");
                },
                delegate { StartEditing(scheduler.Alarms.First(a => a.Id == alarm.Id)); },
                delegate
                {
                    scheduler.Complete(alarm.Id);
                    if (editingId == alarm.Id) CancelEditing();
                    SetStatus("已完成提醒");
                }, alarm.Mode == AlarmMode.Countdown ? alarm.CountdownDuration : null,
                delegate(TimeSpan delay)
                {
                    DateTimeOffset now = DateTimeOffset.Now;
                    AlarmEntry current = scheduler.Alarms.First(a => a.Id == alarm.Id);
                    scheduler.Reschedule(alarm.Id, current.Title, now.Add(delay), AlarmMode.Countdown, now);
                    if (editingId == alarm.Id) CancelEditing();
                    SetStatus("再次提醒已设置 · " + ReminderWindow.FormatDuration(delay) + "后");
                });
            try
            {
                reminder.Icon = Window.Icon;
                reminder.SoundEnabled = Control<CheckBox>("SoundToggle").IsChecked == true;
                reminder.SourceInitialized += delegate { RoundCorners(reminder); };
                reminder.Closed += delegate { reminders.Remove(alarm.Id); };
                reminders.Add(alarm.Id, reminder);
                reminder.Show();
                if (reminders.Count > 1)
                {
                    double offset = Math.Min(reminders.Count - 1, 5) * 26;
                    reminder.Left = Math.Min(reminder.Left + offset, SystemParameters.WorkArea.Right - reminder.ActualWidth);
                    reminder.Top = Math.Min(reminder.Top + offset, SystemParameters.WorkArea.Bottom - reminder.ActualHeight);
                }
                reminder.Activate();
            }
            catch
            {
                reminders.Remove(alarm.Id);
                reminder.CloseForShutdown();
                throw;
            }
        }

        private void OnSchedulerChanged(object sender, EventArgs e)
        {
            if (disposed) return;
            nextPollAttempt = DateTimeOffset.MinValue;
            listDirty = true;
            if (IsPageVisible) TryRenderAlarms();
            UpdateTimer(DateTimeOffset.Now);
        }

        private void TryRenderAlarms()
        {
            try { RenderAlarms(); }
            catch (Exception error)
            {
                // A display failure must not interrupt a schedule that was already saved.
                listDirty = true;
                ShowError(FriendlyError(error));
            }
        }

        private void RenderAlarms()
        {
            StackPanel list = Control<StackPanel>("AlarmList");
            list.Children.Clear();
            remainingLabels.Clear();
            IList<AlarmEntry> alarms = scheduler.Alarms;
            displayedAlarms = alarms;
            listDirty = false;
            Control<TextBlock>("CountText").Text = alarms.Count.ToString();
            Control<StackPanel>("EmptyState").Visibility = alarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (AlarmEntry item in alarms)
            {
                AlarmEntry alarm = item;
                var row = new Grid { Margin = new Thickness(0, 12, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                var glyph = new Border
                {
                    Width = 22, Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = alarm.Mode == AlarmMode.Absolute ? "\uE121" : "\uE916", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17, Foreground = Brush(alarm.State == AlarmState.Ringing ? "#B95F44" : "#54806C"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                row.Children.Add(glyph);
                var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                details.Children.Add(new TextBlock { Text = alarm.Title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = alarm.Title });
                details.Children.Add(new TextBlock { Text = FormatDue(alarm.DueAt) + " · " + (alarm.Mode == AlarmMode.Absolute ? "指定时间" : "倒计时"), FontSize = 11, Foreground = Brush("#7E8882"), Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(details, 1); row.Children.Add(details);
                var remaining = new TextBlock { Text = FormatRemaining(alarm.DueAt - DateTimeOffset.Now), FontSize = 14, FontFamily = new FontFamily("Segoe UI"), Foreground = Brush(alarm.State == AlarmState.Ringing ? "#B95F44" : "#18795E"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
                remainingLabels.Add(alarm.Id, remaining); Grid.SetColumn(remaining, 2); row.Children.Add(remaining);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Button edit = IconButton("\uE70F", "重新设定");
                edit.Click += delegate
                {
                    ReminderWindow ringing;
                    if (reminders.TryGetValue(alarm.Id, out ringing)) { ringing.Activate(); return; }
                    StartEditing(alarm);
                };
                Button remove = IconButton("\uE74D", "删除提醒");
                remove.IsEnabled = alarm.State != AlarmState.Ringing;
                remove.Click += delegate
                {
                    try
                    {
                        scheduler.Remove(alarm.Id);
                        if (editingId == alarm.Id) CancelEditing();
                        SetStatus("已删除提醒");
                    }
                    catch (Exception error) { ShowError(FriendlyError(error)); }
                };
                actions.Children.Add(edit); actions.Children.Add(remove); Grid.SetColumn(actions, 3); row.Children.Add(actions);
                list.Children.Add(new Border { BorderBrush = Brush("#E9EEEB"), BorderThickness = new Thickness(0, 1, 0, 0), Child = row });
            }
        }

        private void UpdateSound()
        {
            foreach (ReminderWindow reminder in reminders.Values)
                reminder.SoundEnabled = Control<CheckBox>("SoundToggle").IsChecked == true;
        }

        private void ShowError(string message)
        {
            Control<TextBlock>("ErrorText").Text = message;
            Control<TextBlock>("ErrorText").Visibility = Visibility.Visible;
        }

        private void ClearError() { Control<TextBlock>("ErrorText").Visibility = Visibility.Collapsed; }
        private void SetStatus(string text) { Control<TextBlock>("StatusText").Text = text; }

        private static string FriendlyError(Exception error)
        {
            if (error is IOException || error is UnauthorizedAccessException) return "提醒未能保存，请检查磁盘空间和文件权限。";
            if (error is KeyNotFoundException) return "此提醒已不存在，请重新添加。";
            return error.Message;
        }

        private static string FormatDue(DateTimeOffset due)
        {
            DateTime local = due.LocalDateTime;
            string day = local.Date == DateTime.Today ? "今天" : (local.Date == DateTime.Today.AddDays(1) ? "明天" : local.ToString("yyyy/M/d"));
            return day + " " + local.ToString("HH:mm:ss");
        }

        private static string FormatRemaining(TimeSpan time)
        {
            if (time <= TimeSpan.Zero) return "即将提醒";
            long seconds = (long)Math.Ceiling(time.TotalSeconds);
            if (seconds >= 86400) return (seconds / 86400) + " 天 " + ((seconds % 86400) / 3600) + " 小时";
            return (seconds / 3600).ToString("00") + ":" + ((seconds / 60) % 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }

        private static void FillNumbers(ComboBox input, int count)
        {
            for (int value = 0; value < count; value++) input.Items.Add(value.ToString("00"));
        }

        private static void SetText(TextBlock label, string value) { if (label.Text != value) label.Text = value; }

        private static SolidColorBrush Brush(string color)
        {
            SolidColorBrush brush;
            if (!brushes.TryGetValue(color, out brush))
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                brush.Freeze();
                brushes.Add(color, brush);
            }
            return brush;
        }

        private static Button IconButton(string glyph, string tooltip)
        {
            return new Button { Content = glyph, ToolTip = tooltip, Style = (Style)Application.Current.FindResource("IconButton") };
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static void RoundCorners(Window window)
        {
            try { int preference = 2; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 33, ref preference, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (timer != null)
            {
                timer.Stop();
                timer.Tick -= OnTimerTick;
            }
            if (timeEventAttached) SystemEvents.TimeChanged -= OnSystemTimeChanged;
            if (powerEventAttached) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Window.IsVisibleChanged -= OnVisibilityChanged;
            Window.StateChanged -= OnWindowStateChanged;
            Window.Closing -= OnClosing;
            scheduler.Changed -= OnSchedulerChanged;
            foreach (ReminderWindow reminder in reminders.Values.ToList()) reminder.CloseForShutdown();
            reminders.Clear();
            pendingReminders.Clear();
            remainingLabels.Clear();
            displayedAlarms = null;
            Control<StackPanel>("AlarmList").Children.Clear();
            if (tray != null)
            {
                Forms.ContextMenuStrip menu = tray.ContextMenuStrip;
                System.Drawing.Icon icon = tray.Icon;
                tray.Visible = false;
                tray.ContextMenuStrip = null;
                tray.Icon = null;
                try { tray.Dispose(); }
                finally
                {
                    if (menu != null) menu.Dispose();
                    if (icon != null) icon.Dispose();
                    tray = null;
                }
            }
        }
    }
}
