import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { FirmwareService } from '../../services/firmware.service';
import { ApiService } from '../../services/api.service';
import { RouterModule } from '@angular/router';
import { TabsComponent } from '../../components/tabs/tabs.component';

@Component({
  standalone: true,
  selector: 'app-firmware-upload',
  templateUrl: './firmware-upload.html',
  styleUrls: ['./firmware-upload.css'],
  imports: [CommonModule, FormsModule, RouterModule, TabsComponent]
})
export class FirmwareUploadComponent {
  selectedFile: File | null = null;
  isUploading = false;
  uploadResult: any = null;
  dragOver = false;
  
  // Mapping modal properties
  showMappingModal = false;
  availableDetectorTypes: string[] = [];
  selectedDetectorTypes: string[] = [];
  isMapping = false;
  uploadedVersion = '';
  mappingResult: any = null;

  constructor(
    private firmwareService: FirmwareService,
    private http: HttpClient,
    private apiService: ApiService
  ) {
    this.loadDetectorTypes();
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.dragOver = true;
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.dragOver = false;
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.dragOver = false;
    
    const files = event.dataTransfer?.files;
    if (files && files.length > 0) {
      this.handleFileSelection(files[0]);
    }
  }

  onFileSelected(event: any): void {
    const file = event.target.files[0];
    if (file) {
      this.handleFileSelection(file);
    }
  }

  private handleFileSelection(file: File): void {
    // Validate file type
    if (!file.name.toLowerCase().endsWith('.zip')) {
      this.showError('Please select a ZIP file.');
      return;
    }

    // Validate file size (e.g., max 50MB)
    if (file.size > 50 * 1024 * 1024) {
      this.showError('File size must be less than 50MB.');
      return;
    }

    this.selectedFile = file;
    this.uploadResult = null;
  }

  uploadFirmware(): void {
    if (!this.selectedFile) {
      this.showError('Please select a file first.');
      return;
    }

    this.isUploading = true;
    this.uploadResult = null;

    this.firmwareService.uploadFirmware(this.selectedFile).subscribe({
      next: (response) => {
        console.log('Upload success:', response);
        this.uploadResult = {
          success: true,
          data: response
        };
        this.selectedFile = null;
        this.uploadedVersion = response.version || 'Unknown';
        
        // Reset file input
        const fileInput = document.getElementById('fileInput') as HTMLInputElement;
        if (fileInput) fileInput.value = '';
        
        // Show mapping modal after successful upload
        this.showMappingModal = true;
      },
      error: (error) => {
        console.error('Upload error:', error);
        this.uploadResult = {
          success: false,
          message: error.error?.message || error.message || 'Upload failed'
        };
      },
      complete: () => {
        this.isUploading = false;
      }
    });
  }

  private showError(message: string): void {
    this.uploadResult = {
      success: false,
      message: message
    };
  }

  clearSelection(): void {
    this.selectedFile = null;
    this.uploadResult = null;
    const fileInput = document.getElementById('fileInput') as HTMLInputElement;
    if (fileInput) fileInput.value = '';
  }

  formatFileSize(bytes: number): string {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }

  triggerFileInput(): void {
    const fileInput = document.getElementById('fileInput') as HTMLInputElement;
    if (fileInput) {
      fileInput.click();
    }
  }

  private loadDetectorTypes(): void {
    // Get detector types from the firmware manifest
    const manifest = this.firmwareService.getFirmwareMap();
    if (Object.keys(manifest).length > 0) {
      this.availableDetectorTypes = Object.keys(manifest);
    } else {
      // If manifest is empty, load it first
      (this.firmwareService as any).loadFirmwareManifest().subscribe({
        next: (loadedManifest: any) => {
          this.availableDetectorTypes = Object.keys(loadedManifest);
        },
        error: (error: any) => {
          console.error('Failed to load detector types:', error);
          // Fallback to hardcoded types if API fails
          //this.availableDetectorTypes = ['P48', 'P47', 'P46', 'V230'];
        }
      });
    }
  }

  onDetectorTypeChange(detectorType: string, checked: boolean): void {
    if (checked) {
      if (!this.selectedDetectorTypes.includes(detectorType)) {
        this.selectedDetectorTypes.push(detectorType);
      }
    } else {
      const index = this.selectedDetectorTypes.indexOf(detectorType);
      if (index > -1) {
        this.selectedDetectorTypes.splice(index, 1);
      }
    }
  }

  isDetectorTypeSelected(detectorType: string): boolean {
    return this.selectedDetectorTypes.includes(detectorType);
  }

  mapFirmware(): void {
    if (this.selectedDetectorTypes.length === 0) {
      this.mappingResult = {
        success: false,
        message: 'Please select at least one detector type'
      };
      return;
    }

    this.isMapping = true;
    this.mappingResult = null;

    // Call the real API to add firmware to manifest
    this.apiService.addFirmwareToManifest(this.uploadedVersion, this.selectedDetectorTypes).subscribe({
      next: (response) => {
        if (response.success) {
          this.mappingResult = {
            success: true,
            message: response.message || `Successfully mapped ${response.firmwareVersion} to ${response.updatedDetectorTypes?.join(', ') || this.selectedDetectorTypes.join(', ')}`
          };
          
          // Refresh the firmware manifest to get updated data
          (this.firmwareService as any).loadFirmwareManifest().subscribe({
            next: () => {
              console.log('Firmware manifest refreshed after adding new version');
            },
            error: (refreshError: any) => {
              console.warn('Failed to refresh manifest after mapping:', refreshError);
            }
          });
          
          // Auto-close modal after 2 seconds on success
          setTimeout(() => {
            this.closeMappingModal();
          }, 2000);
        } else {
          this.mappingResult = {
            success: false,
            message: response.message || 'Failed to add firmware to manifest'
          };
        }
      },
      error: (error) => {
        this.mappingResult = {
          success: false,
          message: error.error?.message || 'Failed to add firmware to manifest: ' + error.message
        };
      },
      complete: () => {
        this.isMapping = false;
      }
    });
  }

  closeMappingModal(): void {
    this.showMappingModal = false;
    this.selectedDetectorTypes = [];
    this.mappingResult = null;
    this.uploadedVersion = '';
  }

  // Prevent closing modal without mapping
  canCloseModal(): boolean {
    return this.mappingResult?.success === true;
  }
}