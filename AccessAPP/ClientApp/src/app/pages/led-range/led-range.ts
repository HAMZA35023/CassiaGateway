import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription, timer, of } from 'rxjs';
import { catchError, switchMap } from 'rxjs/operators';
import { ApiService, LedRangeStateSnapshot, LedRangeStartRequest } from '../../services/api.service';
import { TabsComponent } from '../../components/tabs/tabs.component';

@Component({
  selector: 'app-led-range',
  standalone: true,
  imports: [CommonModule, FormsModule, TabsComponent],
  templateUrl: './led-range.html',
  styleUrls: ['./led-range.css']
})
export class LedRangeComponent implements OnInit, OnDestroy {
  state: LedRangeStateSnapshot | null = null;
  errorText = '';
  isBusy = false;

  modelOptions = ['All', 'MASTER', 'SECONDARY', 'BMS'];

  form: LedRangeStartRequest = {
    minRssi: -75,
    model: 'All',
    maxConnectAttempts: 3,
    pincode: '1234',
    useBothChips: true
  };

  private pollSub?: Subscription;

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    this.pollSub = timer(0, 2000)
      .pipe(
        switchMap(() =>
          this.api.getLedRangeState().pipe(
            catchError(err => {
              this.errorText = `LED range status unavailable. (${err?.status ?? 'unknown'} ${err?.statusText ?? ''})`;
              return of(null);
            })
          )
        )
      )
      .subscribe(state => {
        if (state) {
          this.state = state;
          this.errorText = '';
        }
      });
  }

  ngOnDestroy(): void {
    this.pollSub?.unsubscribe();
  }

  startVisualization(): void {
    this.runAction(() => this.api.startLedRange(this.form));
  }

  retryFailed(): void {
    this.runAction(() => this.api.retryFailedLedRange(this.form));
  }

  disconnectSession(forceAll: boolean): void {
    this.runAction(() => this.api.disconnectLedRange(forceAll));
  }

  private runAction(action: () => any): void {
    if (this.isBusy) return;
    this.isBusy = true;
    this.errorText = '';

    action().subscribe({
      next: () => {
        this.isBusy = false;
      },
      error: (err: any) => {
        this.isBusy = false;
        this.errorText = `Request failed. (${err?.status ?? 'unknown'} ${err?.statusText ?? ''})`;
      }
    });
  }
}
