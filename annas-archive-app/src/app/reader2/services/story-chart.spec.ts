import { buildChart, chosen, fitTo, wheelZoom, zoom } from './story-chart';
import { graphOf } from './story-graph';
import { actor, edge } from '../testing/cast';

/**
 * The chart, against the charting library that actually ships.
 *
 * <p><b>This is the one spec here that must not use a double.</b> Every other
 * test of the map stubs AnyChart, and a stub answers every call the same way —
 * which is precisely how a build that has no <c>springLength()</c> and no
 * <c>group()</c> passed a full suite while drawing a blank rectangle in the
 * reader. The vendored bundle is on Karma's <c>scripts</c> list, so the real
 * thing is available here; asking it to draw into a detached element is the only
 * assertion in the codebase that spans that gap.</p>
 *
 * <p>It asserts nothing about how the picture <i>looks</i> — that is AnyChart's
 * to get right and cannot be checked without eyes. It asserts that every call
 * {@link buildChart} makes is one this build understands, and that a draw
 * completes and puts something on the page.</p>
 */
describe('story-chart, against the real AnyChart build', () => {
  let host: HTMLElement;

  beforeEach(() => {
    host = document.createElement('div');
    host.style.width = '640px';
    host.style.height = '480px';
    document.body.appendChild(host);
  });

  afterEach(() => host.remove());

  const CAST = [
    actor('a1', 'Finn'), actor('a2', 'Ellie'), actor('a3', 'Josias'),
    actor('a4', 'Yatras'), actor('a5', 'Callum')
  ];

  // Two connected, one pair apart from them, one nobody has met — so the
  // clustering, the sizing and the unconnected slot are all exercised.
  const TIES = [edge('a1', 'a2', 'travels-with'), edge('a2', 'a3', 'rescues'), edge('a4', 'a5', 'serves')];

  function drawn() {
    const made = buildChart(graphOf(CAST, TIES), () => undefined);

    made.chart.container(host);
    made.chart.draw();

    return made;
  }

  function span(): { width: number; height: number } {
    const rects = Array.from(host.querySelectorAll('text')).map(t => t.getBoundingClientRect());

    return {
      width: Math.max(...rects.map(r => r.right)) - Math.min(...rects.map(r => r.left)),
      height: Math.max(...rects.map(r => r.bottom)) - Math.min(...rects.map(r => r.top))
    };
  }

  it('draws without throwing', () => {
    expect(() => drawn()).not.toThrow();
  });

  it('puts something on the page', () => {
    drawn();

    expect(host.querySelector('svg')).not.toBeNull();
  });

  /** A blank chart also has an svg in it. This is the check that it drew people. */
  it('draws one shape per person and one per relationship', () => {
    drawn();

    // AnyChart's own count, rather than counting DOM nodes it is free to
    // restructure: the data it accepted is what it will lay out.
    expect(host.querySelectorAll('svg text').length).toBeGreaterThanOrEqual(CAST.length);
  });

  /**
   * <b>The assertion the layout work rests on.</b> Positions are only ours if the
   * chart is told <c>'fixed'</c> <i>and</i> that build honours the coordinates —
   * the build's own layout has two settings and re-randomises everything when its
   * iteration count is above zero, so getting this wrong means every spacing rule
   * in `story-layout.ts` is computed and then thrown away.
   */
  it('draws people where they were placed, rather than laying them out again', () => {
    drawn();

    const xs = Array.from(host.querySelectorAll('svg text'))
      .map(t => t.getBoundingClientRect().left);

    // The two islands are packed well apart; a re-run layout would not reproduce
    // that spread from the same data by chance.
    expect(Math.max(...xs) - Math.min(...xs)).toBeGreaterThan(100);
  });

  it('understands every call the map makes of it', () => {
    const { chart } = drawn();

    for (const call of ['zoomIn', 'zoomOut', 'fit', 'zoom', 'group', 'layout', 'nodes', 'edges']) {
      expect(typeof chart[call]).withContext(call).toBe('function');
    }
  });

  it('zooms without throwing', () => {
    const made = drawn();

    expect(() => {
      zoom.in(made.chart);
      zoom.out(made.chart);
      fitTo(made, { width: 640, height: 480 });
    }).not.toThrow();
  });

  // ─── fitting ────────────────────────────────────────────────────────

  /**
   * <b>The build's own `fit()` is a reset, not a fit.</b> Measured here rather
   * than assumed: it restores scale 1 and centres, which on any cast larger than
   * the panel leaves most of the map outside it — which is exactly what pressing
   * Fit looked like it was doing.
   */
  it('brings the whole drawing inside the panel, however far it was zoomed', () => {
    const made = drawn();

    made.chart.zoomIn(1.5);
    made.chart.zoomIn(1.5);
    made.chart.zoomIn(1.5);
    expect(span().width).toBeGreaterThan(640);

    fitTo(made, { width: 640, height: 480 });

    expect(span().width).toBeLessThanOrEqual(640);
    expect(span().height).toBeLessThanOrEqual(480);
  });

  it('puts a panned map back, not just a zoomed one', () => {
    const made = drawn();
    const inside = () => {
      const rects = Array.from(host.querySelectorAll('text')).map(t => t.getBoundingClientRect());
      const panel = host.getBoundingClientRect();

      return rects.every(r =>
        r.left >= panel.left - 1 && r.right <= panel.right + 1
        && r.top >= panel.top - 1 && r.bottom <= panel.bottom + 1);
    };

    made.chart.move(400, 300);
    expect(inside()).withContext('panned away').toBeFalse();

    fitTo(made, { width: 640, height: 480 });

    expect(inside()).withContext('after Fit').toBeTrue();
  });

  it('reports an extent, so a fit has something to fit', () => {
    expect(drawn().extent.width).toBeGreaterThan(0);
    expect(drawn().extent.height).toBeGreaterThan(0);
  });

  // ─── the wheel ──────────────────────────────────────────────────────

  /**
   * The build's own wheel handling is turned off — left on, it scrolled the
   * drawing about rather than zooming it. The direction is deliberately the
   * reverse of the usual convention, which is what was asked for.
   */
  it('zooms in when the wheel turns toward the reader', () => {
    const made = drawn();
    const before = span().width;

    wheelZoom(made, 120);

    expect(span().width).toBeGreaterThan(before);
  });

  it('zooms out when the wheel turns away', () => {
    const made = drawn();
    const before = span().width;

    wheelZoom(made, -120);

    expect(span().width).toBeLessThan(before);
  });

  it('does nothing for a wheel event that reports no movement', () => {
    const made = drawn();
    const before = span().width;

    wheelZoom(made, 0);

    expect(span().width).toBeCloseTo(before, 0);
  });

  /**
   * A wheel reports several notches for one flick of a finger, so a step sized
   * for a deliberate press of the + button runs away under a mouse. Half as much
   * per notch — and because zoom composes by multiplication, "half" has to mean
   * two notches landing where one press lands, not half the number.
   */
  it('moves half as far on one notch of the wheel as on one press of the button', () => {
    const made = drawn();
    const atRest = span().width;

    wheelZoom(made, 120);
    const oneNotch = span().width;
    wheelZoom(made, 120);
    const twoNotches = span().width;

    // Back to where it started, on this same chart rather than a second one:
    // fit() is a reset to scale 1, which is the fact `fitTo` is built on.
    made.chart.fit();
    expect(span().width).withContext('fit() put it back').toBeCloseTo(atRest, 0);

    zoom.in(made.chart);

    expect(span().width)
      .withContext('two notches compose to exactly one button press')
      .toBeCloseTo(twoNotches, 0);

    expect(oneNotch)
      .withContext('and one notch alone falls short of it')
      .toBeGreaterThan(atRest);
    expect(oneNotch).toBeLessThan(twoNotches);
  });
});

/** Pure, and needs no library — so it is tested apart from the drawing above. */
describe('chosen', () => {
  it('reads a node click as the person it names', () => {
    expect(chosen({ type: 'node', id: 'a7' })).toEqual({ node: 'a7', edge: null });
  });

  /** AnyChart's own `edge_<index>`, indexing the array `graphOf` handed it. */
  it('reads an edge click as an index into the edges it was given', () => {
    expect(chosen({ type: 'edge', id: 'edge_3' })).toEqual({ node: null, edge: 3 });
  });

  it('reports an unreadable edge id as no edge rather than edge zero', () => {
    expect(chosen({ type: 'edge', id: 'edge_' })).toEqual({ node: null, edge: null });
  });

  /**
   * Not a deselection: a reader who misses a small circle should not lose the
   * panel they were reading.
   */
  it('reports a click on the background as nothing at all', () => {
    expect(chosen(undefined)).toBeNull();
    expect(chosen({})).toBeNull();
  });
});
