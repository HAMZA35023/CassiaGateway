using AccessAPP.Logging;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        // Public entrypoint used by MQTT start-update handler
        public Task EnqueueUpgradesAsync(IEnumerable<UpgradeProgress> devices, int numbersOfThreadsInParallel = -1)
        {
            var list = devices?.Where(d => d != null).ToList() ?? new List<UpgradeProgress>();
            if (list.Count == 0) return Task.CompletedTask;

            // IMPORTANT: mark queued immediately (publishes progress "Queued")
            foreach (var d in list)
            {
                if (string.IsNullOrWhiteSpace(d.MacAddress)) continue;
                _deviceStorageService.UpdateFirmwareProgress(d.MacAddress, 0, "Queued", null, d.FirmwareVersion, d.DetectotType);
            }

            // Reuse your existing queue-aware parallel upgrader
            // (UpgradeDevicesInParallel is already the place you wanted to feed)
            _ = UpgradeDevicesInParallel(list, numbersOfThreadsInParallel);

            return Task.CompletedTask;
        }
    }
}
