import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Actor, StoryThread, StoryVocabulary } from '../reader2.models';

/**
 * What is running, what has gone quiet, and what is finished.
 *
 * <p><b>Dormant threads are the reason this panel exists.</b> "We have not heard
 * about this since chapter 61" is the fact a reader of a long novel actually
 * wants, and it is the one a chapter summary cannot supply — the model writing
 * chapter 74 has no idea what chapter 61 contained. So dormancy is set apart
 * visually rather than being one more row, and the gap is spelled out in
 * chapters.</p>
 *
 * <p>Order is by state, then by how recently each moved. A finished thread sinks
 * to the bottom: it is history, not something to keep an eye on.</p>
 */
@Component({
  selector: 'app-reader2-thread-panel',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './thread-panel.component.html',
  styleUrl: './thread-panel.component.scss'
})
export class ThreadPanelComponent {
  @Input({ required: true }) threads: StoryThread[] = [];
  @Input() actors: Actor[] = [];
  @Input({ required: true }) vocabulary!: StoryVocabulary;

  /** Where the reader is, so a gap can be stated in chapters rather than implied. */
  @Input() currentChapter = 0;

  private readonly order: Record<string, number> = {
    Dormant: 0, Active: 1, Resolved: 2, Abandoned: 3
  };

  protected get sorted(): StoryThread[] {
    return [...this.threads].sort((a, b) =>
      this.order[a.status] - this.order[b.status]
      || b.lastAdvancedChapter - a.lastAdvancedChapter);
  }

  /** How long a thread has been quiet, in chapters. Zero when it just moved. */
  protected silence(thread: StoryThread): number {
    return Math.max(0, this.currentChapter - thread.lastAdvancedChapter);
  }

  protected participants(thread: StoryThread): string {
    return this.actors
      .filter(a => thread.participantIds.includes(a.id))
      .map(a => a.canonicalName)
      .join(', ');
  }

  /** The most recent movements, newest first — the whole list is rarely wanted. */
  protected recent(thread: StoryThread): { chapter: number; whatMoved: string }[] {
    return [...thread.beats].sort((a, b) => b.chapter - a.chapter).slice(0, 4);
  }
}
