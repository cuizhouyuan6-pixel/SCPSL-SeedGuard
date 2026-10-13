using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SeedGuard
{
    internal interface IGuardWorld
    {
        bool IsServerActive { get; }
        bool IsLoadingScene { get; }
        bool IsMapGenerated { get; }
        int CurrentSeed { get; }
        void SubscribeMapGenerated(Action handler);
        void UnsubscribeMapGenerated(Action handler);
        void CollectClients(List<ClientView> into);
    }

    /// <summary>Connects the decision core to the game and keeps every game callback exception-free.</summary>
    internal sealed class GuardService : IDisposable
    {
        private const int MaxAttemptsShown = 10;
        private const int MaxAttemptLogsPerMap = 30;
        private readonly GuardCore core;
        private readonly IGuardWorld world;
        private readonly IGuardLog log;
        private readonly GuardSettings settings;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<ClientView> buffer = new List<ClientView>();
        private readonly List<string> attempts = new List<string>();
        private readonly Action mapHandler;
        private long nextCheck;
        private bool subscribed;
        private int errors;
        private int attemptLogs;

        private GuardService(GuardSettings settings, IGuardWorld world, IGuardHost host, IGuardLog log)
        {
            this.settings = settings;
            this.world = world;
            this.log = log;
            core = new GuardCore(settings, host, log);
            mapHandler = OnMapGenerated;
        }

        internal GuardCore Core => core;
        internal int ClientAttempts { get; private set; }

        internal static GuardService Start(GuardSettings settings, IGuardWorld world, IGuardHost host, IGuardLog log)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            var service = new GuardService(settings, world, host, log);
            world.SubscribeMapGenerated(service.mapHandler);
            service.subscribed = true;
            try
            {
                // Enabled after the current map was generated: the connected players already hold the real seed.
                if (world.IsServerActive && !world.IsLoadingScene && world.IsMapGenerated) service.core.BeginMap(world.CurrentSeed);
            }
            catch
            {
                service.Dispose();
                throw;
            }
            return service;
        }

        internal void OnRoundRestarting()
        {
            try { core.Suspend(); }
            catch (Exception ex) { Fail("回合重启处理", ex); }
        }

        internal void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            if (now < nextCheck) return;
            nextCheck = now + settings.CheckIntervalMs;
            try
            {
                buffer.Clear();
                world.CollectClients(buffer);
                bool canSend = core.Active && world.IsServerActive && !world.IsLoadingScene && world.IsMapGenerated;
                core.Sweep(buffer, canSend, now);
            }
            catch (Exception ex) { Fail("玩家检查", ex); }
        }

        internal void RecordClientAttempt(string who)
        {
            ClientAttempts++;
            if (attempts.Count == MaxAttemptsShown) attempts.RemoveAt(0);
            attempts.Add(DateTime.Now.ToString("HH:mm:ss") + " " + who);
            // Players can spam the command; keep counting but cap the log lines per map.
            attemptLogs++;
            if (attemptLogs <= MaxAttemptLogsPerMap) log.Info($"[SeedGuard] {who} 输入了 .seed，已拒绝。");
            else if (attemptLogs == MaxAttemptLogsPerMap + 1) log.Info("[SeedGuard] 本局 .seed 查询过多，后续不再逐条记录，次数仍计入 seedguard status。");
        }

        internal string BuildStatus(bool list)
        {
            var text = new StringBuilder();
            core.AppendStatus(text, list);
            text.Append("通过 .seed 查询被拒绝：").Append(ClientAttempts).Append(" 次");
            if (attempts.Count > 0)
            {
                text.AppendLine("，最近记录：");
                foreach (string attempt in attempts) text.Append("  ").AppendLine(attempt);
            }
            else text.AppendLine();
            text.Append("说明：原版 seed 指令在玩家客户端本地执行，服务器无法得知谁输入了它。");
            return text.ToString();
        }

        public void Dispose()
        {
            if (subscribed)
            {
                subscribed = false;
                world.UnsubscribeMapGenerated(mapHandler);
            }
            buffer.Clear();
        }

        // Runs inside SeedSynchronizer.GenerateLevel; an exception here would break the remaining subscribers.
        private void OnMapGenerated()
        {
            try
            {
                if (!world.IsServerActive) return;
                core.BeginMap(world.CurrentSeed);
                nextCheck = 0;
                errors = 0;
                attemptLogs = 0;
            }
            catch (Exception ex) { Fail("地图生成处理", ex); }
        }

        private void Fail(string stage, Exception ex)
        {
            if (errors++ < 10) log.Error($"[SeedGuard] {stage}出错：{ex}");
        }
    }
}
