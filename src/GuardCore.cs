using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace SeedGuard
{
    internal enum ClientState { Waiting, Protected, Retrying, Abandoned, Carried }

    // One authenticated client as seen by a sweep. Connection and hub stay opaque to the core.
    internal struct ClientView
    {
        public object Connection;
        public object Hub;
        public bool IsReady;
        public bool HasIdentity;
    }

    internal interface IGuardHost
    {
        bool IsAlive(object connection);
        void SendSeed(object connection, int value);
        void SendNotice(object hub, string text, string color);
        string Describe(object hub);
    }

    internal interface IGuardLog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Debug(string message);
    }

    internal sealed class GuardSettings
    {
        internal const long RetryDelayMs = 2000;
        internal const int MaxAttempts = 5;
        internal const int GenerationDisabledSeed = -1;
        public int DisplayedSeed;
        public string Notice = string.Empty;
        public string NoticeColor = "red";
        public long NoticeDelayMs = 3000;
        public long CheckIntervalMs = 250;
        public bool LogEachPlayer;
    }

    internal sealed class ClientRecord
    {
        public object Hub;
        public ClientState State;
        public long ProtectedAt;
        public bool NoticeSent;
        public int Attempts;
        public long RetryAt;
        public string LastError;
        public bool Warned;
    }

    /// <summary>
    /// Decides when a client may receive the replacement seed. The vanilla client only regenerates the
    /// facility while its map is not generated yet, so a replacement is sent once per connection and map,
    /// strictly after that client finished loading and authenticating.
    /// </summary>
    internal sealed class GuardCore
    {
        private readonly Dictionary<object, ClientRecord> records = new Dictionary<object, ClientRecord>(ReferenceComparer.Instance);
        // Authenticated connections of an earlier map that never reconnected; vanilla always reconnects clients on restart.
        private readonly HashSet<object> carried = new HashSet<object>(ReferenceComparer.Instance);
        private readonly List<object> stale = new List<object>();
        private readonly GuardSettings settings;
        private readonly IGuardHost host;
        private readonly IGuardLog log;
        private bool hasPrevious;
        private int previousSeed;
        private int warnings;

        internal GuardCore(GuardSettings settings, IGuardHost host, IGuardLog log)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        internal int MapCount { get; private set; }
        internal bool Active { get; private set; }
        internal int RealSeed { get; private set; }
        internal int ShownSeed { get; private set; }
        internal int ProtectedCount { get; private set; }
        internal int FailedSends { get; private set; }

        /// <summary>Starts a new map. Vanilla has already queued the real seed for every connection.</summary>
        internal void BeginMap(int realSeed)
        {
            if (Active) Summarize();
            foreach (object connection in records.Keys) carried.Add(connection);
            records.Clear();
            MapCount++;
            RealSeed = realSeed;
            ProtectedCount = 0;
            FailedSends = 0;
            warnings = 0;
            ShownSeed = settings.DisplayedSeed != realSeed ? settings.DisplayedSeed : (realSeed == 0 ? 1 : 0);
            Active = realSeed != GuardSettings.GenerationDisabledSeed;
            if (!Active)
            {
                hasPrevious = false;
                log.Info("[SeedGuard] 本局地图生成被禁用（种子 -1），客户端不会生成设施布局，无需隐藏种子。");
                return;
            }
            if (hasPrevious && previousSeed == realSeed)
                log.Warn("[SeedGuard] 本局地图种子与上一局相同，可能配置了固定 map_seed 或有插件固定了种子。地图布局每局不变时，隐藏种子无法阻止玩家记住布局。");
            hasPrevious = true;
            previousSeed = realSeed;
            log.Debug($"[SeedGuard] 第 {MapCount} 张地图已生成，真实种子 {realSeed}；玩家完成加载后 seed 将显示 {ShownSeed}。");
        }

        /// <summary>Round restart began: nothing may be sent until the next map is generated.</summary>
        internal void Suspend()
        {
            if (Active) Summarize();
            Active = false;
        }

        internal void Sweep(List<ClientView> clients, bool canSend, long now)
        {
            for (int i = 0; i < clients.Count; i++)
            {
                ClientView view = clients[i];
                if (view.Connection == null) continue;
                if (!records.TryGetValue(view.Connection, out ClientRecord record))
                {
                    record = new ClientRecord { State = carried.Contains(view.Connection) ? ClientState.Carried : ClientState.Waiting };
                    records.Add(view.Connection, record);
                }
                record.Hub = view.Hub;
                // Ready + spawned identity means the client loaded this scene; the real seed precedes us on the same channel.
                if (!canSend || !view.IsReady || !view.HasIdentity) continue;
                switch (record.State)
                {
                    case ClientState.Waiting:
                    case ClientState.Retrying:
                        if (now >= record.RetryAt) Protect(view, record, now);
                        break;
                    case ClientState.Protected:
                        if (!record.NoticeSent && now - record.ProtectedAt >= settings.NoticeDelayMs) Notify(view, record);
                        break;
                    case ClientState.Carried:
                        if (!record.Warned)
                        {
                            record.Warned = true;
                            log.Warn($"[SeedGuard] {Describe(view.Hub)} 换图后没有重新连接。为避免其客户端用伪种子重建地图，本局不隐藏该玩家的种子。");
                        }
                        break;
                }
            }
            Prune();
        }

        internal void AppendStatus(StringBuilder text, bool list)
        {
            int waiting = 0, done = 0, retrying = 0, abandoned = 0, kept = 0;
            foreach (ClientRecord record in records.Values)
            {
                switch (record.State)
                {
                    case ClientState.Waiting: waiting++; break;
                    case ClientState.Protected: done++; break;
                    case ClientState.Retrying: retrying++; break;
                    case ClientState.Abandoned: abandoned++; break;
                    case ClientState.Carried: kept++; break;
                }
            }
            text.Append("保护状态：").AppendLine(Active ? "运行中" : MapCount == 0 ? "等待首张地图生成" : "已暂停（等待下一张地图生成）");
            if (MapCount > 0)
                text.Append("第 ").Append(MapCount).Append(" 张地图：真实种子 ").Append(RealSeed).Append("，玩家 seed 显示 ").Append(ShownSeed).AppendLine();
            text.Append("在线玩家：已隐藏 ").Append(done).Append("，等待加载 ").Append(waiting).Append("，重试中 ").Append(retrying)
                .Append("，已放弃 ").Append(abandoned).Append("，换图未重连 ").Append(kept).AppendLine();
            text.Append("本局累计：隐藏 ").Append(ProtectedCount).Append(" 次，发送失败 ").Append(FailedSends).Append(" 次").AppendLine();
            if (!list) return;
            foreach (ClientRecord record in records.Values)
            {
                text.Append("- ").Append(Describe(record.Hub)).Append("：").Append(StateName(record.State));
                if (record.LastError != null) text.Append("（").Append(record.LastError).Append("）");
                text.AppendLine();
            }
        }

        internal ClientRecord Find(object connection) => connection != null && records.TryGetValue(connection, out ClientRecord record) ? record : null;

        private void Protect(ClientView view, ClientRecord record, long now)
        {
            try
            {
                host.SendSeed(view.Connection, ShownSeed);
            }
            catch (Exception ex)
            {
                record.Attempts++;
                FailedSends++;
                record.LastError = ex.GetType().Name + ": " + ex.Message;
                record.State = record.Attempts >= GuardSettings.MaxAttempts ? ClientState.Abandoned : ClientState.Retrying;
                record.RetryAt = now + GuardSettings.RetryDelayMs;
                if (warnings++ < 5)
                    log.Warn($"[SeedGuard] 向 {Describe(view.Hub)} 发送伪种子失败（第 {record.Attempts} 次）：{record.LastError}");
                return;
            }
            record.State = ClientState.Protected;
            record.ProtectedAt = now;
            record.LastError = null;
            ProtectedCount++;
            if (settings.LogEachPlayer) log.Info($"[SeedGuard] 已隐藏 {Describe(view.Hub)} 的地图种子（seed 显示 {ShownSeed}）。");
            if (settings.NoticeDelayMs <= 0) Notify(view, record);
        }

        private void Notify(ClientView view, ClientRecord record)
        {
            record.NoticeSent = true;
            if (string.IsNullOrEmpty(settings.Notice)) return;
            try
            {
                host.SendNotice(view.Hub, settings.Notice, settings.NoticeColor);
            }
            catch (Exception ex)
            {
                if (warnings++ < 5) log.Warn($"[SeedGuard] 向 {Describe(view.Hub)} 推送控制台提示失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Prune()
        {
            stale.Clear();
            foreach (object connection in records.Keys)
                if (!host.IsAlive(connection)) stale.Add(connection);
            foreach (object connection in carried)
                if (!records.ContainsKey(connection) && !host.IsAlive(connection)) stale.Add(connection);
            for (int i = 0; i < stale.Count; i++)
            {
                records.Remove(stale[i]);
                carried.Remove(stale[i]);
            }
        }

        private void Summarize()
        {
            log.Info(FailedSends == 0
                ? $"[SeedGuard] 本局已为 {ProtectedCount} 名玩家隐藏地图种子。"
                : $"[SeedGuard] 本局已为 {ProtectedCount} 名玩家隐藏地图种子，发送失败 {FailedSends} 次。");
        }

        private string Describe(object hub)
        {
            try { return host.Describe(hub); }
            catch (Exception) { return "未知玩家"; }
        }

        private static string StateName(ClientState state)
        {
            switch (state)
            {
                case ClientState.Waiting: return "等待加载";
                case ClientState.Protected: return "已隐藏";
                case ClientState.Retrying: return "重试中";
                case ClientState.Abandoned: return "多次失败已放弃";
                default: return "换图未重连，本局不处理";
            }
        }
    }

    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
