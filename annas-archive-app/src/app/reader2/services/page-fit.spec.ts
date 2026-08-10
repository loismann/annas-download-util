import { MIN_PAGE_WORDS } from './pagination';
import { measuredFit } from './page-fit';

/**
 * Real layout in a real browser — the one thing the pure walker cannot check.
 * The claim under test is the user-visible one: a measured page neither runs
 * text past the bottom edge nor stops a line short of it.
 */
describe('measuredFit', () => {
  let surface: HTMLElement;
  let body: HTMLElement;
  const words = Array.from({ length: 2000 }, (_, i) => `word${i % 7}${'x'.repeat(i % 5)}`);

  beforeEach(() => {
    surface = document.createElement('article');
    surface.style.cssText =
      'position:fixed;left:0;top:0;width:400px;height:300px;overflow:hidden;'
      + 'padding:16px;box-sizing:border-box;font:16px/1.6 Georgia,serif;';

    const title = document.createElement('h2');
    title.textContent = 'Chapter One';

    body = document.createElement('p');
    body.className = 'body';
    body.style.margin = '0';

    surface.append(title, body);
    document.body.appendChild(surface);
  });

  afterEach(() => surface.remove());

  function overflows(count: number): boolean {
    body.textContent = words.slice(0, count).join(' ');
    return surface.scrollHeight > surface.clientHeight;
  }

  it('fills the page without running past the bottom edge', () => {
    const fitted = measuredFit(surface, () => words)(0);

    expect(overflows(fitted))
      .withContext('words past the edge are words the reader cannot read')
      .toBeFalse();
  });

  it('fills the page without leaving a line-sized gap at the bottom', () => {
    const fitted = measuredFit(surface, () => words)(0);

    expect(overflows(fitted + 1))
      .withContext('if one more word still fits, the page stopped short')
      .toBeTrue();
  });

  it('takes everything when the remainder fits on one page', () => {
    expect(measuredFit(surface, () => words.slice(0, 30))(0)).toBe(30);
  });

  it('holds the floor rather than paging by nothing in a collapsed container', () => {
    surface.style.height = '10px';

    expect(measuredFit(surface, () => words)(0)).toBeGreaterThanOrEqual(MIN_PAGE_WORDS);
  });

  it('leaves no probe behind in the document', () => {
    measuredFit(surface, () => words)(0);

    expect(document.querySelectorAll('article').length).toBe(1);
  });
});
