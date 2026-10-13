using System;
using CommandSystem;
using Exiled.Permissions.Extensions;

namespace SeedGuard
{
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class SeedGuardCommand : ICommand
    {
        internal const string Permission = "seedguard.status";
        public string Command => "seedguard";
        public string[] Aliases => new[] { "sgd" };
        public string Description => "SeedGuard：查看地图种子隐藏状态。用法：seedguard [status|list]";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (!sender.CheckPermission(Permission))
            {
                response = $"你没有权限（{Permission}）。";
                return false;
            }
            string mode = arguments.Count > 0 ? arguments.Array[arguments.Offset].ToLowerInvariant() : "status";
            if (arguments.Count > 1 || (mode != "status" && mode != "list"))
            {
                response = "用法：seedguard [status|list]";
                return false;
            }
            GuardService service = Plugin.Instance?.Service;
            if (service == null)
            {
                response = "SeedGuard 未启用。";
                return false;
            }
            response = service.BuildStatus(mode == "list");
            return true;
        }
    }

    /// <summary>Only ".seed" reaches the server; the vanilla "seed" runs entirely on the client.</summary>
    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class SeedClientCommand : ICommand
    {
        internal const string DefaultNotice = "服务器禁止使用Seed指令";
        public string Command => "seed";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "地图种子已被服务器隐藏。";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            Plugin plugin = Plugin.Instance;
            string notice = plugin?.Config.Notice;
            response = string.IsNullOrEmpty(notice) ? DefaultNotice : notice;
            plugin?.Service?.RecordClientAttempt(sender?.LogName ?? "未知玩家");
            return false;
        }
    }
}
