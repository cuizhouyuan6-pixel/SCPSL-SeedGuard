using System;
using System.Collections.Generic;
using CentralAuth;
using MapGeneration;
using Mirror;

namespace SeedGuard
{
    /// <summary>
    /// Live game access. Every member used directly here is also referenced by the shipped LabApi.dll,
    /// which is compiled against the real (non-publicized) game assembly.
    /// </summary>
    internal sealed class GameWorld : IGuardWorld, IGuardHost
    {
        private readonly SeedChannel channel;

        internal GameWorld(SeedChannel channel) => this.channel = channel ?? throw new ArgumentNullException(nameof(channel));

        public bool IsServerActive => NetworkServer.active;
        public bool IsLoadingScene => NetworkServer.isLoadingScene;
        public bool IsMapGenerated => channel.IsMapGenerated;
        public int CurrentSeed => SeedSynchronizer.Seed;

        public void SubscribeMapGenerated(Action handler) => SeedSynchronizer.OnGenerationFinished += handler;
        public void UnsubscribeMapGenerated(Action handler) => SeedSynchronizer.OnGenerationFinished -= handler;

        public void CollectClients(List<ClientView> into)
        {
            foreach (ReferenceHub hub in ReferenceHub.AllHubs)
            {
                // Hosts, the dedicated server, dummies and unauthenticated clients never receive the replacement.
                if (hub == null || hub.Mode != ClientInstanceMode.ReadyClient) continue;
                NetworkConnectionToClient connection = hub.connectionToClient;
                if (connection == null || connection is LocalConnectionToClient) continue;
                into.Add(new ClientView
                {
                    Connection = connection,
                    Hub = hub,
                    IsReady = connection.isReady,
                    HasIdentity = connection.identity != null,
                });
            }
        }

        public bool IsAlive(object connection) =>
            connection is NetworkConnectionToClient client
            && NetworkServer.connections.TryGetValue(client.connectionId, out NetworkConnectionToClient current)
            && ReferenceEquals(current, client);

        public void SendSeed(object connection, int value) => channel.Send((NetworkConnectionToClient)connection, value);

        public void SendNotice(object hub, string text, string color) => ((ReferenceHub)hub).gameConsoleTransmission.SendToClient(text, color);

        public string Describe(object hub)
        {
            var player = hub as ReferenceHub;
            if (player == null) return "已离开的玩家";
            return $"{player.nicknameSync.MyNick} (#{player.PlayerId}, {player.authManager.UserId})";
        }
    }
}
