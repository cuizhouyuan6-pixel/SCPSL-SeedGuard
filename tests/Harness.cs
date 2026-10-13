// Offline harness shared by both regression programs. It runs against the real game, Mirror and Unity
// assemblies; objects are created without constructors because Unity's native side is not available.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using CentralAuth;
using MapGeneration;
using Mirror;

namespace SeedGuard.Tests
{
    internal static class H
    {
        internal const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        internal static int Passed;

        internal static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("断言失败：" + message);
            Passed++;
        }

        internal static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"断言失败：{message}（期望 {expected}，实际 {actual}）");
            Passed++;
        }

        internal static T Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T ex) { Passed++; return ex; }
            catch (Exception ex) { throw new Exception($"断言失败：{message}（期望 {typeof(T).Name}，实际 {ex.GetType().Name}: {ex.Message}）"); }
            throw new Exception($"断言失败：{message}（没有抛出 {typeof(T).Name}）");
        }

        internal static FieldInfo Field(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }

        internal static void Set(object target, string name, object value) => Field(target.GetType(), name).SetValue(target, value);
        internal static object Get(object target, string name) => Field(target.GetType(), name).GetValue(target);
        internal static void SetStatic(Type type, string name, object value) => Field(type, name).SetValue(null, value);
        internal static object GetStatic(Type type, string name) => Field(type, name).GetValue(null);
        internal static T Uninit<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

        // Unity treats a managed wrapper with a non-zero native pointer as a live object.
        internal static T Alive<T>(T value) where T : UnityEngine.Object
        {
            Set(value, "m_CachedPtr", new IntPtr(1));
            return value;
        }

        internal static object Call(Type type, string name, object target, params object[] args)
        {
            MethodInfo method = type.GetMethod(name, All) ?? throw new MissingMethodException(type.FullName, name);
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                return null;
            }
        }
    }

    /// <summary>Real vanilla state of SeedSynchronizer and Mirror, manipulated through reflection.</summary>
    internal static class Game
    {
        internal static readonly Type Sync = typeof(SeedSynchronizer);
        internal static readonly Type Message = Sync.GetNestedType("SeedMessage", H.All);
        internal static int Seed { get => SeedSynchronizer.Seed; set => H.SetStatic(Sync, "<Seed>k__BackingField", value); }
        internal static bool MapGenerated { get => (bool)H.GetStatic(Sync, "MapGenerated"); set => H.SetStatic(Sync, "MapGenerated", value); }
        internal static bool TokenValid => (bool)H.GetStatic(Sync, "_seedTokenValid");
        internal static bool ServerActive { set => H.SetStatic(typeof(NetworkServer), "<active>k__BackingField", value); }
        internal static bool Loading { set => NetworkServer.isLoadingScene = value; }

        internal static void SetSingleton(bool alive)
        {
            SeedSynchronizer instance = null;
            if (alive) instance = H.Alive(H.Uninit<SeedSynchronizer>());
            H.SetStatic(Sync, "_singleton", instance);
        }

        internal static void ResetVanilla()
        {
            ServerActive = false;
            Loading = false;
            Seed = 0;
            MapGenerated = false;
            H.SetStatic(Sync, "_seedTokenValid", false);
            H.SetStatic(Sync, "_singleton", null);
            H.SetStatic(Sync, "OnBeforeGenerated", null);
            LastConsoleMessage = null;
            NetworkServer.connections.Clear();
            // ReferenceHub.GetHashCode reads the native GameObject; production code only enumerates this set.
            H.SetStatic(typeof(ReferenceHub), "<AllHubs>k__BackingField", new HashSet<ReferenceHub>(new HubComparer()));
        }

        private sealed class HubComparer : IEqualityComparer<ReferenceHub>
        {
            public bool Equals(ReferenceHub x, ReferenceHub y) => ReferenceEquals(x, y);
            public int GetHashCode(ReferenceHub obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        internal static object NewMessage(int value)
        {
            object message = Activator.CreateInstance(Message);
            Message.GetField("Value", H.All).SetValue(message, value);
            return message;
        }

        internal static int ReadValue(object message) => (int)Message.GetField("Value", H.All).GetValue(message);

        internal static void ClientReceive(int value) => H.Call(Sync, "ClientReceiveSeed", null, NewMessage(value));

        internal static void VanillaSendOnConnect(NetworkConnectionToClient connection) =>
            H.Call(Sync, "OnNewPlayerConnected", H.Uninit<SeedSynchronizer>(), connection);

        internal static Action GenerationFinished => (Action)H.GetStatic(Sync, "OnGenerationFinished");

        internal static void RaiseGenerationFinished() => GenerationFinished?.Invoke();

        internal static void AddBeforeGenerated(Action probe)
        {
            FieldInfo field = H.Field(Sync, "OnBeforeGenerated");
            field.SetValue(null, Delegate.Combine((Action)field.GetValue(null), probe));
        }

        internal static string SeedCommandOutput()
        {
            new CommandSystem.Commands.Console.SeedCommand().Execute(new ArraySegment<string>(new string[0]), null, out string response);
            return response;
        }

        // Registers the exact writer/reader the game's weaver generated for SeedMessage.
        internal static void InstallSerializers()
        {
            Type generated = Sync.Assembly.GetType("Mirror.GeneratedNetworkCode", true);
            MethodInfo write = generated.GetMethod("_Write_MapGeneration.SeedSynchronizer/SeedMessage", H.All);
            MethodInfo read = generated.GetMethod("_Read_MapGeneration.SeedSynchronizer/SeedMessage", H.All);
            Type writerHolder = typeof(Writer<>).MakeGenericType(Message);
            Type readerHolder = typeof(Reader<>).MakeGenericType(Message);
            writerHolder.GetField("write").SetValue(null, Delegate.CreateDelegate(typeof(Action<,>).MakeGenericType(typeof(NetworkWriter), Message), write));
            readerHolder.GetField("read").SetValue(null, Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(NetworkReader), Message), read));
            if (Transport.active == null) Transport.active = H.Alive(H.Uninit<FakeTransport>());
        }

        internal static ushort MessageId => (ushort)typeof(NetworkMessageId<>).MakeGenericType(Message).GetField("Id", H.All).GetValue(null);

        /// <summary>Decodes one packed Mirror message exactly as the client would before invoking its handler.</summary>
        internal static object Decode(byte[] packet, out ushort id)
        {
            var reader = new NetworkReader(new ArraySegment<byte>(packet));
            id = reader.ReadUShort();
            object message = ((Delegate)typeof(Reader<>).MakeGenericType(Message).GetField("read").GetValue(null)).DynamicInvoke(reader);
            if (reader.Remaining != 0) throw new Exception("消息尾部有多余数据");
            return message;
        }

        internal static ReferenceHub AddHub(int playerId, string nick, ClientInstanceMode mode, NetworkConnectionToClient connection, bool spawned)
        {
            ReferenceHub hub = H.Alive(H.Uninit<ReferenceHub>());
            PlayerAuthenticationManager auth = H.Alive(H.Uninit<PlayerAuthenticationManager>());
            H.Set(auth, "_targetInstanceMode", mode);
            H.Set(auth, "_privUserId", "player" + playerId + "@test");
            H.Set(hub, "authManager", auth);
            NicknameSync nickname = H.Alive(H.Uninit<NicknameSync>());
            H.Set(nickname, "<NickSet>k__BackingField", true);
            H.Set(nickname, "_firstNickname", nick);
            H.Set(hub, "nicknameSync", nickname);
            H.Set(hub, "gameConsoleTransmission", H.Alive(H.Uninit<GameConsoleTransmission>()));
            object id = H.Get(hub, "_playerId");
            H.Set(id, "Value", playerId);
            H.Set(hub, "_playerId", id);
            NetworkIdentity identity = H.Alive(H.Uninit<NetworkIdentity>());
            H.Set(identity, "_connectionToClient", connection);
            H.Set(hub, "_identitySet", true);
            H.Set(hub, "_identityCache", identity);
            if (connection != null)
            {
                if (spawned) H.Set(connection, "<identity>k__BackingField", identity);
                NetworkServer.connections[connection.connectionId] = connection;
            }
            ReferenceHub.AllHubs.Add(hub);
            return hub;
        }

        // Set by the SendToClient probe of the test runtime, in vanilla "color#text" form.
        internal static string LastConsoleMessage
        {
            get => (string)H.GetStatic(typeof(GameConsoleTransmission), "TestLastMessage");
            set => H.SetStatic(typeof(GameConsoleTransmission), "TestLastMessage", value);
        }

        internal static void SetMode(ReferenceHub hub, ClientInstanceMode mode) => H.Set(H.Get(hub, "authManager"), "_targetInstanceMode", mode);

        internal static void Spawn(ReferenceHub hub, bool spawned)
        {
            NetworkConnectionToClient connection = hub.connectionToClient;
            H.Set(connection, "<identity>k__BackingField", spawned ? (NetworkIdentity)H.Get(hub, "_identityCache") : null);
        }

        internal static void Disconnect(ReferenceHub hub)
        {
            NetworkServer.connections.Remove(hub.connectionToClient.connectionId);
            ReferenceHub.AllHubs.Remove(hub);
        }
    }

    internal sealed class RecordingConnection : NetworkConnectionToClient
    {
        internal List<Packet> Sent;
        internal int FailuresLeft;

        // Never called: the base constructor reads UnityEngine.Time, which needs the native engine.
        private RecordingConnection() : base(0) { }

        internal static RecordingConnection Create(int id, bool ready = true)
        {
            RecordingConnection connection = H.Uninit<RecordingConnection>();
            connection.Sent = new List<Packet>();
            H.Set(connection, "connectionId", id);
            connection.isReady = ready;
            return connection;
        }

        public override void Send(ArraySegment<byte> segment, int channelId)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new InvalidOperationException("模拟传输层发送失败");
            }
            var data = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, data, 0, segment.Count);
            Sent.Add(new Packet(data, channelId));
        }

        internal int SeedValue(int index)
        {
            object message = Game.Decode(Sent[index].Data, out ushort id);
            if (id != Game.MessageId) throw new Exception("不是 SeedMessage");
            return Game.ReadValue(message);
        }
    }

    internal sealed class Packet
    {
        internal Packet(byte[] data, int channel)
        {
            Data = data;
            Channel = channel;
        }

        internal byte[] Data { get; }
        internal int Channel { get; }
    }

    internal sealed class FakeTransport : Transport
    {
        public override bool Available() => true;
        public override bool ClientConnected() => false;
        public override void ClientConnect(string address) { }
        public override void ClientSend(ArraySegment<byte> segment, int channelId) { }
        public override void ClientDisconnect() { }
        public override Uri ServerUri() => null;
        public override bool ServerActive() => true;
        public override void ServerStart() { }
        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId) { }
        public override void ServerDisconnect(int connectionId) { }
        public override string ServerGetClientAddress(int connectionId) => "test";
        public override void ServerStop() { }
        public override int GetMaxPacketSize(int channelId) => 1200;
        public override void Shutdown() { }
    }

    internal sealed class TestLog : IGuardLog
    {
        internal readonly List<string> Lines = new List<string>();
        public void Info(string message) => Lines.Add("INFO " + message);
        public void Warn(string message) => Lines.Add("WARN " + message);
        public void Error(string message) => Lines.Add("ERROR " + message);
        public void Debug(string message) => Lines.Add("DEBUG " + message);
        internal int Count(string prefix, string text) => Lines.FindAll(l => l.StartsWith(prefix) && l.Contains(text)).Count;
    }

    internal sealed class Abort : Exception
    {
        internal Abort() : base("测试中止原版地图生成") { }
    }
}
