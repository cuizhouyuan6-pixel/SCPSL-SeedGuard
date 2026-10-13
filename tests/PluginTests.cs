// Production Plugin.OnEnabled/OnDisabled and both commands, with EXILED doubles and the real (restricted) game
// assembly. Only the Unity host object is replaced, because it needs the native engine.
using System;
using System.IO;
using System.Reflection;
using CentralAuth;
using Exiled.API.Features;
using ExiledServer = Exiled.Events.Handlers.Server;

namespace SeedGuard.Tests
{
    internal static class PluginTests
    {
        private static int created, disposed;

        private static int Main()
        {
            try
            {
                Console.WriteLine($"运行环境：{(Type.GetType("Mono.Runtime") != null ? "Mono" : "CLR")} / {Environment.Version}");
                Step("A 无关插件缺少依赖时启用不受影响（不扫描程序集）", UnrelatedBrokenAssembly);
                Step("B 生产启用/停用：注册、重复调用与完全撤销", EnableDisable);
                Step("C Unity 宿主创建失败时回滚，并可在同一实例重试", HostFailureRollback);
                Step("D 游戏入口缺失或配置无效时拒绝启用且不留注册", StartupRefusals);
                Step("E 启用后完整链路：新地图→玩家加载→发送伪种子→重启暂停", EndToEnd);
                Step("F .seed 客户端指令与 seedguard 管理指令", Commands);
                Console.WriteLine($"通过：{H.Passed} 项断言（插件生命周期）");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("失败：" + ex);
                return 1;
            }
        }

        private static void Step(string name, Action test)
        {
            Game.ResetVanilla();
            Game.InstallSerializers();
            Log.Lines.Clear();
            Plugin.ChannelFactory = SeedChannel.Resolve;
            Plugin.TickerFactory = FakeTicker;
            created = disposed = 0;
            test();
            H.Check(Plugin.Instance == null, "每项结束后插件已停用");
            Console.WriteLine("  ✓ " + name);
        }

        private static IDisposable FakeTicker(GuardService service)
        {
            created++;
            return new Handle();
        }

        private sealed class Handle : IDisposable
        {
            private bool done;
            public void Dispose()
            {
                if (done) return;
                done = true;
                disposed++;
            }
        }

        private static int Subscribers => Game.GenerationFinished?.GetInvocationList().Length ?? 0;

        private static void UnrelatedBrokenAssembly()
        {
            Assembly broken = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BrokenUnrelatedPlugin.dll"));
            H.Throws<Exception>(() => _ = broken.GetType("Mysqlx.MysqlxReflection", true).GetField("descriptor", H.All).FieldType, "对照：该程序集的字段依赖确实缺失");
            var plugin = new Plugin();
            plugin.OnEnabled();
            H.Check(plugin.Service != null, "启用成功");
            plugin.OnDisabled();
        }

        private static void EnableDisable()
        {
            int before = Subscribers;
            var plugin = new Plugin();
            plugin.OnEnabled();
            H.Check(plugin.Service != null && Plugin.Instance == plugin, "启用后服务就绪");
            H.Equal(before + 1, Subscribers, "订阅真实 OnGenerationFinished");
            H.Equal(1, ExiledServer.RestartingRound.Count, "订阅 RestartingRound");
            H.Equal(1, created, "创建一个 Unity 宿主");
            H.Equal(1, plugin.BaseEnabled, "调用 EXILED 基类启用");
            H.Equal(1, Log.Lines.FindAll(l => l.StartsWith("INFO") && l.Contains("v1.0.1 已启用") && l.Contains("将显示 114514")).Count, "启用日志");
            plugin.OnEnabled();
            H.Equal(before + 1, Subscribers, "重复启用不重复订阅");
            H.Equal(1, ExiledServer.RestartingRound.Count, "重复启用不重复订阅 EXILED 事件");
            H.Equal(1, created, "重复启用不重复创建宿主");
            plugin.OnDisabled();
            H.Equal(before, Subscribers, "停用后退订真实事件");
            H.Equal(0, ExiledServer.RestartingRound.Count, "停用后退订 EXILED 事件");
            H.Equal(1, disposed, "停用后销毁宿主");
            H.Check(plugin.Service == null, "停用后释放服务");
            plugin.OnDisabled();
            H.Equal(1, disposed, "重复停用安全");
            plugin.OnEnabled();
            H.Equal(before + 1, Subscribers, "停用后可再次启用");
            plugin.OnDisabled();
            H.Equal(before, Subscribers, "再次停用");
        }

        private static void HostFailureRollback()
        {
            int before = Subscribers;
            var plugin = new Plugin();
            Plugin.TickerFactory = _ => throw new InvalidOperationException("模拟 Unity 宿主创建失败");
            H.Throws<InvalidOperationException>(() => plugin.OnEnabled(), "宿主失败时启用抛出");
            H.Equal(before, Subscribers, "失败后已退订真实事件");
            H.Equal(0, ExiledServer.RestartingRound.Count, "失败后没有 EXILED 订阅");
            H.Check(plugin.Service == null && Plugin.Instance == null, "失败后无残留实例");
            H.Equal(1, Log.Lines.FindAll(l => l.StartsWith("ERROR") && l.Contains("启用失败")).Count, "失败日志");
            Plugin.TickerFactory = FakeTicker;
            plugin.OnEnabled();
            H.Check(plugin.Service != null, "同一实例重试成功");
            H.Equal(before + 1, Subscribers, "重试后只有一个订阅");
            plugin.OnDisabled();
        }

        private static void StartupRefusals()
        {
            int before = Subscribers;
            var plugin = new Plugin();
            Plugin.ChannelFactory = () => throw new InvalidOperationException("模拟游戏程序集缺少 SeedMessage");
            H.Throws<InvalidOperationException>(() => plugin.OnEnabled(), "入口缺失时拒绝启用");
            H.Equal(0, created, "入口缺失时不创建宿主");
            Plugin.ChannelFactory = SeedChannel.Resolve;
            plugin.Config.NoticeDelay = float.NaN;
            H.Throws<ArgumentException>(() => plugin.OnEnabled(), "NoticeDelay 非法");
            plugin.Config.NoticeDelay = 3;
            plugin.Config.NoticeColor = "红色";
            H.Throws<ArgumentException>(() => plugin.OnEnabled(), "颜色非法");
            plugin.Config.NoticeColor = "red";
            plugin.Config.CheckInterval = 0;
            H.Throws<ArgumentException>(() => plugin.OnEnabled(), "检查间隔非法");
            plugin.Config.CheckInterval = .25f;
            H.Equal(before, Subscribers, "拒绝启用后没有订阅");
            H.Equal(0, ExiledServer.RestartingRound.Count, "拒绝启用后没有 EXILED 订阅");
            var defaults = new Config();
            GuardSettings settings = defaults.ToSettings();
            H.Check(defaults.IsEnabled && !defaults.Debug && !defaults.LogEachPlayer, "默认开关");
            H.Equal(114514, defaults.DisplayedSeed, "默认显示值");
            H.Equal(114514, settings.DisplayedSeed, "默认显示值传入运行设置");
            H.Equal("服务器禁止使用Seed指令", settings.Notice, "默认提示");
            H.Equal(3000L, settings.NoticeDelayMs, "默认提示延迟");
            H.Equal(250L, settings.CheckIntervalMs, "默认检查间隔");
        }

        private static void EndToEnd()
        {
            Game.ServerActive = true;
            var plugin = new Plugin();
            plugin.Config.NoticeDelay = 0;
            plugin.OnEnabled();
            Game.Seed = 20261013;
            Game.MapGenerated = true;
            Game.RaiseGenerationFinished();
            RecordingConnection client = RecordingConnection.Create(70);
            Game.AddHub(2, "玩家甲", ClientInstanceMode.ReadyClient, client, spawned: true);
            plugin.Service.Tick();
            H.Equal(1, client.Sent.Count, "生产链路发送伪种子");
            H.Equal(114514, client.SeedValue(0), "按默认配置发送 114514");
            // Replay the captured packet into the vanilla client handler and console command.
            Game.ServerActive = false;
            Game.SetSingleton(alive: true);
            H.Call(Game.Sync, "ClientReceiveSeed", null, Game.Decode(client.Sent[0].Data, out _));
            H.Equal("Map seed is: 114514", Game.SeedCommandOutput(), "玩家输入 seed 看到 114514");
            Game.ServerActive = true;
            ExiledServer.RestartingRound.Raise();
            H.Check(!plugin.Service.Core.Active, "EXILED 回合重启事件暂停发送");
            RecordingConnection late = RecordingConnection.Create(71);
            Game.AddHub(3, "玩家乙", ClientInstanceMode.ReadyClient, late, spawned: true);
            plugin.Service.Tick();
            H.Equal(0, late.Sent.Count, "暂停期间不发送");
            plugin.OnDisabled();
        }

        private static void Commands()
        {
            var empty = new ArraySegment<string>(new string[0]);
            var client = new SeedClientCommand();
            H.Check(!client.Execute(empty, new TestSender("未启用时", false), out string idle), "插件未启用时同样拒绝");
            H.Equal("服务器禁止使用Seed指令", idle, "未启用时使用默认提示");
            var plugin = new Plugin();
            plugin.OnEnabled();
            H.Check(!client.Execute(empty, new TestSender("玩家甲 (player1@test)", false), out string response), ".seed 返回失败状态");
            H.Equal("服务器禁止使用Seed指令", response, ".seed 回复");
            H.Equal(1, plugin.Service.ClientAttempts, "记录一次查询");
            H.Equal(1, Log.Lines.FindAll(l => l.Contains("玩家甲 (player1@test) 输入了 .seed")).Count, "查询日志");
            plugin.Config.Notice = "自定义提示";
            client.Execute(empty, new TestSender("玩家乙", false), out response);
            H.Equal("自定义提示", response, "使用配置的提示");
            plugin.Config.Notice = "";
            client.Execute(empty, new TestSender("玩家丙", false), out response);
            H.Equal("服务器禁止使用Seed指令", response, "提示为空时使用默认文字");
            H.Equal("seed", client.Command, "客户端指令名");
            var admin = new SeedGuardCommand();
            H.Check(!admin.Execute(empty, new TestSender("访客", false), out response) && response.Contains("seedguard.status"), "无权限拒绝");
            H.Check(admin.Execute(empty, new TestSender("管理员", true), out response) && response.Contains("保护状态"), "status");
            H.Check(response.Contains("被拒绝：3 次") && response.Contains("玩家甲"), "状态包含 .seed 记录");
            H.Check(admin.Execute(new ArraySegment<string>(new[] { "LIST" }), new TestSender("管理员", true), out response), "list");
            H.Check(!admin.Execute(new ArraySegment<string>(new[] { "bad" }), new TestSender("管理员", true), out response) && response.StartsWith("用法"), "未知参数");
            H.Check(!admin.Execute(new ArraySegment<string>(new[] { "status", "x" }), new TestSender("管理员", true), out _), "多余参数");
            for (int i = 0; i < 40; i++) client.Execute(empty, new TestSender("刷屏玩家", false), out _);
            H.Equal(43, plugin.Service.ClientAttempts, "刷屏仍全部计数");
            H.Equal(30, Log.Lines.FindAll(l => l.Contains("输入了 .seed")).Count, "每局最多逐条记录 30 次");
            H.Equal(1, Log.Lines.FindAll(l => l.Contains("查询过多")).Count, "超出后只提示一次");
            Game.ServerActive = true;
            Game.Seed = 99;
            Game.MapGenerated = true;
            Game.RaiseGenerationFinished();
            client.Execute(empty, new TestSender("新一局玩家", false), out _);
            H.Equal(1, Log.Lines.FindAll(l => l.Contains("新一局玩家 输入了 .seed")).Count, "新地图后重新开始记录");
            plugin.OnDisabled();
            H.Check(!admin.Execute(empty, new TestSender("管理员", true), out response) && response.Contains("未启用"), "停用后提示未启用");
        }
    }
}
