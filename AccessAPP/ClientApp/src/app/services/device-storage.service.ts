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
  showTick :  boolean;
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
