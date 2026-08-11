import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Actor, ActorCorrection, ActorGroup, ChapterInfo } from '../reader2.models';
import { ChapterEntry, ChapterLogComponent } from './chapter-log.component';
import { ChapterNamePipe } from '../chapter-name.pipe';

/**
 * Who somebody is, and the reader's chance to correct it.
 *
 * <p>One component because the cast list and the map both answer the same
 * question, and while the markup was duplicated between them the edit controls
 * would have been written twice and drifted on the first change.</p>
 *
 * <p><b>Everything shown here is already stored.</b> The dossier, role, status
 * and arc are written when a chapter is ingested; the note and the preferred
 * name are the reader's own. Nothing on this panel costs anything or reaches a
 * model, which is why the edit form is a plain form and not a purchase.</p>
 */
@Component({
  selector: 'app-reader2-actor-dossier',
  standalone: true,
  imports: [CommonModule, ChapterLogComponent, ChapterNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-container *ngIf="!editing()">
      <p class="dossier" *ngIf="actor.dossier">{{ actor.dossier }}</p>

      <!-- The reader's own, marked as theirs so it is never read as the model's. -->
      <p class="note" *ngIf="actor.readerNote">
        <span class="mine">Your note</span> {{ actor.readerNote }}
      </p>

      <p class="meta">
        <span *ngIf="groupNames">{{ groupNames }}</span>
        <span>First seen in {{ actor.firstSeenChapter | chapterName: chapters }}</span>
        <span>Last seen in {{ actor.lastSeenChapter | chapterName: chapters }}</span>
        <span *ngIf="actor.status">{{ actor.status }}</span>
      </p>

      <app-reader2-chapter-log
        *ngIf="actor.arc.length"
        [entries]="arc"
        [chapters]="chapters" />

      <p class="none" *ngIf="bare">
        Nothing recorded about them yet beyond their name.
      </p>

      <p class="is-hidden" *ngIf="actor.hidden">Hidden from the map.</p>

      <div class="actions">
        <button type="button" class="link" (click)="startEditing()">Edit</button>

        <!--
          One press, not behind the edit form: hiding a walk-on is the thing a
          reader does to twenty people in a row while reading the map, and making
          each one a form to open, fill and submit is making it not worth doing.
        -->
        <button type="button" class="link" (click)="hide.emit(!actor.hidden)">
          {{ actor.hidden ? 'Show on map' : 'Hide from map' }}
        </button>
      </div>
    </ng-container>

    <form class="edit" *ngIf="editing()" (submit)="save($event)">
      <label>
        <span>Show them as</span>
        <input
          type="text"
          [value]="name()"
          (input)="name.set($any($event.target).value)"
          [attr.placeholder]="actor.canonicalName" />
      </label>

      <label>
        <span>Your note</span>
        <textarea rows="3" [value]="note()" (input)="note.set($any($event.target).value)"></textarea>
      </label>

      <label *ngIf="others.length">
        <span>Same person as</span>
        <select multiple size="4" (change)="pickSame($event)">
          <option
            *ngFor="let other of others"
            [value]="other.id"
            [selected]="sameAs().includes(other.id)">
            {{ other.canonicalName }}
          </option>
        </select>
      </label>

      <p class="hint">
        Your corrections are kept separately from the record, so rebuilding it does
        not lose them. Clearing a field puts the original back.
      </p>

      <div class="actions">
        <button type="submit" class="save">Save</button>
        <button type="button" class="link" (click)="editing.set(false)">Cancel</button>
      </div>
    </form>
  `,
  styleUrl: './actor-dossier.component.scss'
})
export class ActorDossierComponent {
  /** Held rather than read straight through, so the arc is mapped once per person. */
  @Input({ required: true }) set actor(value: Actor) {
    this.who = value;
    this.arc = value.arc.map(point => ({ chapter: point.chapter, what: point.change }));
  }

  get actor(): Actor {
    return this.who;
  }

  private who!: Actor;

  protected arc: ChapterEntry[] = [];

  @Input() groups: ActorGroup[] = [];

  /** The contents list, so a chapter is named the way the sidebar names it. */
  @Input() chapters: ChapterInfo[] = [];

  /** Everybody else, for "same person as". The panel supplies the cast. */
  @Input() others: Actor[] = [];

  @Output() correct = new EventEmitter<ActorCorrection>();

  /**
   * Kept off the map, or put back.
   *
   * <p>Its own event, with its own route behind it. A correction is saved whole,
   * and this panel could not resend the rest of one even if it tried: a preferred
   * name is projected onto the canonical name, so nothing it was served
   * distinguishes a name the reader chose from one the model did.</p>
   */
  @Output() hide = new EventEmitter<boolean>();

  protected readonly editing = signal(false);
  protected readonly name = signal('');
  protected readonly note = signal('');
  protected readonly sameAs = signal<string[]>([]);

  /**
   * Nothing but a name so far. Said plainly rather than shown as a blank panel —
   * a walk-on the extraction recorded and had nothing else to say about is a
   * different thing from a dossier that failed to load.
   */
  protected get bare(): boolean {
    return !this.actor.dossier && !this.actor.readerNote && this.actor.arc.length === 0;
  }

  protected get groupNames(): string {
    return this.groups
      .filter(g => this.actor.groupIds.includes(g.id))
      .map(g => g.name)
      .join(', ');
  }

  protected startEditing(): void {
    this.name.set('');
    this.note.set(this.actor.readerNote);
    this.sameAs.set([]);
    this.editing.set(true);
  }

  protected pickSame(event: Event): void {
    this.sameAs.set(Array.from((event.target as HTMLSelectElement).selectedOptions).map(o => o.value));
  }

  /**
   * An empty field is a cleared correction, not an empty one — which is why
   * these are nulled rather than sent as "". The server drops a correction that
   * says nothing, so clearing everything is how an edit is undone.
   */
  protected save(event: Event): void {
    event.preventDefault();
    this.editing.set(false);

    this.correct.emit({
      preferredName: this.name().trim() || null,
      note: this.note().trim() || null,
      sameAs: this.sameAs()
    });
  }

}
