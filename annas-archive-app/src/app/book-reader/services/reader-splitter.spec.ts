import { ReaderSplitter } from './reader-splitter';

/**
 * The clamp is the part worth pinning. Dragging the divider to the far edge
 * used to be reachable only through a real pointer over a real laid-out
 * element, so "can either pane be collapsed to nothing" had never been asked.
 */
describe('ReaderSplitter', () => {
  const bounds = { left: 100, width: 1000 };
  let splitter: ReaderSplitter;

  beforeEach(() => {
    splitter = new ReaderSplitter();
    splitter.start(new MouseEvent('mousedown'));
  });

  it('should start with the panes even', () => {
    const fresh = new ReaderSplitter();
    expect(fresh.leftFlex).toBe('1 1 0');
    expect(fresh.rightFlex).toBe('1 1 0');
  });

  it('should split where the pointer is', () => {
    splitter.dragTo(400, bounds); // 30% across

    expect(splitter.leftFlex).toBe('0.3 1 0');
    expect(splitter.rightFlex).toBe('0.7 1 0');
  });

  it('should always leave the two flex values summing to one', () => {
    for (const x of [100, 250, 600, 900, 1100]) {
      splitter.dragTo(x, bounds);
      const left = parseFloat(splitter.leftFlex);
      const right = parseFloat(splitter.rightFlex);
      expect(left + right).toBeCloseTo(1, 10);
    }
  });

  // ─── The clamp ───────────────────────────────────────────────────────

  it('should not collapse the reading pane past the minimum', () => {
    splitter.dragTo(-500, bounds);

    expect(parseFloat(splitter.leftFlex)).toBeCloseTo(0.2, 10);
  });

  it('should not collapse the analysis pane past the minimum', () => {
    splitter.dragTo(5000, bounds);

    expect(parseFloat(splitter.rightFlex)).toBeCloseTo(0.2, 10);
  });

  // ─── When it reports movement ────────────────────────────────────────

  it('should report movement so the caller repaginates', () => {
    expect(splitter.dragTo(400, bounds)).toBe(true);
  });

  it('should not report movement when the pointer has not moved', () => {
    splitter.dragTo(400, bounds);

    expect(splitter.dragTo(400, bounds)).toBe(false);
  });

  it('should not report movement once past the clamp', () => {
    // Repagination is expensive; dragging further into the wall must not keep
    // triggering it.
    splitter.dragTo(-500, bounds);

    expect(splitter.dragTo(-900, bounds)).toBe(false);
  });

  it('should ignore a pointer move when no drag is in progress', () => {
    const idle = new ReaderSplitter();

    expect(idle.dragTo(400, bounds)).toBe(false);
    expect(idle.leftFlex).toBe('1 1 0');
  });

  it('should ignore a drag with no measurable container', () => {
    // The panes are hidden until a chapter loads, so a bounds of zero width is
    // reachable and would divide by zero.
    expect(splitter.dragTo(400, { left: 0, width: 0 })).toBe(false);
    expect(splitter.dragTo(400, null)).toBe(false);
  });

  // ─── Ending ──────────────────────────────────────────────────────────

  it('should report that a drag ended so the caller repaginates once', () => {
    expect(splitter.end()).toBe(true);
  });

  it('should not report an end when nothing was being dragged', () => {
    // mouseup is bound to the window, so every click on the page arrives here.
    const idle = new ReaderSplitter();

    expect(idle.end()).toBe(false);
  });

  it('should stop responding to pointer moves after the drag ends', () => {
    splitter.end();

    expect(splitter.dragTo(700, bounds)).toBe(false);
  });

  it('should prevent the browser treating the drag as a selection or scroll', () => {
    const event = new MouseEvent('mousedown', { cancelable: true });

    new ReaderSplitter().start(event);

    expect(event.defaultPrevented).toBe(true);
  });
});
