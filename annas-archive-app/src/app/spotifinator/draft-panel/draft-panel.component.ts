import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { SpotifinatorApiService } from '../../services/spotifinator-api.service';
import { LoggerService } from '../../services/logger.service';
import { SpotifinatorPresentation as Present } from '../spotifinator.presentation';
import { SpotifyDiscoveryDraft, SpotifyPlan } from '../spotifinator.models';

/**
 * The draft workspace: a proposed track list that has never touched Spotify.
 *
 * Everything here writes to the draft on the server and hands the result back
 * up, because the draft is also rendered as a card in the transcript and listed
 * in the sidebar — the page as a whole decides what "the current draft" means,
 * this only decides what to ask the server for.
 *
 * None of the writes are cancelled when this component goes away. See the
 * matching note on `SpotifinatorComponent.destroy$`.
 */
@Component({
  selector: 'app-spotify-draft-panel',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatIconModule, MatTooltipModule],
  templateUrl: './draft-panel.component.html',
  styleUrl: './draft-panel.component.scss'
})
export class SpotifyDraftPanelComponent {
  @Input({ required: true }) draft!: SpotifyDiscoveryDraft;

  /** A new version of the draft came back from the server. */
  @Output() changed = new EventEmitter<SpotifyDiscoveryDraft>();

  /** Put the panel away without touching the draft. */
  @Output() closed = new EventEmitter<void>();

  /** Gone from the server; the page still has to clear it from its own lists. */
  @Output() deleted = new EventEmitter<SpotifyDiscoveryDraft>();

  /** A plan awaiting review — pressing Create builds one, it never writes. */
  @Output() planBuilt = new EventEmitter<SpotifyPlan>();

  /** A refusal worth showing in the transcript rather than swallowing. */
  @Output() failed = new EventEmitter<string>();

  readonly present = Present;

  /** One in-flight action at a time, so Create cannot race Delete. */
  pending = false;

  constructor(
    private api: SpotifinatorApiService,
    private logger: LoggerService
  ) {}

  openInSpotify(url: string | null): void {
    if (url) window.open(url, '_blank');
  }

  removeCandidate(candidateId: string): void {
    this.api.updateDiscoveryDraft(this.draft.id, { removeCandidateIds: [candidateId] })
      .subscribe({
        next: updated => this.changed.emit(updated),
        error: err => this.logger.error('[Spotifinator] Could not remove draft candidate:', err)
      });
  }

  moveCandidate(candidateId: string, delta: number): void {
    const ids = this.draft.candidates.map(candidate => candidate.id);
    const from = ids.indexOf(candidateId);
    const to = from + delta;
    if (from < 0 || to < 0 || to >= ids.length) return;
    [ids[from], ids[to]] = [ids[to], ids[from]];
    this.api.updateDiscoveryDraft(this.draft.id, { orderedCandidateIds: ids })
      .subscribe({
        next: updated => this.changed.emit(updated),
        error: err => this.logger.error('[Spotifinator] Could not reorder draft:', err)
      });
  }

  selectAlternative(candidateId: string, trackId: string): void {
    this.api.updateDiscoveryDraft(this.draft.id, {
      candidateSelections: { [candidateId]: trackId }
    }).subscribe({
      next: updated => this.changed.emit(updated),
      error: err => this.logger.error('[Spotifinator] Could not select Spotify match:', err)
    });
  }

  save(): void {
    if (this.pending) return;
    this.pending = true;
    this.api.updateDiscoveryDraft(this.draft.id, { saved: true })
      .subscribe({
        next: updated => {
          this.pending = false;
          this.changed.emit(updated);
        },
        error: err => {
          this.pending = false;
          this.logger.error('[Spotifinator] Could not save draft:', err);
        }
      });
  }

  /**
   * Turns the draft into a real playlist — via the plan flow, not directly. The
   * review opens as a modal, rather than as a card in the chat: the button used
   * to build a plan into a pane the user was not looking at, so it looked like
   * it had done nothing at all.
   */
  createInSpotify(): void {
    if (this.pending || Present.resolvedCandidateCount(this.draft) === 0) return;

    this.pending = true;
    this.api.buildCreateFromDraftPlan(this.draft.id, this.draft.name)
      .subscribe({
        next: plan => {
          this.pending = false;
          this.planBuilt.emit(plan);
        },
        error: err => {
          this.pending = false;
          // A refusal is a 400 carrying the real sentence: nothing resolved, no
          // name, over the ceiling. It is an answer, not a crash.
          this.failed.emit(
            err.error?.error || 'That draft could not be turned into a playlist.');
        }
      });
  }

  /**
   * Throws the draft away for good. Unlike a playlist this really is a delete —
   * the draft has never touched Spotify — so it asks once here rather than going
   * through the plan flow.
   */
  delete(): void {
    if (this.pending) return;

    const draft = this.draft;
    const resolved = Present.resolvedCandidateCount(draft);
    const confirmed = confirm(
      `Delete the draft "${draft.name}"?\n\n`
      + `${draft.candidates.length} candidates (${resolved} matched) will be lost. `
      + 'Nothing on Spotify is affected — this draft was never a playlist.');

    if (!confirmed) return;

    this.pending = true;
    this.api.deleteDiscoveryDraft(draft.id).subscribe({
      next: () => {
        this.pending = false;
        this.deleted.emit(draft);
      },
      error: err => {
        this.pending = false;
        this.failed.emit(err.error?.error || 'That draft could not be deleted.');
      }
    });
  }
}
