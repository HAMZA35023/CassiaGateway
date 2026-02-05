import { Component, Inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';

@Component({
  selector: 'app-upgrade-prep-dialog',
  standalone: true,
  templateUrl: './upgrade-prep-dialog.html',
  styleUrls: ['./upgrade-prep-dialog.css'],
  imports: [
    CommonModule,
    FormsModule,
    MatFormFieldModule,
    MatSelectModule,
    MatInputModule,
    MatButtonModule
  ]
})
export class UpgradePrepDialogComponent {
  detectorOptions: string[] = ['P46', 'P47', 'P48', 'P42', 'M48'];

  constructor(
    public dialogRef: MatDialogRef<UpgradePrepDialogComponent>,
    @Inject(MAT_DIALOG_DATA)
    public readonly data: {
      unknownDevices?: any[];
      lockedDevices?: any[];
    }
  ) {}

  cancel(): void {
    this.dialogRef.close({ confirmed: false });
  }

  confirm(): void {
    const updatedDevices = [
      ...(this.data.unknownDevices ?? []),
      ...(this.data.lockedDevices ?? []).map(device => ({
        ...device,
        pin: `${device.pin1 ?? ''}${device.pin2 ?? ''}${device.pin3 ?? ''}${device.pin4 ?? ''}`
      }))
    ];

    this.dialogRef.close({
      confirmed: true,
      updatedDevices
    });
  }

  onPinInput(event: any, device: any, currentField: string) {
    const value = event.target.value;

    // Enforce digit-only input
    if (!/^\d$/.test(value)) {
      event.target.value = ''; // clear invalid input
      return;
    }

    // Update value in device model
    device[currentField] = value;

    // Auto-focus next input
    const fieldOrder = ['pin1', 'pin2', 'pin3', 'pin4'];
    const currentIndex = fieldOrder.indexOf(currentField);

    if (currentIndex < fieldOrder.length - 1) {
      const parent = event.target.closest('.pin-input-group');
      const inputs = parent.querySelectorAll('input');
      const nextInput = inputs[currentIndex + 1];
      if (nextInput) {
        nextInput.focus();
      }
    }
  }
}
