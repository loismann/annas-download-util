import {
  ChangeDetectionStrategy, Component, ElementRef, EventEmitter, Input, OnDestroy, Output,
  ViewChild, computed, inject, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Actor, ActorCorrection, ActorEdge, ActorGroup, ChapterInfo } from '../reader2.models';
import { MapDetailComponent } from './map-detail.component';
import { AnychartLoaderService } from '../../services/anychart-loader.service';
import { graphOf } from '../services/story-graph';
import { ChartTag, Drawn, buildChart, chosen, fitTo, wheelZoom, zoom } from '../services/story-chart';

/**
 * The relationship map, as a force-directed network.
 *
 * <p><b>Drawn by AnyChart rather than by hand.</b> The first version laid the
 * cast out on a deterministic grid, which is readable for a chain of command and
 * wrong for everything else — a novel's cast is a web, and a grid of it says
 * nothing about who is near whom. Force-directed layout, wheel zoom, drag to pan,
 * and node sizing by degree are all things the vendored graph module already
 * does; writing a second, worse version of them here would be the reason to
 * regret it later.</p>
 *
 * <p>The library is loaded on demand by {@link AnychartLoaderService} — it is
 * ~960 kB, and nobody who never opens the map should pay for it. The shape of
 * the data it is given is decided by {@link graphOf}, which is pure and tested;
 * everything in this file is the part that cannot be.</p>
 *
 * <p>Clicking a node or a line opens {@link MapDetailComponent} over what is
 * <i>already loaded</i>. Neither costs anything: a dossier is written when the
 * chapter is ingested, and an edge carries its own chapter-tagged history.</p>
 */
@Component({
  selector: 'app-reader2-story-map',
  standalone: true,
  imports: [CommonModule, MapDetailComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './story-map.component.html',
  styleUrl: './story-map.component.scss'
})
export class StoryMapComponent implements OnDestroy {
  private readonly loader = inject(AnychartLoaderService);

  @ViewChild('canvas') private canvas?: ElementRef<HTMLDivElement>;

  private readonly cast = signal<Actor[]>([]);
  private readonly ties = signal<ActorEdge[]>([]);

  /** The cast tab's own word for its people, so a failure names the right tab. */
  @Input() castLabel = 'Characters';

  @Input() groups: ActorGroup[] = [];

  /** The contents list, so a chapter is named the way the sidebar names it. */
  @Input() chapters: ChapterInfo[] = [];

  @Output() correct = new EventEmitter<{ actorId: string; correction: ActorCorrection }>();
  @Output() hide = new EventEmitter<{ actorId: string; hidden: boolean }>();

  /** Everybody but whoever is selected, for the "same person as" picker. */
  protected readonly others = computed(() =>
    this.cast().filter(a => a.id !== this.chosenId()));

  @Input({ required: true }) set actors(value: Actor[]) {
    this.cast.set(value);
    this.redraw();
  }

  @Input({ required: true }) set edges(value: ActorEdge[]) {
    this.ties.set(value);
    this.redraw();
  }

  protected readonly graph = computed(() => graphOf(this.cast(), this.ties()));

  protected readonly failed = signal(false);
  private readonly chosenId = signal<string | null>(null);
  private readonly chosenTie = signal<number | null>(null);

  protected readonly chosenActor = computed(() =>
    this.cast().find(a => a.id === this.chosenId()) ?? null);

  protected readonly chosenEdge = computed(() => {
    const at = this.chosenTie();

    return at === null ? null : this.graph().edges[at]?.tie ?? null;
  });

  private drawn?: Drawn;

  ngOnDestroy(): void {
    this.dispose();
  }

  protected nameOf(id: string | undefined): string {
    return this.cast().find(a => a.id === id)?.canonicalName ?? '';
  }

  protected clearChoice(): void {
    this.chosenId.set(null);
    this.chosenTie.set(null);
  }

  protected zoomBy(direction: number): void {
    (direction > 0 ? zoom.in : zoom.out)(this.drawn?.chart);
  }

  /**
   * The wheel, ours rather than the library's — its own handling is turned off,
   * because what it did with the wheel was scroll the drawing rather than zoom
   * it. Prevented, or the panel behind scrolls at the same time.
   */
  protected onWheel(event: WheelEvent): void {
    event.preventDefault();
    wheelZoom(this.drawn, event.deltaY);
  }

  /** Everybody on screen at once, however far the reader has wandered. */
  protected fit(): void {
    fitTo(this.drawn, this.view());
  }

  /** The panel the map is drawn into, which is what a fit has to fit inside. */
  private view(): { width: number; height: number } {
    const box = this.canvas?.nativeElement.getBoundingClientRect();

    return { width: box?.width ?? 0, height: box?.height ?? 0 };
  }

  /**
   * Rebuilds the chart. Both inputs arrive separately, so this runs twice on
   * open; drawing is cheap next to loading the library, and a chart built from
   * half the model would be a picture of a cast with no relationships.
   */
  private async redraw(): Promise<void> {
    this.clearChoice();
    if (!this.graph().nodes.length) return;

    // Both setters call this, and the load between them is slow enough that the
    // first can finish after the second. Only the newest attempt may draw, or
    // two charts end up in one container.
    const attempt = ++this.attempt;

    // Nothing below is awaited by a caller, so an escaping throw would be an
    // unhandled rejection and the reader would get a blank rectangle with
    // working buttons on it. That is exactly what a bad chart call once did, so
    // drawing is inside the same net as loading rather than beside it.
    try {
      await this.loader.load();

      // The view is behind an *ngIf that has only just become true.
      await Promise.resolve();
      if (!this.canvas || attempt !== this.attempt) return;

      this.dispose();
      this.drawn = buildChart(this.graph(), tag => this.choose(tag));
      this.drawn.chart.container(this.canvas.nativeElement);
      this.drawn.chart.draw();

      // Opened fitted, not at whatever scale the drawing happens to be. A cast
      // larger than the panel would otherwise open part-way off the edge of it,
      // and the reader would have to find the controls to see their own book.
      this.fit();
      this.failed.set(false);
    } catch {
      this.dispose();
      this.failed.set(true);
    }
  }

  private attempt = 0;

  /** Decoding the tag is {@link chosen}'s job; holding the answer is this one's. */
  private choose(tag: ChartTag | undefined): void {
    const hit = chosen(tag);

    if (!hit) return;

    this.chosenId.set(hit.node);
    this.chosenTie.set(hit.edge);
  }

  private dispose(): void {
    this.drawn?.chart?.dispose?.();
    this.drawn = undefined;
  }
}
