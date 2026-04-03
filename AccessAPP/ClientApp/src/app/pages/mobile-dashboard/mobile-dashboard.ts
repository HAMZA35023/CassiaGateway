import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { finalize } from 'rxjs/operators';
import { ScannerService, ScannedDevice } from '../../services/scanner.service';
import { FirmwareService } from '../../services/firmware.service';
import { DeviceStorageService, FirmwareProgress } from '../../services/device-storage.service';
import { LayoutService } from '../../services/layout.service';

interface MobileDevice {
  mac: string;
  version: string;
  Name: string;
  productNumber: string;
  rssi: number;
  isLocked: boolean;
}

// Predefined upgrade groups shown as quick-action buttons
const UPGRADE_GROUPS: { label: string; types: string[] }[] = [
  { label: 'P46', types: ['P46'] },
  { label: 'P47 + P48', types: ['P47', 'P48'] },
  { label: 'P41 + P42', types: ['P41', 'P42'] },
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

  // All discovered devices
  allDevices: MobileDevice[] = [];
  // Devices shown in the Devices tab (filtered)
  visibleDevices: MobileDevice[] = [];

  isLoading = true;

  minRssi = -75;
  showCompleted = false;

  readonly rssiOptions = [
    { label: 'All', value: -127 },
    { label: '≥ -80 dBm', value: -80 },
    { label: '≥ -75 dBm', value: -75 },
    { label: '≥ -70 dBm', value: -70 },
    { label: '≥ -65 dBm', value: -65 },
  ];

  // Firmware manifest: type → versions[] (sorted desc, [0] = latest)
  firmwareMap: { [type: string]: string[] } = {};
  // Per-type selected firmware version (defaults to latest)
  selectedFirmwareByType: { [type: string]: string } = {};

  progressMap: Record<string, FirmwareProgress> = {};

  showConfirmation = false;
  pendingDevices: { mac: string; version: string; selectedFirmware: string }[] = [];
  pendingLabel = '';

  constructor(
    private scanner: ScannerService,
    private firmware: FirmwareService,
    private storage: DeviceStorageService,
    private router: Router,
    private layoutService: LayoutService
  ) {}

  ngOnInit(): void {
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
      if (!this.showCompleted) {
        const p = this.progressMap[d.mac];
        if (p?.status === 'Device Upgrade Completed.') return false;
      }
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

  get fwTypeKeys(): string[] {
    return Object.keys(this.firmwareMap).sort();
  }

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

  isDeviceActive(device: MobileDevice): boolean {
    const p = this.progressMap[device.mac];
    return !!p && p.status !== 'Device Upgrade Completed.';
  }

  get queueItems(): FirmwareProgress[] {
    return Object.values(this.progressMap)
      .filter(p => p.status !== 'Device Upgrade Completed.')
      .sort((a, b) => (a.status === 'Queued' ? 1 : 0) - (b.status === 'Queued' ? 1 : 0));
  }

  get doneItems(): FirmwareProgress[] {
    return Object.values(this.progressMap)
      .filter(p => p.status === 'Device Upgrade Completed.')
      .sort((a, b) => (b.lastUpdated ?? '').localeCompare(a.lastUpdated ?? ''));
  }

  getCardColorClass(device: MobileDevice): string {
    const p = this.progressMap[device.mac];
    if (!p) return '';
    if (p.status === 'Device Upgrade Completed.') {
      const r = (p.finalResult ?? '').toLowerCase();
      if (r === 'success') return 'card-success';
      if (r === 'warn') return 'card-warn';
      return 'card-failed';
    }
    if (p.status === 'Queued' || p.progress > 0) return 'card-queued';
    return '';
  }

  getDoneClass(item: FirmwareProgress): string {
    const r = (item.finalResult ?? '').toLowerCase();
    if (r === 'success') return 'card-success';
    if (r === 'warn') return 'card-warn';
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
      mac: d.mac,
      version: d.version,
      selectedFirmware: this.selectedFirmwareByType[d.version] ?? ''
    }));
    const noFw = withFw.filter(d => !d.selectedFirmware);
    if (noFw.length) {
      const missing = [...new Set(noFw.map(d => d.version))];
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
    // Map to shape expected by FirmwareService.bulkSensorUpgrade
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
