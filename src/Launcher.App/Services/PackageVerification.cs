using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.App.ViewModels;
using Launcher.App.Views;
using Launcher.Core;
using SkiaSharp;
using Microsoft.Web.WebView2.Core;
using Launcher.AI;

namespace Launcher.App.Services;

/// <summary>Offline release check: execute the actual bundle without opening a window or touching user data.</summary>
public static class PackageVerification
{
    public static async Task<int> RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "novaina-package-" + Guid.NewGuid().ToString("N")));
        MainViewModel? vm = null; LogService? log = null;
        using var http = new HttpClient(new OfflineTransport());
        try
        {
            AppPaths.ConfigureData(root);
            var folder = BundledAssets.SkinViewerDirectory;
            var files = new[] { "index.html", "viewer.js", "skinview3d.bundle.js", "steve.png" };
            if (files.Any(f => !File.Exists(Path.Combine(folder, f)))) throw new InvalidDataException("缺少内置皮肤预览资源");
            using var skin = SKBitmap.Decode(Path.Combine(folder, "steve.png"));
            if (skin is null || skin.Width != 64) throw new InvalidDataException("Skia/Steve 纹理不可用");
            var licenses = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(folder)!, "licenses"), "*", SearchOption.AllDirectories);
            if (!licenses.Any(f => f.Contains("OpenAI")) || !licenses.Any(f => f.Contains("Markdig"))) throw new InvalidDataException("缺少内置许可证");
            string webViewRuntime;
            try { webViewRuntime = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (WebView2RuntimeNotFoundException) { webViewRuntime = "未安装，皮肤预览会提供安装入口"; }
            log = new(root); var secrets = new SecretStore(root); var accounts = new AccountService(http, secrets, log); var java = new JavaService(http, log);
            vm = new MainViewModel(new SettingsStore(root), secrets, accounts, java, new VersionService(), new LaunchService(http, accounts, java, log), log, http, false);
            Controls.MotionPolicy.Set(UiAnimationMode.Off);
            var window = new MainWindow(vm, playStartupAnimation: false);
            if (window.Title != "Novaina Launcher") throw new InvalidDataException("窗口名称不正确");
            var visual = (FrameworkElement)window.Content;
            foreach (var page in new[] { "Launch", "Versions", "Accounts", "Settings" })
            {
                vm.Navigate(page); visual.Measure(new Size(1080, 700)); visual.Arrange(new Rect(0, 0, 1080, 700)); visual.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            }
            var guides = new[] { "workflow", "fabric", "forge", "neoforge", "resources", "sides-network", "testing", "troubleshooting", "delivery" };
            if (guides.Any(topic => ModDevelopmentGuides.Read(topic).Length < 400)) throw new InvalidDataException("缺少内嵌 Mod 开发指南");
            var aiSettings = new Views.Pages.AiSettingsPage { DataContext = vm.AgentVM };
            aiSettings.Measure(new Size(860, 650)); aiSettings.Arrange(new Rect(0, 0, 860, 650)); aiSettings.UpdateLayout();
            var aiBitmap = new RenderTargetBitmap(860, 650, 96, 96, PixelFormats.Pbgra32); aiBitmap.Render(aiSettings);
            var conversationStore = new AgentConversationStore(root); var conversationId = Guid.NewGuid();
            conversationStore.Save(new(new(conversationId, AgentMode.Workbench, "发布检查", DateTimeOffset.UtcNow), new(new(), "检查", [], 0), []));
            if (conversationStore.Load(AgentMode.Workbench, conversationId)?.Session.Goal != "检查" || conversationStore.List(AgentMode.Basic).Count > 0) throw new InvalidDataException("会话存储或模式隔离异常");
            var avatar = await vm.Skins.GetCachedAsync(null);
            if (!File.Exists(vm.Skins.HeadFile(avatar))) throw new InvalidDataException("默认头像不可用");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, title = window.Title, runtime = Environment.Version.ToString(), webViewRuntime, pages = 4, aiSettings = true, embeddedGuides = guides.Length, encryptedConversations = true, assets = files.Length, licenses = licenses.Length, skin = "Steve", executable = Environment.ProcessPath, baseDirectory = AppContext.BaseDirectory }));
            return 0;
        }
        catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = error.ToString() })); return 1; }
        finally
        {
            if (vm is not null) { await vm.AccountsVM.CancelAndWaitAsync(); (vm.Skins as IDisposable)?.Dispose(); vm.LogsVM.Dispose(); vm.SettingsVM.Dispose(); }
            log?.Dispose();
            if (Directory.Exists(root) && root.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "novaina-package-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
    private sealed class OfflineTransport : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("发布检查不得联网"); }
}
