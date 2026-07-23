import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';

import { BookSearchApiService } from '../../services/book-search-api.service';
import { LoggerService } from '../../services/logger.service';

/**
 * Standalone VPN on/off + region toggle for Anna's Archive traffic. Applies
 * immediately server-side on change — deliberately NOT wired through
 * SearchFormComponent's submit-event pattern, since this is a live app-wide
 * setting rather than a per-search parameter.
 */
@Component({
  selector: 'app-vpn-toggle',
  standalone: true,
  imports: [CommonModule, FormsModule, MatSlideToggleModule, MatFormFieldModule, MatSelectModule],
  templateUrl: './vpn-toggle.component.html',
  styleUrl: './vpn-toggle.component.css'
})
export class VpnToggleComponent implements OnInit {
  // Off by default (matches the backend's default) until the real value
  // loads from the server.
  enabled = false;
  region = '';
  availableRegions: string[] = [];
  saving = false;
  loaded = false;

  constructor(
    private api: BookSearchApiService,
    private logger: LoggerService
  ) {}

  ngOnInit(): void {
    this.api.getVpnSettings().subscribe({
      next: (resp) => {
        this.enabled = resp.enabled;
        this.region = resp.region;
        this.availableRegions = resp.availableRegions;
        this.loaded = true;
      },
      error: (err) => {
        this.logger.error('[vpn-toggle] Failed to load VPN settings', err);
        // Fine to just leave the control at its safe defaults if this
        // fails — it isn't required for the app's core function.
        this.loaded = true;
      }
    });
  }

  onToggleChange(): void {
    this.save();
  }

  onRegionChange(): void {
    this.save();
  }

  private save(): void {
    this.saving = true;
    this.api.updateVpnSettings(this.enabled, this.region).subscribe({
      next: (resp) => {
        this.enabled = resp.enabled;
        this.region = resp.region;
        this.saving = false;
      },
      error: (err) => {
        this.logger.error('[vpn-toggle] Failed to update VPN settings', err);
        this.saving = false;
      }
    });
  }
}
