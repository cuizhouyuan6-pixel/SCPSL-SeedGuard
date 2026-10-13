// Regression program for the game-facing part. Runs on the restricted copy of the real game assembly, where
// every member without runtime evidence of being public is internal (see Restrict.ps1).
using System;
using System.Collections.Generic;
using System.Linq;
using CentralAuth;
using Mirror;

namespace SeedGuard.Tests
{
    internal static class GameTests
    {
        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "--all";
            try
            {
                Console.WriteLine($"运行环境：{(Type.GetType("Mono.Runtime") != null ? "Mono" : "CLR")} / {Environment.Version}；模式 {mode}");
                switch (mode)
                {
                    case "--all": RunAll(); break;
                    case "--naive": NaiveControl(); break;
                    case "--guardless": GuardlessControl(); break;
                    default: throw new ArgumentException("未知模式 " + mode);
                }
                Console.WriteLine($"通过：{H.Passed} 项断言（{mode}）");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("失败：" + ex);
                return 1;
            }
        }

        private static void RunAll()
        {
            Game.InstallSerializers();
            Step("A 反射解析在非公开运行权限下成功", ResolveUnderRestrictedAccess);
            Step("B1 伪种子经 Mirror 原生读写器编解码，走可靠通道", WireFormat);
            Step("B2 伪种子与原版真实种子消息逐字节一致", WireFormatMatchesVanilla, monoOnly: true);
            Step("C 原版客户端处理：已生成地图只改显示值，不重建", ClientKeepsMapAfterGeneration);
            Step("D 负向对照：地图生成前收到伪种子会用伪种子生成", ClientRegeneratesBeforeGeneration);
            Step("E 顺序到达：真实种子生成地图，随后伪种子只改 seed 输出", OrderedDeliveryEndToEnd, monoOnly: true);
            Step("F 生产 GameWorld 只选择已认证真人并读取连接状态", GameWorldSelection);
            Step("G 生产管线：加载完成后每图每人只发一次，提示延迟推送", PipelineSendsOncePerMap);
            Step("H 回合重启、加载中、换图未重连与断线清理", PipelineAcrossRestart);
            Step("I 中途启用、禁用生成(-1)、重复种子告警、显示值与真实值相同", PipelineEdgeCases);
            Step("J 发送失败重试、放弃上限且不影响其他玩家", PipelineFailures);
            Step("K 地图生成回调异常不外泄，后续订阅者照常执行", HandlerNeverThrowsIntoGame);
            Step("L 释放后退订真实 OnGenerationFinished", DisposeUnsubscribes);
        }

        private static void Step(string name, Action test, bool monoOnly = false)
        {
            if (monoOnly && Type.GetType("Mono.Runtime") == null)
            {
                // Vanilla Mirror.NetworkClient needs ArraySegment<T> conversions that only Unity's Mono BCL provides.
                Console.WriteLine("  - 仅 Mono 执行：" + name + "（原版 NetworkClient 依赖 .NET Framework 没有的 BCL 接口）");
                return;
            }
            Game.ResetVanilla();
            test();
            Console.WriteLine("  ✓ " + name);
        }

        private static void WireFormat()
        {
            SeedChannel channel = SeedChannel.Resolve();
            RecordingConnection ours = RecordingConnection.Create(2);
            channel.Send(ours, 123456789);
            channel.Send(ours, 0);
            H.Equal(2, ours.Sent.Count, "每次发送一条消息");
            H.Equal(Channels.Reliable, ours.Sent[0].Channel, "可靠通道");
            H.Equal(123456789, ours.SeedValue(0), "解码值");
            H.Equal(0, ours.SeedValue(1), "伪种子 0 解码");
            H.Equal(6, ours.Sent[1].Data.Length, "消息长度：2 字节编号 + 4 字节 int");
        }

        private static void ResolveUnderRestrictedAccess()
        {
            H.Check(!Game.Message.IsNestedPublic, "测试运行时中 SeedMessage 必须为非公开类型");
            H.Check(!H.Field(Game.Sync, "MapGenerated").IsPublic, "测试运行时中 MapGenerated 必须为非公开字段");
            SeedChannel channel = SeedChannel.Resolve();
            Game.MapGenerated = true;
            H.Check(channel.IsMapGenerated, "MapGenerated 读取");
            Game.MapGenerated = false;
            H.Check(!channel.IsMapGenerated, "MapGenerated 读取（false）");
        }

        private static void WireFormatMatchesVanilla()
        {
            SeedChannel channel = SeedChannel.Resolve();
            Game.Seed = 123456789;
            RecordingConnection vanilla = RecordingConnection.Create(1), ours = RecordingConnection.Create(2);
            Game.VanillaSendOnConnect(vanilla);
            channel.Send(ours, 123456789);
            H.Equal(1, vanilla.Sent.Count, "原版连接时发送一条种子消息");
            H.Equal(1, ours.Sent.Count, "插件发送一条消息");
            H.Check(vanilla.Sent[0].Data.SequenceEqual(ours.Sent[0].Data), "字节完全一致");
            H.Equal(Channels.Reliable, vanilla.Sent[0].Channel, "原版使用可靠通道");
            H.Equal(vanilla.Sent[0].Channel, ours.Sent[0].Channel, "与原版同一通道，保证先后顺序");
            H.Equal(123456789, ours.SeedValue(0), "解码值");
            channel.Send(ours, 0);
            H.Equal(0, ours.SeedValue(1), "伪种子 0 解码");
        }

        private static void ClientKeepsMapAfterGeneration()
        {
            int generations = 0;
            Game.AddBeforeGenerated(() => { generations++; throw new Abort(); });
            Game.Seed = 555;
            Game.MapGenerated = true;
            Game.SetSingleton(alive: true);
            Game.ClientReceive(0);
            H.Equal(0, generations, "地图已生成时不会再次生成");
            H.Equal(0, Game.Seed, "客户端保存的种子被覆盖");
            H.Check(Game.MapGenerated, "地图状态保持已生成");
            H.Equal("Map seed is: 0", Game.SeedCommandOutput(), "原版 seed 指令输出");
        }

        private static void ClientRegeneratesBeforeGeneration()
        {
            int seen = int.MinValue;
            Game.AddBeforeGenerated(() => { seen = Game.Seed; throw new Abort(); });
            Game.SetSingleton(alive: true);
            H.Throws<Abort>(() => Game.ClientReceive(0), "未生成地图时收到种子会立即生成");
            H.Equal(0, seen, "会用伪种子生成错误地图，所以必须等客户端加载完成后再发送");
            Game.ResetVanilla();
            Game.ClientReceive(0);
            H.Check(Game.TokenValid, "场景尚未加载时种子被暂存，加载后同样会用它生成");
        }

        private static void OrderedDeliveryEndToEnd()
        {
            SeedChannel channel = SeedChannel.Resolve();
            Game.Seed = 987654;
            RecordingConnection client = RecordingConnection.Create(3);
            Game.VanillaSendOnConnect(client);
            channel.Send(client, 0);
            // Client side: replay the two packets in transport order.
            Game.ResetVanilla();
            Game.SetSingleton(alive: true);
            var built = new List<int>();
            Game.AddBeforeGenerated(() => { built.Add(Game.Seed); Game.MapGenerated = true; throw new Abort(); });
            H.Throws<Abort>(() => H.Call(Game.Sync, "ClientReceiveSeed", null, Game.Decode(client.Sent[0].Data, out _)), "真实种子触发生成");
            H.Call(Game.Sync, "ClientReceiveSeed", null, Game.Decode(client.Sent[1].Data, out _));
            H.Equal(1, built.Count, "整个过程只生成一次地图");
            H.Equal(987654, built[0], "地图由真实种子生成");
            H.Equal("Map seed is: 0", Game.SeedCommandOutput(), "玩家输入 seed 只看到伪值");
        }

        private static void GameWorldSelection()
        {
            Game.ServerActive = true;
            var world = new GameWorld(SeedChannel.Resolve());
            ReferenceHub player = Game.AddHub(2, "玩家甲", ClientInstanceMode.ReadyClient, RecordingConnection.Create(10), spawned: true);
            Game.AddHub(3, "未认证", ClientInstanceMode.Unverified, RecordingConnection.Create(11), spawned: true);
            Game.AddHub(4, "假人", ClientInstanceMode.Dummy, RecordingConnection.Create(12), spawned: true);
            Game.AddHub(1, "服务器", ClientInstanceMode.DedicatedServer, null, spawned: false);
            ReferenceHub loading = Game.AddHub(5, "玩家乙", ClientInstanceMode.ReadyClient, RecordingConnection.Create(13, ready: false), spawned: false);
            var views = new List<ClientView>();
            world.CollectClients(views);
            H.Equal(2, views.Count, "只收集已认证的真人客户端");
            ClientView a = views.Single(v => v.Hub == (object)player), b = views.Single(v => v.Hub == (object)loading);
            H.Check(a.IsReady && a.HasIdentity, "已加载玩家状态");
            H.Check(!b.IsReady && !b.HasIdentity, "加载中玩家状态");
            H.Check(world.IsAlive(a.Connection), "在线连接");
            H.Equal("玩家甲 (#2, player2@test)", world.Describe(player), "玩家描述");
            Game.Disconnect(player);
            H.Check(!world.IsAlive(a.Connection), "断线连接");
            world.SendNotice(loading, "服务器禁止使用Seed指令", "red");
            H.Equal("red#服务器禁止使用Seed指令", Game.LastConsoleMessage, "调用原版 SendToClient，颜色与文字按原版格式传入");
            H.Equal(987, Read(world, 987), "CurrentSeed 读取真实值");
        }

        private static int Read(GameWorld world, int seed)
        {
            Game.Seed = seed;
            return world.CurrentSeed;
        }

        private static GuardService StartService(GuardSettings settings, TestLog log, out GameWorld world)
        {
            world = new GameWorld(SeedChannel.Resolve());
            return GuardService.Start(settings, world, world, log);
        }

        private static GuardSettings Settings(long noticeDelay = 0, bool logEach = false) => new GuardSettings
        {
            DisplayedSeed = 0,
            Notice = "服务器禁止使用Seed指令",
            NoticeColor = "red",
            NoticeDelayMs = noticeDelay,
            CheckIntervalMs = 0,
            LogEachPlayer = logEach,
        };

        private static void NewMap(int seed)
        {
            Game.Seed = seed;
            Game.MapGenerated = true;
            Game.RaiseGenerationFinished();
        }

        private static void PipelineSendsOncePerMap()
        {
            Game.ServerActive = true;
            var log = new TestLog();
            using (GuardService service = StartService(Settings(noticeDelay: 60000), log, out _))
            {
                RecordingConnection early = RecordingConnection.Create(20, ready: false);
                ReferenceHub hub = Game.AddHub(2, "早到玩家", ClientInstanceMode.Unverified, early, spawned: false);
                service.Tick();
                H.Equal(0, early.Sent.Count, "地图未生成前绝不发送");
                NewMap(424242);
                Game.SetMode(hub, ClientInstanceMode.ReadyClient);
                service.Tick();
                H.Equal(0, early.Sent.Count, "未就绪、未生成玩家对象时不发送");
                early.isReady = true;
                Game.Spawn(hub, true);
                service.Tick();
                H.Equal(1, early.Sent.Count, "加载完成后发送一次");
                H.Equal(0, early.SeedValue(0), "发送配置的显示值");
                for (int i = 0; i < 5; i++) service.Tick();
                H.Equal(1, early.Sent.Count, "同一张地图不重复发送");
                ClientRecord record = service.Core.Find(early);
                H.Check(record.State == ClientState.Protected && !record.NoticeSent, "提示等待延迟");
                H.Equal(1, service.Core.ProtectedCount, "计数");
                H.Check(service.BuildStatus(true).Contains("早到玩家"), "状态列表包含玩家");
            }
            var verbose = new TestLog();
            using (GuardService service = StartService(Settings(noticeDelay: 0, logEach: true), verbose, out _))
            {
                RecordingConnection late = RecordingConnection.Create(21);
                Game.AddHub(3, "中途加入", ClientInstanceMode.ReadyClient, late, spawned: true);
                service.Tick();
                H.Check(service.Core.Find(late).NoticeSent, "无延迟时立即推送提示");
                H.Equal("red#服务器禁止使用Seed指令", Game.LastConsoleMessage, "提示经原版控制台通道发出");
                H.Equal(1, late.Sent.Count, "中途启用时已生成地图上的玩家同样处理");
                H.Equal(1, verbose.Count("INFO", "已隐藏 中途加入 (#3, player3@test) 的地图种子"), "逐人日志");
            }
        }

        private static void PipelineAcrossRestart()
        {
            Game.ServerActive = true;
            var log = new TestLog();
            using (GuardService service = StartService(Settings(), log, out _))
            {
                NewMap(1001);
                RecordingConnection stay = RecordingConnection.Create(30), leave = RecordingConnection.Create(31);
                ReferenceHub stayHub = Game.AddHub(2, "未重连客户端", ClientInstanceMode.ReadyClient, stay, spawned: true);
                ReferenceHub leaveHub = Game.AddHub(3, "正常重连客户端", ClientInstanceMode.ReadyClient, leave, spawned: true);
                service.Tick();
                H.Equal(1, stay.Sent.Count, "第一张图已处理 A");
                H.Equal(1, leave.Sent.Count, "第一张图已处理 B");
                service.OnRoundRestarting();
                H.Check(log.Count("INFO", "本局已为 2 名玩家") == 1, "重启时输出本局汇总");
                // Vanilla: clients are set not ready and the old scene objects disappear while loading.
                stay.isReady = false;
                Game.Spawn(stayHub, false);
                Game.Disconnect(leaveHub);
                Game.Loading = true;
                Game.MapGenerated = false;
                RecordingConnection fresh = RecordingConnection.Create(32);
                ReferenceHub freshHub = Game.AddHub(4, "新连接", ClientInstanceMode.Unverified, fresh, spawned: true);
                service.Tick();
                H.Equal(0, fresh.Sent.Count, "重启后、新地图生成前不发送");
                H.Check(service.Core.Find(fresh) == null, "未认证连接不建立记录");
                NewMap(2002);
                // Authentication needs the player object of the new scene, so it always follows the new map.
                Game.SetMode(freshHub, ClientInstanceMode.ReadyClient);
                service.Tick();
                H.Equal(0, fresh.Sent.Count, "场景仍在加载时不发送");
                Game.Loading = false;
                stay.isReady = true;
                Game.Spawn(stayHub, true);
                service.Tick();
                H.Equal(1, fresh.Sent.Count, "新连接在新地图上被处理");
                H.Equal(1, stay.Sent.Count, "换图未重连的旧连接本局不处理");
                H.Equal(ClientState.Carried, service.Core.Find(stay).State, "旧连接标记为换图未重连");
                H.Equal(1, log.Count("WARN", "换图后没有重新连接"), "旧连接告警一次");
                service.Tick();
                H.Equal(1, log.Count("WARN", "换图后没有重新连接"), "告警不重复");
                Game.Disconnect(freshHub);
                service.Tick();
                H.Check(service.Core.Find(fresh) == null, "断线后清理记录");
                Game.Disconnect(stayHub);
                RecordingConnection reconnect = RecordingConnection.Create(33);
                Game.AddHub(5, "重新连接", ClientInstanceMode.ReadyClient, reconnect, spawned: true);
                service.Tick();
                H.Equal(1, reconnect.Sent.Count, "重新连接后作为新连接处理");
            }
        }

        private static void PipelineEdgeCases()
        {
            Game.ServerActive = true;
            Game.Seed = 31337;
            Game.MapGenerated = true;
            RecordingConnection current = RecordingConnection.Create(40);
            Game.AddHub(2, "已在服务器", ClientInstanceMode.ReadyClient, current, spawned: true);
            var log = new TestLog();
            GuardSettings settings = Settings();
            settings.DisplayedSeed = 31337;
            using (GuardService service = StartService(settings, log, out _))
            {
                service.Tick();
                H.Equal(1, current.Sent.Count, "中途启用：当前地图上的玩家被处理");
                H.Equal(0, current.SeedValue(0), "显示值与真实种子相同时改用 0");
                NewMap(-1);
                RecordingConnection none = RecordingConnection.Create(41);
                Game.AddHub(3, "禁用生成", ClientInstanceMode.ReadyClient, none, spawned: true);
                service.Tick();
                H.Equal(0, none.Sent.Count, "地图生成被禁用时不发送");
                H.Equal(1, log.Count("INFO", "种子 -1"), "禁用生成提示");
                NewMap(777);
                NewMap(777);
                H.Equal(1, log.Count("WARN", "与上一局相同"), "连续相同种子告警");
            }
            Game.ResetVanilla();
            Game.ServerActive = true;
            Game.Loading = true;
            Game.Seed = 5;
            Game.MapGenerated = true;
            using (GuardService service = StartService(Settings(), new TestLog(), out _))
                H.Equal(0, service.Core.MapCount, "场景加载中启用时等待新地图事件");
        }

        private static void PipelineFailures()
        {
            Game.ServerActive = true;
            var log = new TestLog();
            using (GuardService service = StartService(Settings(), log, out _))
            {
                NewMap(9000);
                RecordingConnection flaky = RecordingConnection.Create(50), broken = RecordingConnection.Create(51), fine = RecordingConnection.Create(52);
                flaky.FailuresLeft = 1;
                broken.FailuresLeft = int.MaxValue;
                Game.AddHub(2, "偶发失败", ClientInstanceMode.ReadyClient, flaky, spawned: true);
                Game.AddHub(3, "持续失败", ClientInstanceMode.ReadyClient, broken, spawned: true);
                Game.AddHub(4, "正常", ClientInstanceMode.ReadyClient, fine, spawned: true);
                service.Tick();
                H.Equal(1, fine.Sent.Count, "其他玩家不受影响");
                H.Equal(ClientState.Retrying, service.Core.Find(flaky).State, "失败后进入重试");
                service.Tick();
                H.Equal(0, flaky.Sent.Count, "重试前等待退避时间");
                for (int attempt = 0; attempt < GuardSettings.MaxAttempts + 1; attempt++)
                {
                    foreach (object connection in new object[] { flaky, broken })
                    {
                        ClientRecord record = service.Core.Find(connection);
                        record.RetryAt = 0;
                    }
                    service.Tick();
                }
                H.Equal(1, flaky.Sent.Count, "重试成功");
                H.Equal(ClientState.Abandoned, service.Core.Find(broken).State, "达到上限后放弃");
                H.Equal(GuardSettings.MaxAttempts, service.Core.Find(broken).Attempts, "尝试次数上限");
                H.Equal(5, log.Lines.Count(l => l.StartsWith("WARN") && l.Contains("发送伪种子失败")), "六次失败只记录五条日志");
            }
        }

        private static void HandlerNeverThrowsIntoGame()
        {
            Game.ServerActive = true;
            var log = new TestLog();
            var world = new ThrowingWorld();
            using (GuardService service = GuardService.Start(Settings(), world, new GameWorld(SeedChannel.Resolve()), log))
            {
                bool later = false;
                world.Subscribe(() => later = true);
                world.Generated = true;
                world.Raise();
                H.Check(later, "后续订阅者仍被调用");
                H.Equal(1, log.Count("ERROR", "地图生成处理出错"), "异常被记录");
                service.Tick();
                H.Equal(1, log.Count("ERROR", "玩家检查出错"), "检查异常被记录");
            }
        }

        private static void DisposeUnsubscribes()
        {
            Game.ServerActive = true;
            int before = Game.GenerationFinished?.GetInvocationList().Length ?? 0;
            GuardService service = StartService(Settings(), new TestLog(), out _);
            H.Equal(before + 1, Game.GenerationFinished.GetInvocationList().Length, "订阅真实事件");
            service.Dispose();
            service.Dispose();
            H.Equal(before, Game.GenerationFinished?.GetInvocationList().Length ?? 0, "释放后退订且可重复释放");
        }

        // Proves the restricted runtime catches direct use of members the plugin must only reach by reflection.
        private static void NaiveControl()
        {
            Exception error = null;
            try { NaiveAccess.Send(RecordingConnection.Create(1)); }
            catch (Exception ex) { error = ex; }
            H.Check(error is MemberAccessException || error is TypeLoadException, "直接访问非公开成员应失败，实际：" + (error?.GetType().Name ?? "无异常"));
            Console.WriteLine("  ✓ 负向对照：直接引用 SeedMessage/MapGenerated 在非公开运行权限下失败：" + error.GetType().Name);
        }

        // Proves the startup check refuses a game build whose client handler would rebuild the map.
        private static void GuardlessControl()
        {
            var error = H.Throws<InvalidOperationException>(() => SeedChannel.Resolve(), "去掉 MapGenerated 保护后必须拒绝启用");
            H.Check(error.Message.Contains("拒绝启用"), "错误说明");
            Console.WriteLine("  ✓ 负向对照：客户端处理逻辑被改动时拒绝启用：" + error.Message);
        }

        private sealed class ThrowingWorld : IGuardWorld
        {
            private Action handlers;
            internal bool Generated;
            public bool IsServerActive => true;
            public bool IsLoadingScene => false;
            public bool IsMapGenerated => Generated;
            public int CurrentSeed => throw new InvalidOperationException("模拟读取失败");
            public void SubscribeMapGenerated(Action handler) => handlers += handler;
            public void UnsubscribeMapGenerated(Action handler) => handlers -= handler;
            public void CollectClients(List<ClientView> into) => throw new InvalidOperationException("模拟枚举失败");
            internal void Subscribe(Action handler) => handlers += handler;
            internal void Raise() => handlers?.Invoke();
        }
    }
}
