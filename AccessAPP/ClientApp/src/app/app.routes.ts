import { Routes } from '@angular/router';
import { WelcomeComponent } from './pages/welcome/welcome';
import { DashboardComponent } from './pages/dashboard/dashboard';

export const routes: Routes = [
  { path: '', component: WelcomeComponent },
  { path: 'dashboard', component: DashboardComponent },
  {
    path: 'logs-dashboard',
    loadComponent: () =>
      import('./pages/logs-dashboard/logs-dashboard').then(m => m.LogsDashboardComponent)
  },
  {
    path: 'firmware-upload',
    loadComponent: () =>
      import('./pages/firmware-upload/firmware-upload').then(m => m.FirmwareUploadComponent)
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./pages/settings/settings').then(m => m.SettingsComponent)
  }
];
