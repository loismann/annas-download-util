import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
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
  styleUrl: './vpn-toggle.component.scss'
})
export class VpnToggleComponent implements OnInit {
  /**
   * Ends in-flight reads when the component is destroyed.
   *
   * Reads only: unsubscribing an HttpClient call aborts the request, so routing
   * a write through this would mean navigating away cancels the user's action.
   */
  private readonly destroyRef = inject(DestroyRef);

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

  /**
   * The last state the server confirmed. `enabled`/`region` are bound with
   * `[(ngModel)]`, so they already hold the user's new choice by the time
   * `(change)` fires — without a remembered value there is nothing to go back to
   * when the save fails, and the control would keep claiming a setting the
   * server rejected. Unlike the Spotify shuffle toggle there is no poll here to
   * correct it, so the lie would stand until a reload.
   */
  private confirmedEnabled = false;
  private confirmedRegion = '';

  ngOnInit(): void {
    this.api.getVpnSettings().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (resp) => {
        this.enabled = resp.enabled;
        this.region = resp.region;
        this.availableRegions = resp.availableRegions;
        this.confirmedEnabled = resp.enabled;
        this.confirmedRegion = resp.region;
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

  /**
   * Deliberately NOT routed through `takeUntilDestroyed`: this is a write, and
   * unsubscribing an HttpClient call aborts the request. Guarding it would mean
   * navigating away mid-save silently cancels the VPN change.
   */
  private save(): void {
    this.saving = true;
    this.api.updateVpnSettings(this.enabled, this.region).subscribe({
      next: (resp) => {
        // The server is authoritative — it may answer with something other than
        // what was asked for, and that is the value the control must show.
        this.enabled = resp.enabled;
        this.region = resp.region;
        this.confirmedEnabled = resp.enabled;
        this.confirmedRegion = resp.region;
        this.saving = false;
      },
      error: (err) => {
        this.logger.error('[vpn-toggle] Failed to update VPN settings', err);
        this.enabled = this.confirmedEnabled;
        this.region = this.confirmedRegion;
        this.saving = false;
      }
    });
  }
}
