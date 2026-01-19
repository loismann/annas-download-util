import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { LibraryApiService, LibraryUploadResponse, LibrarySupportedFormatsResponse } from '../../services/library-api.service';

export interface UploadResult {
  file: File;
  status: 'pending' | 'uploading' | 'success' | 'error';
  response?: LibraryUploadResponse;
  error?: string;
}

@Component({
  selector: 'app-file-upload-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule
  ],
  templateUrl: './file-upload-dialog.component.html',
  styleUrls: ['./file-upload-dialog.component.scss']
})
export class FileUploadDialogComponent implements OnInit {
  supportedFormats: string[] = [];
  maxFileSizeMb = 500;
  uploadQueue: UploadResult[] = [];
  isUploading = false;
  isDragOver = false;

  constructor(
    public dialogRef: MatDialogRef<FileUploadDialogComponent>,
    private libraryApi: LibraryApiService
  ) {}

  ngOnInit(): void {
    this.loadSupportedFormats();
  }

  private loadSupportedFormats(): void {
    this.libraryApi.getSupportedFormats().subscribe({
      next: (response: LibrarySupportedFormatsResponse) => {
        this.supportedFormats = response.formats;
        this.maxFileSizeMb = response.maxFileSizeMb;
      },
      error: () => {
        // Use defaults if API fails
        this.supportedFormats = ['.epub', '.pdf', '.mobi', '.azw3', '.azw', '.kfx', '.pobi', '.fb2', '.txt', '.rtf', '.lit', '.djvu'];
        this.maxFileSizeMb = 500;
      }
    });
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = true;
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = false;
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isDragOver = false;

    const files = event.dataTransfer?.files;
    if (files) {
      this.addFiles(Array.from(files));
    }
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files) {
      this.addFiles(Array.from(input.files));
      input.value = ''; // Reset input to allow re-selecting same file
    }
  }

  private addFiles(files: File[]): void {
    for (const file of files) {
      // Check if already in queue
      if (this.uploadQueue.some(item => item.file.name === file.name && item.file.size === file.size)) {
        continue;
      }

      // Validate extension
      const ext = this.getFileExtension(file.name);
      if (!this.supportedFormats.includes(ext.toLowerCase())) {
        this.uploadQueue.push({
          file,
          status: 'error',
          error: `Unsupported format. Supported: ${this.supportedFormats.join(', ')}`
        });
        continue;
      }

      // Validate size
      if (file.size > this.maxFileSizeMb * 1024 * 1024) {
        this.uploadQueue.push({
          file,
          status: 'error',
          error: `File too large. Maximum size is ${this.maxFileSizeMb}MB.`
        });
        continue;
      }

      this.uploadQueue.push({
        file,
        status: 'pending'
      });
    }
  }

  private getFileExtension(filename: string): string {
    const lastDot = filename.lastIndexOf('.');
    return lastDot >= 0 ? filename.substring(lastDot) : '';
  }

  removeFromQueue(index: number): void {
    if (this.uploadQueue[index].status !== 'uploading') {
      this.uploadQueue.splice(index, 1);
    }
  }

  get hasPendingFiles(): boolean {
    return this.uploadQueue.some(item => item.status === 'pending');
  }

  get hasSuccessFiles(): boolean {
    return this.uploadQueue.some(item => item.status === 'success');
  }

  get allComplete(): boolean {
    return this.uploadQueue.length > 0 &&
           this.uploadQueue.every(item => item.status === 'success' || item.status === 'error');
  }

  startUpload(): void {
    if (this.isUploading) return;
    this.uploadNextFile();
  }

  private uploadNextFile(): void {
    const nextItem = this.uploadQueue.find(item => item.status === 'pending');
    if (!nextItem) {
      this.isUploading = false;
      return;
    }

    this.isUploading = true;
    nextItem.status = 'uploading';

    this.libraryApi.uploadBook(nextItem.file).subscribe({
      next: (response: LibraryUploadResponse) => {
        nextItem.status = 'success';
        nextItem.response = response;
        this.uploadNextFile();
      },
      error: (error) => {
        nextItem.status = 'error';
        nextItem.error = error?.error?.error || error?.message || 'Upload failed';
        this.uploadNextFile();
      }
    });
  }

  formatFileSize(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
  }

  onClose(): void {
    this.dialogRef.close(this.hasSuccessFiles);
  }
}
