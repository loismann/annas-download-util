import { ComponentFixture, TestBed } from '@angular/core/testing';
import { StoryMapComponent } from './story-map.component';
import { AnychartLoaderService } from '../../services/anychart-loader.service';
import { actor, edge } from '../testing/cast';

/**
 * The map, with the charting library stubbed.
 *
 * <p>What is worth asserting here is everything the library is <i>not</i>: that
 * it is asked for only when there is something to draw, that a failure to load
 * it leaves the reader a sentence rather than a blank panel, and that a click on
 * a node or a line resolves to the right person or the right relationship. The
 * force layout itself is AnyChart's to get right.</p>
 */
describe('StoryMapComponent', () => {
  let fixture: ComponentFixture<StoryMapComponent>;
  let loader: jasmine.SpyObj<AnychartLoaderService>;
  let chart: Record<string, jasmine.Spy>;
  let clicked: ((event: unknown) => void) | undefined;
  let given: { nodes: unknown[]; edges: unknown[] } | undefined;

  /**
   * The real library is on Karma's `scripts` list, for `story-chart.spec.ts`,
   * which draws against it. Deleting the global here rather than putting it back
   * would break that spec depending on which file Karma happened to load first.
   */
  let real: unknown;

  /** Every configuration call chains, so one self-returning spy stands in for all of them. */
  function chainable(): unknown {
    const node: Record<string, unknown> = {};
    const handler: ProxyHandler<Record<string, unknown>> = {
      get: (_t, prop) => (prop === 'then' ? undefined : () => new Proxy(node, handler))
    };

    return new Proxy(node, handler);
  }

  beforeEach(async () => {
    clicked = undefined;
    given = undefined;

    chart = {
      layout: jasmine.createSpy('layout').and.callFake(chainable),
      nodes: jasmine.createSpy('nodes').and.callFake(chainable),
      edges: jasmine.createSpy('edges').and.callFake(chainable),
      interactivity: jasmine.createSpy('interactivity').and.callFake(chainable),
      group: jasmine.createSpy('group').and.callFake(chainable),
      zoomIn: jasmine.createSpy('zoomIn'),
      zoomOut: jasmine.createSpy('zoomOut'),
      fit: jasmine.createSpy('fit'),
      listen: jasmine.createSpy('listen').and.callFake((_e: string, fn: (event: unknown) => void) => {
        clicked = fn;
      }),
      container: jasmine.createSpy('container'),
      draw: jasmine.createSpy('draw'),
      dispose: jasmine.createSpy('dispose')
    };

    real = (window as unknown as { anychart?: unknown }).anychart;

    (window as unknown as { anychart: unknown }).anychart = {
      graph: (data: { nodes: unknown[]; edges: unknown[] }) => {
        given = data;

        return chart;
      }
    };

    loader = jasmine.createSpyObj<AnychartLoaderService>('AnychartLoaderService', ['load']);
    loader.load.and.resolveTo();

    await TestBed.configureTestingModule({
      imports: [StoryMapComponent],
      providers: [{ provide: AnychartLoaderService, useValue: loader }]
    }).compileComponents();

    fixture = TestBed.createComponent(StoryMapComponent);
  });

  afterEach(() => {
    (window as unknown as { anychart?: unknown }).anychart = real;
  });

  async function draw(actors = CAST, ties = TIES): Promise<HTMLElement> {
    fixture.componentRef.setInput('actors', actors);
    fixture.componentRef.setInput('edges', ties);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  const CAST = [actor('a1', 'Finn'), actor('a2', 'Ellie'), actor('a3', 'Josias')];
  const TIES = [edge('a1', 'a2', 'travels-with', {
    notes: [{ chapter: 4, what: 'she cuts him down from a tree' }]
  })];

  it('says so rather than drawing an empty chart when there is nobody', async () => {
    const page = await draw([], []);

    expect(page.querySelector('.empty')?.textContent).toContain('Nobody to draw yet');
  });

  /** ~960 kB nobody who never opens the map should pay for. */
  it('does not reach for the charting library with nothing to draw', async () => {
    await draw([], []);

    expect(loader.load).not.toHaveBeenCalled();
  });

  it('draws the cast once there is somebody', async () => {
    await draw();

    expect(chart['draw']).toHaveBeenCalled();
    expect(given!.nodes.length).toBe(3);
    expect(given!.edges.length).toBe(1);
  });

  it('leaves a sentence rather than a blank panel when the library will not load', async () => {
    loader.load.and.rejectWith(new Error('offline'));

    const page = await draw();

    expect(page.querySelector('.failed')?.textContent).toContain('could not be drawn');
  });

  /**
   * Nothing awaits the redraw, so a throw from the library is an unhandled
   * rejection — which is how a bad chart call once left the reader a blank
   * rectangle with working zoom buttons and no explanation at all.
   */
  it('leaves the same sentence when the library throws while drawing', async () => {
    chart['draw'].and.throwError('no such group');

    const page = await draw();

    expect(page.querySelector('.failed')?.textContent).toContain('could not be drawn');
  });

  // ─── clicking ───────────────────────────────────────────────────────

  it('shows who somebody is when their node is clicked', async () => {
    const page = await draw(
      [actor('a1', 'Finn', 'Major', { dossier: 'the heir, on the run' }), actor('a2', 'Ellie')],
      []);

    clicked!({ domTarget: { tag: { type: 'node', id: 'a1' } } });
    fixture.detectChanges();

    expect(page.textContent).toContain('the heir, on the run');
  });

  it('shows how two people know each other when the line between them is clicked', async () => {
    const page = await draw();

    clicked!({ domTarget: { tag: { type: 'edge', id: 'edge_0' } } });
    fixture.detectChanges();

    expect(page.textContent).toContain('she cuts him down from a tree');
    expect(page.textContent).toContain('Finn');
    expect(page.textContent).toContain('Ellie');
  });

  it('ignores a click on the background', async () => {
    const page = await draw();

    clicked!({ domTarget: {} });
    fixture.detectChanges();

    expect(page.querySelector('.detail')).toBeNull();
  });

  /**
   * Both inputs arrive separately and the load between them is slow enough that
   * the first attempt can finish after the second. Two charts in one container
   * is a map drawn twice on top of itself.
   */
  it('leaves one chart in the container however the inputs arrive', async () => {
    await draw();

    expect(chart['draw']).toHaveBeenCalledTimes(1);
  });

  it('shows one thing at a time', async () => {
    const page = await draw();

    clicked!({ domTarget: { tag: { type: 'edge', id: 'edge_0' } } });
    clicked!({ domTarget: { tag: { type: 'node', id: 'a3' } } });
    fixture.detectChanges();

    expect(page.querySelectorAll('.detail').length).toBe(1);
    expect(page.textContent).toContain('Josias');
  });
});
