import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { finalize } from 'rxjs/operators';
import { ScannerService, ScannedDevice } from '../../services/scanner.service';
import { FirmwareService } from '../../services/firmware.service';
import { DeviceStorageService, FirmwareProgress } from '../../services/device-storage.service';
import { ApiService } from '../../services/api.service';
import { LayoutService } from '../../services/layout.service';

interface MobileDevice {
  mac: string;
  version: string;
  Name: string;
  productNumber: string;
  rssi: number;
  isLocked: boolean;
}

/** Parsed from the persistent upgrade log — survives backend reboots. */
interface LogEntry {
  result: string;       // 'Success' | 'Failed'
  firmware: string;     // target firmware version
  time: string;         // ISO-ish timestamp string from log
}

// Predefined upgrade groups shown as quick-action buttons
const UPGRADE_GROUPS: { label: string; types: string[] }[] = [
  { label: 'P46',        types: ['P46'] },
  { label: 'P47 + P48',  types: ['P47', 'P48'] },
  { label: 'P41 + P42',  types: ['P41', 'P42'] },
];

@Component({
  standalone: true,
  selector: 'app-mobile-dashboard',
  templateUrl: './mobile-dashboard.html',
  styleUrls: ['./mobile-dashboard.css'],
  imports: [CommonModule, FormsModule]
})
export class MobileDashboardComponent implements OnInit, OnDestroy {
  private intervals: ReturnType<typeof setInterval>[] = [];
  private productTypeCache = new Map<string, string>();

  readonly upgradeGroups = UPGRADE_GROUPS;

  activeTab: 'devices' | 'queue' | 'done' | 'fw' = 'devices';

  allDevices: MobileDevice[] = [];
  visibleDevices: MobileDevice[] = [];

  isLoading = true;

  minRssi = -75;
  showCompleted = false;

  readonly rssiOptions = [
    { label: 'All',       value: -127 },
    { label: '≥ -80 dBm', value: -80  },
    { label: '≥ -75 dBm', value: -75  },
    { label: '≥ -70 dBm', value: -70  },
    { label: '≥ -65 dBm', value: -65  },
  ];

  firmwareMap: { [type: string]: string[] } = {};
  selectedFirmwareByType: { [type: string]: string } = {};

  progressMap: Record<string, FirmwareProgress> = {};

  /**
   * Most recent completed upgrade per MAC, parsed from the persistent upgrade log.
   * Populated once on init — survives backend reboots where progressMap would be empty.
   */
  logDoneMap = new Map<string, LogEntry>();

  showConfirmation = false;
  pendingDevices: { mac: string; version: string; selectedFirmware: string }[] = [];
  pendingLabel = '';

  constructor(
    private scanner: ScannerService,
    private firmware: FirmwareService,
    private storage: DeviceStorageService,
    private api: ApiService,
    private router: Router,
    private layoutService: LayoutService
  ) {}

  ngOnInit(): void {
    this.loadUpgradeLogs();
    this.loadDevices();
    this.intervals.push(setInterval(() => this.loadDevices(), 15000));
    this.loadFirmware();
    this.loadProgress();
    this.intervals.push(setInterval(() => this.loadProgress(), 3000));
  }

  ngOnDestroy(): void {
    this.intervals.forEach(id => clearInterval(id));
  }

  switchToDesktop(): void {
    this.layoutService.setLayout('desktop');
    this.router.navigate(['/dashboard']);
  }

  // ── Persistent upgrade log ─────────────────────────────────────

  /**
   * Fetches the upgrade log text once on init and builds logDoneMap.
   * For each MAC we keep only the most recent completed entry.
   * This ensures completed devices are visible after a backend reboot.
   */
  private loadUpgradeLogs(): void {
    this.api.getLogs().subscribe({
      next: (text: string) => {
        this.logDoneMap = this.parseLogToMap(text);
        this.applyFilters();
      },
      error: () => { /* log unavailable — proceed with in-memory only */ }
    });
  }

  private parseLogToMap(text: string): Map<string, LogEntry> {
    const perMac = new Map<string, LogEntry>();

    for (const line of text.split('\n')) {
      // Only process completion lines — avoids logId-grouping complexity
      if (!line.includes('Device Upgrade Completed.')) continue;

      const time   = line.match(/time=([\d\-\.: ]+)/)?.[1]?.trim();
      const mac    = line.match(/mac=([A-F0-9:]+)/i)?.[1];
      const fw     = line.match(/\bfw=(\S+)/)?.[1] ?? '';
      const status = line.match(/status=(\S+)/)?.[1]?.trim() ?? '';

      if (!time || !mac) continue;

      // status values from backend: 'Success' | 'Warn' | 'Failed'
      const result = status === 'Success' ? 'Success'
                   : status === 'Warn'    ? 'Warn'
                   : 'Failed';

      // Keep the most recent completion per MAC
      const existing = perMac.get(mac);
      if (!existing || time > existing.time)
        perMac.set(mac, { result, firmware: fw, time });
    }

    return perMac;
  }

  // ── Devices ────────────────────────────────────────────────────

  loadDevices(): void {
    const firstLoad = this.allDevices.length === 0;
    if (firstLoad) this.isLoading = true;

    this.scanner.fetchNearbyDevices().pipe(
      finalize(() => { this.isLoading = false; })
    ).subscribe({
      next: (data: ScannedDevice[]) => {
        const byMac = new Map(this.allDevices.map(d => [d.mac, d]));
        data.forEach(d => {
          const mac = d.bdaddrs[0]?.bdaddr ?? 'N/A';
          const existing = byMac.get(mac);
          if (existing) { existing.rssi = d.rssi; return; }
          const rawType = d.detectorShortDescription
            ? (d.detectorShortDescription.includes('-')
                ? d.detectorShortDescription.split('-')[0]
                : d.detectorShortDescription)
            : '';
          if (rawType && d.productNumber) this.productTypeCache.set(d.productNumber, rawType);
          const version = rawType
            || (d.productNumber ? this.productTypeCache.get(d.productNumber) : undefined)
            || 'Unknown';
          const newDevice: MobileDevice = {
            mac, version, Name: d.name, productNumber: d.productNumber,
            rssi: d.rssi, isLocked: d.isLocked
          };
          this.allDevices.push(newDevice);
          byMac.set(mac, newDevice);
        });
        this.applyFilters();
      },
      error: err => console.error('Failed to load devices:', err)
    });
  }

  applyFilters(): void {
    this.visibleDevices = this.allDevices.filter(d => {
      const rssiOk = this.minRssi === -127 || (d.rssi != null && d.rssi >= this.minRssi);
      if (!rssiOk) return false;
      if (!this.showCompleted && this.isCompleted(d.mac)) return false;
      return true;
    });
  }

  // ── Firmware ───────────────────────────────────────────────────

  loadFirmware(): void {
    this.firmware.loadFirmwareManifest().subscribe({
      next: manifest => {
        for (const type of Object.keys(manifest))
          manifest[type] = this.sortVersionsDesc(manifest[type]);
        this.firmwareMap = manifest;
        for (const type of Object.keys(manifest)) {
          if (!this.selectedFirmwareByType[type] && manifest[type]?.length)
            this.selectedFirmwareByType[type] = manifest[type][0];
        }
      }
    });
  }

  private sortVersionsDesc(versions: string[]): string[] {
    return [...versions].sort((a, b) => {
      const parts = (v: string) => v.replace(/^v/i, '').split('.').map(n => parseInt(n, 10) || 0);
      const [ap, bp] = [parts(a), parts(b)];
      for (let i = 0; i < Math.max(ap.length, bp.length); i++) {
        const diff = (bp[i] ?? 0) - (ap[i] ?? 0);
        if (diff !== 0) return diff;
      }
      return 0;
    });
  }

  get fwTypeKeys(): string[] { return Object.keys(this.firmwareMap).sort(); }

  isLatestSelected(type: string): boolean {
    const versions = this.firmwareMap[type];
    return !!versions?.length && this.selectedFirmwareByType[type] === versions[0];
  }

  // ── Progress ───────────────────────────────────────────────────

  loadProgress(): void {
    this.storage.getUpgradeProgress().subscribe({
      next: (data: FirmwareProgress[]) => {
        const newMap: Record<string, FirmwareProgress> = {};
        data.forEach(item => { newMap[item.macAddress] = item; });
        for (const mac in this.progressMap)
          if (!newMap[mac]) newMap[mac] = this.progressMap[mac];
        this.progressMap = newMap;
        this.applyFilters();
      },
      error: err => console.error('Failed to load progress:', err)
    });
  }

  getProgress(device: MobileDevice): FirmwareProgress | undefined {
    return this.progressMap[device.mac];
  }

  /**
   * True when a device has a SUCCESSFUL completed upgrade.
   * Failed / Warn devices are always shown so the user can re-queue them.
   */
  isCompleted(mac: string): boolean {
    const p = this.progressMap[mac];
    if (p?.status === 'Device Upgrade Completed.' && p.finalResult === 'Success') return true;
    return this.logDoneMap.get(mac)?.result === 'Success';
  }

  /** True when the device is currently queued or being upgraded (this session only). */
  isDeviceActive(device: MobileDevice): boolean {
    const p = this.progressMap[device.mac];
    return !!p && p.status !== 'Device Upgrade Completed.';
  }

  get queueItems(): FirmwareProgress[] {
    return Object.values(this.progressMap)
      .filter(p => p.status !== 'Device Upgrade Completed.')
      .sort((a, b) => (a.status === 'Queued' ? 1 : 0) - (b.status === 'Queued' ? 1 : 0));
  }

  /**
   * Completed items for the Done tab.
   * Merges live progressMap completions (current session) with the persistent log
   * so the list survives a backend reboot. progressMap takes precedence for the
   * same MAC (more up-to-date status).
   */
  get doneItems(): FirmwareProgress[] {
    const items = new Map<string, FirmwareProgress>();

    // Add log-based completions first (older/background data)
    for (const [mac, entry] of this.logDoneMap) {
      items.set(mac, {
        macAddress: mac,
        progress: 100,
        status: 'Device Upgrade Completed.',
        lastUpdated: entry.time,
        showTick: true,
        targetFirmwareVersion: entry.firmware,
        finalResult: entry.result,
      });
    }

    // Overwrite with live completions from this session (always more current)
    for (const p of Object.values(this.progressMap)) {
      if (p.status === 'Device Upgrade Completed.')
        items.set(p.macAddress, p);
    }

    return [...items.values()]
      .sort((a, b) => (b.lastUpdated ?? '').localeCompare(a.lastUpdated ?? ''));
  }

  getCardColorClass(device: MobileDevice): string {
    const p = this.progressMap[device.mac];
    if (p) {
      if (p.status === 'Device Upgrade Completed.') {
        const r = (p.finalResult ?? '').toLowerCase();
        if (r === 'success') return 'card-success';
        if (r === 'warn')    return 'card-warn';
        return 'card-failed';
      }
      if (p.status === 'Queued' || p.progress > 0) return 'card-queued';
    }
    // Fall back to log entry if no live progress
    const log = this.logDoneMap.get(device.mac);
    if (log) return log.result === 'Success' ? 'card-success'
                  : log.result === 'Warn'    ? 'card-warn'
                  : 'card-failed';
    return '';
  }

  getDoneClass(item: FirmwareProgress): string {
    const r = (item.finalResult ?? '').toLowerCase();
    if (r === 'success') return 'card-success';
    if (r === 'warn')    return 'card-warn';
    return 'card-failed';
  }

  getQueueStatusClass(item: FirmwareProgress): string {
    const s = (item.status ?? '').toLowerCase();
    if (s === 'queued') return 'pill-queued';
    if (s.includes('fail') || s.includes('error')) return 'pill-failed';
    return 'pill-progress';
  }

  // ── Quick-action groups ────────────────────────────────────────

  countEligible(types: string[]): number {
    return this.visibleDevices.filter(d => types.includes(d.version) && !this.isDeviceActive(d)).length;
  }

  hasAnyFirmware(types: string[]): boolean {
    return types.some(t => !!this.firmwareMap[t]?.length);
  }

  queueGroup(label: string, types: string[]): void {
    const eligible = this.visibleDevices.filter(d => types.includes(d.version) && !this.isDeviceActive(d));
    if (!eligible.length) {
      this.showToast('No eligible devices found for: ' + types.join(', '));
      return;
    }
    const withFw = eligible.map(d => ({
      mac: d.mac, version: d.version,
      selectedFirmware: this.selectedFirmwareByType[d.version] ?? ''
    }));
    const missing = [...new Set(withFw.filter(d => !d.selectedFirmware).map(d => d.version))];
    if (missing.length) {
      this.showToast('No firmware configured for: ' + missing.join(', ') + '. Check the Firmware tab.');
      return;
    }
    this.pendingDevices = withFw;
    this.pendingLabel = label;
    this.showConfirmation = true;
  }

  queueSingle(device: MobileDevice): void {
    const fw = this.selectedFirmwareByType[device.version];
    if (!fw) {
      this.showToast('No firmware for type: ' + device.version + '. Check the Firmware tab.');
      return;
    }
    this.pendingDevices = [{ mac: device.mac, version: device.version, selectedFirmware: fw }];
    this.pendingLabel = device.mac;
    this.showConfirmation = true;
  }

  confirmUpgrade(): void {
    this.showConfirmation = false;
    if (!this.pendingDevices.length) return;
    this.pendingDevices.forEach(d => {
      this.progressMap[d.mac] = {
        macAddress: d.mac, progress: 0, status: 'Queued',
        lastUpdated: new Date().toISOString(), showTick: false,
        targetFirmwareVersion: d.selectedFirmware, detectorType: d.version
      };
    });
    const devicesForService = this.pendingDevices.map(d => ({
      mac: d.mac, version: d.version, pin: '', sensorVersion: '',
      selectedFirmware: d.selectedFirmware
    }));
    this.firmware.bulkSensorUpgrade(devicesForService, false).subscribe({
      error: (err: any) => console.error('Upgrade failed:', err)
    });
    this.pendingDevices = [];
    this.activeTab = 'queue';
  }

  cancelUpgrade(): void {
    this.showConfirmation = false;
    this.pendingDevices = [];
  }

  get pendingFwSummary(): { type: string; firmware: string; count: number }[] {
    const map = new Map<string, { firmware: string; count: number }>();
    for (const d of this.pendingDevices) {
      const existing = map.get(d.version);
      if (existing) existing.count++;
      else map.set(d.version, { firmware: d.selectedFirmware, count: 1 });
    }
    return [...map.entries()].map(([type, v]) => ({ type, ...v }));
  }

  // ── Utilities ──────────────────────────────────────────────────

  trackByMac(_: number, item: { mac?: string; macAddress?: string }): string {
    return item.mac ?? item.macAddress ?? '';
  }

  showToast(message: string): void {
    const el = document.createElement('div');
    el.innerHTML = `<div class="toast-glass">${message}</div>`;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 3000);
  }
}
