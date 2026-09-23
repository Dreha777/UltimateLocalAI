using System.IO;
using System.Windows;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureDirectories();
        LogService.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Error("UI exception", args.Exception);
            MessageBox.Show("Произошла ошибка интерфейса. Подробности сохранены в папке Logs.\n\n" + args.Exception.Message,
                "Ultimate Local AI", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogService.Error("Unhandled exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }
}
