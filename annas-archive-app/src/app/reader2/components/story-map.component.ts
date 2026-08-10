import { ChangeDetectionStrategy, Component, Input, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Actor, ActorEdge } from '../reader2.models';
import { StoryLayout, layOutStory } from '../services/story-layout';

/** Breathing room around the drawing, in layout units. */
const PADDING = 24;

/**
 * The relationship map, drawn from {@link layOutStory}.
 *
 * <p>This component only renders — every coordinate comes from the layout
 * module, which is a pure function with its own tests. Chains of command are
 * solid lines in an echelon tree; rivalries, liaisons, and everything lateral
 * are dashed and drawn over it, so a rivalry between two divisions cannot be
 * read as one commanding the other.</p>
 */
@Component({
  selector: 'app-reader2-story-map',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="empty" *ngIf="!layout().clusters.length">Nobody to draw yet.</p>

    <div class="scroll" *ngIf="layout().clusters.length">
      <svg
        [attr.viewBox]="viewBox()"
        [style.min-width.px]="layout().width + PADDING * 2"
        role="img"
        aria-label="Relationship map">
        <g
          *ngFor="let cluster of layout().clusters"
          [attr.transform]="'translate(' + (cluster.x + PADDING) + ',' + (cluster.y + PADDING) + ')'">
          <g *ngFor="let edge of cluster.edges">
            <line
              [class]="edge.kind"
              [attr.x1]="edge.from.x" [attr.y1]="edge.from.y"
              [attr.x2]="edge.to.x" [attr.y2]="edge.to.y" />
            <text
              class="tie"
              [attr.x]="(edge.from.x + edge.to.x) / 2"
              [attr.y]="(edge.from.y + edge.to.y) / 2 - 4">{{ edge.type }}</text>
          </g>

          <g *ngFor="let actor of cluster.actors">
            <circle [attr.cx]="actor.x" [attr.cy]="actor.y" r="7" />
            <text class="who" [attr.x]="actor.x" [attr.y]="actor.y + 22">{{ actor.name }}</text>
          </g>
        </g>
      </svg>
    </div>
  `,
  styleUrl: './story-map.component.scss'
})
export class StoryMapComponent {
  protected readonly PADDING = PADDING;

  private readonly cast = signal<Actor[]>([]);
  private readonly ties = signal<ActorEdge[]>([]);

  @Input({ required: true }) set actors(value: Actor[]) {
    this.cast.set(value);
  }

  @Input({ required: true }) set edges(value: ActorEdge[]) {
    this.ties.set(value);
  }

  protected readonly layout = computed<StoryLayout>(() =>
    layOutStory(this.cast(), this.ties()));

  protected viewBox(): string {
    const drawn = this.layout();

    return `0 0 ${drawn.width + PADDING * 2} ${drawn.height + PADDING * 2}`;
  }
}
