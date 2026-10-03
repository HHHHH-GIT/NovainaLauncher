using System.Net.Http;
using System.Diagnostics;
using System.Windows;
using Launcher.App.ViewModels;
using Launcher.App.Views;
using Launcher.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Launcher.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    public DataDirectoryService DataDirectories { get; } = new();
    public bool IsRestarting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Contains("--verify-package"))
        {
            Shutdown(await Services.PackageVerification.RunAsync());
            return;
        }

        if (e.Args.Contains("--install-host"))
        {
            Shutdown(await InstallHost.RunAsync());
            return;
        }
        HostedGameInstallEngine.Executable = Environment.ProcessPath;
        if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            HostedGameInstallEngine.ManagedEntryPoint = System.IO.Path.Combine(AppContext.BaseDirectory, "NovainaLauncher.dll");

        try { AppPaths.ConfigureData(await Task.Run(() => DataDirectories.InitializeAsync())); }
        catch (Exception ex)
        {
            var choice = Launcher.App.Controls.AppDialog.Show("数据目录无法使用，请选择可写目录。\n" + ex.Message, "数据目录", MessageBoxButton.YesNo);
            if (choice != MessageBoxResult.Yes) { Shutdown(); return; }
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "数据保存位置" };
            if (picker.ShowDialog() != true) { Shutdown(); return; }
            try
            {
                DataDirectories.WriteLocation(picker.FolderName);
                AppPaths.ConfigureData(await Task.Run(() => DataDirectories.InitializeAsync()));
            }
            catch (Exception error)
            {
                Launcher.App.Controls.AppDialog.Show("无法保存数据路径。请将启动器移到可写目录后重试。\n" + error.Message, "数据目录");
                Shutdown(); return;
            }
        }

        var collection = new ServiceCollection();

        // Register Core Services
        collection.AddSingleton<HttpClient>();
        collection.AddSingleton<SettingsStore>();
        collection.AddSingleton<SecretStore>();
        collection.AddSingleton<LogService>();
        collection.AddSingleton<AccountService>();
        collection.AddSingleton<JavaService>();
        collection.AddSingleton<VersionService>();
        collection.AddSingleton<LaunchService>();

        // Register ViewModels
        collection.AddSingleton<MainViewModel>();

        // Register Views
        collection.AddSingleton<MainWindow>();

        _services = collection.BuildServiceProvider();

        // Global unhandled exception handler
        DispatcherUnhandledException += (s, args) =>
        {
            var log = _services?.GetService<LogService>();
            log?.Write($"未捕获异常: {args.Exception.Message}", LogLevel.Error, true);
            Launcher.App.Controls.AppDialog.Show($"程序发生错误: {args.Exception.Message}", "未捕获异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    public void Restart()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法找到启动器程序");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true };
        if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "NovainaLauncher.dll"));
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法重新启动");
        IsRestarting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<LogService>()?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }
}
