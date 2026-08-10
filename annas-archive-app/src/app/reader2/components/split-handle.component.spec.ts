import { MAX_SPLIT, MIN_SPLIT, ratioFrom } from './split-handle.component';

/**
 * The arithmetic behind the divider, tested without a browser.
 *
 * <p>The failure that matters is a pane collapsing to nothing: once the analysis
 * pane is zero wide there is no handle left to grab, and the reader cannot get
 * it back without clearing their preferences. Clamping is the whole feature.</p>
 */
describe('ratioFrom', () => {
  it('is the fraction of the container the pointer sits at', () => {
    expect(ratioFrom(500, 0, 1000)).toBe(0.5);
    expect(ratioFrom(700, 200, 1000)).toBe(0.5);
  });

  it('never lets either pane collapse, however far the pointer goes', () => {
    expect(ratioFrom(-9999, 0, 1000)).toBe(MIN_SPLIT);
    expect(ratioFrom(9999, 0, 1000)).toBe(MAX_SPLIT);
  });

  it('clamps exactly at the boundaries rather than one pixel inside them', () => {
    expect(ratioFrom(MIN_SPLIT * 1000, 0, 1000)).toBe(MIN_SPLIT);
    expect(ratioFrom(MAX_SPLIT * 1000, 0, 1000)).toBe(MAX_SPLIT);
  });

  /** A container measured before layout has no width; dividing by it is NaN. */
  it('returns the minimum rather than NaN for an unmeasured container', () => {
    expect(ratioFrom(500, 0, 0)).toBe(MIN_SPLIT);
    expect(ratioFrom(500, 0, -10)).toBe(MIN_SPLIT);
  });

  it('honours bounds a caller supplies instead of the defaults', () => {
    expect(ratioFrom(0, 0, 1000, 0.4, 0.6)).toBe(0.4);
    expect(ratioFrom(1000, 0, 1000, 0.4, 0.6)).toBe(0.6);
  });
});
