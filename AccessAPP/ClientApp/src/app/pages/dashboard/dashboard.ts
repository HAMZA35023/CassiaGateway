import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ScannerService, ScannedDevice } from '../../services/scanner.service';
import { FirmwareService } from '../../services/firmware.service';
import { FilterByMacPipe } from '../../filter-by-mac-pipe';
import { TabsComponent } from '../../components/tabs/tabs.component';
import { ConfirmationModalComponent } from '../../components/confirmation-modal/confirmation-modal.component';
import { UpgradePrepDialogComponent } from '../../components/upgrade-prep-dialog/upgrade-prep-dialog';
import { MatDialog } from '@angular/material/dialog';
import { DeviceStorageService, FirmwareProgress } from '../../services/device-storage.service';
import { ApiService } from '../../services/api.service';
import { Router, RouterModule } from '@angular/router';
import { finalize } from 'rxjs/operators';

type RowColor = 'queued' | 'failed' | 'success' | 'warn' | 'nofwread' | '';

@Component({
  standalone: true,
  selector: 'app-dashboard',
  templateUrl: './dashboard.html',
  styleUrls: ['./dashboard.css'],
  imports: [CommonModule, FormsModule, FilterByMacPipe, TabsComponent, ConfirmationModalComponent, RouterModule]
})
export class DashboardComponent implements OnInit, OnDestroy {
  private intervals: ReturnType<typeof setInterval>[] = [];
  // ── Tabs ──────────────────────────────────────────────────────────────────
  activeTab: 'devices' | 'queue' | 'log' | 'runtime' = 'devices';

  // ── Firmware manifest ──────────────────────────────────────────────────────
  firmwareMap: { [type: string]: string[] } = {};
  selectedFirmwareByType: { [type: string]: string } = {};
  selectedTypeFlags: { [type: string]: boolean } = {};
  detectorTypesInUse: string[] = [];

  // ── Devices ────────────────────────────────────────────────────────────────
  private productTypeCache = new Map<string, string>();
  devices: any[] = [];
  filteredDevices: any[] = [];
  selectedCount = 0;
  searchTerm = '';
  isLoading = true;
  forceUpdate = false;
  minRssiFilter: number = -127;
  readonly rssiOptions = [
    { label: 'All', value: -127 },
    { label: '≥ -80 dBm', value: -80 },
    { label: '≥ -75 dBm', value: -75 },
    { label: '≥ -70 dBm', value: -70 },
    { label: '≥ -65 dBm', value: -65 },
  ];

  // ── Confirmation ───────────────────────────────────────────────────────────
  showConfirmation = false;
  pendingDevices: any[] = [];

  // ── Progress ───────────────────────────────────────────────────────────────
  progressMap: Record<string, FirmwareProgress> = {};
  logEntries: FirmwareProgress[] = [];

  // ── Identifying ───────────────────────────────────────────────────────────
  identifyingMacs = new Set<string>();

  // ── Runtime variables ─────────────────────────────────────────────────────
  runtimeVars: Record<string, any> = {};
  runtimeSaving = false;
  runtimeSaveResult = '';

  constructor(
    private scannerService: ScannerService,
    private firmwareService: FirmwareService,
    private apiService: ApiService,
    private dialog: MatDialog,
    private deviceStorageService: DeviceStorageService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.loadNearbyDevices();
    this.intervals.push(setInterval(() => this.loadNearbyDevices(), 15000));
    this.loadFirmwareData();
    this.loadProgress();
    this.intervals.push(setInterval(() => this.loadProgress(), 3000));
  }

  ngOnDestroy(): void {
    this.intervals.forEach(id => clearInterval(id));
  }

  // ── Tab ────────────────────────────────────────────────────────────────────

  setTab(tab: 'devices' | 'queue' | 'log' | 'runtime'): void {
    this.activeTab = tab;
    if (tab === 'runtime' && Object.keys(this.runtimeVars).length === 0) this.loadRuntimeVars();
  }

  // ── Firmware manifest ──────────────────────────────────────────────────────

  private sortVersionsDesc(versions: string[]): string[] {
    return [...versions].sort((a, b) => {
      const parts = (v: string) => v.replace(/^v/i, '').split('.').map(n => parseInt(n, 10) || 0);
      const [ap, bp] = [parts(a), parts(b)];
      for (let i = 0; i < Math.max(ap.length, bp.length); i++) {
        const d = (bp[i] ?? 0) - (ap[i] ?? 0);
        if (d !== 0) return d;
      }
      return 0;
    });
  }

  private loadFirmwareData(): void {
    this.firmwareService.loadFirmwareManifest().subscribe({
      next: (manifest) => {
        for (const type of Object.keys(manifest))
          manifest[type] = this.sortVersionsDesc(manifest[type]);
        this.firmwareMap = manifest;
        this.detectorTypesInUse = Object.keys(manifest);
        for (const type of this.detectorTypesInUse) {
          if (!this.selectedFirmwareByType[type] && manifest[type]?.length)
            this.selectedFirmwareByType[type] = manifest[type][0]; // [0] is now highest
          if (this.selectedTypeFlags[type] === undefined) this.selectedTypeFlags[type] = true;
        }
        this.rebuildTypeRows();
        this.filterDevicesByAllCriteria();
      },
      error: () => {
        this.firmwareMap = this.firmwareService.getFirmwareMap();
        this.detectorTypesInUse = Object.keys(this.firmwareMap);
      }
    });
  }

  // Stable array — updated explicitly, NOT a getter, to avoid change-detection loops
  allTypeRows: { type: string; count: number; inManifest: boolean }[] = [];

  private rebuildTypeRows(): void {
    const fromDevices = [...new Set(this.devices.map(d => d.version))];
    const all = [...new Set([...this.detectorTypesInUse, ...fromDevices])];
    this.allTypeRows = all.map(type => ({
      type,
      count: this.devices.filter(d => d.version === type).length,
      inManifest: !!this.firmwareMap[type]?.length
    }));
  }

  trackByType(_: number, row: { type: string }): string { return row.type; }
  trackByMac(_: number, item: { mac?: string; macAddress?: string }): string {
    return item.mac ?? item.macAddress ?? '';
  }

  onTypeToggle(type: string): void {
    this.devices.forEach(d => { if (d.version === type && !this.selectedTypeFlags[type]) d.selected = false; });
    this.selectedCount = this.devices.filter(d => d.selected).length;
    this.filterDevicesByAllCriteria();
  }

  // ── Device list ────────────────────────────────────────────────────────────

  loadNearbyDevices(): void {
    const firstLoad = this.devices.length === 0;
    if (firstLoad) this.isLoading = true;

    this.scannerService.fetchNearbyDevices().pipe(
      finalize(() => { this.isLoading = false; })
    ).subscribe({
      next: (data: ScannedDevice[]) => {
        const byMac = new Map(this.devices.map(d => [d.mac, d]));

        data.forEach(d => {
          const mac = d.bdaddrs[0]?.bdaddr ?? 'N/A';
          const existing = byMac.get(mac);
          if (existing) { existing.rssi = d.rssi; return; }

          const rawType = d.detectorShortDescription
            ? (d.detectorShortDescription.includes('-') ? d.detectorShortDescription.split('-')[0] : d.detectorShortDescription)
            : '';

          if (rawType && d.productNumber) this.productTypeCache.set(d.productNumber, rawType);
          const version = rawType || (d.productNumber ? this.productTypeCache.get(d.productNumber) : undefined) || 'Unknown';

          const newDevice = {
            mac,
            version,
            Name: d.name,
            productNumber: d.productNumber,
            sensorVersion: '',
            rssi: d.rssi,
            selected: false,
            isLocked: d.isLocked,
            isFirmwareLoading: false
          };
          this.devices.push(newDevice);
          byMac.set(mac, newDevice);

          if (this.selectedTypeFlags[newDevice.version] === undefined)
            this.selectedTypeFlags[newDevice.version] = true;
        });

        this.rebuildTypeRows();
        this.filterDevicesByAllCriteria();
      },
      error: err => console.error('Failed to load devices:', err)
    });
  }

  filterDevicesByAllCriteria(): void {
    const enabled = Object.keys(this.selectedTypeFlags).filter(t => this.selectedTypeFlags[t]);
    this.filteredDevices = this.devices.filter(d => {
      const typeOk = enabled.includes(d.version);
      const macOk = !this.searchTerm || d.mac.toLowerCase().includes(this.searchTerm.toLowerCase());
      const rssiOk = this.minRssiFilter === -127 || (d.rssi != null && d.rssi >= this.minRssiFilter);
      return typeOk && macOk && rssiOk;
    });
  }

  onRssiFilterChange(): void { this.filterDevicesByAllCriteria(); }

  // ── Row color (WPF-style) ──────────────────────────────────────────────────

  getRowColor(device: any): RowColor {
    const p = this.progressMap[device.mac];
    if (!p) return '';
    if (p.status === 'Queued' || (p.progress > 0 && p.progress < 100)) return 'queued';
    if (p.status === 'Device Upgrade Completed.') {
      const r = (p.finalResult ?? '').toLowerCase();
      if (r === 'success') return 'success';
      if (r === 'warn') return 'warn';
      return 'failed';
    }
    const s = (p.status ?? '').toLowerCase();
    if (s.includes('fail') || s.includes('error')) return 'failed';
    return '';
  }

  // ── FW version display ─────────────────────────────────────────────────────

  extractAppVersion(v: string): string {
    if (!v) return '';
    try {
      const s = v.match(/Sensor:\s*App:\s*([A-Za-z0-9.]+)/i)?.[1];
      const a = v.match(/Actor:\s*App:\s*([A-Za-z0-9.]+)/i)?.[1];
      if (s && a) return `S:${s} A:${a}`;
      if (s) return `S:${s}`;
      if (a) return `A:${a}`;
      return '';
    } catch { return ''; }
  }

  refreshFirmwareVersion(device: any, event: Event): void {
    event.preventDefault();
    device.isFirmwareLoading = true;
    this.scannerService.getFirmwareVersionsByMac([device.mac]).subscribe({
      next: (m: any) => { device.sensorVersion = m[device.mac] ?? ''; },
      error: () => this.showToast(`Failed to fetch FW for ${device.mac}`),
      complete: () => { device.isFirmwareLoading = false; }
    });
  }

  fetchFirmwareForSelectedDevices(): void {
    const sel = this.filteredDevices.filter(d => d.selected);
    if (!sel.length) { this.showToast('Select at least one device.'); return; }
    sel.forEach(device => {
      device.isFirmwareLoading = true;
      this.scannerService.getFirmwareVersionsByMac([device.mac]).subscribe({
        next: (m: any) => { device.sensorVersion = m[device.mac] ?? ''; },
        error: () => this.showToast(`Error fetching FW for ${device.mac}`),
        complete: () => { device.isFirmwareLoading = false; }
      });
    });
  }

  // ── Selection ──────────────────────────────────────────────────────────────

  onDeviceClick(device: any, event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.checked && this.devices.filter(d => d.selected).length >= 100) {
      event.preventDefault(); input.checked = false;
      this.showToast('Maximum 100 devices can be selected.'); return;
    }
    device.selected = input.checked;
    this.selectedCount = this.devices.filter(d => d.selected).length;
  }

  toggleAllSelection(checked: boolean): void {
    if (checked) {
      let c = 0;
      for (const d of this.filteredDevices) { d.selected = c < 100; if (c < 100) c++; }
      this.selectedCount = c;
      if (this.filteredDevices.length > 100) this.showToast('Only first 100 devices selected.');
    } else {
      this.filteredDevices.forEach(d => d.selected = false);
      this.selectedCount = 0;
    }
  }

  onMasterCheckboxChange(e: Event): void { this.toggleAllSelection((e.target as HTMLInputElement).checked); }
  areAllSelected(): boolean {
    const s = this.filteredDevices.filter(d => d.selected).length;
    return s > 0 && s <= 100 && s === this.filteredDevices.length;
  }

  // ── Add to Queue ───────────────────────────────────────────────────────────

  addToQueue(): void {
    const sel = this.filteredDevices.filter(d => d.selected);
    if (!sel.length) { alert('No devices selected.'); return; }
    this.prepareAndConfirm(sel);
  }

  addSingleToQueue(device: any): void { this.prepareAndConfirm([device]); }

  private prepareAndConfirm(devices: any[]): void {
    const unknown = devices.filter(d => d.version === 'Unknown');
    const locked = devices.filter(d => d.isLocked);

    if (unknown.length || locked.length) {
      this.dialog.open(UpgradePrepDialogComponent, {
        width: '700px', data: { unknownDevices: unknown, lockedDevices: locked }
      }).afterClosed().subscribe(result => {
        if (!result?.confirmed) return;
        const updated = result.updatedDevices.map((d: any) => ({ ...d, selectedFirmware: this.selectedFirmwareByType[d.version] ?? '' }));
        const final = devices.map((o: any) => {
          const u = updated.find((x: any) => x.mac === o.mac);
          return u ? { ...o, ...u } : { ...o, selectedFirmware: this.selectedFirmwareByType[o.version] ?? '' };
        });
        const missing = [...new Set(final.map((d: any) => d.version as string))].filter(t => !this.selectedFirmwareByType[t]);
        if (missing.length) { alert(`Select firmware for: ${missing.join(', ')}`); return; }
        this.pendingDevices = final;
        this.showConfirmation = true;
      });
      return;
    }

    const missing = [...new Set(devices.map(d => d.version as string))].filter(t => !this.selectedFirmwareByType[t]);
    if (missing.length) { alert(`Select firmware for: ${missing.join(', ')}`); return; }
    this.pendingDevices = devices.map(d => ({ ...d, selectedFirmware: this.selectedFirmwareByType[d.version] }));
    this.showConfirmation = true;
  }

  confirmUpgrade(): void {
    this.showConfirmation = false;
    if (!this.pendingDevices?.length) return;
    this.pendingDevices.forEach(d => {
      this.progressMap[d.mac] = {
        macAddress: d.mac, progress: 0, status: 'Queued',
        lastUpdated: new Date().toISOString(), showTick: false,
        targetFirmwareVersion: d.selectedFirmware, detectorType: d.version
      };
    });
    this.firmwareService.bulkSensorUpgrade(this.pendingDevices, this.forceUpdate).subscribe({
      error: (err: any) => console.error('Upgrade failed:', err)
    });
    this.pendingDevices = [];
    this.setTab('queue');
  }

  cancelUpgrade(): void { this.showConfirmation = false; this.pendingDevices = []; }

  // ── Progress / Queue ───────────────────────────────────────────────────────

  loadProgress(): void {
    this.deviceStorageService.getUpgradeProgress().subscribe({
      next: (data: FirmwareProgress[]) => {
        const newMap: Record<string, FirmwareProgress> = {};
        data.forEach(item => { newMap[item.macAddress] = item; });
        for (const mac in this.progressMap)
          if (!newMap[mac]) newMap[mac] = this.progressMap[mac];
        this.progressMap = newMap;

        data.forEach(item => {
          if (item.status === 'Device Upgrade Completed.') {
            const ex = this.logEntries.find(e => e.macAddress === item.macAddress);
            if (!ex) this.logEntries.unshift({ ...item });
            else Object.assign(ex, item);
          }
        });
      },
      error: err => console.error('Failed to load progress:', err)
    });
  }

  get queueItems(): FirmwareProgress[] {
    return Object.values(this.progressMap)
      .filter(p => p.progress < 100 && !(p.status ?? '').toLowerCase().includes('fail'))
      .sort((a, b) => (a.status === 'Queued' ? 1 : 0) - (b.status === 'Queued' ? 1 : 0));
  }

  removeFromQueue(mac: string): void {
    this.firmwareService.removeFromQueue(mac).subscribe({
      next: () => delete this.progressMap[mac],
      error: err => console.error('Remove failed:', err)
    });
  }

  clearQueue(): void {
    this.firmwareService.clearQueue().subscribe({
      next: () => {
        for (const mac in this.progressMap)
          if (this.progressMap[mac].status === 'Queued') delete this.progressMap[mac];
      },
      error: err => console.error('Clear queue failed:', err)
    });
  }

  canRemoveFromQueue(item: FirmwareProgress): boolean { return item.status === 'Queued' && item.progress === 0; }

  getQueueStatusClass(item: FirmwareProgress): string {
    const s = (item.status ?? '').toLowerCase();
    if (s === 'queued') return 'pill-queued';
    if (s.includes('fail') || s.includes('error')) return 'pill-failed';
    return 'pill-progress';
  }

  // ── Log ────────────────────────────────────────────────────────────────────

  clearLog(): void { this.logEntries = []; }

  getLogResult(entry: FirmwareProgress): string {
    return entry.finalResult ?? entry.status ?? '';
  }

  isLogSuccess(entry: FirmwareProgress): boolean {
    const r = (entry.finalResult ?? '').toLowerCase();
    return r === 'success';
  }

  // ── Identify ───────────────────────────────────────────────────────────────

  identifyDevice(device: any): void {
    if (this.identifyingMacs.has(device.mac)) return;
    this.identifyingMacs.add(device.mac);
    this.apiService.identifyDevice(device.mac, device.pin).subscribe({
      next: (r: any) => this.showToast(`Identify ${device.mac}: ${r.success ? 'OK' : (r.error ?? 'Failed')}`),
      error: () => this.showToast(`Identify failed for ${device.mac}`),
      complete: () => this.identifyingMacs.delete(device.mac)
    });
  }

  // ── Runtime variables ──────────────────────────────────────────────────────

  runtimeVarKeyList: string[] = [];

  loadRuntimeVars(): void {
    this.apiService.getRuntimeVariables().subscribe({
      next: (v) => { this.runtimeVars = v; this.runtimeVarKeyList = Object.keys(v).sort(); },
      error: err => console.error('Failed to load runtime vars:', err)
    });
  }

  saveRuntimeVars(): void {
    this.runtimeSaving = true; this.runtimeSaveResult = '';
    this.apiService.setRuntimeVariables(this.runtimeVars).subscribe({
      next: (res) => {
        this.runtimeSaving = false;
        this.runtimeSaveResult = res.errors?.length
          ? `Saved with errors: ${res.errors.join(', ')}`
          : `Saved ${res.updated?.length ?? 0} variable(s)`;
      },
      error: () => { this.runtimeSaving = false; this.runtimeSaveResult = 'Save failed'; }
    });
  }

  getRuntimeVarType(key: string): 'bool' | 'number' | 'string' {
    const v = this.runtimeVars[key];
    if (typeof v === 'boolean') return 'bool';
    if (typeof v === 'number') return 'number';
    return 'string';
  }

  trackByKey(_: number, key: string): string { return key; }

  // ── Utility ────────────────────────────────────────────────────────────────

  showToast(message: string): void {
    const el = document.createElement('div');
    el.innerHTML = `<div class="toast-glass">${message}</div>`;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 3000);
  }

  viewLogs(): void { this.router.navigate(['/logs-dashboard']); }
}
