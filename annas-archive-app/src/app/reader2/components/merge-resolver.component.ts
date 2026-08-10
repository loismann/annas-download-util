import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Actor, CandidateMerge } from '../reader2.models';

/** The reader's answer to one question. */
export interface MergeAnswer {
  mergeId: string;
  accept: boolean;
}

/**
 * The merger's open questions, each answerable in one click.
 *
 * <p>These are the decisions the backend refused to take on its own: merging two
 * entries is the only way an actor is ever removed, and a wrong merge is a story
 * the reader cannot see is wrong. So the question is put in words, with the
 * merger's reason, and both answers are equally easy — a design that nudged
 * toward "yes" would be auto-merge with extra steps.</p>
 */
@Component({
  selector: 'app-reader2-merge-resolver',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="question" *ngFor="let question of questions" role="group">
      <p class="ask">
        <ng-container *ngIf="question.otherActorId === null; else twoPeople">
          Is “{{ question.alias }}” another name for
          <strong>{{ name(question.actorId) }}</strong>?
        </ng-container>
        <ng-template #twoPeople>
          Are <strong>{{ name(question.actorId) }}</strong> and
          <strong>{{ name(question.otherActorId!) }}</strong> the same person?
        </ng-template>
      </p>

      <p class="reason">{{ question.reason }}</p>

      <div class="answers">
        <button
          type="button"
          (click)="resolve.emit({ mergeId: question.id, accept: true })">
          {{ question.otherActorId === null ? 'Yes, same person' : 'Merge them' }}
        </button>
        <button
          type="button"
          (click)="resolve.emit({ mergeId: question.id, accept: false })">
          Keep apart
        </button>
      </div>
    </div>
  `,
  styleUrl: './merge-resolver.component.scss'
})
export class MergeResolverComponent {
  @Input({ required: true }) questions: CandidateMerge[] = [];
  @Input() actors: Actor[] = [];

  @Output() resolve = new EventEmitter<MergeAnswer>();

  /**
   * Falls back to the id rather than hiding the question — an unanswerable
   * question the reader can dismiss beats one that silently disappears.
   */
  protected name(actorId: string): string {
    return this.actors.find(a => a.id === actorId)?.canonicalName ?? actorId;
  }
}
