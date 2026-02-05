import { Component } from '@angular/core';
import { RouterModule } from '@angular/router';

@Component({
  selector: 'app-tabs',
  standalone: true,
  imports: [RouterModule],
  template: `
    <div class="tabs-container">
      <a routerLink="/dashboard" routerLinkActive="active-tab">Detector Dashboard</a>
      <a routerLink="/logs-dashboard" routerLinkActive="active-tab">Upgrade Logs</a>
      <a routerLink="/firmware-upload" routerLinkActive="active-tab">Firmware Upload</a>
      <a routerLink="/settings" routerLinkActive="active-tab">Settings</a>
    </div>
  `,
  styles: [`
    .tabs-container {
      display: flex;
      border-bottom: 2px solid #ccc;
      background-color: #f9f9f9;
      padding: 0.75rem 1.5rem;
      gap: 0.5rem;
      flex-wrap: wrap;
    }

    .tabs-container a {
      padding: 0.6rem 1.2rem;
      font-weight: 500;
      text-decoration: none;
      color: #333;
      background-color: #eee;
      border-radius: 8px 8px 0 0;
      transition: all 0.2s ease;
    }

    .tabs-container a:hover {
      background-color: #ddd;
      color: #222;
    }

    .tabs-container a.active-tab {
      background-color: #dbeafe;
      color: #003c8f;
      font-weight: 600;
      border: 1px solid #c7dffe;
      border-bottom: none;
      box-shadow: inset 0 -1px 0 white;
    }
  `]
})
export class TabsComponent {}
