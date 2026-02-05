import { Component,ElementRef, HostListener,  OnInit,ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ScannerService, ScannedDevice } from '../../services/scanner.service';
import { FirmwareService } from '../../services/firmware.service';
import { FilterByMacPipe } from '../../filter-by-mac-pipe';
import { TabsComponent } from '../../components/tabs/tabs.component';
import { ConfirmationModalComponent } from '../../components/confirmation-modal/confirmation-modal.component';
import { UpgradePrepDialogComponent  } from '../../components/upgrade-prep-dialog/upgrade-prep-dialog';
import { MatDialog } from '@angular/material/dialog';
import { DeviceStorageService, FirmwareProgress } from '../../services/device-storage.service';
import { Router, RouterModule } from '@angular/router';

@Component({
  standalone: true,
  selector: 'app-dashboard',
  templateUrl: './dashboard.html',
  styleUrls: ['./dashboard.css'],
  imports: [CommonModule, FormsModule, FilterByMacPipe, TabsComponent, ConfirmationModalComponent,RouterModule ]
})
export class DashboardComponent implements OnInit {
  upgradeType = 'Sensor';
  fwVersions: string[] = [];
  sensorFwVersions: string[] = [];
  blFwVersions: string[] = [];
  selectedFwVersion: string = '';

  devices: any[] = [];
  filteredDevices: any[] = [];

  selectedCount = 0;
  searchTerm: string = '';
  showConfirmation = false;
  pendingDevices: any[] = [];
  isLoading = true;

  firmwareMap: { [key: string]: string[] } = {};
  selectedFirmwareByType: { [key: string]: string } = {};
  expandedGroups: { [key: string]: boolean } = {};
  selectedTypeFlags: { [key: string]: boolean } = {};
  detectorTypesInUse: string[] = [];
  progressMap: Record<string, FirmwareProgress> = {};

  @ViewChild('firmwareDropdown', { static: false }) firmwareDropdownRef!: ElementRef;
  showFirmwarePanel = false;

  constructor(
     private scannerService: ScannerService,
     private firmwareService: FirmwareService,
     private dialog: MatDialog,
     private deviceStorageService: DeviceStorageService,
     private router: Router
  ) {}

  ngOnInit(): void {
    this.loadNearbyDevices(); // initial

    setInterval(() => {
      this.loadNearbyDevices(); // only adds new devices
    }, 15000); // every 15 seconds
    
    // Load firmware manifest and initialize firmware map
    this.loadFirmwareData();

     this.loadProgress();
     setInterval(() => this.loadProgress(), 3000);
  }

  private loadFirmwareData(): void {
    console.log('Dashboard: Loading firmware data...');
    this.firmwareService.loadFirmwareManifest().subscribe({
      next: (manifest) => {
        console.log('Dashboard: Firmware manifest received:', manifest);
        this.firmwareMap = manifest;
        this.detectorTypesInUse = Object.keys(manifest);
        console.log('Dashboard: Available detector types:', this.detectorTypesInUse);
      },
      error: (error) => {
        console.error('Dashboard: Failed to load firmware manifest:', error);
        // Fallback to current map
        this.firmwareMap = this.firmwareService.getFirmwareMap();
        this.detectorTypesInUse = Object.keys(this.firmwareMap);
      }
    });
  }

loadNearbyDevices() {
  this.isLoading = true;

  this.scannerService.fetchNearbyDevices().subscribe({
    next: (data: ScannedDevice[]) => {
      const existingMacs = new Set(this.devices.map(d => d.mac));
      const newDevices: any[] = [];

      data.forEach((d, i) => {
        const mac = d.bdaddrs[0]?.bdaddr ?? 'N/A';
        if (existingMacs.has(mac)) return; // Skip known devices

        newDevices.push({
          mac,
          version: d.detectorShortDescription
            ? (d.detectorShortDescription.includes('-')
                ? d.detectorShortDescription.split('-')[0]
                : d.detectorShortDescription)
            : 'Unknown',
          Name: d.name,
          productNumber: d.productNumber,
          sensorVersion: '', // will enrich below
          rssi: d.rssi,
          ctStatus: 'Idle',
          selected: false,
          isLocked: d.isLocked,
          connectionStatus: 'Disconnected',
          isConnected: false,
          isConnecting: false,
          isDisconnecting: false
        });
      });

      // Add only the new devices to the array
      this.devices.push(...newDevices);
      this.filteredDevices = [...this.devices];

      // Update detector types
      newDevices.forEach(d => {
        if (!this.detectorTypesInUse.includes(d.version)) {
          this.detectorTypesInUse.push(d.version);
          this.selectedTypeFlags[d.version] = true;
          this.expandedGroups[d.version] = true;
        }
      });

      this.filterDevicesBySelectedTypes();
      this.isLoading = false;
    },
    error: (err) => {
      console.error("Failed to load devices", err);
      this.isLoading = false;
    }
  });
}


refreshFirmwareVersion(device: any, event: Event): void {
  event.preventDefault(); // prevent href navigation

  device.isFirmwareLoading = true;

  this.scannerService.getFirmwareVersionsByMac([device.mac]).subscribe({
    next: (fwMap: { [mac: string]: string }) => {
      device.sensorVersion = fwMap[device.mac] ?? '';
    },
    error: (err) => {
      console.error(`Failed to fetch firmware for ${device.mac}:`, err);
      this.showToast(`Failed to fetch firmware for ${device.mac}`);
    },
    complete: () => {
      device.isFirmwareLoading = false;
    }
  });
}

fetchFirmwareForSelectedDevices(): void {
  const selectedDevices = this.filteredDevices.filter(d => d.selected);

  if (selectedDevices.length === 0) {
    this.showToast('Please select at least one device.');
    return;
  }

  selectedDevices.forEach(device => {
    device.isFirmwareLoading = true;

    this.scannerService.getFirmwareVersionsByMac([device.mac]).subscribe({
      next: (fwMap: { [mac: string]: string }) => {
        device.sensorVersion = fwMap[device.mac] ?? '';
        console.log("API response for MAC:", device.mac, fwMap); // DEBUG

        const version = fwMap[device.mac];
        console.log(`Extracted firmware version for ${device.mac}:`, version); 
      },
      error: (err) => {
        console.error(`Failed to fetch firmware for ${device.mac}:`, err);
        this.showToast(`Error fetching firmware for ${device.mac}`);
      },
      complete: () => {
        device.isFirmwareLoading = false;
      }
    });
  });
}


extractAppVersion(versionString: string): string {
  if (!versionString || typeof versionString !== 'string') return '';

  try {
    const sensorMatch = versionString.match(/Sensor:\s*App:\s*([A-Za-z0-9.]+)/i);
    const actorMatch = versionString.match(/Actor:\s*App:\s*([A-Za-z0-9.]+)/i);

    const sensorApp = sensorMatch?.[1] ?? null;
    const actorApp = actorMatch?.[1] ?? null;

    if (sensorApp && actorApp) {
      return `Sensor: ${sensorApp}, Actor: ${actorApp}`;
    } else if (sensorApp) {
      return `Sensor: ${sensorApp}`;
    } else if (actorApp) {
      return `Actor: ${actorApp}`;
    } else {
      return '';
    }
  } catch {
    return '';
  }
}



  // loadFirmwareVersions() {
  //   this.fwVersions = this.firmwareService.getFirmwareVersions('actor');
  //   this.sensorFwVersions = this.firmwareService.getFirmwareVersions('sensor');
  //   this.blFwVersions = this.firmwareService.getFirmwareVersions('bootloader');
  // }

  filterDevicesBySelectedTypes(): void {
  this.filterDevicesByAllCriteria();
}

  toggleFirmwarePanel(): void {
    this.showFirmwarePanel = !this.showFirmwarePanel;
  }

   @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const clickedInside = this.firmwareDropdownRef?.nativeElement.contains(event.target);
    if (!clickedInside) {
      this.showFirmwarePanel = false;
    }
  }

  toggleExpanded(type: string): void {
    this.expandedGroups[type] = !this.expandedGroups[type];
  }

 onTypeToggle(): void {
  this.filterDevicesBySelectedTypes();

  const selectedTypes = Object.keys(this.selectedTypeFlags).filter(type => this.selectedTypeFlags[type]);

  // Deselect devices whose type is now hidden
  this.devices.forEach(d => {
    if (!selectedTypes.includes(d.version)) {
      d.selected = false;
    }
  });

  // Ensure child firmware is selected when type is checked
  selectedTypes.forEach(type => {
    const firmwareSelected = this.selectedFirmwareByType[type];
    const availableFirmwares = this.firmwareMap[type];

    if (!firmwareSelected && availableFirmwares?.length) {
      // Auto-select the first available firmware version
      this.selectedFirmwareByType[type] = availableFirmwares[0];
    }
  });

  // Recalculate selected count
  this.selectedCount = this.devices.filter(d => d.selected).length;
}



  onDeviceClick(device: any, event: Event): void {
    const input = event.target as HTMLInputElement;
    const checked = input.checked;
    const currentSelectedCount = this.devices.filter(d => d.selected).length;

    if (checked && currentSelectedCount >= 100) {
      event.preventDefault();
      input.checked = false;
      this.showToast('⚠️ You can select a maximum of 100 devices at a time.');
      return;
    }

    device.selected = checked;
    this.selectedCount = this.devices.filter(d => d.selected).length;
  }

  toggleAllSelection(checked: boolean): void {
  if (checked) {
    let count = 0;
    for (let d of this.filteredDevices) {
      if (count < 100) {
        d.selected = true;
        count++;
      } else {
        d.selected = false;
      }
    }
    this.selectedCount = count;

    // ✅ Only show toast if more than 10 filtered devices exist
    if (this.filteredDevices.length > 100) {
      this.showToast(`Only the first 100 devices were selected.`);
    }
  } else {
    this.filteredDevices.forEach(d => d.selected = false);
    this.selectedCount = 0;
  }
}

  onMasterCheckboxChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.toggleAllSelection(input.checked);
  }

  areAllSelected(): boolean {
    const selected = this.filteredDevices.filter(d => d.selected).length;
    return selected > 0 && selected <= 100 && selected === this.filteredDevices.length;
  }

  showToast(message: string) {
    const toast = document.createElement('div');
    toast.innerHTML = `<div class="toast-glass">${message}</div>`;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 3000);
  }

  confirmUpgrade(): void {
    this.showConfirmation = false;
    if (!this.pendingDevices || this.pendingDevices.length === 0) return;

    
  this.pendingDevices.forEach(device => {
    // Inject a queued progress state directly
    this.progressMap[device.mac] = {
        macAddress: device.mac,
        progress: 0,
        status: 'Queued',
        lastUpdated: new Date().toISOString(),
        showTick: false
      };

  });

   

    switch (this.upgradeType) {
      case 'Sensor':
        this.firmwareService.bulkSensorUpgrade(this.pendingDevices, this.selectedFwVersion)
          .subscribe({
            next: (res: any) => {
              console.log('Sensor upgrade response:', res);
              this.pendingDevices.forEach(d => d.ctStatus = 'done');
            },
            error: (err: any) => {
              console.error('Sensor upgrade failed:', err);
              this.pendingDevices.forEach(d => d.ctStatus = 'error');
            }
          });
        break;

      default:
        alert("Unsupported upgrade type selected.");
        break;
    }

    this.pendingDevices = [];
  }

  cancelUpgrade(): void {
    this.showConfirmation = false;
    this.pendingDevices = [];
  }

upgrade(): void {
  const selectedDevices = this.filteredDevices.filter((device: any) => device.selected);

  if (selectedDevices.length === 0) {
    alert("No devices selected.");
    return;
  }

  const unknownDevices = selectedDevices.filter((d: any) => d.version === 'Unknown');
  const lockedDevices = selectedDevices.filter((d: any) => d.isLocked);

  if (unknownDevices.length > 0 || lockedDevices.length > 0) {
    this.dialog.open(UpgradePrepDialogComponent, {
      width: '700px',
      data: { unknownDevices, lockedDevices }
    }).afterClosed().subscribe(result => {
      if (result?.confirmed) {
        // Step 1: attach selectedFirmware to the updated subset
        const updatedSubset = result.updatedDevices.map((d: any) => ({
          ...d,
          selectedFirmware: this.selectedFirmwareByType[d.version] ?? ''
        }));

        // Step 2: merge updated subset into the original full selection
        const finalDevices = selectedDevices.map((original: any) => {
          const updated = updatedSubset.find((u: any) => u.mac === original.mac);
          return updated
            ? { ...original, ...updated }
            : {
                ...original,
                selectedFirmware: this.selectedFirmwareByType[original.version] ?? ''
              };
        });

        // Step 3: check for any missing firmware versions
        const missingTypes = [...new Set(finalDevices.map((d: any) => d.version))]
          .filter(type => !this.selectedFirmwareByType[type]);

        if (missingTypes.length > 0) {
          alert(`Please select firmware Versions for: ${missingTypes.join(', ')}`);
          return;
        }

        // Ready for confirmation
        this.pendingDevices = finalDevices;
        this.showConfirmation = true;
      }
    });

    return;
  }

  // No locked/unknown devices, proceed directly
  const requiredTypes = [...new Set(selectedDevices.map((d: any) => d.version))] as string[];
  const missingTypes = requiredTypes.filter(type => !this.selectedFirmwareByType[type]);

  if (missingTypes.length > 0) {
    alert(`Please select firmware Versions for: ${missingTypes.join(', ')}`);
    return;
  }

  this.pendingDevices = selectedDevices.map((d: any) => ({
    ...d,
    selectedFirmware: this.selectedFirmwareByType[d.version]
  }));

  this.showConfirmation = true;
}




  filterDevicesByAllCriteria(): void {
  const selectedTypes = Object.keys(this.selectedTypeFlags).filter(type => this.selectedTypeFlags[type]);
  this.filteredDevices = this.devices.filter(d => {
    const matchesType = selectedTypes.includes(d.version);
    const matchesMac = this.searchTerm ? d.mac.toLowerCase().includes(this.searchTerm.toLowerCase()) : true;
    return matchesType && matchesMac;
  });
}

loadProgress(): void {
  this.deviceStorageService.getUpgradeProgress().subscribe({
    next: (data: FirmwareProgress[]) => {
      const newMap: Record<string, FirmwareProgress> = {};

      // Step 1: Add all updated entries from the backend
      data.forEach(item => {
        newMap[item.macAddress] = item;
      });

      // Step 2: Retain existing entries that were manually set but not yet updated by backend
      for (const mac in this.progressMap) {
        if (!newMap[mac]) {
          newMap[mac] = this.progressMap[mac];  // retain existing
        }
      }

      this.progressMap = newMap;
    },
    error: (err) => {
      console.error('Failed to load upgrade progress:', err);
    }
  });
}



viewLogs(mac: string): void {
  this.router.navigate(['/logs-dashboard']
  );
}

connectToDevice(device: any): void {
  device.isConnecting = true;
  device.connectionStatus = 'Connecting...';

  this.firmwareService.connectToDevices([device.mac]).subscribe({
    next: (response) => {
      console.log('Connect response:', response);
      if (response && response.length > 0) {
        const result = response[0];
        if (result.status === 200 && result.data === 'OK') {
          device.connectionStatus = 'Connected';
          device.isConnected = true;
          this.showToast(`Successfully connected to device ${device.mac}`);
        } else {
          device.connectionStatus = 'Connection Failed';
          device.isConnected = false;
          this.showToast(`Failed to connect to device ${device.mac}: ${result.data || 'Connection failed'}`);
        }
      } else {
        device.connectionStatus = 'Connection Failed';
        device.isConnected = false;
        this.showToast(`Failed to connect to device ${device.mac}`);
      }
    },
    error: (err) => {
      console.error('Connect error:', err);
      device.connectionStatus = 'Connection Error';
      device.isConnected = false;
      this.showToast(`Error connecting to device ${device.mac}: ${err.message || 'Network error'}`);
    },
    complete: () => {
      device.isConnecting = false;
    }
  });
}

disconnectFromDevice(device: any): void {
  device.isDisconnecting = true;
  device.connectionStatus = 'Disconnecting...';

  this.firmwareService.disconnectFromDevices([device.mac]).subscribe({
    next: (response) => {
      console.log('Disconnect response:', response);
      if (response && response.length > 0) {
        const result = response[0];
        if (result.status === 200 && result.data === 'OK') {
          device.connectionStatus = 'Disconnected';
          device.isConnected = false;
          this.showToast(`Successfully disconnected from device ${device.mac}`);
        } else {
          device.connectionStatus = 'Disconnect Failed';
          this.showToast(`Failed to disconnect from device ${device.mac}: ${result.data || 'Disconnection failed'}`);
        }
      } else {
        device.connectionStatus = 'Disconnect Failed';
        this.showToast(`Failed to disconnect from device ${device.mac}`);
      }
    },
    error: (err) => {
      console.error('Disconnect error:', err);
      device.connectionStatus = 'Disconnect Error';
      this.showToast(`Error disconnecting from device ${device.mac}: ${err.message || 'Network error'}`);
    },
    complete: () => {
      device.isDisconnecting = false;
    }
  });
}

connectSelectedDevices(): void {
  const selectedDevices = this.filteredDevices.filter(d => d.selected);
  
  if (selectedDevices.length === 0) {
    this.showToast('Please select at least one device to connect.');
    return;
  }

  const macAddresses = selectedDevices.map(d => d.mac);
  
  selectedDevices.forEach(device => {
    device.isConnecting = true;
    device.connectionStatus = 'Connecting...';
  });

  this.firmwareService.connectToDevices(macAddresses).subscribe({
    next: (response) => {
      console.log('Bulk connect response:', response);
      if (response && Array.isArray(response)) {
        response.forEach((result) => {
          const device = selectedDevices.find(d => d.mac === result.macAddress);
          if (device) {
            if (result.status === 200 && result.data === 'OK') {
              device.connectionStatus = 'Connected';
              device.isConnected = true;
            } else {
              device.connectionStatus = 'Connection Failed';
              device.isConnected = false;
            }
            device.isConnecting = false;
          }
        });
        const successCount = response.filter(r => r.status === 200 && r.data === 'OK').length;
        this.showToast(`Connection completed: ${successCount}/${selectedDevices.length} devices connected successfully`);
      }
    },
    error: (err) => {
      console.error('Bulk connect error:', err);
      selectedDevices.forEach(device => {
        device.connectionStatus = 'Connection Error';
        device.isConnected = false;
        device.isConnecting = false;
      });
      this.showToast(`Error connecting to devices: ${err.message || 'Network error'}`);
    }
  });
}

disconnectSelectedDevices(): void {
  const selectedDevices = this.filteredDevices.filter(d => d.selected);
  
  if (selectedDevices.length === 0) {
    this.showToast('Please select at least one device to disconnect.');
    return;
  }

  const macAddresses = selectedDevices.map(d => d.mac);
  
  selectedDevices.forEach(device => {
    device.isDisconnecting = true;
    device.connectionStatus = 'Disconnecting...';
  });

  this.firmwareService.disconnectFromDevices(macAddresses).subscribe({
    next: (response) => {
      console.log('Bulk disconnect response:', response);
      if (response && Array.isArray(response)) {
        response.forEach((result) => {
          const device = selectedDevices.find(d => d.mac === result.macAddress);
          if (device) {
            if (result.status === 200 && result.data === 'OK') {
              device.connectionStatus = 'Disconnected';
              device.isConnected = false;
            } else {
              device.connectionStatus = 'Disconnect Failed';
            }
            device.isDisconnecting = false;
          }
        });
        const successCount = response.filter(r => r.status === 200 && r.data === 'OK').length;
        this.showToast(`Disconnection completed: ${successCount}/${selectedDevices.length} devices disconnected successfully`);
      }
    },
    error: (err) => {
      console.error('Bulk disconnect error:', err);
      selectedDevices.forEach(device => {
        device.connectionStatus = 'Disconnect Error';
        device.isDisconnecting = false;
      });
      this.showToast(`Error disconnecting from devices: ${err.message || 'Network error'}`);
    }
  });
}

  // onUpgradeTypeChange(): void {
  //   const normalized = this.upgradeType.toLowerCase().replace(' - sensor', '').toLowerCase();

  //   if (normalized === 'sensor') {
  //     this.fwVersions = this.firmwareService.getFirmwareVersions('sensor');
  //   } else if (normalized === 'actor') {
  //     this.fwVersions = this.firmwareService.getFirmwareVersions('actor');
  //   } else if (normalized === 'bootloader') {
  //     this.fwVersions = this.firmwareService.getFirmwareVersions('bootloader');
  //   }

  //   this.selectedFwVersion = this.fwVersions.length > 0 ? this.fwVersions[0] : '';
  // }
}
