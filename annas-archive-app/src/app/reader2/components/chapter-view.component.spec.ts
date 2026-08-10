import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ChapterViewComponent } from './chapter-view.component';

/**
 * The reading surface, including the half of it a mouse never touches.
 *
 * <p>A reader who pages with the keyboard is not an edge case — it is how you
 * read a long book without your hand on the trackpad — and it is the half that
 * silently rots, because every manual test is done with a mouse.</p>
 */
describe('ChapterViewComponent', () => {
  let fixture: ComponentFixture<ChapterViewComponent>;
  let component: ChapterViewComponent;
  let forward: number;
  let back: number;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ChapterViewComponent] }).compileComponents();

    fixture = TestBed.createComponent(ChapterViewComponent);
    component = fixture.componentInstance;

    forward = 0;
    back = 0;
    component.forward.subscribe(() => { forward++; });
    component.back.subscribe(() => { back++; });

    component.title = 'Opening';
    component.text = 'one two three';
    component.canBack = true;
    component.canForward = true;
    fixture.detectChanges();
  });

  function surface(): HTMLElement {
    return fixture.nativeElement.querySelector('.surface');
  }

  function press(key: string): void {
    surface().dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    fixture.detectChanges();
  }

  it('is focusable, so a reader can reach it with the keyboard at all', () => {
    expect(surface().getAttribute('tabindex')).toBe('0');
  });

  it('names itself for a screen reader with the chapter it is showing', () => {
    expect(surface().getAttribute('aria-label')).toBe('Opening');
  });

  it('pages forward on the right arrow and page down', () => {
    press('ArrowRight');
    press('PageDown');

    expect(forward).toBe(2);
    expect(back).toBe(0);
  });

  it('pages back on the left arrow and page up', () => {
    press('ArrowLeft');
    press('PageUp');

    expect(back).toBe(2);
    expect(forward).toBe(0);
  });

  it('ignores keys that mean nothing here', () => {
    press('Enter');
    press('a');

    expect(forward + back).toBe(0);
  });

  /** The page number is read out as it changes, not only drawn. */
  it('announces the position politely rather than interrupting', () => {
    const position = fixture.nativeElement.querySelector('.position');

    expect(position.getAttribute('aria-live')).toBe('polite');
    expect(position.textContent).toContain('Page 1 of 1');
  });

  it('labels the pager buttons, which are glyphs a screen reader cannot read', () => {
    const buttons = fixture.nativeElement.querySelectorAll('.pager button');

    expect(buttons[0].getAttribute('aria-label')).toBe('Previous page');
    expect(buttons[1].getAttribute('aria-label')).toBe('Next page');
  });

  it('disables the pager at each end of the book', () => {
    fixture.componentRef.setInput('canBack', false);
    fixture.componentRef.setInput('canForward', false);
    fixture.detectChanges();

    const buttons = fixture.nativeElement.querySelectorAll('.pager button');
    expect(buttons[0].disabled).toBeTrue();
    expect(buttons[1].disabled).toBeTrue();
  });

  it('applies the reader’s type and theme to the surface itself', () => {
    fixture.componentRef.setInput(
      'preferences', { fontFamily: 'mono', fontSize: 22, theme: 'dark', splitRatio: 0.6 });
    fixture.detectChanges();

    expect(surface().style.fontSize).toBe('22px');
    expect(surface().style.fontFamily).toContain('Menlo');
    expect(surface().classList).toContain('theme-dark');
  });

  /** An empty selection is not a passage; asking about one would spend nothing usefully. */
  it('reports nothing when the reader has selected nothing', () => {
    let selections = 0;
    component.selected.subscribe(() => { selections++; });

    window.getSelection()?.removeAllRanges();
    component.reportSelection();

    expect(selections).toBe(0);
  });
});
