using System;
using UnityEngine;

namespace SeedGuard
{
    /// <summary>Drives the throttled sweep from Unity's main loop and survives scene reloads.</summary>
    internal sealed class GuardTicker : MonoBehaviour
    {
        private GuardService service;

        internal static IDisposable Attach(GuardService service)
        {
            var host = new GameObject("SeedGuard");
            try
            {
                DontDestroyOnLoad(host);
                GuardTicker ticker = host.AddComponent<GuardTicker>();
                ticker.service = service;
                return new Handle(host, ticker);
            }
            catch
            {
                Destroy(host);
                throw;
            }
        }

        private void Update() => service?.Tick();

        private sealed class Handle : IDisposable
        {
            private GameObject host;
            private GuardTicker ticker;

            internal Handle(GameObject host, GuardTicker ticker)
            {
                this.host = host;
                this.ticker = ticker;
            }

            public void Dispose()
            {
                // Destroy is deferred to the end of the frame; detach first so no further tick runs.
                if (ticker != null) ticker.service = null;
                if (host != null) Destroy(host);
                ticker = null;
                host = null;
            }
        }
    }
}
