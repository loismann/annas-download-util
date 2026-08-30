import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ReaderToolsComponent } from './reader-tools.component';

/**
 * Only the fullscreen control is covered here, and it earns a spec for one
 * reason: in immersive mode it is the way back, and on a tablet it may be the
 * only way back. There is no Esc key, and if the browser refused the fullscreen
 * request the page is filling the window without the browser knowing it, so no
 * native exit control appears either. A button that does not say it will undo
 * what it did strands the reader in a view with no toolbar and no menu.
 */
describe('ReaderToolsComponent — the fullscreen control', () => {
  let fixture: ComponentFixture<ReaderToolsComponent>;

  const button = (): HTMLButtonElement => {
    const match = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>
    ).find(b => /fullscreen/i.test(b.title));

    if (!match) throw new Error('the reading toolbar has no fullscreen control');
    return match;
  };

  /** The ligature, exactly — `fullscreen_exit` contains `fullscreen`, so a
   *  substring assertion here would pass whichever icon was drawn. */
  const icon = (): string => (button().querySelector('mat-icon')?.textContent ?? '').trim();

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ReaderToolsComponent],
      providers: [provideNoopAnimations()]
    }).compileComponents();

    fixture = TestBed.createComponent(ReaderToolsComponent);
    fixture.detectChanges();
  });

  it('offers fullscreen when the reader is not in it', () => {
    expect(button().title).toBe('Fullscreen');
    expect(icon()).toBe('fullscreen');
    expect(button().getAttribute('aria-pressed')).toBe('false');
  });

  it('offers the way out once the reader is in it', () => {
    fixture.componentRef.setInput('immersive', true);
    fixture.detectChanges();

    expect(button().title).toBe('Leave fullscreen');
    expect(icon()).toBe('fullscreen_exit');
    expect(button().getAttribute('aria-pressed')).toBe('true');
  });

  it('asks once per press, and leaves deciding which way to the reader', () => {
    let asked = 0;
    fixture.componentInstance.toggleFullscreen.subscribe(() => asked++);

    button().click();
    fixture.componentRef.setInput('immersive', true);
    fixture.detectChanges();
    button().click();

    expect(asked).toBe(2);
  });
});
