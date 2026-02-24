import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TabsComponent } from '../../components/tabs/tabs.component';
import { ApiService, MqttConfig } from '../../services/api.service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, TabsComponent],
  templateUrl: './settings.html',
  styleUrls: ['./settings.css']
})
export class SettingsComponent implements OnInit {
  isLoading = false;
  statusText = '';
  statusKind: 'ok' | 'error' | '' = '';

  config: MqttConfig = {
    name: 'cassia-01',
    networkId: 'dk-lab',
    host: 'prod.statistics.niko-test.nu',
    port: 18883,
    useTls: false,
    username: 'accessapp',
    password: '',
    baseTopic: 'accessapp',
    keepAliveSeconds: 30,
    reconnectDelaySeconds: 10,
    subscribeToAllTarget: true
  };

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.statusText = '';
    this.statusKind = '';

    this.api.getMqttConfig().subscribe({
      next: (cfg) => {
        // backend returns the raw config object
        this.config = { ...this.config, ...cfg };
        this.isLoading = false;
      },
      error: (err) => {
        this.isLoading = false;
        this.statusKind = 'error';
        this.statusText = `Could not load mqtt.json from backend. (${err?.status ?? 'unknown'} ${err?.statusText ?? ''})`;
      }
    });
  }

  save(): void {
    this.isLoading = true;
    this.statusText = '';
    this.statusKind = '';

    // normalize numbers coming from inputs
    this.config.port = Number(this.config.port) || 0;
    this.config.keepAliveSeconds = Number(this.config.keepAliveSeconds) || 0;
    this.config.reconnectDelaySeconds = Number(this.config.reconnectDelaySeconds) || 0;

    this.api.saveMqttConfig(this.config).subscribe({
      next: () => {
        this.isLoading = false;
        this.statusKind = 'ok';
        this.statusText = 'Saved mqtt.json successfully.';
      },
      error: (err) => {
        this.isLoading = false;
        this.statusKind = 'error';
        this.statusText = `Save failed. (${err?.status ?? 'unknown'} ${err?.statusText ?? ''})`;
      }
    });
  }
}
