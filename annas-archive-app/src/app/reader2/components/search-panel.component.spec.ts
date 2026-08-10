import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SearchPanelComponent, HitTarget } from './search-panel.component';
import { SearchHit } from '../reader2.models';

function hit(chapterId: number, extra: Partial<SearchHit> = {}): SearchHit {
  return {
    chapterId, chapterTitle: `Chapter ${chapterId + 1}`, matchCount: 3,
    snippet: '…the passage…', firstWordOffset: 120, ...extra
  };
}

describe('SearchPanelComponent', () => {
  let fixture: ComponentFixture<SearchPanelComponent>;
  let component: SearchPanelComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SearchPanelComponent] }).compileComponents();

    fixture = TestBed.createComponent(SearchPanelComponent);
    component = fixture.componentInstance;
  });

  function render(hits: SearchHit[] = [], extra: Record<string, unknown> = {}): HTMLElement {
    fixture.componentRef.setInput('hits', hits);
    for (const [name, value] of Object.entries(extra)) fixture.componentRef.setInput(name, value);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  function type(host: HTMLElement, query: string): void {
    const input = host.querySelector<HTMLInputElement>('input')!;
    input.value = query;
    input.dispatchEvent(new Event('input'));
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
  }

  /**
   * Searching happens on submit, never per keystroke. Reader I fired on every
   * key against an endpoint that could also summarise, so typing cost money.
   */
  it('does not search while the reader is typing', () => {
    let searches = 0;
    component.search.subscribe(() => { searches++; });

    type(render(), 'reification');

    expect(searches).toBe(0);
  });

  it('searches when the form is submitted', () => {
    let query = '';
    component.search.subscribe((q: string) => { query = q; });

    const host = render();
    type(host, '  reification  ');
    host.querySelector('form')!.dispatchEvent(new Event('submit'));

    expect(query).toBe('reification', 'trimmed, because the server counts characters');
  });

  it('refuses a query below the server’s minimum rather than being told off for it', () => {
    let searches = 0;
    component.search.subscribe(() => { searches++; });

    const host = render();
    type(host, 'ab');

    expect(host.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBeTrue();
    expect(host.querySelector('.hint')?.textContent).toContain('At least 3');

    host.querySelector('form')!.dispatchEvent(new Event('submit'));
    expect(searches).toBe(0);
  });

  it('lists each hit with its chapter and match count', () => {
    const host = render([hit(0), hit(4, { matchCount: 9 })]);
    const rows = Array.from(host.querySelectorAll('.hits li'));

    expect(rows.length).toBe(2);
    expect(rows[1].querySelector('.count')?.textContent?.trim()).toBe('9');
    expect(rows[0].querySelector('.snippet')?.textContent).toContain('the passage');
  });

  /** A hit is a place, so it carries both halves of one — chapter and offset. */
  it('jumps to the chapter and the word offset together', () => {
    let target: HitTarget | null = null;
    component.jump.subscribe((t: HitTarget) => { target = t; });

    render([hit(4, { firstWordOffset: 730 })])
      .querySelector<HTMLButtonElement>('.hits button')!.click();

    expect(target!).toEqual({ chapter: 4, wordOffset: 730 });
  });

  it('says nothing was found only after something was actually searched for', () => {
    expect(render([]).querySelector('.empty')).toBeNull();
    expect(render([], { searched: 'reification' }).querySelector('.empty')?.textContent)
      .toContain('reification');
  });
});
