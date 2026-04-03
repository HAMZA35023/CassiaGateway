import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class LayoutService {
  private _mobile: boolean = this.detectMobile();

  get isMobile(): boolean { return this._mobile; }

  /** Overrides auto-detection for this session only. Resets on next page load. */
  setLayout(mode: 'mobile' | 'desktop'): void {
    this._mobile = mode === 'mobile';
  }

  private detectMobile(): boolean {
    return /Mobi|Android|iPhone|iPad|iPod/i.test(navigator.userAgent);
  }
}
