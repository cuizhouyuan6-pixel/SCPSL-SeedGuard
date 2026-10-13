using System;
using Exiled.API.Features;
using ServerEvents = Exiled.Events.Handlers.Server;

namespace SeedGuard
{
    public sealed class Plugin : Plugin<Config>
    {
        // Replaced only by the offline regression harness, which cannot create Unity objects.
        internal static Func<SeedChannel> ChannelFactory = SeedChannel.Resolve;
        internal static Func<GuardService, IDisposable> TickerFactory = GuardTicker.Attach;

        private IDisposable ticker;

        internal static Plugin Instance { get; private set; }
        internal GuardService Service { get; private set; }

        public override string Name => "SeedGuard";
        public override string Prefix => "seed_guard";
        public override string Author => "HHS";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredExiledVersion => new Version(9, 14, 2);

        public override void OnEnabled()
        {
            if (Service != null) return;
            GuardService service = null;
            IDisposable host = null;
            bool restartHooked = false;
            try
            {
                Config.Validate();
                SeedChannel channel = ChannelFactory();
                var world = new GameWorld(channel);
                service = GuardService.Start(Config.ToSettings(), world, world, ExiledLog.Instance);
                host = TickerFactory(service);
                ServerEvents.RestartingRound += service.OnRoundRestarting;
                restartHooked = true;
                Service = service;
                ticker = host;
                Instance = this;
                base.OnEnabled();
                Log.Info($"[SeedGuard] v1.0.0 已启用：玩家加载完地图后，客户端 seed 指令将显示 {Config.DisplayedSeed}。目标 SCP:SL 14.2.7 / EXILED 9.14.2。");
            }
            catch (Exception ex)
            {
                if (restartHooked) ServerEvents.RestartingRound -= service.OnRoundRestarting;
                host?.Dispose();
                service?.Dispose();
                Service = null;
                ticker = null;
                if (Instance == this) Instance = null;
                Log.Error("[SeedGuard] 启用失败，已撤销本次注册：" + ex.Message);
                throw;
            }
        }

        public override void OnDisabled()
        {
            GuardService service = Service;
            IDisposable host = ticker;
            Service = null;
            ticker = null;
            if (Instance == this) Instance = null;
            if (service != null) ServerEvents.RestartingRound -= service.OnRoundRestarting;
            try { host?.Dispose(); }
            finally
            {
                service?.Dispose();
                base.OnDisabled();
            }
        }
    }

    internal sealed class ExiledLog : IGuardLog
    {
        internal static readonly ExiledLog Instance = new ExiledLog();
        public void Info(string message) => Log.Info(message);
        public void Warn(string message) => Log.Warn(message);
        public void Error(string message) => Log.Error(message);
        public void Debug(string message) => Log.Debug(message);
    }
}
