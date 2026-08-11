import { MIN_PAGE_WORDS } from './pagination';
import { measuredFit } from './page-fit';
import { paragraphStarts, paragraphsOf } from './paragraphs';

/**
 * Real layout in a real browser — the one thing the pure walker cannot check.
 * The claim under test is the user-visible one: a measured page neither runs
 * text past the bottom edge nor stops a line short of it.
 */
describe('measuredFit', () => {
  let surface: HTMLElement;
  const words = Array.from({ length: 2000 }, (_, i) => `word${i % 7}${'x'.repeat(i % 5)}`);

  /** How the surface is asked to draw a range. One paragraph, unless said. */
  let page: (start: number, count: number) => string[];

  beforeEach(() => {
    surface = document.createElement('article');
    surface.style.cssText =
      'position:fixed;left:0;top:0;width:400px;height:300px;overflow:hidden;'
      + 'padding:16px;box-sizing:border-box;font:16px/1.6 Georgia,serif;';

    const title = document.createElement('h2');
    title.textContent = 'Chapter One';

    surface.append(title, paragraph(''));
    document.body.appendChild(surface);

    page = (start, count) => [words.slice(start, start + count).join(' ')];
  });

  afterEach(() => surface.remove());

  function paragraph(text: string): HTMLElement {
    const element = document.createElement('p');
    element.className = 'body';
    element.style.margin = '0 0 14px';
    element.textContent = text;

    return element;
  }

  /** Draws a range the way the surface would, and reports whether it spills. */
  function overflows(count: number): boolean {
    for (const old of Array.from(surface.querySelectorAll('.body'))) old.remove();
    for (const text of page(0, count)) surface.appendChild(paragraph(text));

    return surface.scrollHeight > surface.clientHeight;
  }

  function fit(): number {
    return measuredFit(surface, () => words.length, page)(0);
  }

  /**
   * Makes the surface draw the same words in paragraphs of `size` instead of one
   * block. Big enough that a page still holds several — one-word paragraphs
   * overflow any page and drive the answer to the floor, where a measurement is
   * no longer being tested.
   */
  function inParagraphsOf(size: number): void {
    const text = words.map((word, at) => (at > 0 && at % size === 0 ? `\n\n${word}` : word)).join(' ');
    const starts = paragraphStarts(text);

    page = (start, count) => paragraphsOf(words, starts, start, start + count);
  }

  it('fills the page without running past the bottom edge', () => {
    expect(overflows(fit()))
      .withContext('words past the edge are words the reader cannot read')
      .toBeFalse();
  });

  it('fills the page without leaving a line-sized gap at the bottom', () => {
    expect(overflows(fit() + 1))
      .withContext('if one more word still fits, the page stopped short')
      .toBeTrue();
  });

  it('takes everything when the remainder fits on one page', () => {
    expect(measuredFit(surface, () => 30, page)(0)).toBe(30);
  });

  it('holds the floor rather than paging by nothing in a collapsed container', () => {
    surface.style.height = '10px';

    expect(fit()).toBeGreaterThanOrEqual(MIN_PAGE_WORDS);
  });

  it('leaves no probe behind in the document', () => {
    fit();

    expect(document.querySelectorAll('article').length).toBe(1);
  });

  /**
   * <b>The reason the fit takes paragraphs rather than a string.</b> Every
   * paragraph after the first costs a gap, and a probe that laid one block where
   * the reader sees several would report a page several gaps too tall — which is
   * exactly that much prose running off the bottom of every page in the book.
   */
  it('leaves room for the gaps between paragraphs', () => {
    // Tall enough that the answer is a measurement rather than the floor, and
    // that a page holds enough paragraphs for their gaps to add up to something.
    surface.style.height = '600px';

    const asOneBlock = fit();

    inParagraphsOf(8);

    expect(fit())
      .withContext('the same words, minus the room the gaps between them take')
      .toBeLessThan(asOneBlock);
  });

  /**
   * The probe clones a real paragraph rather than building a fresh one, because
   * that is what carries Angular's scoping attribute — and with it the styles
   * that put the gap there in the first place.
   */
  it('measures against the surface’s own paragraph styling', () => {
    surface.style.height = '600px';
    inParagraphsOf(8);

    const styled = surface.querySelector<HTMLElement>('.body')!;

    styled.style.margin = '0';
    const tight = fit();

    styled.style.margin = '0 0 40px';

    expect(fit())
      .withContext('the same prose in the same box, differing only by the gap between paragraphs')
      .toBeLessThan(tight);
  });
});
