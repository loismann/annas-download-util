import { Component, Inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';

export interface JellyfinPlayerModalData {
  title: string;
  embedUrl: string;
}

@Component({
  selector: 'app-jellyfin-player-modal',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule],
  templateUrl: './jellyfin-player-modal.component.html',
  styleUrl: './jellyfin-player-modal.component.css'
})
export class JellyfinPlayerModalComponent {
  safeEmbedUrl: SafeResourceUrl;

  constructor(
    private dialogRef: MatDialogRef<JellyfinPlayerModalComponent>,
    private sanitizer: DomSanitizer,
    @Inject(MAT_DIALOG_DATA) public data: JellyfinPlayerModalData
  ) {
    // The embed URL is our own backend-resolved Jellyfin deep link (routed
    // through the CSP-stripping proxy) — trusted, not user-supplied.
    this.safeEmbedUrl = this.sanitizer.bypassSecurityTrustResourceUrl(data.embedUrl);
  }

  close(): void {
    this.dialogRef.close();
  }
}
