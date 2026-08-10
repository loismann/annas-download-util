import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { Lens } from '../reader2.models';

/** The side panels, one open at a time. */
export type ToolPanel = 'sections' | 'story' | 'vocabulary' | 'flashcards' | 'search' | 'appearance';

/**
 * The toolbar above the text: which panel is open, and the book type.
 *
 * <p>The book type is a chip rather than a hidden setting because it is the one
 * choice that changes what every generate button will produce, and a reader who
 * cannot see it has no way to explain why a summary reads the way it does.
 * Pressing it opens the picker; it never changes the type on its own.</p>
 *
 * <p>The panel list is data, so adding a panel is one entry rather than a
 * button, an <c>*ngIf</c>, and a branch in the shell.</p>
 */
@Component({
  selector: 'app-reader2-reader-tools',
  standalone: true,
  imports: [CommonModule, MatIconModule, MatMenuModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tools" role="toolbar" aria-label="Reading tools">
      <button
        type="button"
        class="icon"
        [attr.aria-pressed]="sidebarOpen"
        title="Show or hide the contents"
        (click)="toggleSidebar.emit()">
        <mat-icon>{{ sidebarOpen ? 'menu_open' : 'menu' }}</mat-icon>
      </button>

      <button
        type="button"
        class="chip"
        *ngIf="lens"
        [title]="lens.description + ' — press to change'"
        (click)="changeType.emit()">
        <mat-icon>{{ lens.icon }}</mat-icon>
        {{ lens.displayName }}
      </button>

      <span class="spacer"></span>

      <ng-content></ng-content>

      <!--
        The analysis pane is the default (open === null), but a default with no
        button is unreachable once any panel is open — the only way back was
        knowing to click a toggled icon a second time. This names it.
      -->
      <button
        type="button"
        class="icon"
        [class.selected]="open === null"
        [attr.aria-pressed]="open === null"
        title="Chapter summary and analysis"
        (click)="openChange.emit(null)">
        <mat-icon>psychology</mat-icon>
      </button>

      <button
        *ngFor="let panel of panels"
        type="button"
        class="icon"
        [class.selected]="open === panel.key"
        [attr.aria-pressed]="open === panel.key"
        [title]="panel.title"
        (click)="openChange.emit(open === panel.key ? null : panel.key)">
        <mat-icon>{{ panel.icon }}</mat-icon>
      </button>

      <a
        class="icon"
        *ngIf="exportUrl"
        [href]="exportUrl"
        download
        title="Download everything generated for this book">
        <mat-icon>download</mat-icon>
      </a>

      <button type="button" class="icon" title="Fullscreen" (click)="toggleFullscreen.emit()">
        <mat-icon>fullscreen</mat-icon>
      </button>

      <!--
        The two operations that change what exists rather than what is shown.
        Behind a menu because neither is wanted often and both ask first.
      -->
      <button type="button" class="icon" title="More" [matMenuTriggerFor]="more">
        <mat-icon>more_vert</mat-icon>
      </button>

      <mat-menu #more="matMenu">
        <button mat-menu-item type="button" (click)="reIndex.emit()">
          <mat-icon>sync</mat-icon>
          <span>Extract this book again</span>
        </button>
        <button mat-menu-item type="button" (click)="remove.emit()">
          <mat-icon>delete_outline</mat-icon>
          <span>Remove from Reader II</span>
        </button>
      </mat-menu>
    </div>
  `,
  styleUrl: './reader-tools.component.scss'
})
export class ReaderToolsComponent {
  /** The book's current type, as the server describes it. */
  @Input() lens: Lens | null = null;

  @Input() open: ToolPanel | null = null;
  @Input() sidebarOpen = true;
  @Input() exportUrl: string | null = null;

  @Output() openChange = new EventEmitter<ToolPanel | null>();
  @Output() changeType = new EventEmitter<void>();
  @Output() toggleSidebar = new EventEmitter<void>();
  @Output() toggleFullscreen = new EventEmitter<void>();
  @Output() reIndex = new EventEmitter<void>();
  @Output() remove = new EventEmitter<void>();

  /**
   * The story panel appears only for a type that keeps a cast, and takes its
   * name from the lens — the toolbar says "Characters" over a novel and
   * "Commanders & Units" over a campaign history, from the same line of data.
   */
  protected get panels(): { key: ToolPanel; icon: string; title: string }[] {
    const story = this.lens?.buildsStoryModel && this.lens.storyVocabulary
      ? [{ key: 'story' as ToolPanel, icon: 'groups', title: this.lens.storyVocabulary.actors }]
      : [];

    return [
      { key: 'sections', icon: 'segment', title: 'Sections' },
      ...story,
      { key: 'vocabulary', icon: 'spellcheck', title: 'Vocabulary' },
      { key: 'flashcards', icon: 'style', title: 'Cards' },
      { key: 'search', icon: 'search', title: 'Search this book' },
      { key: 'appearance', icon: 'text_format', title: 'Appearance' }
    ];
  }
}
