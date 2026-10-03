using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.App;
using Launcher.App.Controls;
using Launcher.App.ViewModels;
using Launcher.App.Views;
using Launcher.Core;
using Launcher.AI;
using Xunit;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Input;

namespace Launcher.Tests;

public class UiRegressionTests
{
    [Fact]
    public void Navigation_Contains_Exactly_One_Page_And_Controls_Render()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                foreach (var name in new[] { "Icons", "AppleTheme", "ControlTemplates", "GlassTheme" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/NovainaLauncher;component/Styles/{name}.xaml", UriKind.Relative) });
                app.Resources["BoolToVis"] = new StateConverter(); app.Resources["EqualConverter"] = new EqualConverter();
                app.Resources["CurrentItemConverter"] = new CurrentItemConverter(); app.Resources["DanmakuActiveConverter"] = new DanmakuActiveConverter();
                app.Resources["LoaderIconConverter"] = new LoaderIconConverter();
                var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ikun-ui-" + Guid.NewGuid());
                using var http = new HttpClient();
                using var log = new LogService(directory);
                var secrets = new SecretStore(directory);
                var accounts = new AccountService(http, secrets, log);
                var java = new JavaService(http, log);
                var vm = new MainViewModel(new SettingsStore(directory), secrets, accounts, java, new VersionService(), new LaunchService(http, accounts, java, log), log, http, false);
                var window = new MainWindow(vm, playStartupAnimation: false);
                var initialMemory = vm.SettingsVM.RefreshMemoryAsync();
                var initialDeadline = DateTime.UtcNow.AddSeconds(4);
                while (!initialMemory.IsCompleted && DateTime.UtcNow < initialDeadline) { Pump(); Thread.Sleep(10); }
                Assert.True(initialMemory.IsCompletedSuccessfully);
                var root = (FrameworkElement)window.Content;
                var cachedPages = new Dictionary<string, UserControl>();
                foreach (var page in new[] { "Launch", "Versions", "Downloads", "Accounts", "Logs", "Settings", "Launch", "Accounts" })
                {
                    vm.Navigate(page);
                    root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout();
                    var host = Descendants(root).OfType<TransitionHost>().Single();
                    Assert.Same(vm.CurrentViewModel, host.Content);
                    Assert.Single(Descendants(host).OfType<UserControl>());
                    var activeView = Descendants(host).OfType<UserControl>().Single();
                    if (cachedPages.TryGetValue(page, out var earlier)) Assert.Same(earlier, activeView);
                    else cachedPages.Add(page, activeView);
                    foreach (var bar in Descendants(host).OfType<System.Windows.Controls.Primitives.ScrollBar>())
                        Assert.Equal(10, bar.Orientation == Orientation.Vertical ? bar.Width : bar.Height);
                    var bitmap = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var output = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui"));
                    System.IO.Directory.CreateDirectory(output);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(System.IO.Path.Combine(output, page + ".png")); encoder.Save(stream);
                }
                Assert.Equal(6, Descendants(root).OfType<TransitionHost>().Single().CachedViewCount);
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                var shade = (Border)window.FindName("DialogShade");
                using (window.ShowDialogShade())
                {
                    Assert.Equal(Visibility.Visible, shade.Visibility);
                    using (window.ShowDialogShade()) Assert.Equal(Visibility.Visible, shade.Visibility);
                    Assert.Equal(Visibility.Visible, shade.Visibility);
                }
                Assert.Equal(Visibility.Collapsed, shade.Visibility);
                CheckAppDialog(app);
                CheckDownloadWizard(vm, root);
                vm.AccountsVM.OpenLittleSkinModal(); vm.AccountsVM.LittleSkinPassword = "temporary";
                vm.Navigate("Accounts"); vm.AccountsVM.OpenLittleSkinModal();
                vm.AccountsVM.LittleSkinProfiles.Add(new LittleSkinProfile("YouriKunDad", "sample"));
                vm.AccountsVM.SelectedLittleSkinProfile = vm.AccountsVM.LittleSkinProfiles[0]; vm.AccountsVM.ShowLittleSkinProfilePicker = true;
                root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout();
                var roles = Descendants(root).OfType<ComboBox>().Single(c => c.ItemsSource == vm.AccountsVM.LittleSkinProfiles);
                Assert.NotNull(roles.ItemTemplate);
                Assert.Contains(Descendants(roles).OfType<TextBlock>(), t => t.Text == "YouriKunDad");
                Assert.DoesNotContain(Descendants(roles).OfType<TextBlock>(), t => t.Text.Contains("LittleSkinProfile {"));
                var earlierAccount = vm.CurrentAccount;
                vm.CurrentAccount = new AccountProfile { Name = "LittleSkin", Kind = AccountKind.LittleSkin };
                root.UpdateLayout();
                Assert.False(vm.AccountsVM.ShowSkinEditor); Assert.False(vm.AccountsVM.CanImportSkin);
                Assert.Equal(Visibility.Collapsed, Descendants(root).OfType<Button>().Single(b => Equals(b.Command, vm.AccountsVM.ImportSkinCommand)).Visibility);
                Assert.Contains(Descendants(root).OfType<TextBlock>(), t => t.Text == "换皮肤请到在线网站，换完后刷新。" && t.Visibility == Visibility.Visible);
                vm.CurrentAccount = earlierAccount;
                vm.Navigate("Launch");
                Assert.False(vm.AccountsVM.ShowLittleSkinModal);
                Assert.Empty(vm.AccountsVM.LittleSkinPassword);
                vm.Navigate("Settings");
                root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout();
                var settingsPage = Descendants(root).OfType<Launcher.App.Views.Pages.SettingsPage>().Single();
                settingsPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                vm.SettingsVM.AnimationIndex = 0;
                Assert.Equal("关闭", vm.SettingsVM.AnimationLabel);
                Assert.Equal(TimeSpan.Zero, MotionPolicy.Page);
                Assert.NotNull(window.Icon);
                Assert.Equal(64, Assert.IsType<BitmapImage>(((Image)window.FindName("LauncherBrandIcon")).Source).DecodePixelWidth);
                var themeSelector = Assert.IsType<SlidingTabStrip>(settingsPage.FindName("ThemeSelector"));
                var themeTabs = Descendants(themeSelector).OfType<RadioButton>().ToArray();
                Assert.Equal(3, themeTabs.Length);
                foreach (var theme in new[] { "Light", "Dark", "System" })
                {
                    vm.SettingsVM.SelectThemeCommand.Execute(theme); root.UpdateLayout();
                    Assert.Equal(theme, themeSelector.SelectedKey);
                    Assert.Equal(theme, new SettingsStore(directory).Load().Theme);
                    Assert.Equal(theme, Assert.Single(themeTabs, tab => tab.IsChecked == true).CommandParameter);
                    foreach (var tab in themeTabs)
                        Assert.Contains(Descendants(tab).OfType<TextBlock>(), text => text.ActualWidth > 0 && text.DesiredSize.Width <= text.ActualWidth + 1);
                    if (theme != "System") SavePreview(root, "Theme-selector-" + theme + ".png");
                }
                vm.SettingsVM.SelectDanmakuModeCommand.Execute("All"); Assert.Equal(DanmakuMode.All, vm.DanmakuMode);
                vm.CycleDanmakuMode(); Assert.Equal("Off", vm.SettingsVM.DanmakuModeKey);
                vm.SettingsVM.BlockLog4j = true;
                Assert.True(vm.SettingsVM.DanmakuFilter.IsBlocked(new(DateTimeOffset.Now, LogLevel.Info, "log4j test")));
                vm.SettingsVM.SelectDanmakuStyleCommand.Execute("Rainbow"); Assert.Equal(DanmakuStyle.Rainbow, vm.Settings.DanmakuStyle);
                vm.SettingsVM.AddDanmakuRuleCommand.Execute(null);
                var rule = vm.SettingsVM.DanmakuRules.Last(); rule.MatchIndex = 1; rule.Pattern = "["; rule.Enabled = true;
                Assert.False(rule.Enabled); Assert.NotEmpty(rule.Error);
                foreach (var section in new[] { "Danmaku", "Downloads", "Data" })
                {
                    vm.SettingsVM.NavigateSection(section); root.UpdateLayout();
                    Assert.Equal(section, vm.SettingsVM.SelectedSection); Assert.True(vm.SettingsVM.ScrollOffset > 0);
                    var snapshot = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32); snapshot.Render(root);
                    var image = new PngBitmapEncoder(); image.Frames.Add(BitmapFrame.Create(snapshot));
                    using var screenshot = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/" + section + "-settings-light.png"))); image.Save(screenshot);
                }
                vm.SettingsVM.NavigateSection("Advanced"); root.UpdateLayout();
                Assert.False(vm.SettingsVM.HasPendingSection);
                Assert.True(vm.SettingsVM.ScrollOffset > 0);
                Assert.Equal("Advanced", vm.SettingsVM.SelectedSection);
                var scroll = vm.SettingsVM.ScrollOffset;
                vm.ToggleSidebar(); root.UpdateLayout();
                Assert.Same(settingsPage, Descendants(root).OfType<Launcher.App.Views.Pages.SettingsPage>().Single());
                Assert.Equal(scroll, vm.SettingsVM.ScrollOffset);
                vm.SettingsVM.AnimationIndex = 1;
                Assert.Equal("性能", vm.SettingsVM.AnimationLabel);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.Equal(267.75, MotionPolicy.Page.TotalMilliseconds, 2);
                    Assert.Equal(229.5, MotionPolicy.Sidebar.TotalMilliseconds, 2);
                    Assert.Equal(267.75, MotionPolicy.Scroll.TotalMilliseconds, 2);
                    Assert.Equal(114.75, MotionPolicy.Interaction.TotalMilliseconds, 2);
                    Assert.Equal(153, MotionPolicy.Menu.TotalMilliseconds, 2);
                }
                vm.SettingsVM.AnimationIndex = 2;
                Assert.Equal("舒缓", vm.SettingsVM.AnimationLabel);
                if (SystemParameters.ClientAreaAnimation)
                {
                    Assert.Equal(644, MotionPolicy.Page.TotalMilliseconds, 2);
                    Assert.Equal(552, MotionPolicy.Sidebar.TotalMilliseconds, 2);
                    Assert.Equal(276, MotionPolicy.Interaction.TotalMilliseconds, 2);
                    Assert.Equal(368, MotionPolicy.Menu.TotalMilliseconds, 2);
                    Assert.Equal(644, MotionPolicy.Scroll.TotalMilliseconds, 2);
                }
                var capsule = new CapsuleSlider { Style = (Style)app.Resources["AnimationModeSlider"], Width = 300, Height = 36, Minimum = 0, Maximum = 2 };
                foreach (var value in new[] { 1d, 2d })
                {
                    capsule.Value = value;
                    capsule.Measure(new Size(300, 36)); capsule.Arrange(new Rect(0, 0, 300, 36)); capsule.UpdateLayout();
                    var capsuleBitmap = new RenderTargetBitmap(300, 36, 96, 96, PixelFormats.Pbgra32); capsuleBitmap.Render(capsule);
                    var pixel = new byte[4]; capsuleBitmap.CopyPixels(new Int32Rect(value == 1 ? 134 : 268, 7, 1, 1), pixel, 4, 0);
                    Assert.True(pixel[0] > 170 && pixel[2] < 205 && pixel[1] < 165, "Blue-purple fill must continue beneath the thumb without a white gap");
                    if (value == 2)
                    {
                        var capsuleEncoder = new PngBitmapEncoder(); capsuleEncoder.Frames.Add(BitmapFrame.Create(capsuleBitmap));
                        using var capsuleStream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Animation-slider-fixed.png"))); capsuleEncoder.Save(capsuleStream);
                    }
                }
                vm.SettingsVM.Theme = "Dark";
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                vm.SettingsVM.NavigateSection("Appearance");
                // Render DPI variants without claiming a desktop or monitor-DPI test.
                foreach (var dpi in new[] { 120d, 144d, 192d })
                {
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)(1080 * dpi / 96), (int)(700 * dpi / 96), dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(root);
                    var output = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"Settings-dark-{dpi}.png")); encoder.Save(stream);
                }
                vm.SettingsVM.ShowDownloadDetails = true;
                for (int i = 1; i <= 4; i++) vm.SettingsVM.DownloadConnections.Add(new(i, i * 100000, 1000000, 300000, "下载中"));
                root.UpdateLayout();
                var detailsBitmap = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32); detailsBitmap.Render(root);
                var detailsEncoder = new PngBitmapEncoder(); detailsEncoder.Frames.Add(BitmapFrame.Create(detailsBitmap));
                using (var stream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Download-details.png")))) detailsEncoder.Save(stream);
                settingsPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                vm.SettingsVM.ShowDownloadDetails = false;
                // Native HWND checks are distinct from a visible desktop/DPI acceptance test.
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                vm.SettingsVM.WindowTransparencyPercent = 30;
                Assert.True(GetLayeredWindowAttributes(handle, out _, out var alpha, out _));
                Assert.InRange((int)alpha, 178, 179);
                Pump();
                Assert.True(GetLayeredWindowAttributes(handle, out _, out alpha, out _));
                Assert.InRange((int)alpha, 178, 179);
                vm.SettingsVM.WindowTransparencyPercent = 0;
                Assert.False(GetLayeredWindowAttributes(handle, out _, out _, out _));
                Pump();
                if (Environment.OSVersion.Version.Build < 22000)
                {
                    Assert.Null(System.Windows.Shell.WindowChrome.GetWindowChrome(window));
                    Assert.Equal(0, GetWindowLongPtr(handle, -16).ToInt64() & 0x00C00000);
                    Assert.True(GetClientRect(handle, out var client)); Assert.True(GetWindowRect(handle, out var windowBounds));
                    Assert.Equal(windowBounds.Right - windowBounds.Left, client.Right - client.Left);
                    Assert.Equal(windowBounds.Bottom - windowBounds.Top, client.Bottom - client.Top);
                    var region = CreateRectRgn(0, 0, 0, 0);
                    try
                    {
                        Assert.NotEqual(0, GetWindowRgn(handle, region));
                        GetRgnBox(region, out var bounds);
                        Assert.Equal(windowBounds.Right - windowBounds.Left, bounds.Right - bounds.Left);
                        Assert.Equal(windowBounds.Bottom - windowBounds.Top, bounds.Bottom - bounds.Top);
                        Assert.False(PtInRegion(region, bounds.Left, bounds.Top));
                        Assert.False(PtInRegion(region, bounds.Right - 1, bounds.Top));
                        Assert.False(PtInRegion(region, bounds.Left, bounds.Bottom - 1));
                        Assert.False(PtInRegion(region, bounds.Right - 1, bounds.Bottom - 1));
                        Assert.True(PtInRegion(region, (bounds.Right + bounds.Left) / 2, (bounds.Bottom + bounds.Top) / 2));
                    }
                    finally { DeleteObject(region); }
                }
                if (Environment.OSVersion.Version.Build < 22000) CheckRealizedFrame();
                var gameRoot = System.IO.Path.Combine(directory, "游戏目录"); System.IO.Directory.CreateDirectory(gameRoot);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(gameRoot, "mods"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(gameRoot, "mods", "测试模组.jar"), "fixture");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(gameRoot, "saves", "测试世界"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(gameRoot, "saves", "测试世界", "level.dat"), "fixture");
                var version = new VersionInfo("测试 Fabric", gameRoot, "Fabric", 21);
                var fixtureDirectory = System.IO.Path.Combine(gameRoot, "versions", version.Id);
                System.IO.Directory.CreateDirectory(fixtureDirectory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(fixtureDirectory, version.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = version.Id, type = "release", mainClass = "net.minecraft.client.main.Main", minecraftArguments = "--version ${version_name}",
                    assets = "legacy", javaVersion = new { majorVersion = 21 }, libraries = new[] { new { name = "net.fabricmc:fabric-loader:0.16.10" } }
                }));
                System.IO.File.WriteAllText(System.IO.Path.Combine(fixtureDirectory, ".ikun-profile.json"), System.Text.Json.JsonSerializer.Serialize(new GameProfile(version.GameName, "1.21")));
                vm.VersionsVM.GameRoot = gameRoot;
                version = VersionService.ReadDetails(gameRoot, version.Id);
                vm.VersionsVM.Versions.Add(version);
                foreach (var loader in new[] { "原版", "Forge", "NeoForge", "Quilt", "OptiFine" })
                    vm.VersionsVM.Versions.Add(new VersionInfo(loader == "NeoForge" ? "内战1111" : "1.20.1-" + loader, gameRoot, loader, 17) { MinecraftVersion = "1.20.1" });
                vm.Navigate("Versions");
                vm.VersionsVM.SelectVersion(version);
                Assert.Equal("Versions", vm.CurrentPage);
                vm.VersionsVM.SelectAndLaunch(version);
                Assert.Equal("Launch", vm.CurrentPage);
                vm.Navigate("Versions"); root.UpdateLayout(); Pump(); root.UpdateLayout();
                var versionRow = Descendants(root).OfType<Border>().Single(x => x.Style == app.Resources["SelectedItemRow"] && Equals(x.Tag, true));
                Assert.Equal("#22007AFF", versionRow.Background.ToString());
                Assert.Equal("管理", Assert.Single(Descendants(versionRow).OfType<Button>()).Content);
                Assert.InRange(versionRow.ActualHeight, 58, 64);
                Assert.NotNull(Assert.Single(Descendants(versionRow).OfType<Image>()).Source);
                SavePreview(root, "Version-rows.png");
                var iconConverter = new LoaderIconConverter();
                foreach (var loader in new[] { "原版", "Forge", "NeoForge", "Fabric", "Quilt", "OptiFine", "未知" })
                    Assert.IsType<BitmapImage>(iconConverter.Convert(loader, typeof(ImageSource), null, CultureInfo.InvariantCulture));
                vm.ContentVM.OpenOverview(version); root.UpdateLayout(); Pump();
                var overviewDeadline = DateTime.UtcNow.AddSeconds(4);
                while (vm.ContentVM.IsBusy && DateTime.UtcNow < overviewDeadline) { Pump(); Thread.Sleep(10); }
                Assert.True(vm.ContentVM.IsOverview);
                Assert.Contains("1 Mod", vm.ContentVM.ContentSummary);
                Assert.Contains("1 存档", vm.ContentVM.ContentSummary);
                Assert.Contains(Descendants(root).OfType<TextBox>(), x => x.Text == version.GameName);
                SavePreview(root, "Version-overview.png");
                vm.Settings.IsolationOverrides[AppPaths.VersionKey(version)] = IsolationMode.Shared;
                vm.ContentVM.GameNameDraft = "我的生存 世界";
                var rename = vm.ContentVM.RenameCommand.ExecuteAsync(null);
                var renameDeadline = DateTime.UtcNow.AddSeconds(6);
                while (!rename.IsCompleted && DateTime.UtcNow < renameDeadline) { Pump(); Thread.Sleep(10); }
                Assert.True(rename.IsCompletedSuccessfully);
                Assert.Equal("", vm.ContentVM.Status);
                Assert.Equal("我的生存 世界", vm.CurrentVersion!.GameName);
                Assert.Equal("我的生存 世界", vm.CurrentVersion.Id);
                Assert.Equal(vm.CurrentVersion.Id, vm.Settings.SelectedVersionId);
                Assert.Equal(IsolationMode.Shared, vm.Settings.IsolationOverrides[AppPaths.VersionKey(vm.CurrentVersion)]);
                Assert.False(vm.Settings.IsolationOverrides.ContainsKey(AppPaths.VersionKey(version)));
                Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(gameRoot, "versions", "我的生存 世界")));
                Assert.Equal(gameRoot, vm.ContentVM.GameDirectory);
                version = vm.CurrentVersion;
                overviewDeadline = DateTime.UtcNow.AddSeconds(4);
                var sectionChange = vm.ContentVM.SwitchSectionAsync("Shaders");
                while (!sectionChange.IsCompleted && DateTime.UtcNow < overviewDeadline) { Pump(); Thread.Sleep(10); }
                Assert.True(sectionChange.IsCompletedSuccessfully);
                Assert.Equal(VersionContentKind.ShaderPack, vm.ContentVM.Kind);
                Assert.False(vm.ContentVM.IsOverview);
                Assert.Same(vm.ContentVM, Descendants(root).OfType<TransitionHost>().Single().Content);
                vm.ContentVM.BackCommand.Execute(null);
                var account = AccountService.Offline("TestPlayer"); vm.AccountsVM.Accounts.Add(account);
                vm.CurrentAccount = AccountService.Offline("OtherPlayer");
                vm.Navigate("Accounts"); root.UpdateLayout(); Pump(); root.UpdateLayout();
                var accountRow = Descendants(root).OfType<Border>().Single(x => x.Style == app.Resources["SelectedItemRow"] && ReferenceEquals(x.DataContext, account));
                accountRow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Assert.Same(account, vm.CurrentAccount);
                Assert.Equal("Accounts", vm.CurrentPage);
                root.UpdateLayout(); Pump(); root.UpdateLayout();
                Assert.Equal(true, accountRow.Tag);
                Assert.Equal("移除账户", Assert.Single(Descendants(accountRow).OfType<Button>()).ToolTip);
                Assert.Contains(Descendants(accountRow).OfType<TextBlock>(), x => x.Text == " · 当前" && x.Visibility == Visibility.Visible);
                SavePreview(root, "Account-selection.png");
                vm.AccountsVM.SelectAndReturnHome(account);
                Assert.Same(account, vm.CurrentAccount);
                Assert.Equal("Launch", vm.CurrentPage);
                vm.Navigate("Versions"); root.UpdateLayout();
                vm.ContentVM.Open(version, VersionContentKind.Mod); root.UpdateLayout(); Pump(); root.UpdateLayout();
                var scanDeadline = DateTime.UtcNow.AddSeconds(4);
                while (vm.ContentVM.IsBusy && DateTime.UtcNow < scanDeadline) { Pump(); Thread.Sleep(10); }
                Assert.Equal("", vm.ContentVM.Status);
                Assert.Single(vm.ContentVM.Items);
                var cachedMod = vm.ContentVM.Items[0];
                WaitForUi(vm.ContentVM.SwitchSectionAsync("Saves"));
                WaitForUi(vm.ContentVM.SwitchSectionAsync("Mods"));
                Assert.Same(cachedMod, vm.ContentVM.Items[0]);
                WaitForUi(vm.ContentVM.ToggleCommand.ExecuteAsync(cachedMod));
                Assert.False(vm.ContentVM.Items[0].Enabled);
                WaitForUi(vm.ContentVM.SwitchSectionAsync("Saves"));
                WaitForUi(vm.ContentVM.SwitchSectionAsync("Mods"));
                Assert.False(vm.ContentVM.Items[0].Enabled);
                WaitForUi(vm.ContentVM.ToggleCommand.ExecuteAsync(vm.ContentVM.Items[0]));
                Assert.True(vm.ContentVM.Items[0].Enabled);
                Assert.Same(vm.ContentVM, Descendants(root).OfType<TransitionHost>().Single().Content);
                Assert.Single(Descendants(Descendants(root).OfType<TransitionHost>().Single()).OfType<UserControl>());
                Assert.Contains(Descendants(root).OfType<Button>(), x => Equals(x.Content, "导入"));
                vm.ContentVM.BackCommand.Execute(null); root.UpdateLayout();
                Assert.Same(vm.VersionsVM, Descendants(root).OfType<TransitionHost>().Single().Content);
                vm.ContentVM.Open(version, VersionContentKind.Save); root.UpdateLayout();
                scanDeadline = DateTime.UtcNow.AddSeconds(4);
                while (vm.ContentVM.IsBusy && DateTime.UtcNow < scanDeadline) { Pump(); Thread.Sleep(10); }
                Assert.Equal("", vm.ContentVM.Status);
                Assert.Single(vm.ContentVM.Items);
                var contentBitmap = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32); contentBitmap.Render(root);
                var contentEncoder = new PngBitmapEncoder(); contentEncoder.Frames.Add(BitmapFrame.Create(contentBitmap));
                using (var stream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Content-management.png")))) contentEncoder.Save(stream);
                var memory = vm.SettingsVM.RefreshMemoryAsync(version);
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while ((!memory.IsCompleted || vm.ContentVM.IsBusy) && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
                Assert.True(memory.IsCompletedSuccessfully); Assert.InRange(vm.SettingsVM.MemoryMb, 256, vm.SettingsVM.AvailableMemoryMb);
                Assert.True(vm.SettingsVM.SmartMemory);
                vm.SettingsVM.MemoryMb = 256; Assert.False(vm.SettingsVM.SmartMemory);
                CheckCacheAndVirtualization(vm, root);
                CheckLightButtons(vm, root, app);
                CheckHorizontalMotion(app);
                CheckAgentMode(vm, window, root);
                vm.LogsVM.Dispose(); vm.SettingsVM.Dispose();
                Assert.Equal(UiAnimationMode.Calm, new SettingsStore(directory).Load().UiAnimationMode);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI layout timed out");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckAgentMode(MainViewModel vm, MainWindow window, FrameworkElement root)
    {
        var page = vm.CurrentPage; var sidebar = vm.SidebarExpanded; var offset = vm.SettingsVM.ScrollOffset;
        MotionPolicy.Set(UiAnimationMode.Off);
        vm.IsAiMode = true; Pump(); Layout();
        Assert.False(vm.EffectiveSidebarExpanded); Assert.False(vm.TopNavigationVisible); Assert.Equal(sidebar, vm.SidebarExpanded);
        Assert.Equal(46, ((Border)window.FindName("TitleBar")).ActualHeight);
        Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("NormalSurface")).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("AgentSurface")).Visibility);
        vm.SettingsVM.Theme = "Light"; Capture("AI-welcome-light");
        vm.SettingsVM.Theme = "Dark"; Capture("AI-welcome-dark");
        vm.AgentVM.IsConfigured = true;
        vm.AgentVM.Models.Add(new("fixture-pro", "DeepSeek Pro")); vm.AgentVM.Models.Add(new("fixture-flash", "DeepSeek Flash")); vm.AgentVM.SelectedModel = vm.AgentVM.Models[0];
        vm.AgentVM.Timeline.Add(new(new(AgentUiEventKind.User, "你", "想玩一个探索类整合包")));
        vm.AgentVM.Timeline.Add(new(new(AgentUiEventKind.Assistant, "DeepSeek", "找到了几个候选。先选一下版本和玩法，再为你安装。")));
        var question = new AgentTimelineItem(new(AgentUiEventKind.Question, "需要你的选择"));
        question.Questions.Add(new(new("version", "想用哪个版本？", [new("1.20.1", "整合包选择较多", true), new("1.21.1", "较新的玩法")])));
        vm.AgentVM.Timeline.Add(question);
        Assert.Null(question.Questions[0].SelectedOption);
        vm.SettingsVM.Theme = "Light"; Capture("AI-timeline-light"); vm.SettingsVM.Theme = "Dark"; Capture("AI-timeline-dark");
        CheckAgentScrollingAndComposition();
        CheckAgentThemeMarkdownAndProgress();
        vm.RequestAccountLogin(); Layout(); Assert.False(vm.AiSurfaceVisible); Assert.True(vm.NormalSurfaceVisible); Assert.Equal("Accounts", vm.CurrentPage);
        vm.CompleteAccountLogin(); Layout(); Assert.Equal(page, vm.CurrentPage);
        vm.IsAiMode = false; Pump(); Layout(); Assert.Equal(page, vm.CurrentPage); Assert.Equal(sidebar, vm.EffectiveSidebarExpanded);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("AgentSurface")).Visibility);
        Assert.Equal(offset, vm.SettingsVM.ScrollOffset);
        vm.AgentVM.IsConfigured = false; vm.AgentVM.Timeline.Clear(); MotionPolicy.Set(UiAnimationMode.Calm);
        void Layout() { root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout(); }
        void Capture(string name)
        {
            Layout(); var image = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/" + name + ".png"))); encoder.Save(output);
        }
        void CheckAgentScrollingAndComposition()
        {
            var agentPage = (Launcher.App.Views.Pages.AgentPage)window.FindName("AgentSurface");
            var list = (ListBox)agentPage.FindName("TimelineList");
            var composer = (TextBox)agentPage.FindName("Composer");
            var placeholder = (TextBlock)agentPage.FindName("ComposerPlaceholder");
            var tall = new AgentTimelineItem(new(AgentUiEventKind.Question, "需要你的选择"));
            for (var i = 0; i < 3; i++)
                tall.Questions.Add(new(new("q" + i, "请选择第 " + (i + 1) + " 项", [new("方案 A", "先查询候选，再为你安装"), new("方案 B", "保留现有设置"), new("方案 C", "按你的选择继续")])));
            vm.AgentVM.Timeline.Add(tall); Layout(); Pump(); Layout();
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
            var scroll = (ScrollViewer)list.Template.FindName("PART_ScrollViewer", list);
            Assert.True(scroll.ScrollableHeight > 300);
            scroll.ScrollToEnd(); Layout(); Pump(); Layout();
            var card = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(tall);
            var submit = Descendants(card).OfType<Button>().Single(x => Equals(x.Content, "继续"));
            var top = submit.TranslatePoint(new Point(), scroll).Y;
            Assert.InRange(top, 0, scroll.ViewportHeight - submit.ActualHeight + 1);
            var listBottom = list.TranslatePoint(new Point(0, list.ActualHeight), agentPage).Y;
            Assert.True(listBottom <= composer.TranslatePoint(new Point(), agentPage).Y);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 10.5); Layout(); Pump(); Layout();
            Assert.InRange(scroll.ScrollableHeight - scroll.VerticalOffset, 10, 11);
            var options = Descendants(card).OfType<ListBox>().Last();
            var before = scroll.VerticalOffset;
            options.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Layout(); Pump(); Layout(); Assert.InRange(before - scroll.VerticalOffset, 60, 85);
            vm.AgentVM.Input = ""; Assert.Equal(Visibility.Visible, placeholder.Visibility);
            var composition = new TextComposition(InputManager.Current, composer, "wo");
            composer.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            Assert.Equal(Visibility.Collapsed, placeholder.Visibility); Assert.Equal("", composer.Text);
            composer.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputUpdateEvent });
            Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
            composer.Text = "我";
            composer.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent });
            Pump(); Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
            composer.Text = ""; Assert.Equal(Visibility.Visible, placeholder.Visibility);
            composer.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            composer.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, composer, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Assert.Equal(Visibility.Visible, placeholder.Visibility);
            scroll.ScrollToEnd(); Layout(); Pump(); Capture("AI-question-scroll-bottom");
            vm.AgentVM.Timeline.Remove(tall);
        }
        void CheckAgentThemeMarkdownAndProgress()
        {
            var agentPage = (Launcher.App.Views.Pages.AgentPage)window.FindName("AgentSurface");
            var assistant = vm.AgentVM.Timeline.First(x => x.IsAssistant);
            assistant.Text = "## 安装结果\n\n**已经安装** `cloth-config.jar`，请检查日志。\n\n- Minecraft 1.20.1\n- Forge\n\n[项目页面](https://example.test/mod) [不安全链接](file:///C:/private)\n\n```text\n游戏已就绪\n```";
            question.Questions[0].SelectedOption = question.Questions[0].Options[1]; question.CanRespond = false;
            Layout(); Pump(); Layout();
            var markdown = Descendants(agentPage).OfType<MarkdownText>().Single(x => x.Text == assistant.Text);
            var text = Descendants(markdown).OfType<TextBlock>().ToArray();
            Assert.Contains(text, x => x.Inlines.OfType<System.Windows.Documents.Bold>().Any());
            Assert.Single(text.SelectMany(x => x.Inlines.OfType<System.Windows.Documents.Hyperlink>()));
            Assert.DoesNotContain(Descendants(markdown), x => x is Image);
            var outer = (ListBox)agentPage.FindName("TimelineList");
            var options = Descendants(outer).OfType<ListBox>().Single();
            foreach (var theme in new[] { "Light", "Dark" })
            {
                vm.SettingsVM.Theme = theme; Layout(); Pump(); Layout();
                var selected = (ListBoxItem)options.ItemContainerGenerator.ContainerFromIndex(1);
                var choice = (Border)selected.Template.FindName("Choice", selected);
                var color = Assert.IsType<SolidColorBrush>(choice.Background).Color;
                Assert.Equal(((SolidColorBrush)Application.Current.Resources["AiChoiceSelectedBrush"]).Color, color);
                if (theme == "Dark") Assert.True(color.R < 80 && color.G < 100 && color.B < 120);
                Capture("AI-answered-markdown-" + theme.ToLowerInvariant());
            }
            var id = Guid.NewGuid();
            var state = new DownloadTaskInfo(id, "测试游戏", DownloadTaskState.Downloading, "资源文件", new(100, 100, 1000, [new(1, 100, 100, 1000, "下载中")])) { OverallProgress = new(2, 4, "安装与校验游戏") };
            var item = new AgentTimelineItem(new(AgentUiEventKind.Operation, state.Name, Id: id.ToString()) { TaskProgress = state }) { IsProgressExpanded = true };
            vm.AgentVM.Timeline.Add(item); Layout(); Pump(); Layout();
            outer.ScrollIntoView(item); Layout(); Pump(); Layout();
            var progress = Descendants(agentPage).OfType<TaskProgressView>().Single(x => x.Task?.Id == id);
            Assert.IsType<LinearGradientBrush>(progress.Accent);
            Assert.IsType<LinearGradientBrush>(Descendants(progress).OfType<AnimatedStepProgressBar>().Single().Foreground);
            Assert.Single(Descendants(progress).OfType<Expander>()); Assert.True(progress.IsExpanded);
            item.Update(new(AgentUiEventKind.Operation, state.Name, Id: id.ToString()) { TaskProgress = state with { Progress = new(0, 1000, 1000, []) } });
            Layout(); Pump(); Layout(); Assert.True(progress.IsExpanded);
            Assert.Equal(50, progress.Task!.Percent); Assert.Equal(0, progress.Task.StagePercent);
            Capture("AI-parent-progress-dark");
            vm.AgentVM.Timeline.Remove(item);
        }
    }

    [Fact]
    public void State_Converter_Handles_Count_Inverse_And_Enabled()
    {
        var converter = new StateConverter(); var culture = CultureInfo.InvariantCulture;
        Assert.Equal(Visibility.Visible, converter.Convert(0, typeof(Visibility), "Inverse", culture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(true, typeof(Visibility), "Inverse", culture));
        Assert.Equal(false, converter.Convert(true, typeof(bool), "Inverse", culture));
        Assert.Equal(Visibility.Visible, converter.Convert(2, typeof(Visibility), null!, culture));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void SavePreview(FrameworkElement root, string name)
    {
        root.UpdateLayout(); Pump(); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui", name)));
        encoder.Save(stream);
    }
    private static void CheckCacheAndVirtualization(MainViewModel vm, FrameworkElement root)
    {
        vm.ContentVM.BackCommand.Execute(null); root.UpdateLayout(); Pump(); root.UpdateLayout();
        var host = Descendants(root).OfType<TransitionHost>().Single();
        var page = host.PresentedView;
        var original = vm.VersionsVM.Versions.ToArray();
        vm.VersionsVM.Versions.ReplaceAll(Enumerable.Range(0, 2000).Select(i => new VersionInfo("测试游戏 " + i, vm.VersionsVM.GameRoot, "Fabric", 21)));
        root.UpdateLayout(); Pump(); root.UpdateLayout();
        var list = Descendants(host).OfType<ListBox>().Single();
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        Assert.InRange(Descendants(list).OfType<ListBoxItem>().Count(), 1, 60);
        var scroll = (ScrollViewer)list.Template.FindName("PART_ScrollViewer", list);
        scroll.ScrollToVerticalOffset(1200); root.UpdateLayout(); Pump(); root.UpdateLayout();
        Assert.InRange(Descendants(list).OfType<ListBoxItem>().Count(), 1, 60);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            vm.Navigate("Accounts"); root.UpdateLayout();
            vm.Navigate("Versions"); root.UpdateLayout();
            Assert.Same(page, host.PresentedView);
            Assert.Single(Descendants(host).OfType<UserControl>());
        }
        watch.Stop();
        Assert.Equal(7, host.CachedViewCount);
        System.IO.File.WriteAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Navigation-performance.txt")), $"40 cached navigation/layout changes with 2000 versions: {watch.Elapsed.TotalMilliseconds:F1} ms; realized rows: {Descendants(list).OfType<ListBoxItem>().Count()}; cached pages: {host.CachedViewCount}. Offscreen measurement, not desktop FPS.");
        vm.VersionsVM.Versions.ReplaceAll(original); vm.VersionsVM.ScrollOffset = 0;
    }
    private static void WaitForUi(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
        Assert.True(task.IsCompletedSuccessfully, task.Exception?.ToString());
    }
    private static void CheckLightButtons(MainViewModel vm, FrameworkElement root, Application app)
    {
        vm.SettingsVM.Theme = "Light";
        SavePreview(root, "Versions-light-cached.png");
        var edge = Assert.IsType<SolidColorBrush>(app.Resources["GlassEdgeBrush"]);
        Assert.InRange(edge.Color.A, (byte)32, (byte)64); Assert.Equal((byte)0, edge.Color.R);
        MotionPolicy.Set(UiAnimationMode.Off);
        var normal = new Button { Content = "普通按钮", Width = 150, Style = (Style)app.Resources["AppleSecondaryButton"], Margin = new Thickness(12) };
        var hover = new Button { Content = "悬停光效", Width = 150, Style = normal.Style, Margin = normal.Margin };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        panel.Children.Add(normal); panel.Children.Add(hover);
        panel.Measure(new Size(348, 60)); panel.Arrange(new Rect(0, 0, 348, 60)); panel.UpdateLayout();
        hover.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        var highlight = Assert.IsType<Border>(hover.Template.FindName("PART_Highlight", hover));
        Assert.Equal(.62, highlight.Opacity, 2);
        var hoverSurface = Assert.IsType<Border>(hover.Template.FindName("Surface", hover));
        Assert.Equal("#40828F9F", hoverSurface.BorderBrush.ToString());
        Assert.Equal(new Thickness(0), hoverSurface.BorderThickness);
        var bitmap = new RenderTargetBitmap(348, 60, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
        var first = new byte[4]; var second = new byte[4];
        bitmap.CopyPixels(new Int32Rect(87, 20, 1, 1), first, 4, 0); bitmap.CopyPixels(new Int32Rect(261, 20, 1, 1), second, 4, 0);
        Assert.True(first[2] - second[2] > 15, "Light glass hover must remain visible on a white surface");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Light-button-contrast.png"))); encoder.Save(stream);
        var primary = new Button { Content = "继续 →", Width = 150, Style = (Style)app.Resources["ApplePrimaryButton"] };
        primary.Measure(new Size(150, 40)); primary.Arrange(new Rect(0, 0, 150, 40)); primary.UpdateLayout();
        var blue = Assert.IsType<SolidColorBrush>(primary.Background);
        Assert.Equal(((SolidColorBrush)app.Resources["AccentBrush"]).Color, blue.Color);
        Assert.Equal(Color.FromRgb(0, 122, 255), blue.Color);
        Assert.Equal(normal.Height, primary.Height); Assert.Equal(normal.Padding, primary.Padding); Assert.Equal(normal.FontSize, primary.FontSize);
        Assert.Equal(((Border)normal.Template.FindName("Surface", normal)).CornerRadius, ((Border)primary.Template.FindName("Surface", primary)).CornerRadius);
        Assert.Equal(new Thickness(1), ((Border)primary.Template.FindName("Edge", primary)).BorderThickness);
        var primaryBitmap = new RenderTargetBitmap(150, 40, 96, 96, PixelFormats.Pbgra32); primaryBitmap.Render(primary);
        var primaryEncoder = new PngBitmapEncoder(); primaryEncoder.Frames.Add(BitmapFrame.Create(primaryBitmap));
        using var primaryStream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Primary-button-refined.png"))); primaryEncoder.Save(primaryStream);
        MotionPolicy.Set(UiAnimationMode.Calm);
    }
    private static void CheckAppDialog(Application app)
    {
        var previous = app.MainWindow;
        var owner = new Window { Left = -20000, Top = -20000, Width = 800, Height = 600, ShowInTaskbar = false, ShowActivated = false };
        Exception? failure = null;
        try
        {
            owner.Show(); app.MainWindow = owner;
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = app.Windows.OfType<Window>().Single(w => w.Title == "删除游戏（预览）");
                try
                {
                    Assert.False(dialog.AllowsTransparency);
                    var surface = Assert.IsType<Border>(dialog.Content);
                    Assert.Equal(new Thickness(1), surface.BorderThickness);
                    Assert.NotEqual(Colors.Transparent, Assert.IsType<SolidColorBrush>(surface.Background).Color);
                    Assert.NotNull(surface.Clip);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Delete-dialog-refined.png"))); encoder.Save(stream);
                    Descendants(surface).OfType<Button>().Single(b => b.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }));
            var result = AppDialog.Show("将“测试游戏”及其独立目录移入回收站？", "删除游戏（预览）", MessageBoxButton.YesNo);
            if (failure is not null) throw failure;
            Assert.Equal(MessageBoxResult.No, result);
        }
        finally { app.MainWindow = previous; owner.Close(); }
    }
    private static void CheckHorizontalMotion(Application app)
    {
        var strip = new SlidingTabStrip { SelectedKey = "Overview", HorizontalAlignment = HorizontalAlignment.Left };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var key in new[] { "Overview", "Mods", "Saves" }) tabs.Children.Add(new RadioButton { Content = key, CommandParameter = key, Width = 80, Height = 36, Style = (Style)app.Resources["AppleNavButtonStyle"] });
        strip.Children.Add(tabs);
        var body = new Border { Height = 60, Background = Brushes.White }; SectionSlideMotion.SetKey(body, "Overview");
        var panel = new StackPanel(); panel.Children.Add(strip); panel.Children.Add(body);
        var probe = new Window { Left = -20000, Top = -20000, Width = 300, Height = 160, ShowInTaskbar = false, ShowActivated = false, Content = panel };
        try
        {
            probe.Show(); Pump(); panel.UpdateLayout();
            MotionPolicy.Set(UiAnimationMode.Calm);
            strip.SelectedKey = "Saves"; SectionSlideMotion.SetKey(body, "Saves");
            var indicator = strip.Children.OfType<Border>().Single();
            if (SystemParameters.ClientAreaAnimation)
            {
                Assert.True(((TranslateTransform)indicator.RenderTransform).HasAnimatedProperties);
                Assert.True(((TranslateTransform)body.RenderTransform).HasAnimatedProperties);
            }
            strip.SelectedKey = "Mods"; SectionSlideMotion.SetKey(body, "Mods");
            MotionPolicy.Set(UiAnimationMode.Off);
            Assert.False(((TranslateTransform)indicator.RenderTransform).HasAnimatedProperties);
            Assert.Equal(80, ((TranslateTransform)indicator.RenderTransform).X);
            Assert.Equal(0, ((TranslateTransform)body.RenderTransform).X);
            Assert.Equal(1, body.Opacity);
        }
        finally { probe.Close(); MotionPolicy.Set(UiAnimationMode.Calm); }
    }
    private static void CheckDownloadWizard(MainViewModel vm, FrameworkElement root)
    {
        var previousMode = MotionPolicy.Mode;
        MotionPolicy.Set(UiAnimationMode.Off);
        vm.Navigate("Downloads"); root.UpdateLayout();
        var page = Descendants(root).OfType<Launcher.App.Views.Pages.DownloadsPage>().Single();
        foreach (var element in Descendants(page).OfType<FrameworkElement>().Where(e => WelcomeMotion.GetDelay(e) >= 0))
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var stepSurface = (Grid)page.FindName("StepSurface");
        vm.DownloadsVM.Projects.Add(new("fixture", "Fixture Mod", "", CatalogKind.Mod));
        vm.DownloadsVM.Tab = "Mod"; vm.DownloadsVM.Step = 0; root.UpdateLayout();
        Assert.DoesNotContain(Descendants(page).OfType<CheckBox>(), check => Equals(check.Content, "适合目标游戏"));
        Assert.NotEmpty(Descendants(page).OfType<ModProjectIcon>());
        Assert.Same(vm.DownloadsVM.RetryReleasesCommand, ((Button)page.FindName("RefreshModVersions")).Command);
        foreach (var theme in new[] { "Light", "Dark" })
        {
            vm.SettingsVM.Theme = theme;
            Pump();
            var foreground = (SolidColorBrush)Application.Current.Resources["TextPrimaryBrush"];
            Assert.Equal(theme == "Dark", foreground.Color.R > 200);
            foreach (var kind in new[] { "Game", "Mod", "Pack" })
            {
                vm.DownloadsVM.Tab = kind;
                for (int step = 0; step < (kind == "Game" ? 4 : 3); step++)
                {
                    vm.DownloadsVM.Step = step; root.UpdateLayout();
                    Assert.Single(stepSurface.Children.OfType<Grid>().Where(g => g.Visibility == Visibility.Visible));
                }
            }
            vm.DownloadsVM.Tab = "Welcome"; root.UpdateLayout();
            Assert.Equal(3, Descendants(page).OfType<Button>().Count(button => button.Command == vm.DownloadsVM.BeginFlowCommand));
            var image = new RenderTargetBitmap(1080, 700, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = System.IO.File.Create(System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/Welcome-" + theme + ".png"))); encoder.Save(stream);
        }
        var oldTarget = vm.DownloadsVM.Target;
        var target = new VersionInfo("fixture-target", System.IO.Path.GetTempPath(), "Forge", 17) { GameName = "我的世界", MinecraftVersion = "1.20.1" };
        vm.VersionsVM.Versions.Add(target); vm.DownloadsVM.Target = target;
        vm.DownloadsVM.Tab = "Mod"; vm.DownloadsVM.Step = 1;
        ContentRelease Release(string id, string mc, string loader) => new(id, "fixture", "测试 " + id, ContentPlatform.Modrinth, CatalogKind.Mod, [mc], [loader], true, [new(id + ".jar", "https://fixture.test/" + id, 10, null)], []);
        vm.DownloadsVM.ApplyReleaseIndex(new([Release("forge", "1.20.1", "forge"), Release("fabric", "1.21.1", "fabric")]));
        root.UpdateLayout();
        Assert.Equal("Forge · 1.20.1", target.DownloadTargetTag);
        Assert.Equal("1.20.1", vm.DownloadsVM.ModGameVersion); Assert.Equal("Forge", vm.DownloadsVM.ModLoader);
        Assert.Equal("forge", vm.DownloadsVM.SelectedRelease!.Id);
        vm.DownloadsVM.ModGameVersion = "1.21.1"; root.UpdateLayout();
        Assert.Equal("Fabric", vm.DownloadsVM.ModLoader); Assert.Equal("fabric", vm.DownloadsVM.SelectedRelease!.Id);
        Assert.Equal(2, ((ComboBox)page.FindName("ModGameVersionFilter")).Items.Count);
        Assert.Equal(48, ((ComboBox)page.FindName("ModVersionFilter")).Height);
        vm.DownloadsVM.IsLoadingReleases = true; root.UpdateLayout();
        var spinner = (ContentControl)page.FindName("ModVersionsLoading");
        Assert.Equal(Visibility.Visible, ((FrameworkElement)spinner.Parent).Visibility);
        Assert.False(((ComboBox)page.FindName("ModVersionFilter")).IsEnabled);
        vm.DownloadsVM.IsLoadingReleases = false; root.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)spinner.Parent).Visibility);
        vm.DownloadsVM.IsRefreshingReleases = true; root.UpdateLayout();
        Assert.Equal(Visibility.Visible, ((FrameworkElement)spinner.Parent).Visibility);
        Assert.True(((ComboBox)page.FindName("ModVersionFilter")).IsEnabled);
        vm.DownloadsVM.ApplyReleaseIndex(new([Release("forge", "1.20.1", "forge"), Release("fabric", "1.21.1", "fabric"), Release("new", "1.21.1", "fabric")]));
        Assert.Equal("fabric", vm.DownloadsVM.SelectedRelease!.Id);
        Assert.Equal("1.21.1", vm.DownloadsVM.ModGameVersion); Assert.Equal("Fabric", vm.DownloadsVM.ModLoader);
        vm.DownloadsVM.IsRefreshingReleases = false; root.UpdateLayout();
        SavePreview(root, "Mod-local-filters.png");
        vm.VersionsVM.Versions.Remove(target); vm.DownloadsVM.Target = oldTarget;
        var id = Guid.NewGuid();
        DownloadTaskInfo TaskWith(int count, int bytes) => new(id, "Fixture", DownloadTaskState.Downloading, "下载", new(bytes, 1000000, 1000, Enumerable.Range(1, count).Select(i => new DownloadConnectionProgress(i, bytes, 1000000, 1000, "下载中", "file:" + i, "fixture.jar")).ToArray())) { OverallProgress = new(2, 4, "安装与校验游戏") };
        var apply = typeof(DownloadsViewModel).GetMethod("ApplyTaskAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _ = (Task)apply.Invoke(vm.DownloadsVM, new object[] { TaskWith(1, 10) })!;
        vm.DownloadsVM.ShowTasks = true; vm.DownloadsVM.Tasks.Single(x => x.Id == id).IsExpanded = true;
        root.UpdateLayout(); Pump(); root.UpdateLayout();
        var panel = (Border)page.FindName("TaskPanel");
        var progressView = Descendants(panel).OfType<TaskProgressView>().Single(x => x.Task?.Id == id);
        var size = panel.RenderSize; var row = progressView.Connections[0];
        foreach (var count in new[] { 4, 8, 2, 1 })
        {
            _ = (Task)apply.Invoke(vm.DownloadsVM, new object[] { TaskWith(count, 20) })!;
            root.UpdateLayout(); Assert.Equal(size, panel.RenderSize); Assert.Same(row, progressView.Connections[0]);
            Assert.True(progressView.IsExpanded); Assert.Single(Descendants(progressView).OfType<Expander>());
        }
        Assert.Equal(600, panel.ActualWidth); Assert.Equal(480, panel.ActualHeight);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(((Border)panel.Parent).Background).Color);
        Assert.NotNull(panel.Effect); Assert.Equal(new Thickness(1), panel.BorderThickness);
        vm.SettingsVM.Theme = "Light"; root.UpdateLayout();
        SavePreview(root, "Task-panel-light-refined.png");
        vm.SettingsVM.Theme = "Dark"; root.UpdateLayout();
        SavePreview(root, "Task-panel-dark-refined.png");
        vm.DownloadsVM.ShowTasks = false;
        CheckDownloadPresentation(vm, root, page);
        vm.DownloadsVM.Step = 0; vm.SettingsVM.Theme = "Light";
        MotionPolicy.Set(previousMode);
        CheckWelcomeEntrance();
    }
    private static void CheckDownloadPresentation(MainViewModel vm, FrameworkElement root, Launcher.App.Views.Pages.DownloadsPage page)
    {
        var model = vm.DownloadsVM;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cache = (Dictionary<string, (IReadOnlyList<LoaderCatalogVersion> Loaders, IReadOnlyList<OptiFineCatalogVersion> Extras)>)typeof(DownloadsViewModel).GetField("_componentCache", flags)!.GetValue(model)!;
        foreach (var kind in model.LoaderKinds) cache["1.20.1|" + kind] = (kind == "原版" ? [] : [new("47.3.0", "1.20.1", kind)], []);
        model.SelectedGame = new("1.20.1", "release", "https://fixture.test/version.json", new string('0', 40));
        model.Tab = "Game"; model.Step = 1;
        model.SelectLoaderCommand.Execute("Forge"); root.UpdateLayout();
        var choices = Descendants(page).OfType<Button>().Where(button => button.Command == model.SelectLoaderCommand).ToArray();
        Assert.Equal(4, choices.Length);
        var selected = choices.Single(button => Equals(button.CommandParameter, "Forge"));
        Assert.Equal(((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color, ((SolidColorBrush)selected.BorderBrush).Color);
        Assert.All(choices, button => Assert.NotNull(Descendants(button).OfType<Image>().Single().Source));
        Assert.Equal(4, choices.Select(button => Descendants(button).OfType<Image>().Single().Source).Distinct().Count());
        var loaderVersion = Descendants(page).OfType<ComboBox>().Single(combo => ReferenceEquals(combo.ItemsSource, model.Loaders));
        var stepSurface = (Grid)page.FindName("StepSurface");
        Assert.InRange(loaderVersion.TranslatePoint(new Point(0, loaderVersion.ActualHeight), stepSurface).Y, 0, stepSurface.ActualHeight);
        foreach (var theme in new[] { "Light", "Dark" }) { vm.SettingsVM.Theme = theme; SavePreview(root, "Loader-cards-" + theme + ".png"); }
        var localized = typeof(DownloadsViewModel).GetField("_localizingProjects", flags)!;
        localized.SetValue(model, true);
        try { model.SelectedProject = new("iris", "Iris Shaders", "", CatalogKind.Mod) { ChineseName = "鸢尾" }; }
        finally { localized.SetValue(model, false); }
        model.Target = new("my-world", System.IO.Path.GetTempPath(), "Fabric", 21) { GameName = "我的世界", MinecraftVersion = "1.21.1" };
        model.SelectedRelease = new("iris-file", "iris", "Iris 1.7.3 for Minecraft 1.21", ContentPlatform.Modrinth, CatalogKind.Mod, ["1.21", "1.21.1"], ["fabric", "quilt"], true, [new("iris.jar", "https://fixture.test/iris.jar", 10, null)], []);
        model.Tab = "Mod"; model.Step = 2; root.UpdateLayout();
        var surface = (Grid)page.FindName("StepSurface");
        var texts = Descendants(surface.Children.OfType<Grid>().Single(grid => grid.Visibility == Visibility.Visible)).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Contains("目标游戏", texts); Assert.Contains("模组版本", texts); Assert.Contains("加载器", texts); Assert.Contains("Minecraft", texts);
        Assert.DoesNotContain("兼容性由你确认", texts); Assert.Equal("Fabric / Quilt", model.ReleaseLoaders);
        SavePreview(root, "Mod-confirm-labeled.png");
        var pack = new DownloadCatalogItem("fixture-pack", "New World Pack", "", CatalogKind.Modpack);
        var browse = (Dictionary<string, DownloadCatalogItem[]>)typeof(DownloadsViewModel).GetField("_browseCache", flags)!.GetValue(model)!;
        browse["Pack"] = [pack]; model.Tab = "Pack";
        localized.SetValue(model, true);
        try { model.SelectedProject = pack; } finally { localized.SetValue(model, false); }
        model.SelectedRelease = new("pack-file", "fixture-pack", "New World Pack 1.5.2", ContentPlatform.Modrinth, CatalogKind.Modpack, ["1.20.1"], ["fabric"], true, [new("pack.mrpack", "https://fixture.test/pack.mrpack", 10, null)], []);
        model.PackName = "我的冒险"; model.Step = 2; root.UpdateLayout();
        texts = Descendants(surface.Children.OfType<Grid>().Single(grid => grid.Visibility == Visibility.Visible)).OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Contains("游戏名称", texts); Assert.Contains("整合包", texts); Assert.Contains("文件版本", texts); Assert.Contains("安装目录", texts);
        SavePreview(root, "Pack-confirm-labeled.png");
        CheckStepProgressAnimation((Style)page.Resources["WizardProgressStyle"]);
        var oldRoot = vm.Settings.GameRoot;
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ikun-wizard-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var archivePath = System.IO.Path.Combine(directory, "queue-fixture.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new System.IO.StreamWriter(archive.CreateEntry("modrinth.index.json").Open()))
                writer.Write("{\"formatVersion\":1,\"game\":\"minecraft\",\"name\":\"fixture\",\"dependencies\":{\"minecraft\":\"1.20.1\",\"quilt-loader\":\"0.26.4\"},\"files\":[]}");
            vm.Settings.GameRoot = directory;
            var import = model.ImportModpackFilesAsync([archivePath]);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!import.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Assert.True(import.IsCompletedSuccessfully);
            Assert.True(model.IsWelcome); Assert.True(model.ShowTasks); Assert.Equal(0, model.Step);
            var stop = model.Queue.CancelAndWaitAsync();
            while (!stop.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Assert.True(stop.IsCompletedSuccessfully);
            MotionPolicy.Set(UiAnimationMode.Off);
            foreach (var element in Descendants(page).OfType<FrameworkElement>().Where(element => WelcomeMotion.GetDelay(element) >= 0))
                element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            root.UpdateLayout(); SavePreview(root, "Task-with-welcome-background.png");
            model.ShowTasks = false; root.UpdateLayout(); Assert.True(model.IsWelcome);
            Assert.Equal(Visibility.Visible, ((Grid)page.FindName("FlowSurface")).Children.OfType<Grid>().First().Visibility);
            SavePreview(root, "Download-home-after-queue.png");
        }
        finally { vm.Settings.GameRoot = oldRoot; System.IO.Directory.Delete(directory, true); }
    }
    private static void CheckStepProgressAnimation(Style style)
    {
        var previous = MotionPolicy.Mode;
        var progress = new AnimatedStepProgressBar { Style = style, Value = 25 };
        var probe = new Window { Content = progress, Width = 400, Height = 40, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            foreach (var mode in new[] { UiAnimationMode.Calm, UiAnimationMode.Performance })
            {
                MotionPolicy.Set(mode); if (!probe.IsVisible) probe.Show(); Pump();
                progress.Value = mode == UiAnimationMode.Calm ? 50 : 75;
                var clip = (RectangleGeometry)((Border)progress.Template.FindName("PART_AnimatedFill", progress)).Clip;
                if (MotionPolicy.Page > TimeSpan.Zero) Assert.True(clip.HasAnimatedProperties);
                Assert.Equal(8, progress.ActualHeight);
            }
            MotionPolicy.Set(UiAnimationMode.Off); progress.Value = 25; Pump();
            var final = (RectangleGeometry)((Border)progress.Template.FindName("PART_AnimatedFill", progress)).Clip;
            Assert.False(final.HasAnimatedProperties); Assert.Equal(progress.ActualWidth / 4, final.Rect.Width, 2);
        }
        finally { probe.Close(); MotionPolicy.Set(previous); }
    }
    private static void CheckWelcomeEntrance()
    {
        MotionPolicy.Set(UiAnimationMode.Calm);
        var button = new Button { Width = 500, Height = 90 };
        WelcomeMotion.SetDelay(button, 300);
        var surface = new Canvas { ClipToBounds = true }; surface.Children.Add(button); Canvas.SetLeft(button, 200);
        var probe = new Window { Content = surface, Width = 850, Height = 250, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            probe.Show(); Pump();
            if (MotionPolicy.Page != TimeSpan.Zero)
            {
                Assert.Equal(0, button.Opacity);
                var offset = ((TransformGroup)button.RenderTransform).Children.OfType<TranslateTransform>().Single();
                Assert.True(Canvas.GetLeft(button) + offset.X + button.ActualWidth < 0);
            }
            MotionPolicy.Set(UiAnimationMode.Off); Pump();
            Assert.Equal(1, button.Opacity);
            Assert.Equal(0, ((TransformGroup)button.RenderTransform).Children.OfType<TranslateTransform>().Single().X);
        }
        finally { probe.Close(); MotionPolicy.Set(UiAnimationMode.Calm); }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    private static void CheckRealizedFrame()
    {
        var surface = new Border { Background = Brushes.SlateGray };
        var probe = new Window { Content = surface, WindowStyle = WindowStyle.None, AllowsTransparency = false,
            ResizeMode = ResizeMode.CanResize, Width = 480, Height = 320, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false };
        NativeWindowFrame? frame = null; WindowOutline? outline = null;
        probe.SourceInitialized += (_, _) =>
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(probe).Handle)!;
            frame = new NativeWindowFrame(probe, source); outline = new WindowOutline(probe, surface, source);
        };
        try
        {
            probe.Show(); Pump(); Check();
            probe.Left += 32; probe.Width += 120; probe.Height += 70; Pump(); Check();
            probe.WindowState = WindowState.Minimized; Pump(); probe.WindowState = WindowState.Normal; Pump(); Check();
        }
        finally { outline?.Dispose(); frame?.Dispose(); probe.Close(); }
        void Check()
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
            Assert.True(GetClientRect(handle, out var client)); Assert.True(GetWindowRect(handle, out var rect));
            Assert.Equal(rect.Right - rect.Left, client.Right); Assert.Equal(rect.Bottom - rect.Top, client.Bottom);
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(handle, region)); GetRgnBox(region, out var bounds);
                Assert.Equal(client.Right, bounds.Right); Assert.Equal(client.Bottom, bounds.Bottom);
                Assert.False(PtInRegion(region, 0, 0)); Assert.False(PtInRegion(region, client.Right - 1, 0));
                Assert.False(PtInRegion(region, 0, client.Bottom - 1)); Assert.False(PtInRegion(region, client.Right - 1, client.Bottom - 1));
                Assert.True(PtInRegion(region, client.Right / 2, client.Bottom / 2));
            }
            finally { DeleteObject(region); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect bounds);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
