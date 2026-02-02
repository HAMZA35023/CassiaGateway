using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services.UpgradeCore
{
    /// <summary>
    /// Cassia X2000 dual-chip support.
    ///
    /// This is a direct extraction of the previous in-class ChipAllocator + chip map,
    /// to keep behavior identical while moving the responsibility into a dedicated type.
    /// </summary>
    internal sealed class ChipAllocationManager
    {
        private readonly Func<int> _getParallelProgrammers;

        // Each active upgrade is pinned to a chip (0/1) for consistent REST behavior.
        private readonly ConcurrentDictionary<string, int> _chipByMac = new(StringComparer.OrdinalIgnoreCase);
        private readonly ChipAllocator _allocator;

        public ChipAllocationManager(Func<int> getParallelProgrammers)
        {
            _getParallelProgrammers = getParallelProgrammers;
            _allocator = new ChipAllocator(getParallelProgrammers);
        }

        public int GetChipForMac(string mac, int defaultChip)
        {
            if (string.IsNullOrWhiteSpace(mac)) return defaultChip;
            return _chipByMac.TryGetValue(mac, out var chip) ? chip : defaultChip;
        }

        public void BindMacToChip(string mac, int chip)
        {
            if (string.IsNullOrWhiteSpace(mac)) return;
            _chipByMac[mac] = chip;
        }

        public void UnbindMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return;
            _chipByMac.TryRemove(mac, out _);
        }

        public int GetBoundChipOrThrow(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) throw new ArgumentException("mac is empty", nameof(mac));
            return _chipByMac[mac];
        }

        public Task<ChipLease> AcquireAsync() => _allocator.AcquireAsync();

        public static ChipLease Fixed(int chip) => ChipLease.Fixed(chip);

        // ------------------- Extracted allocator -------------------
        internal sealed class ChipAllocator
        {
            private readonly Func<int> _getParallel;
            private readonly object _gate = new();
            private int _inUse0;
            private int _inUse1;
            private int _rr;
            private TaskCompletionSource<bool> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ChipAllocator(Func<int> getParallel) => _getParallel = getParallel;

            private int GetMaxUpgradesPerChip()
            {
                // If user sets parallel=4 => allow 2 upgrades on each chip.
                var parallel = Math.Max(1, _getParallel());
                var perChip = (parallel + 1) / 2; // ceil(parallel/2)
                return Math.Clamp(perChip, 1, 8);
            }

            public async Task<ChipLease> AcquireAsync()
            {
                while (true)
                {
                    Task waitTask;
                    lock (_gate)
                    {
                        var maxPerChip = GetMaxUpgradesPerChip();
                        var prefer = Interlocked.Increment(ref _rr) & 1;

                        if (TryTake(prefer == 0 ? 0 : 1, maxPerChip, out var lease) ||
                            TryTake(prefer == 0 ? 1 : 0, maxPerChip, out lease))
                        {
                            return lease;
                        }

                        waitTask = _signal.Task;
                    }

                    await waitTask.ConfigureAwait(false);
                }
            }

            private bool TryTake(int chip, int maxPerChip, out ChipLease lease)
            {
                if (chip == 0)
                {
                    if (_inUse0 >= maxPerChip)
                    {
                        lease = default;
                        return false;
                    }
                    _inUse0++;
                    lease = ChipLease.Create(this, 0);
                    return true;
                }
                else
                {
                    if (_inUse1 >= maxPerChip)
                    {
                        lease = default;
                        return false;
                    }
                    _inUse1++;
                    lease = ChipLease.Create(this, 1);
                    return true;
                }
            }

            internal void Release(int chip)
            {
                lock (_gate)
                {
                    if (chip == 0) _inUse0 = Math.Max(0, _inUse0 - 1);
                    else _inUse1 = Math.Max(0, _inUse1 - 1);

                    var old = _signal;
                    _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    old.TrySetResult(true);
                }
            }
        }

        internal readonly struct ChipLease : IDisposable
        {
            public int Chip { get; }
            private readonly ChipAllocator? _allocator;

            private ChipLease(ChipAllocator? allocator, int chip)
            {
                _allocator = allocator;
                Chip = chip;
            }

            // Must not be public because ChipAllocator is a private nested type.
            internal static ChipLease Create(ChipAllocator allocator, int chip) => new ChipLease(allocator, chip);
            public static ChipLease Fixed(int chip) => new ChipLease(null, chip);

            public void Dispose()
            {
                try
                {
                    _allocator?.Release(Chip);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
