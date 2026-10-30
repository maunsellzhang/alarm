using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingReminder.Core;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace QingReminder
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            AppContext.SetSwitch("Switch.System.Windows.DoNotUsePresentationDpiCapabilityTier2OrGreater", false);
            bool first;
            using (var instance = new Mutex(true, "Local\\QingReminder.Desktop", out first))
            using (var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\QingReminder.Show"))
            {
                if (!first)
                {
                    showRequest.Set();
                    return 0;
                }
                MainWindowController main = null;
                SingleInstanceActivationListener activationListener = null;
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    LoadTheme(app);
                    string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingReminder");
                    var store = new AlarmStore(Path.Combine(dataDirectory, "reminders.json"));
                    var scheduler = new AlarmScheduler(store);
                    main = new MainWindowController(scheduler, delegate { main.Dispose(); app.Shutdown(); }, true);
                    app.MainWindow = main.Window;
                    activationListener = new SingleInstanceActivationListener(showRequest, app.Dispatcher, main.Show);
                    app.Exit += delegate { activationListener.Dispose(); main.Dispose(); };
                    main.Show();
                    if (store.LastRecoveryMessage != null)
                        MessageBox.Show(main.Window, "提醒数据无法读取，原文件已备份：\n" + store.RecoveryBackupPath,
                            "轻提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
                    app.Run();
                    return 0;
                }
                catch (Exception error)
                {
                    MessageBox.Show("应用未能启动。\n\n" + error.Message, "轻提醒", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally
                {
                    if (activationListener != null) activationListener.Dispose();
                    if (main != null) main.Dispose();
                    instance.ReleaseMutex();
                }
            }
        }

        internal static void LoadTheme(Application app)
        {
            using (Stream stream = Resource("QingReminder.Theme.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        }

        internal static Stream Resource(string name)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) throw new InvalidOperationException("缺少应用资源：" + name);
            return stream;
        }

        internal static BitmapFrame WindowIcon()
        {
            using (Stream stream = Resource("QingReminder.Icon"))
                return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
    }

    // Wait for another launch without repeatedly waking the UI thread while the app is idle.
    internal sealed class SingleInstanceActivationListener : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Dispatcher _dispatcher;
        private readonly Action _show;
        private RegisteredWaitHandle _registration;
        private DispatcherOperation _pendingOperation;
        private bool _disposed;

        internal SingleInstanceActivationListener(WaitHandle request, Dispatcher dispatcher, Action show)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");
            if (show == null) throw new ArgumentNullException("show");
            _dispatcher = dispatcher;
            _show = show;
            _registration = ThreadPool.RegisterWaitForSingleObject(request, OnRequested, null, Timeout.Infinite, false);
        }

        private void OnRequested(object state, bool timedOut)
        {
            lock (_gate)
            {
                if (_disposed || _pendingOperation != null || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
                try
                {
                    _pendingOperation = _dispatcher.BeginInvoke(new Action(delegate
                    {
                        lock (_gate)
                        {
                            _pendingOperation = null;
                            if (!_disposed) _show();
                        }
                    }));
                }
                catch (InvalidOperationException)
                {
                    // Dispatcher shutdown can start after the check above.
                }
            }
        }

        public void Dispose()
        {
            RegisteredWaitHandle registration;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_pendingOperation != null)
                {
                    _pendingOperation.Abort();
                    _pendingOperation = null;
                }
                registration = _registration;
                _registration = null;
            }
            if (registration != null) registration.Unregister(null);
        }
    }
}
