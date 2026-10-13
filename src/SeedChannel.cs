using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using MapGeneration;
using Mirror;

namespace SeedGuard
{
    /// <summary>
    /// Sends the vanilla SeedSynchronizer.SeedMessage. The message type and the MapGenerated flag are reached
    /// through reflection because the publicized reference hides their real accessibility.
    /// </summary>
    internal sealed class SeedChannel
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];
        private readonly Type messageType;
        private readonly FieldInfo valueField;
        private readonly MethodInfo send;
        private readonly FieldInfo mapGenerated;

        static SeedChannel()
        {
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!(field.GetValue(null) is OpCode code)) continue;
                int value = (ushort)code.Value;
                if (code.Size == 1) OneByte[value] = code;
                else TwoByte[value & 0xFF] = code;
            }
        }

        private SeedChannel(Type messageType, FieldInfo valueField, MethodInfo send, FieldInfo mapGenerated)
        {
            this.messageType = messageType;
            this.valueField = valueField;
            this.send = send;
            this.mapGenerated = mapGenerated;
        }

        internal bool IsMapGenerated => (bool)mapGenerated.GetValue(null);

        internal static SeedChannel Resolve()
        {
            Type sync = typeof(SeedSynchronizer);
            Type message = sync.GetNestedType("SeedMessage", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw Missing("类型 MapGeneration.SeedSynchronizer+SeedMessage");
            if (!message.IsValueType || !typeof(NetworkMessage).IsAssignableFrom(message))
                throw Changed("SeedSynchronizer+SeedMessage 不再是 Mirror 网络消息结构");
            FieldInfo[] fields = message.GetFields(AnyInstance);
            if (fields.Length != 1 || fields[0].Name != "Value" || fields[0].FieldType != typeof(int))
                throw Changed("SeedSynchronizer+SeedMessage 的字段布局不再是单个 int Value");
            FieldInfo generated = sync.GetField("MapGenerated", AnyStatic);
            if (generated == null || generated.FieldType != typeof(bool)) throw Missing("静态字段 SeedSynchronizer.MapGenerated (bool)");
            MethodInfo receive = sync.GetMethod("ClientReceiveSeed", AnyStatic, null, new[] { message }, null);
            if (receive == null || receive.ReturnType != typeof(void)) throw Missing("方法 SeedSynchronizer.ClientReceiveSeed(SeedMessage)");
            VerifyClientHandler(receive, generated, sync);
            MethodInfo generic = null;
            foreach (MethodInfo candidate in typeof(NetworkConnection).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (candidate.Name != "Send" || !candidate.IsGenericMethodDefinition || candidate.ReturnType != typeof(void)) continue;
                Type[] arguments = candidate.GetGenericArguments();
                ParameterInfo[] parameters = candidate.GetParameters();
                if (arguments.Length == 1 && parameters.Length == 2 && parameters[0].ParameterType == arguments[0] && parameters[1].ParameterType == typeof(int))
                {
                    generic = candidate;
                    break;
                }
            }
            if (generic == null) throw Missing("方法 Mirror.NetworkConnection.Send<T>(T, int)");
            return new SeedChannel(message, fields[0], generic.MakeGenericMethod(message), generated);
        }

        /// <summary>Queues the message on the reliable channel the vanilla real seed already used.</summary>
        internal void Send(NetworkConnectionToClient connection, int value)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            object message = Activator.CreateInstance(messageType);
            valueField.SetValue(message, value);
            try
            {
                send.Invoke(connection, new object[] { message, Channels.Reliable });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        /// <summary>
        /// The replacement is only safe while the client handler skips regeneration once MapGenerated is set.
        /// If a game update changes that guard, refuse to start instead of risking a wrong client map.
        /// </summary>
        private static void VerifyClientHandler(MethodInfo receive, FieldInfo generated, Type sync)
        {
            MethodBody body = receive.GetMethodBody() ?? throw Changed("无法读取 ClientReceiveSeed 的方法体");
            List<Instruction> code = Decode(body.GetILAsByteArray());
            Module module = receive.Module;
            int guardBranch = -1, guardEnd = -1, guarded = 0;
            for (int i = 0; i + 1 < code.Count; i++)
            {
                if (code[i].Code != OpCodes.Ldsfld) continue;
                FieldInfo field = ResolveField(module, code[i].Token);
                if (field == null || field.Module != generated.Module || field.MetadataToken != generated.MetadataToken) continue;
                Instruction branch = code[i + 1];
                if (branch.Code != OpCodes.Brtrue_S && branch.Code != OpCodes.Brtrue) continue;
                guardBranch = branch.Offset;
                guardEnd = branch.Target;
            }
            foreach (Instruction instruction in code)
            {
                if (instruction.Code != OpCodes.Call && instruction.Code != OpCodes.Callvirt) continue;
                MethodBase target = ResolveMethod(module, instruction.Token);
                if (target == null || target.DeclaringType != sync || (target.Name != "GenerateLevel" && target.Name != "GenerateFacility")) continue;
                if (guardBranch < 0 || instruction.Offset <= guardBranch || instruction.Offset >= guardEnd)
                    throw Changed("ClientReceiveSeed 在地图已生成时仍可能重建地图");
                guarded++;
            }
            if (guardBranch < 0 || guarded == 0) throw Changed("ClientReceiveSeed 不再由 MapGenerated 控制地图生成");
        }

        private static List<Instruction> Decode(byte[] il)
        {
            var list = new List<Instruction>();
            int position = 0;
            while (position < il.Length)
            {
                int start = position;
                byte first = il[position++];
                OpCode code = first == 0xFE ? TwoByte[il[position++]] : OneByte[first];
                if (code.Size == 0) throw Changed($"无法解析 ClientReceiveSeed 偏移 {start} 处的指令");
                int operand = 0;
                switch (code.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        operand = (sbyte)il[position];
                        position += 1;
                        break;
                    case OperandType.InlineVar: position += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: position += 8; break;
                    case OperandType.InlineSwitch: position += 4 + 4 * BitConverter.ToInt32(il, position); break;
                    default:
                        operand = BitConverter.ToInt32(il, position);
                        position += 4;
                        break;
                }
                int target = code.OperandType == OperandType.ShortInlineBrTarget || code.OperandType == OperandType.InlineBrTarget ? position + operand : -1;
                list.Add(new Instruction(start, code, operand, target));
            }
            return list;
        }

        private static FieldInfo ResolveField(Module module, int token)
        {
            try { return module.ResolveField(token); }
            catch (Exception) { return null; }
        }

        private static MethodBase ResolveMethod(Module module, int token)
        {
            try { return module.ResolveMethod(token); }
            catch (Exception) { return null; }
        }

        private static InvalidOperationException Missing(string what) =>
            new InvalidOperationException($"游戏程序集中找不到{what}。SeedGuard 针对 SCP:SL 14.2.7 编写，游戏更新后需要重新核验。");

        private static InvalidOperationException Changed(string what) =>
            new InvalidOperationException($"{what}。补发伪种子可能导致玩家客户端地图错误，SeedGuard 拒绝启用，请等待插件更新。");

        private struct Instruction
        {
            internal Instruction(int offset, OpCode code, int token, int target)
            {
                Offset = offset;
                Code = code;
                Token = token;
                Target = target;
            }

            internal int Offset { get; }
            internal OpCode Code { get; }
            internal int Token { get; }
            internal int Target { get; }
        }
    }
}
