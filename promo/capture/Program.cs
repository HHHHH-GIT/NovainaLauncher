using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Launcher.AI;
using Launcher.App.Controls;
using Launcher.App.ViewModels;
using Launcher.App.Views;
using Launcher.Core;

// Native product visuals with isolated, explicitly scripted demo data. No API keys or user data.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "../public/native");
        Directory.CreateDirectory(output);
        var fixture = Path.Combine(Path.GetTempPath(), "novaina-promo-" + Guid.NewGuid().ToString("N"));
        AppPaths.ConfigureData(fixture);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var name in new[]{"Icons","AppleTheme","ControlTemplates","GlassTheme"})
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri($"pack://application:,,,/NovainaLauncher;component/Styles/{name}.xaml") });
        app.Resources["BoolToVis"]=new Launcher.App.StateConverter(); app.Resources["EqualConverter"]=new Launcher.App.EqualConverter();
        app.Resources["CurrentItemConverter"]=new Launcher.App.CurrentItemConverter(); app.Resources["LoaderIconConverter"]=new Launcher.App.LoaderIconConverter();
        app.Resources["DanmakuActiveConverter"]=new Launcher.App.DanmakuActiveConverter();
        var fontStyle=new Style(typeof(Window));fontStyle.Setters.Add(new Setter(Control.FontFamilyProperty,new FontFamily("Segoe UI Variable, Segoe UI, -apple-system, PingFang SC, Microsoft YaHei, sans-serif")));app.Resources[typeof(Window)]=fontStyle;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var http = new HttpClient(new Offline());
        using var log = new LogService(fixture);
        var secrets = new SecretStore(fixture);
        var accounts = new AccountService(http, secrets, log); var java = new JavaService(http, log);
        var vm = new MainViewModel(new SettingsStore(fixture), secrets, accounts, java, new VersionService(), new LaunchService(http, accounts, java, log), log, http, false);
        var window = new MainWindow(vm, playStartupAnimation: false);
        vm.SettingsVM.WindowTransparencyPercent=100;
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var root = (FrameworkElement)window.Content;
        var shots = new List<string>();
        void Layout() { root.Measure(new Size(1080, 700)); root.Arrange(new Rect(0, 0, 1080, 700)); root.UpdateLayout(); }
        void Wait(Task task) { var end = DateTime.UtcNow.AddSeconds(15); while (!task.IsCompleted && DateTime.UtcNow < end) Pump(); task.GetAwaiter().GetResult(); }
        void Shot(string name)
        {
            if (!AppPaths.Data.Equals(fixture, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Capture data directory escaped its fixture.");
            Layout(); Pump(); Layout(); Pump(); Layout();
            // Only display paths are substituted; all file access remains in the temporary fixture.
            foreach(var text in Descendants(root).OfType<TextBlock>().Where(x=>x.Text.Contains(fixture,StringComparison.OrdinalIgnoreCase)))
                text.SetCurrentValue(TextBlock.TextProperty,text.Text.Replace(fixture,@"C:\Novaina",StringComparison.OrdinalIgnoreCase));
            var pathBindings=new List<(TextBox Text,System.Windows.Data.BindingBase? Binding)>();
            foreach(var text in Descendants(root).OfType<TextBox>().Where(x=>x.Text.Contains(fixture,StringComparison.OrdinalIgnoreCase)))
            {
                var label=text.Text.Replace(fixture,@"C:\Novaina",StringComparison.OrdinalIgnoreCase);
                pathBindings.Add((text,System.Windows.Data.BindingOperations.GetBindingBase(text,TextBox.TextProperty)));
                System.Windows.Data.BindingOperations.ClearBinding(text,TextBox.TextProperty); text.Text=label;
            }
            var cached=vm.Skins.GetCachedAsync(null); Wait(cached); var head=new BitmapImage(); head.BeginInit(); head.CacheOption=BitmapCacheOption.OnLoad; head.UriSource=new Uri(vm.Skins.HeadFile(cached.Result)); head.EndInit();
            foreach(var avatar in Descendants(root).OfType<AccountAvatar>())
                ((Image)typeof(AccountAvatar).GetField("_image",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(avatar)!).Source=head;
            Pump(); Layout();
            var bitmap = new RenderTargetBitmap(2160, 1400, 192, 192, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
            foreach(var (text,binding) in pathBindings) if(binding is not null)System.Windows.Data.BindingOperations.SetBinding(text,TextBox.TextProperty,binding);
            shots.Add(name); Console.WriteLine(name);
        }
        void PopupShot(string name, FrameworkElement child)
        {
            child.DataContext=vm.AgentVM; child.Measure(new Size(860,900)); child.Arrange(new Rect(new Point(),child.DesiredSize)); child.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth*2),(int)Math.Ceiling(child.ActualHeight*2),192,192,PixelFormats.Pbgra32); bitmap.Render(child);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
        }
        void Feed(AgentUiEvent e) => typeof(AgentViewModel).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm.AgentVM, [e]);
        void ResetTimeline() { vm.AgentVM.Timeline.Clear(); typeof(AgentViewModel).GetField("_toolItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm.AgentVM)!.GetType().GetMethod("Clear")!.Invoke(typeof(AgentViewModel).GetField("_toolItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm.AgentVM), null); vm.AgentVM.Input = ""; }
        try
        {
            vm.SettingsVM.AnimationIndex=(int)UiAnimationMode.Off; MotionPolicy.Set(UiAnimationMode.Off); vm.SettingsVM.Theme = "Dark";
            var gameRoot = Path.Combine(fixture, "games"); Directory.CreateDirectory(gameRoot);
            vm.Settings.GameRoot = gameRoot; vm.VersionsVM.GameRoot = gameRoot;
            var versions = new[] {
                new VersionInfo("NovaAdventure", gameRoot, "Fabric", 21) { GameName = "新星冒险", MinecraftVersion = "1.21.1", LoaderVersion = "0.16.14" },
                new VersionInfo("Creative", gameRoot, "原版", 21) { GameName = "创造世界", MinecraftVersion = "1.21.1" },
                new VersionInfo("Engineering", gameRoot, "NeoForge", 21) { GameName = "自动化实验室", MinecraftVersion = "1.21.1", LoaderVersion = "21.1.211" },
                new VersionInfo("Classic", gameRoot, "Forge", 17) { GameName = "经典冒险", MinecraftVersion = "1.20.1", LoaderVersion = "47.4.0" }
            };
            foreach (var v in versions) { vm.VersionsVM.Versions.Add(v); Directory.CreateDirectory(Path.Combine(gameRoot, "versions", v.Id)); }
            vm.CurrentVersion = versions[0];
            var jdk = new JavaRuntimeInfo(@"C:\Java\jdk-21\bin\java.exe", 21, new Version(21,0,8), "x64", "Eclipse Adoptium"); vm.SettingsVM.InstalledJavas.Add(jdk); vm.CurrentJava = jdk;
            var offline = new AccountProfile { Id = "demo-nova", Name = "Nova", Kind = AccountKind.Offline };
            vm.AccountsVM.Accounts.Add(offline); vm.AccountsVM.Accounts.Add(new() { Id="demo-skin", Name="NovaCraft", Kind=AccountKind.LittleSkin }); vm.CurrentAccount=offline;
            Wait(vm.AccountsVM.CancelAndWaitAsync());
            vm.Navigate("Launch"); Shot("home-dark"); vm.SettingsVM.Theme="Light"; Shot("home-light");
            vm.SettingsVM.Theme="Dark"; vm.Navigate("Downloads"); Shot("downloads");
            typeof(DownloadsViewModel).GetField("_loaded",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm.DownloadsVM,true);
            var components=(Dictionary<string,(IReadOnlyList<LoaderCatalogVersion> Loaders,IReadOnlyList<OptiFineCatalogVersion> Extras)>)typeof(DownloadsViewModel).GetField("_componentCache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm.DownloadsVM)!;
            components["1.21.1|原版"]=(Array.Empty<LoaderCatalogVersion>(),Array.Empty<OptiFineCatalogVersion>());
            vm.DownloadsVM.Tab="Game"; vm.DownloadsVM.SelectedGame=new("1.21.1","release","",""); vm.DownloadsVM.Step=1; Shot("loaders");
            vm.DownloadsVM.Tab="Welcome"; vm.Navigate("Versions"); Shot("versions");
            vm.ContentVM.OpenOverview(versions[0]); Pump(); Shot("version-overview");
            vm.ContentVM.Open(versions[0], VersionContentKind.Mod); Pump();
            while(vm.ContentVM.IsBusy) Pump();
            vm.ContentVM.Items.Add(new("sodium.jar","钠 (Sodium)",VersionContentKind.Mod,true,false,1342177,DateTime.Now));
            vm.ContentVM.Items.Add(new("iris.jar","鸢尾 (Iris Shaders)",VersionContentKind.Mod,true,false,3145728,DateTime.Now));
            vm.ContentVM.Items.Add(new("journeymap.jar","旅行地图 (JourneyMap)",VersionContentKind.Mod,true,false,5452595,DateTime.Now)); Shot("mods");
            vm.VersionsVM.InContentPage=false;
            vm.Navigate("Accounts"); Layout();
            var skinPreview=Descendants(root).OfType<SkinPreview>().FirstOrDefault(); if(skinPreview is not null)skinPreview.Visibility=Visibility.Hidden;
            Shot("accounts");
            var accountPage = Descendants(root).OfType<Launcher.App.Views.Pages.AccountsPage>().First();
            var preview=(FrameworkElement)accountPage.FindName("Preview");
            var pos=preview.TranslatePoint(new Point(),root);
            File.WriteAllText(Path.Combine(output,"skin-region.json"),JsonSerializer.Serialize(new{x=pos.X,y=pos.Y,width=preview.ActualWidth,height=preview.ActualHeight}));
            vm.Navigate("Launch"); Shot("danmaku-background");
            vm.Navigate("Logs");
            foreach(var text in new[]{"正在准备 Minecraft 1.21.1","Java 21 · x64 · 环境就绪","Fabric Loader 初始化完成","资源完整性校验通过","游戏进程已启动"}) log.Write(text,LogLevel.Info,true);
            Pump(); Shot("logs");
            // Enter while not configured: no model-network request. Then inject only local fixture state.
            vm.IsAiMode=true; Wait(vm.ModeChangeTask); Pump();
            var agent=vm.AgentVM; agent.IsConfigured=true; agent.ShowConnection=false;
            agent.Models.Add(new("demo-flash","DeepSeek-V4.1-Flash",1048576,393216,["low","high","max"])); agent.SelectedModel=agent.Models[0];
            agent.Conversations.Add(new(Guid.NewGuid(),AgentMode.Basic,"新星冒险整合包",DateTimeOffset.Parse("2026-10-06T09:20:00+08:00")));
            agent.Conversations.Add(new(Guid.NewGuid(),AgentMode.Basic,"分析启动日志",DateTimeOffset.Parse("2026-10-06T09:00:00+08:00")));
            Shot("basic-empty"); agent.Input="帮我打造一个冒险整合包，加入地图和性能优化。"; Shot("basic-draft");
            ResetTimeline(); Feed(new(AgentUiEventKind.User,"你","帮我打造一个冒险整合包，加入地图和性能优化。"));
            Feed(new(AgentUiEventKind.Assistant,"DeepSeek","先确认你的玩法与游戏版本，再为你准备环境。","basic-intro"));
            var card=new AgentTimelineItem(new(AgentUiEventKind.Question,"需要你的选择"));
            card.Questions.Add(new(new("style","你想玩什么类型？",[new("探索与冒险","地图、探索与更多生存体验",true),new("科技与自动化","机械、能源与生产线")])));
            card.Questions.Add(new(new("version","选择游戏版本",[new("Minecraft 1.21.1","较新的版本与丰富的模组选择",true),new("Minecraft 1.20.1","成熟的模组生态")])));
            agent.Timeline.Add(card); Shot("basic-questions");
            foreach(var q in card.Questions) q.SelectedOption=q.Options[0]; Shot("basic-selected");
            card.ResolveQuestions(new Dictionary<string,string>{{"style","探索与冒险"},{"version","Minecraft 1.21.1"}}); Shot("basic-answered");
            ResetTimeline(); Feed(new(AgentUiEventKind.User,"你","准备 Minecraft 1.21.1 的冒险整合包。"));
            foreach(var (id,title) in new[]{("state","查询版本与环境"),("install","安装游戏与加载器"),("mods","安装地图与性能模组")}) Feed(new(AgentUiEventKind.ToolStarted,title){ToolCallId=id,ToolState=AgentToolState.Running});
            Feed(new(AgentUiEventKind.ToolCompleted,"已确认 Minecraft 1.21.1 · Java 21",Detail:"本地环境检查完成。") {ToolCallId="state",ToolState=AgentToolState.Completed});
            var taskId=Guid.NewGuid();
            Feed(new(AgentUiEventKind.Operation,"新星冒险",Id:"demo-download") {ToolCallId="install",TaskProgress=new(taskId,"新星冒险",DownloadTaskState.Downloading,"下载游戏资源",new(48*1048576,64*1048576,4*1048576,[])){OverallProgress=new(2,4,"安装与校验游戏")}}); Shot("basic-running");
            Feed(new(AgentUiEventKind.ToolCompleted,"游戏与 Fabric 已安装",Detail:"Minecraft：1.21.1\n加载器：Fabric\nJava：21") {ToolCallId="install",ToolState=AgentToolState.Completed});
            Feed(new(AgentUiEventKind.ToolCompleted,"地图与性能模组已准备",Detail:"钠 (Sodium)\n旅行地图 (JourneyMap)\n文件校验通过。") {ToolCallId="mods",ToolState=AgentToolState.Completed});
            Shot("basic-collapsed"); var group=(AgentToolGroupItem)agent.Timeline.Last(); group.IsExpanded=true; group.Tools[1].IsDetailExpanded=true; Shot("basic-details");
            group.IsExpanded=false; Feed(new(AgentUiEventKind.Assistant,"DeepSeek","**新星冒险已准备就绪。**\n\nMinecraft 1.21.1 · Fabric · Java 21\n\n地图与性能模组已加入。你可以启动游戏，或继续导出为整合包。","basic-done")); Shot("basic-result");
            var page=(Launcher.App.Views.Pages.AgentPage)window.FindName("AgentSurface");
            agent.Input="/"; agent.UpdateSuggestions(1); Shot("commands"); PopupShot("commands-popup",(FrameworkElement)((System.Windows.Controls.Primitives.Popup)page.FindName("SuggestionsPopup")).Child); agent.ShowSuggestions=false; agent.Input="@"; agent.UpdateSuggestions(1); Shot("references"); PopupShot("references-popup",(FrameworkElement)((System.Windows.Controls.Primitives.Popup)page.FindName("SuggestionsPopup")).Child); agent.ShowSuggestions=false; agent.Input="";
            agent.ContextUsage=new(471000,1048576,[new("系统提示与工具定义",14000),new("用户输入",12000),new("模型输出与推理",130000),new("工具调用与结果",160000),new("日志内容",140000),new("压缩摘要",9000),new("目标与引用",6000)]) {CapacityVerified=true,IsEstimate=true,CompactionCount=1}; Shot("context-ring");
            var usageButton=Descendants(page).OfType<Button>().FirstOrDefault(x=>x.Name.Contains("Context",StringComparison.OrdinalIgnoreCase));
            var usagePopup=(System.Windows.Controls.Primitives.Popup)page.FindName("ContextPopup"); PopupShot("context-popup",(FrameworkElement)usagePopup.Child);
            agent.CompactionNotice="上下文已压缩\n已保留目标、已选版本与实际操作状态。"; Shot("compacted"); agent.CompactionNotice="";
            agent.OpenAiSettingsCommand.Execute(null); Shot("ai-settings"); agent.CloseAiSettingsCommand.Execute(null);
            agent.SelectedModeChoice=agent.ModeChoices.Single(x=>x.Mode==AgentMode.Workbench); while(agent.IsManaging)Pump(); Pump(); ResetTimeline();
            agent.Conversations.Clear(); agent.Conversations.Add(new(Guid.NewGuid(),AgentMode.Workbench,"星莓派 Mod",DateTimeOffset.Parse("2026-10-06T09:35:00+08:00"))); agent.Conversations.Add(new(Guid.NewGuid(),AgentMode.Workbench,"修复编译错误",DateTimeOffset.Parse("2026-10-06T09:10:00+08:00"))); Shot("workbench-empty");
            agent.Input="做一个可以合成的星莓派 Mod，提供中文名称，编译后交付 JAR。"; Shot("workbench-draft");
            agent.Input=""; Feed(new(AgentUiEventKind.User,"你","做一个可以合成的星莓派 Mod，提供中文名称，编译后交付 JAR。"));
            Feed(new(AgentUiEventKind.Assistant,"DeepSeek","我会先核对官方开发流程，再实现物品、配方与中文资源，最后编译打包。","dev-plan")); Shot("workbench-plan");
            ResetTimeline(); var projectFixture=Path.Combine(fixture,"Starberry"); Directory.CreateDirectory(projectFixture); ((WorkbenchOperations)typeof(AgentViewModel).GetField("_workbench",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(agent)!).Authorize(projectFixture); agent.WorkspacePath=@"C:\Novaina\Projects\Starberry";
            agent.SessionGoal="创建星莓派 Mod · 编译并交付 JAR";
            Feed(new(AgentUiEventKind.User,"你","使用 Fabric 1.21.1，继续实现星莓派。"));
            foreach(var (id,title) in new[]{("guide","读取内置开发指南"),("template","初始化官方 Mod 模板"),("code","编辑源码与中文资源"),("build","运行 Gradle 构建")})Feed(new(AgentUiEventKind.ToolStarted,title){ToolCallId=id,ToolState=AgentToolState.Running});
            Feed(new(AgentUiEventKind.ToolCompleted,"已核对版本、映射与 JDK",Detail:"Minecraft 1.21.1\nFabric · Java 21\n官方文档与内置指南") {ToolCallId="guide",ToolState=AgentToolState.Completed});
            Feed(new(AgentUiEventKind.ToolCompleted,"官方模板已准备",Detail:"Fabric 项目结构已初始化。") {ToolCallId="template",ToolState=AgentToolState.Completed});
            Shot("workbench-running");
            Feed(new(AgentUiEventKind.ToolCompleted,"源码、配方与中文资源已写入",Detail:"StarberryMod.java\nassets/starberry/lang/zh_cn.json\ndata/starberry/recipe/starberry_pie.json") {ToolCallId="code",ToolState=AgentToolState.Completed});
            var devGroup=(AgentToolGroupItem)agent.Timeline.Last(); devGroup.Tools[2].IsDetailExpanded=true; Shot("workbench-code");
            Feed(new(AgentUiEventKind.ToolCompleted,"Gradle 构建通过",Detail:"> Task :compileJava\n> Task :processResources\n> Task :remapJar\nBUILD SUCCESSFUL\n\n产物：starberry-1.0.0.jar") {ToolCallId="build",ToolState=AgentToolState.Completed}); Shot("workbench-collapsed");
            devGroup.IsExpanded=true; devGroup.Tools[3].IsDetailExpanded=true; Shot("workbench-build");
            ResetTimeline(); Feed(new(AgentUiEventKind.Assistant,"DeepSeek","## 星莓派 Mod 已编译打包\n\n**交付文件**：`starberry-1.0.0.jar`\n\n- 星莓派物品与合成配方\n- 中文名称与资源文件\n- Fabric 1.21.1 · Java 21\n\n**已验证**：编译、资源处理、JAR 打包。\n\n**待验证**：进入游戏后的玩法与显示效果。","dev-delivery")); Shot("workbench-delivery");
            File.WriteAllText(Path.Combine(output,"capture-manifest.json"),JsonSerializer.Serialize(new{capturedAt=DateTimeOffset.UtcNow,source="Native WPF product pages; scripted offline fixtures",width=2160,height=1400,dipWidth=1080,dipHeight=700,network="offline handler",shots},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally { Wait(vm.AccountsVM.CancelAndWaitAsync()); Wait(vm.DownloadsVM.CancelAndWaitAsync()); vm.LogsVM.Dispose(); vm.SettingsVM.Dispose(); (vm.Skins as IDisposable)?.Dispose(); log.Dispose(); app.Shutdown(); if(Path.GetFullPath(fixture).StartsWith(Path.Combine(Path.GetTempPath(),"novaina-promo-"),StringComparison.OrdinalIgnoreCase))Directory.Delete(fixture,true); }
    }
    private static void Pump(){var frame=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private sealed class Offline:HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("Offline promo fixture")});}
}
