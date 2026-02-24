import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiService } from './api.service';

export interface ScannedDevice {
  bdaddrs: { bdaddr: string; bdaddrType: string }[];
  chipId: number;
  evtType: number;
  rssi: number;
  adData: string | null;
  scanData: string;
  name: string;
  productNumber: string;
  detectorFamily: string;
  detectorType: string;
  detectorOutputInfo: string;
  detectorDescription: string;
  detectorShortDescription: string;
  range: number;
  detectorMountDescription: string;
  lockedHex: string;
  isLocked: boolean;
}


@Injectable({ providedIn: 'root' })
export class ScannerService {

  constructor(private http: HttpClient, private apiService: ApiService) {}

  fetchNearbyDevices(): Observable<ScannedDevice[]> {
    return this.apiService.fetchNearbyDevices();
  }

  getFirmwareVersionsByMac(macList: string[]) {
    return this.apiService.getFirmwareVersionsByMac(macList);
  }

}
