import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AppearanceControlsComponent } from './appearance-controls.component';
import { DEFAULT_PREFERENCES, ReadingPreferences } from '../reader2.models';

const DEFAULTS: ReadingPreferences = DEFAULT_PREFERENCES;

describe('AppearanceControlsComponent', () => {
  let fixture: ComponentFixture<AppearanceControlsComponent>;
  let component: AppearanceControlsComponent;
  let emitted: ReadingPreferences | null;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AppearanceControlsComponent] })
      .compileComponents();

    fixture = TestBed.createComponent(AppearanceControlsComponent);
    component = fixture.componentInstance;
    emitted = null;
    component.change.subscribe((p: ReadingPreferences) => { emitted = p; });
  });

  function render(preferences: Partial<ReadingPreferences> = {}): HTMLElement {
    fixture.componentRef.setInput('preferences', { ...DEFAULTS, ...preferences });
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function rows(host: HTMLElement): HTMLElement[] {
    return Array.from(host.querySelectorAll('.row'));
  }

  /**
   * The whole set, every time. Emitting one field would let a caller persist a
   * partial record and lose the others.
   */
  it('emits a complete set of preferences, not the field that changed', () => {
    const host = render({ fontSize: 20, theme: 'sepia' });

    rows(host)[0].querySelectorAll('button')[1].click();

    expect(emitted).toEqual({ fontFamily: 'sans', fontSize: 20, theme: 'sepia', splitRatio: 0.6 });
  });

  it('steps the size by one in each direction', () => {
    const host = render({ fontSize: 18 });
    const [smaller, larger] = Array.from(rows(host)[1].querySelectorAll('button'));

    larger.click();
    expect(emitted!.fontSize).toBe(19);

    smaller.click();
    expect(emitted!.fontSize).toBe(17);
  });

  /**
   * The server rejects anything outside 8–48. Disabling at the edge is what
   * keeps a reader from being told off for pressing a button we offered them.
   */
  it('stops at the size the server would refuse, in both directions', () => {
    const smallest = rows(render({ fontSize: 8 }))[1].querySelectorAll('button')[0];
    expect(smallest.disabled).toBeTrue();

    const largest = rows(render({ fontSize: 48 }))[1].querySelectorAll('button')[1];
    expect(largest.disabled).toBeTrue();
  });

  it('marks the current choices for assistive technology, not just visually', () => {
    const host = render({ fontFamily: 'mono', theme: 'dark' });

    const fonts = Array.from(rows(host)[0].querySelectorAll('button'));
    expect(fonts[2].getAttribute('aria-pressed')).toBe('true');
    expect(fonts[0].getAttribute('aria-pressed')).toBe('false');

    const dark = host.querySelector('.theme[data-theme="dark"]')!;
    expect(dark.getAttribute('aria-pressed')).toBe('true');
    expect(dark.classList).toContain('selected');
  });

  it('leaves the split ratio alone — it is dragged, not chosen here', () => {
    rows(render({ splitRatio: 0.42 }))[0].querySelectorAll('button')[1].click();

    expect(emitted!.splitRatio).toBe(0.42);
  });
});
