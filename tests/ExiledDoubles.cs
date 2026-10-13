// Offline substitutes for the EXILED members SeedGuard uses. Audit.ps1 checks every signature here against the
// real Exiled.API / Exiled.Events / Exiled.Permissions 9.14.2 assemblies.
using System;
using System.Collections.Generic;
using CommandSystem;

namespace Exiled.API.Interfaces
{
    public interface IConfig
    {
        bool IsEnabled { get; set; }
        bool Debug { get; set; }
    }
}

namespace Exiled.API.Features
{
    public abstract class Plugin<TConfig> where TConfig : Exiled.API.Interfaces.IConfig, new()
    {
        internal int BaseEnabled, BaseDisabled;
        public virtual string Name { get; }
        public virtual string Prefix { get; }
        public virtual string Author { get; }
        public virtual Version Version { get; }
        public virtual Version RequiredExiledVersion { get; }
        public virtual TConfig Config { get; } = new TConfig();
        public virtual void OnEnabled() => BaseEnabled++;
        public virtual void OnDisabled() => BaseDisabled++;
    }

    public static class Log
    {
        internal static readonly List<string> Lines = new List<string>();
        public static void Info(string message) => Lines.Add("INFO " + message);
        public static void Warn(string message) => Lines.Add("WARN " + message);
        public static void Error(string message) => Lines.Add("ERROR " + message);
        public static void Debug(string message) => Lines.Add("DEBUG " + message);
    }
}

namespace Exiled.Events.Features
{
    public delegate void CustomEventHandler();

    public class Event
    {
        private readonly List<CustomEventHandler> handlers = new List<CustomEventHandler>();
        internal int Count => handlers.Count;

        public static Event operator +(Event @event, CustomEventHandler handler)
        {
            @event.handlers.Add(handler);
            return @event;
        }

        public static Event operator -(Event @event, CustomEventHandler handler)
        {
            @event.handlers.Remove(handler);
            return @event;
        }

        internal void Raise()
        {
            foreach (CustomEventHandler handler in handlers.ToArray()) handler();
        }
    }
}

namespace Exiled.Events.Handlers
{
    public static class Server
    {
        public static Exiled.Events.Features.Event RestartingRound { get; set; } = new Exiled.Events.Features.Event();
    }
}

namespace Exiled.Permissions.Extensions
{
    public static class Permissions
    {
        public static bool CheckPermission(this ICommandSender sender, string permission) =>
            sender is SeedGuard.Tests.TestSender test && test.Allowed && permission == "seedguard.status";
    }
}

namespace SeedGuard.Tests
{
    internal sealed class TestSender : ICommandSender
    {
        internal TestSender(string name, bool allowed)
        {
            LogName = name;
            Allowed = allowed;
        }

        internal bool Allowed { get; }
        public string LogName { get; }
        public void Respond(string message, bool success = true) { }
    }
}
