import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { RouterModule } from '@angular/router';
import { TabsComponent } from '../../components/tabs/tabs.component';
import { ApiService } from '../../services/api.service';
import { MatPaginatorModule } from '@angular/material/paginator';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatNativeDateModule } from '@angular/material/core';

@Component({
  selector: 'app-logs-dashboard',
  standalone: true,
  imports: [
    CommonModule,
    RouterModule,
    TabsComponent,
    MatPaginatorModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatDatepickerModule,
    MatNativeDateModule
  ],
  templateUrl: './logs-dashboard.html',
  styleUrls: ['./logs-dashboard.css']
})
export class LogsDashboardComponent implements OnInit {
  groupedLogs: any[] = [];
  paginatedLogs: any[] = [];
  pageSize = 10;
  pageIndex = 0;
  macFilter: string = '';
  startDate: Date | null = null;
  endDate: Date | null = null;
  filteredLogs: any[] = [];

  constructor(private http: HttpClient, private apiService: ApiService) {}

  ngOnInit(): void {
    this.apiService.getLogs().subscribe({
      next: (text) => {
        this.groupedLogs = this.parseLogs(text);
        this.filteredLogs = [...this.groupedLogs];
        this.updatePaginatedLogs();
      },
      error: (err) => {
        console.error('Failed to fetch logs:', err);
      }
    });
  }

  onPageChange(event: any): void {
    this.pageSize = event.pageSize;
    this.pageIndex = event.pageIndex;
    this.updatePaginatedLogs();
  }

  updatePaginatedLogs(): void {
    const start = this.pageIndex * this.pageSize;
    const end = start + this.pageSize;
    this.paginatedLogs = this.filteredLogs.slice(start, end);
  }

  toggle(log: any): void {
    log.expanded = !log.expanded;
  }

  applyFilters(): void {
    this.filteredLogs = this.groupedLogs.filter(log => {
      const macMatch = this.macFilter
        ? log.mac.toLowerCase().includes(this.macFilter.toLowerCase())
        : true;

      const logTime = new Date(log.start).getTime();
      const startOk = this.startDate ? logTime >= new Date(this.startDate).getTime() : true;
      const endOk = this.endDate ? logTime <= new Date(this.endDate).getTime() : true;

      return macMatch && startOk && endOk;
    });

    this.pageIndex = 0;
    this.updatePaginatedLogs();
  }

  resetFilters(): void {
    this.macFilter = '';
    this.startDate = null;
    this.endDate = null;
    this.filteredLogs = [...this.groupedLogs];
    this.pageIndex = 0;
    this.updatePaginatedLogs();
  }

  
  parseLogs(text: string): any[] {
    const map = new Map<string, any>();
    const lines = text.split('\n');

    for (let line of lines) {
      const logId = line.match(/\[logId=(.*?)\]/)?.[1];
      const stage = line.match(/stage=(.*?)\s(?:time=|mac=|status=)/)?.[1];
      const time = line.match(/time=([\d\- :]+)/)?.[1];
      const mac = line.match(/mac=([A-F0-9:]+)/i)?.[1];
      const fw = line.match(/fw=(\S+)/)?.[1] ?? '-';
      const oldFw = line.match(/oldfw=(\S+)/)?.[1] ?? '-';
      const status = line.match(/status=(.+)$/)?.[1]?.toLowerCase() ?? '';

      if (!logId || !stage || !time || !mac) continue;

      if (!map.has(logId)) {
        map.set(logId, {
          id: logId,
          mac,
          fw,
          oldFw,
          start: time,
          stages: [],
          expanded: false,
          result: 'Failed'
        });


       const log = map.get(logId)!;

        const isCurrentFw = stage.trim().toLowerCase().startsWith('current fw version');

        if (isCurrentFw) {
          // Extract old FW from Sensor section only
          if (!log.oldFw || log.oldFw === '-') {
            const sensorMatch = line.match(/Sensor:\s*([^|]+)/i);
            if (sensorMatch) {
              const appMatch = sensorMatch[1].match(/App:\s*([A-Z]?\d+\.\d+)/i);
              if (appMatch) {
                log.oldFw = appMatch[1]; // e.g. "B2.38"
              }
            }
          }
        }
        


      if (stage.toLowerCase() === 'sensorprogrammingcomplete') {
        log.result = status === 'success' ? 'Success' : 'Failed';
      }
      }

      const log = map.get(logId)!;
      log.stages.push({ time, stage, status });

      if (stage.toLowerCase() === 'sensorprogrammingcomplete') {
        log.result = status === 'success' ? 'Success' : 'Failed';
      }
    }

    for (const log of map.values()) {
      const hasProg = log.stages.some(
        (s: { stage: string }) => s.stage.toLowerCase() === 'sensorprogrammingcomplete'
      );
      if (!hasProg) log.result = 'Failed';
    }

    

    return Array.from(map.values()).sort((a, b) => {
      const timeA = new Date(a.start).getTime();
      const timeB = new Date(b.start).getTime();
      return timeB - timeA; // Latest first
    });
  }
}
