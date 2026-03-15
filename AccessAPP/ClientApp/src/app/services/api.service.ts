import { Injectable } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface MqttConfig {
  name: string;
  networkId: string;
  host: string;
  port: number;
  useTls: boolean;
  username: string;
  password: string;
  baseTopic: string;
  keepAliveSeconds: number;
  reconnectDelaySeconds: number;
  subscribeToAllTarget: boolean;
}

export interface LedRangeDeviceRow {
  mac: string;
  model: string;
  rssi: number;
  chip: number;
  color: string;
  status: string;
  error: string;
}

export interface LedRangeStateSnapshot {
  statusText: string;
  progressText: string;
  requestedTotal: number;
  triedCount: number;
  connectedCount: number;
  failedCount: number;
  progressPercent: number;
  requestId: string;
  stage: string;
  minRssi: number;
  lastUpdatedUtc: string;
  connectedDevices: LedRangeDeviceRow[];
  failedDevices: LedRangeDeviceRow[];
}

export interface LedRangeStartRequest {
  minRssi: number;
  model: string;
  maxConnectAttempts: number;
  pincode: string;
  useBothChips: boolean;
}

@Injectable({
  providedIn: 'root'
})
export class ApiService {
  private readonly baseUrl = environment.apiBaseUrl;

  constructor(private http: HttpClient) {}

  // Firmware manifest endpoints
  getFirmwareManifest(): Observable<any> {
    return this.http.get(`${this.baseUrl}/Cassia/firmware/manifest`);
  }

  addFirmwareToManifest(firmwareVersion: string, supportedDetectorTypes: string[]): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/firmware/manifest/add`, {
      firmwareVersion,
      supportedDetectorTypes
    });
  }

  // Firmware upload
  uploadFirmware(formData: FormData): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/firmware/upload`, formData);
  }

  // Device operations
  fetchNearbyDevices(): Observable<any> {
    return this.http.get(`${this.baseUrl}/Cassia/devices`);
  }

  getFirmwareVersionsByMac(macAddresses: string[]): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/deviceversions`, macAddresses);
  }

  bulkDeviceUpgrade(devices: any[]): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/BulkDeviceUpgrade`, devices);
  }

  connectToDevices(macAddresses: string[]): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/connect`, macAddresses);
  }

  disconnectFromDevices(macAddresses: string[]): Observable<any> {
    return this.http.delete(`${this.baseUrl}/Cassia/disconnect`, { body: macAddresses });
  }

  // Progress and logs
  getProgress(): Observable<any> {
    return this.http.get(`${this.baseUrl}/Cassia/upgrade/progress`);
  }

  removeFromQueue(mac: string): Observable<any> {
    return this.http.delete(`${this.baseUrl}/Cassia/upgrade/queue/${encodeURIComponent(mac)}`);
  }

  clearQueue(): Observable<any> {
    return this.http.delete(`${this.baseUrl}/Cassia/upgrade/queue`);
  }

  getLogs(): Observable<any> {
    return this.http.get(`${this.baseUrl}/Cassia/logs`, { responseType: 'text' });
  }

  identifyDevice(macAddress: string, pincode?: string, secondsToStayConnected = 15): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/identify`, { macAddress, pincode, secondsToStayConnected });
  }

  getRuntimeVariables(): Observable<any> {
    return this.http.get(`${this.baseUrl}/Cassia/runtime`);
  }

  setRuntimeVariables(values: Record<string, any>): Observable<any> {
    return this.http.put(`${this.baseUrl}/Cassia/runtime`, values);
  }

  // MQTT config (mqtt.json)
  getMqttConfig(): Observable<MqttConfig> {
    return this.http.get<MqttConfig>(`${this.baseUrl}/Cassia/mqtt/config`);
  }

  saveMqttConfig(config: MqttConfig): Observable<any> {
    return this.http.put(`${this.baseUrl}/Cassia/mqtt/config`, config);
  }

  // LED range visualizer (local)
  getLedRangeState(): Observable<LedRangeStateSnapshot> {
    return this.http.get<LedRangeStateSnapshot>(`${this.baseUrl}/Cassia/led-range/state`);
  }

  startLedRange(request: LedRangeStartRequest): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/led-range/start`, request);
  }

  retryFailedLedRange(request: LedRangeStartRequest): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/led-range/retry-failed`, request);
  }

  disconnectLedRange(forceAll: boolean): Observable<any> {
    return this.http.post(`${this.baseUrl}/Cassia/led-range/disconnect`, { forceAll });
  }

  // Legacy endpoint for firmware mapping
  mapFirmwareToDetectors(firmwareVersion: string, detectorTypes: string[]): Observable<any> {
    return this.http.post(`${this.baseUrl}/firmware/map`, {
      firmwareVersion,
      detectorTypes
    });
  }

  // Generic method for custom endpoints
  get<T>(endpoint: string): Observable<T> {
    return this.http.get<T>(`${this.baseUrl}${endpoint}`);
  }

  post<T>(endpoint: string, data: any): Observable<T> {
    return this.http.post<T>(`${this.baseUrl}${endpoint}`, data);
  }

  put<T>(endpoint: string, data: any): Observable<T> {
    return this.http.put<T>(`${this.baseUrl}${endpoint}`, data);
  }

  delete<T>(endpoint: string, options?: { body?: any, headers?: HttpHeaders | { [header: string]: string | string[] }, params?: any }): Observable<T> {
    return this.http.delete<T>(`${this.baseUrl}${endpoint}`, options) as Observable<T>;
  }
}
