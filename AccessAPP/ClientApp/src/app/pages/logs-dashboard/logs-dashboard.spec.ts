import { Component, OnInit } from '@angular/core';
import { HttpClient } from '@angular/common/http';

interface LogStage {
  time: string;
  stage: string;
}

interface GroupedLog {
  id: string;
  mac: string;
  fw: string;
  start: string;
  stages: LogStage[];
  expanded: boolean;
}

@Component({
  selector: 'app-logs-dashboard',
  templateUrl: './logs-dashboard.component.html',
  styleUrls: ['./logs-dashboard.component.css']
})
export class LogsDashboardComponent implements OnInit {
  groupedLogs: GroupedLog[] = [];

  constructor(private http: HttpClient) {}

  ngOnInit(): void {
    this.http.get('/assets/upgrade_logs.txt', { responseType: 'text' }).subscribe(logText => {
      this.groupedLogs = this.parseLogs(logText);
    });
  }

  parseLogs(logText: string): GroupedLog[] {
    const lines = logText.trim().split('\n');
    const map = new Map<string, GroupedLog>();

    lines.forEach(line => {
      const logIdMatch = line.match(/\[logId=(.*?)\]/);
      const stageMatch = line.match(/stage=(.*?)\s/);
      const timeMatch = line.match(/time=([\d\- :]+)/);
      const macMatch = line.match(/mac=([A-F0-9:]+)/i);
      const fwMatch = line.match(/fw=(\S+)/);

      if (!logIdMatch || !stageMatch || !timeMatch || !macMatch) return;

      const logId = logIdMatch[1];
      const stage = stageMatch[1];
      const time = timeMatch[1];
      const mac = macMatch[1];
      const fw = fwMatch ? fwMatch[1] : '-';

      if (!map.has(logId)) {
        map.set(logId, {
          id: logId,
          mac,
          fw,
          start: time,
          stages: [],
          expanded: false
        });
      }

      map.get(logId)!.stages.push({ time, stage });
    });

    return Array.from(map.values());
  }

  toggle(log: GroupedLog): void {
    log.expanded = !log.expanded;
  }
}
