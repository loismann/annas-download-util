import { ComponentFixture, TestBed } from '@angular/core/testing';
import { VocabularyPanelComponent, TermChange } from './vocabulary-panel.component';
import { Definition, TermState, VocabularyTerm } from '../reader2.models';

function definition(term: string, meaning = `what ${term} means`): Definition {
  return { term, meaning, norm: term.toLowerCase() };
}

function filed(term: string, state: TermState): VocabularyTerm {
  return {
    term, termNorm: term.toLowerCase(), state, definition: null,
    firstSeenBookId: null, updatedAtUtc: ''
  };
}

describe('VocabularyPanelComponent', () => {
  let fixture: ComponentFixture<VocabularyPanelComponent>;
  let component: VocabularyPanelComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [VocabularyPanelComponent] })
      .compileComponents();

    fixture = TestBed.createComponent(VocabularyPanelComponent);
    component = fixture.componentInstance;
  });

  function render(terms: Definition[] = [], extra: Record<string, unknown> = {}): HTMLElement {
    fixture.componentRef.setInput('terms', terms);
    for (const [name, value] of Object.entries(extra)) fixture.componentRef.setInput(name, value);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('lists each term with its meaning', () => {
    const host = render([definition('reification'), definition('praxis')]);

    expect(host.querySelectorAll('.found li').length).toBe(2);
    expect(host.querySelector('.meaning')?.textContent).toContain('what reification means');
  });

  /**
   * Nothing generates on its own. An empty passage list is a button and a
   * sentence, never a request.
   */
  it('says nothing has been found rather than generating on its own', () => {
    let generated = 0;
    component.generate.subscribe(() => { generated++; });

    expect(render([]).querySelector('.idle')).not.toBeNull();
    expect(generated).toBe(0);
  });

  it('offers no regenerate control until there is something to regenerate', () => {
    expect(render([]).querySelector('.regenerate')).toBeNull();
    expect(render([definition('praxis')]).querySelector('.regenerate')).not.toBeNull();
  });

  it('asks to regenerate with force, and to generate without it', () => {
    const forced: boolean[] = [];
    component.generate.subscribe((f: boolean) => { forced.push(f); });

    const host = render([definition('praxis')]);
    host.querySelector<HTMLButtonElement>('.controls button')!.click();
    host.querySelector<HTMLButtonElement>('.regenerate')!.click();

    expect(forced).toEqual([false, true]);
  });

  /** Filing carries the definition, so the reader's list is not left blank. */
  it('files a term with the meaning already on screen', () => {
    let change: TermChange | null = null;
    component.file.subscribe((c: TermChange) => { change = c; });

    render([definition('reification')])
      .querySelectorAll<HTMLButtonElement>('.actions button')[0].click();

    expect(change!).toEqual({
      term: 'reification', state: 'Known', definition: 'what reification means'
    });
  });

  it('files as studying from the second action', () => {
    let change: TermChange | null = null;
    component.file.subscribe((c: TermChange) => { change = c; });

    render([definition('praxis')])
      .querySelectorAll<HTMLButtonElement>('.actions button')[1].click();

    expect(change!.state).toBe('Studying');
    expect(change!.definition).toBe('what praxis means');
  });

  it('asks for a deep dive by term', () => {
    let asked = '';
    component.learnMore.subscribe((t: string) => { asked = t; });

    render([definition('praxis')])
      .querySelectorAll<HTMLButtonElement>('.actions button')[2].click();

    expect(asked).toBe('praxis');
  });

  it('renders the deep dive the server wrote', () => {
    const host = render([], { dive: { term: 'praxis', html: '<p>A <strong>practice</strong>.</p>' } });

    expect(host.querySelector('.dive h3')?.textContent).toContain('praxis');
    expect(host.querySelector('.dive-body strong')?.textContent).toBe('practice');
  });

  it('shows both filed lists with their counts', () => {
    const host = render([], {
      studying: [filed('praxis', 'Studying')],
      known: [filed('reification', 'Known'), filed('aporia', 'Known')]
    });

    const counts = Array.from(host.querySelectorAll('.filed .count'));
    expect(counts.map(c => c.textContent?.trim())).toEqual(['1', '2']);
  });

  it('clears the list the control belongs to, not the other one', () => {
    let cleared: TermState | null = null;
    component.clear.subscribe((s: TermState) => { cleared = s; });

    render([], { known: [filed('reification', 'Known')] })
      .querySelector<HTMLButtonElement>('.clear')!.click();

    expect(cleared!).toBe('Known');
  });
  /** One call per section, so it is named for the chapter it will bill for. */
  it('offers the whole chapter separately from this section', () => {
    const section: boolean[] = [];
    const chapter: boolean[] = [];
    component.generate.subscribe((f: boolean) => { section.push(f); });
    component.generateChapter.subscribe((f: boolean) => { chapter.push(f); });

    const buttons = render([]).querySelectorAll<HTMLButtonElement>('.controls button');
    buttons[0].click();
    buttons[1].click();

    expect(section).toEqual([false]);
    expect(chapter).toEqual([false]);
  });

  /** The definition was paid for when the word was found; a card is free. */
  it('makes a card from the definition already on screen', () => {
    let card: { term: string; definition: string } | null = null;
    component.makeCard.subscribe((c: { term: string; definition: string }) => { card = c; });

    render([definition('praxis')])
      .querySelectorAll<HTMLButtonElement>('.actions button')[3].click();

    expect(card!).toEqual({ term: 'praxis', definition: 'what praxis means' });
  });

  it('offers to forget this book\u2019s vocabulary without touching the reader\u2019s own words', () => {
    let forgotten = 0;
    component.forgetBook.subscribe(() => { forgotten++; });

    render([]).querySelector<HTMLButtonElement>('.forget-book')!.click();

    expect(forgotten).toBe(1);
  });
});
