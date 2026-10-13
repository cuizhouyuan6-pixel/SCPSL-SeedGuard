// Negative control only: compiles against the publicized reference but must fail on the restricted runtime.
using System.Runtime.CompilerServices;
using MapGeneration;
using Mirror;

namespace SeedGuard.Tests
{
    internal static class NaiveAccess
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Send(NetworkConnectionToClient connection)
        {
            if (SeedSynchronizer.MapGenerated) connection.Send(new SeedSynchronizer.SeedMessage { Value = 0 }, Channels.Reliable);
        }
    }
}
