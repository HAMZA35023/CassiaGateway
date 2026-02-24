import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, of } from 'rxjs';
import { map } from 'rxjs/operators';
import { ApiService } from './api.service';

@Injectable({ providedIn: 'root' })
export class FirmwareService {
  private firmwareManifest: { [key: string]: string[] } = {};
  private manifestLoaded = false;

  constructor(private http: HttpClient, private apiService: ApiService) {
    console.log('FirmwareService: Constructor called, loading manifest...');
    this.loadFirmwareManifest().subscribe({
      next: (manifest) => {
        console.log('FirmwareService: Firmware manifest loaded in constructor:', manifest);
      },
      error: (error) => {
        console.error('FirmwareService: Failed to load firmware manifest in constructor:', error);
      }
    });
  }

  getFirmwareMap(): { [key: string]: string[] } {
    console.log('FirmwareService: getFirmwareMap called, returning:', this.firmwareManifest);
    return this.firmwareManifest;
  }

  loadFirmwareManifest(): Observable<{ [key: string]: string[] }> {
    console.log('FirmwareService: Making API call to load manifest...');
    return this.apiService.getFirmwareManifest().pipe(
      map(response => {
        console.log('FirmwareService: API response received:', response);
        if (response.success && response.manifest) {
          this.firmwareManifest = response.manifest;
          this.manifestLoaded = true;
          console.log('FirmwareService: Manifest stored:', this.firmwareManifest);
          return response.manifest;
        } else {
          console.error('FirmwareService: Invalid API response:', response);
          throw new Error('Failed to load firmware manifest');
        }
      })
    );
  }

  isManifestLoaded(): boolean {
    return this.manifestLoaded;
  }

  getAvailableDetectorTypes(): Observable<string[]> {
    if (this.manifestLoaded) {
      return of(Object.keys(this.firmwareManifest));
    } else {
      return this.loadFirmwareManifest().pipe(
        map(manifest => Object.keys(manifest))
      );
    }
  }

  mapFirmwareToDetectors(firmwareVersion: string, detectorTypes: string[]): Observable<any> {
    return this.apiService.mapFirmwareToDetectors(firmwareVersion, detectorTypes);
  }

  bulkSensorUpgrade(devices: any[], selectedFwVersion: string): Observable<any> {
    const payload = devices.map(d => ({
      MacAddress: d.mac,
      Pincode: d.pin ?? '',
      bActor: false,
      DetctorType: d.version,
      FirmwareVersion: d.selectedFirmware,
      CurrentFirmwareVersion: d.sensorVersion
    }));

    return this.apiService.bulkDeviceUpgrade(payload);
  }

  connectToDevices(macAddresses: string[]): Observable<any> {
    return this.apiService.connectToDevices(macAddresses);
  }

  disconnectFromDevices(macAddresses: string[]): Observable<any> {
    return this.apiService.disconnectFromDevices(macAddresses);
  }

  uploadFirmware(file: File): Observable<any> {
    const formData = new FormData();
    formData.append('zipFile', file);
    
    return this.apiService.uploadFirmware(formData);
  }
}
