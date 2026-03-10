import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiService } from './api.service';

// Define the interface outside the class
export interface FirmwareProgress {
  macAddress: string;
  progress: number;
  status: string;
  lastUpdated: string;
  showTick: boolean;
  speedPctPerMin?: number | null;
  targetFirmwareVersion?: string | null;
  detectorType?: string | null;
  /** Set when status is "Device Upgrade Completed." — values: "Success", "Failed", "Warn" */
  finalResult?: string | null;
}

@Injectable({
  providedIn: 'root'
})
export class DeviceStorageService {
  constructor(private http: HttpClient, private apiService: ApiService) {}

  getUpgradeProgress(): Observable<FirmwareProgress[]> {
    return this.apiService.getProgress();
  }
}
