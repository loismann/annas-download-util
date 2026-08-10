import { TestBed } from '@angular/core/testing';
import { ProsePipe } from './prose.pipe';

describe('ProsePipe', () => {
  let pipe: ProsePipe;

  beforeEach(() => {
    pipe = TestBed.runInInjectionContext(() => new ProsePipe());
  });

  it('renders the markdown the lenses ask for by name', () => {
    const html = pipe.transform('### Finn\n\n**Who is present:** Finn.\n\n- one\n- two');

    expect(html).toContain('<h3>Finn</h3>');
    expect(html).toContain('<strong>Who is present:</strong>');
    expect(html).toContain('<li>one</li>');
  });

  it('leaves nothing raw — the bug this pipe exists for', () => {
    expect(pipe.transform('**bold**')).not.toContain('**');
  });

  /** The text comes from a model reading an arbitrary EPUB. */
  it('strips scripts and event handlers rather than trusting the model', () => {
    const html = pipe.transform('hello <script>alert(1)</script> <img src=x onerror="alert(1)">');

    expect(html).not.toContain('<script');
    expect(html).not.toContain('onerror');
  });

  it('renders nothing for nothing', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform('')).toBe('');
  });
});
