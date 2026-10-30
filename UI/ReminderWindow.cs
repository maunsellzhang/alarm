using System;
using System.ComponentModel;
using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QingReminder.Core;

namespace QingReminder
{
    internal sealed class ReminderWindow : Window
    {
        private readonly Action<TimeSpan> _postpone;
        private readonly Action<TimeSpan> _setCountdown;
        private readonly Action _reschedule;
        private readonly Action _complete;
        private readonly DispatcherTimer _soundTimer;
        private readonly TextBox _repeatHours;
        private readonly TextBox _repeatMinutes;
        private readonly TextBox _repeatSeconds;
        private readonly TextBlock _repeatError;
        private bool _actionCompleted;
        private bool _processingAction;
        private bool _closeRequested;
        private bool _silenced;
        private bool _closed;
        private bool _soundEnabled = true;
        private DispatcherOperation _closeOperation;

        public bool SoundEnabled
        {
            get { return _soundEnabled; }
            set
            {
                _soundEnabled = value;
                UpdateSoundTimer();
            }
        }

        public ReminderWindow(string title, DateTimeOffset dueAt, Action<TimeSpan> postpone,
            Action reschedule, Action complete)
            : this(title, dueAt, postpone, reschedule, complete, null)
        {
        }

        public ReminderWindow(string title, DateTimeOffset dueAt, Action<TimeSpan> postpone,
            Action reschedule, Action complete, TimeSpan? countdownDuration)
            : this(title, dueAt, postpone, reschedule, complete, countdownDuration, null)
        {
        }

        public ReminderWindow(string title, DateTimeOffset dueAt, Action<TimeSpan> postpone,
            Action reschedule, Action complete, TimeSpan? countdownDuration, Action<TimeSpan> setCountdown)
        {
            try
            {
                if (postpone == null) throw new ArgumentNullException("postpone");
                if (reschedule == null) throw new ArgumentNullException("reschedule");
                if (complete == null) throw new ArgumentNullException("complete");
                if (countdownDuration.HasValue && (countdownDuration.Value <= TimeSpan.Zero || countdownDuration.Value > TimeSpan.FromDays(365)))
                    throw new ArgumentOutOfRangeException("countdownDuration");
                TimeSpan defaultDelay = countdownDuration ?? TimeSpan.FromMinutes(5);

                _postpone = postpone;
                _setCountdown = setCountdown;
                _reschedule = reschedule;
                _complete = complete;
                Title = "轻提醒";
                Width = 480;
                MinWidth = 420;
                SizeToContent = SizeToContent.Height;
                MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
                ResizeMode = ResizeMode.NoResize;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Background = Brush("#F8FAF9");
                Foreground = Brush("#18332B");
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
                FontSize = 14;
                ShowInTaskbar = true;
                Topmost = true;
                UseLayoutRounding = true;
                SnapsToDevicePixels = true;

                var content = new Grid { Margin = new Thickness(30, 28, 30, 26) };
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var heading = new StackPanel { Orientation = Orientation.Horizontal };
                heading.Children.Add(new Border
                {
                    Width = 52,
                    Height = 52,
                    CornerRadius = new CornerRadius(8),
                    Background = Brush("#FBECE6"),
                    Child = new TextBlock
                    {
                        Text = "\uE823",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 26,
                        Foreground = Brush("#BC5A43"),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                });
                heading.Children.Add(new TextBlock
                {
                    Text = "时间到了",
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(16, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                content.Children.Add(heading);

                var reminderDetails = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
                reminderDetails.Children.Add(new TextBlock
                {
                    Text = String.IsNullOrWhiteSpace(title) ? "提醒" : title.Trim(),
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 150,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                reminderDetails.Children.Add(new TextBlock
                {
                    Text = "原定于 " + dueAt.ToLocalTime().ToString("M月d日 dddd HH:mm", CultureInfo.GetCultureInfo("zh-CN")),
                    FontSize = 13,
                    Foreground = Brush("#64736D"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                });
                Grid.SetRow(reminderDetails, 1);
                content.Children.Add(reminderDetails);

                var divider = new Border
                {
                    Height = 1,
                    Background = Brush("#DCE5E0"),
                    Margin = new Thickness(0, 26, 0, 23)
                };
                Grid.SetRow(divider, 2);
                content.Children.Add(divider);

                var repeatSettings = new StackPanel();
                repeatSettings.Children.Add(new TextBlock
                {
                    Text = "再次提醒间隔",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                var durationRow = new Grid();
                for (int i = 0; i < 3; i++)
                    durationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _repeatHours = CreateDurationInput("RepeatHours", "再次提醒小时", 0);
                _repeatMinutes = CreateDurationInput("RepeatMinutes", "再次提醒分钟", 1);
                _repeatSeconds = CreateDurationInput("RepeatSeconds", "再次提醒秒", 2);
                AddDurationField(durationRow, _repeatHours, "时", 0);
                AddDurationField(durationRow, _repeatMinutes, "分", 1);
                AddDurationField(durationRow, _repeatSeconds, "秒", 2);
                SetDurationInputs(defaultDelay);
                repeatSettings.Children.Add(durationRow);
                _repeatError = new TextBlock
                {
                    Name = "RepeatError",
                    Foreground = Brush("#B14336"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                    Visibility = Visibility.Collapsed
                };
                AutomationProperties.SetName(_repeatError, "再次提醒间隔输入错误");
                repeatSettings.Children.Add(_repeatError);
                var postponeButton = CreateButton(countdownDuration.HasValue ? "再次提醒" : "延后提醒", true);
                postponeButton.Margin = new Thickness(0, 16, 0, 0);
                postponeButton.TabIndex = 3;
                postponeButton.IsDefault = true;
                postponeButton.Click += delegate { SetCountdown(); };
                repeatSettings.Children.Add(postponeButton);
                Grid.SetRow(repeatSettings, 3);
                content.Children.Add(repeatSettings);

                var secondaryRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
                secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                secondaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var rescheduleButton = CreateButton("更多设置", false);
                rescheduleButton.TabIndex = 4;
                rescheduleButton.Margin = new Thickness(0, 0, 8, 0);
                rescheduleButton.Click += delegate { PerformAction(_reschedule); };
                secondaryRow.Children.Add(rescheduleButton);
                var completeButton = CreateButton("完成", false);
                completeButton.TabIndex = 5;
                completeButton.Margin = new Thickness(8, 0, 0, 0);
                completeButton.Click += delegate { PerformAction(_complete); };
                Grid.SetColumn(completeButton, 1);
                secondaryRow.Children.Add(completeButton);
                Grid.SetRow(secondaryRow, 4);
                content.Children.Add(secondaryRow);
                Content = new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };

                _soundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _soundTimer.Tick += OnSoundTick;
                Loaded += delegate
                {
                    PlaySound();
                    UpdateSoundTimer();
                    _repeatHours.Focus();
                    _repeatHours.SelectAll();
                };
                Closed += OnClosed;
                Closing += OnClosing;
                PreviewKeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.Key != Key.Escape) return;
                    e.Handled = true;
                    Postpone(TimeSpan.FromMinutes(5));
                };
            }
            catch
            {
                // A Window registers with Application even if its derived constructor fails.
                // Complete the partial instance so cleanup cannot postpone the alarm.
                _actionCompleted = true;
                OnClosed(this, EventArgs.Empty);
                Close();
                throw;
            }
        }

        public void Silence()
        {
            _silenced = true;
            if (_soundTimer != null) _soundTimer.Stop();
        }

        private void UpdateSoundTimer()
        {
            if (_soundTimer == null) return;
            if (_soundEnabled && IsLoaded && IsVisible && !_silenced && !_actionCompleted && !_closed)
                _soundTimer.Start();
            else
                _soundTimer.Stop();
        }

        private void OnSoundTick(object sender, EventArgs e)
        {
            PlaySound();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _closed = true;
            Silence();
            if (_soundTimer != null) _soundTimer.Tick -= OnSoundTick;
            if (_closeOperation != null)
            {
                _closeOperation.Abort();
                _closeOperation = null;
            }
        }

        public void CloseForShutdown()
        {
            _actionCompleted = true;
            Silence();
            Close();
        }

        private void PlaySound()
        {
            if (!SoundEnabled || _silenced || _actionCompleted) return;
            try { SystemSounds.Exclamation.Play(); }
            catch (InvalidOperationException) { }
            catch (System.IO.IOException) { }
        }

        private void Postpone(TimeSpan delay)
        {
            PerformAction(delegate { _postpone(delay); });
        }

        private void SetCountdown()
        {
            TimeSpan duration;
            try
            {
                duration = CountdownInput.Parse(_repeatHours.Text, _repeatMinutes.Text, _repeatSeconds.Text);
            }
            catch (ArgumentException error)
            {
                _repeatError.Text = error.Message;
                _repeatError.Visibility = Visibility.Visible;
                return;
            }

            SetDurationInputs(duration);
            ClearRepeatError();
            PerformAction(delegate
            {
                if (_setCountdown != null) _setCountdown(duration);
                else _postpone(duration);
            });
        }

        private void SetDurationInputs(TimeSpan duration)
        {
            TimeSpan normalized = TimeSpan.FromSeconds(Math.Ceiling(duration.TotalSeconds));
            _repeatHours.Text = ((long)normalized.TotalHours).ToString("00", CultureInfo.InvariantCulture);
            _repeatMinutes.Text = normalized.Minutes.ToString("00", CultureInfo.InvariantCulture);
            _repeatSeconds.Text = normalized.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void ClearRepeatError()
        {
            if (_repeatError == null) return;
            _repeatError.Text = "";
            _repeatError.Visibility = Visibility.Collapsed;
        }

        private TextBox CreateDurationInput(string name, string label, int tabIndex)
        {
            var input = new TextBox
            {
                Name = name,
                Height = 42,
                MinWidth = 52,
                FontSize = 18,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                BorderBrush = Brush("#B9CAC0"),
                TabIndex = tabIndex
            };
            AutomationProperties.SetName(input, label);
            input.TextChanged += delegate { ClearRepeatError(); };
            input.GotKeyboardFocus += delegate { input.SelectAll(); };
            return input;
        }

        private static void AddDurationField(Grid row, TextBox input, string unit, int column)
        {
            var field = new Grid { Margin = new Thickness(0, 0, column < 2 ? 14 : 0, 0) };
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            field.Children.Add(input);
            var label = new TextBlock
            {
                Text = unit,
                Foreground = Brush("#64736D"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(label, 1);
            field.Children.Add(label);
            Grid.SetColumn(field, column);
            row.Children.Add(field);
        }

        private void PerformAction(Action action)
        {
            if (_processingAction || _actionCompleted) return;
            _processingAction = true;
            try
            {
                action();
                _actionCompleted = true;
                Silence();
                Close();
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "未能更新提醒，请重试。\n\n" + error.Message,
                    "轻提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _processingAction = false;
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_actionCompleted) return;
            e.Cancel = true;
            if (_closeRequested || _processingAction) return;
            _closeRequested = true;
            // Defer so a successful callback can close outside the current Closing event.
            _closeOperation = Dispatcher.BeginInvoke(new Action(delegate
            {
                _closeOperation = null;
                _closeRequested = false;
                Postpone(TimeSpan.FromMinutes(5));
            }));
        }

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        internal static string FormatDuration(TimeSpan delay)
        {
            long seconds = (long)Math.Ceiling(delay.TotalSeconds);
            if (seconds % 86400 == 0) return (seconds / 86400).ToString(CultureInfo.InvariantCulture) + " 天";
            if (seconds % 3600 == 0) return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + " 小时";
            if (seconds % 60 == 0) return (seconds / 60).ToString(CultureInfo.InvariantCulture) + " 分钟";
            string text = "";
            if (seconds >= 3600) text += (seconds / 3600).ToString(CultureInfo.InvariantCulture) + "时";
            if (seconds >= 60) text += ((seconds / 60) % 60).ToString(CultureInfo.InvariantCulture) + "分";
            return text + (seconds % 60).ToString(CultureInfo.InvariantCulture) + "秒";
        }

        private static Button CreateButton(string label, bool primary)
        {
            var button = new Button
            {
                Content = label,
                Height = 42,
                Padding = new Thickness(16, 0, 16, 0),
                FontSize = 14,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Background = Brush(primary ? "#18795E" : "#FFFFFF"),
                Foreground = primary ? Brushes.White : Brush("#275743"),
                BorderBrush = Brush(primary ? "#18795E" : "#C6D5CC"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(TextBlock));
            presenter.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            presenter.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(primary ? "#12664E" : "#EFF5F1")));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Control.BackgroundProperty, Brush(primary ? "#105840" : "#E1ECE5")));
            template.Triggers.Add(pressed);
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(primary ? "#87B9A6" : "#18795E")));
            focused.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
            template.Triggers.Add(focused);
            button.Template = template;
            return button;
        }
    }
}
