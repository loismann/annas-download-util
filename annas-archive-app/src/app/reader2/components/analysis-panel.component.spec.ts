import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AnalysisPanelComponent } from './analysis-panel.component';
import { PassageSelection } from '../reader2.models';

const SELECTION: PassageSelection = { text: 'the owl of Minerva', wordOffset: 420 };

describe('AnalysisPanelComponent', () => {
  let fixture: ComponentFixture<AnalysisPanelComponent>;
  let component: AnalysisPanelComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AnalysisPanelComponent] }).compileComponents();

    fixture = TestBed.createComponent(AnalysisPanelComponent);
    component = fixture.componentInstance;
  });

  function render(inputs: Record<string, unknown> = {}): HTMLElement {
    for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  // ─── a selection is an offer, not a purchase ────────────────────────

  /**
   * The rule the whole reader is built on. Reader I sent the passage the moment
   * the mouse came up, so a stray drag while reading was a billed request.
   */
  it('generates nothing merely because the reader highlighted something', () => {
    let generations = 0;
    component.analyseSelection.subscribe(() => { generations++; });
    component.generate.subscribe(() => { generations++; });

    render({ selection: SELECTION });

    expect(generations).toBe(0);
  });

  it('shows what was highlighted, so the reader can see what they would pay for', () => {
    expect(render({ selection: SELECTION }).querySelector('.quoted')?.textContent)
      .toContain('the owl of Minerva');
  });

  it('offers no selection controls when nothing is highlighted', () => {
    expect(render({ selection: null }).querySelector('.selection')).toBeNull();
  });

  it('asks for an explanation only when the named button is pressed', () => {
    let asked: PassageSelection | null = null;
    component.analyseSelection.subscribe((s: PassageSelection) => { asked = s; });

    render({ selection: SELECTION })
      .querySelectorAll<HTMLButtonElement>('.selection-actions button')[0].click();

    expect(asked!).toEqual(SELECTION);
  });

  /** Filing a word costs nothing, so it stays available while something streams. */
  it('offers filing as a separate, free choice', () => {
    let filed: PassageSelection | null = null;
    component.fileSelection.subscribe((s: PassageSelection) => { filed = s; });

    const buttons = render({ selection: SELECTION, busy: { what: 'Working', step: null } })
      .querySelectorAll<HTMLButtonElement>('.selection-actions button');

    expect(buttons[0].disabled).toBeTrue();  // explaining bills, so it waits
    expect(buttons[1].disabled).toBeFalse();  // filing does not bill

    buttons[1].click();
    expect(filed!).toEqual(SELECTION);
  });

  it('lets the reader take neither choice', () => {
    let dismissed = 0;
    component.dismissSelection.subscribe(() => { dismissed++; });

    render({ selection: SELECTION }).querySelector<HTMLButtonElement>('.dismiss')!.click();

    expect(dismissed).toBe(1);
  });

  // ─── the generating controls ────────────────────────────────────────

  it('keeps Reader I’s button name, which readers know it by', () => {
    expect(render().textContent).toContain("I'm a Dummy");
  });

  /** The lenses ask for bold headings by name; raw asterisks are the bug. */
  it('renders the summary as formatted prose, not raw markdown', () => {
    const prose = render({ markdown: '### Finn\n\n**Who is present:** Finn.' })
      .querySelector('.prose');

    expect(prose?.querySelector('h3')?.textContent).toBe('Finn');
    expect(prose?.querySelector('strong')).not.toBeNull();
    expect(prose?.textContent).not.toContain('**');
  });

  it('asks to generate without forcing, and to regenerate with it', () => {
    const kinds: string[] = [];
    const forced: string[] = [];
    component.generate.subscribe((k: string) => { kinds.push(k); });
    component.regenerate.subscribe((k: string) => { forced.push(k); });

    const host = render({ markdown: 'a summary', kind: 'summary' });
    host.querySelectorAll<HTMLButtonElement>('.controls button')[0].click();
    host.querySelector<HTMLButtonElement>('.regenerate')!.click();

    expect(kinds).toEqual(['summary']);
    expect(forced).toEqual(['summary']);
  });

  it('offers no regenerate control until there is something to replace', () => {
    expect(render({ markdown: null }).querySelector('.regenerate')).toBeNull();
    expect(render({ markdown: 'a summary' }).querySelector('.regenerate')).not.toBeNull();
  });

  it('hides the regenerate control while something is streaming', () => {
    expect(render({ markdown: 'a summary', busy: { what: 'Summarising', step: null } })
      .querySelector('.regenerate')).toBeNull();
  });

  it('reports progress with its step count while a stream runs', () => {
    const host = render({
      busy: { what: 'Summarising', step: { stage: 'chunk', stepNumber: 2, totalSteps: 4, message: 'Chunk 2' } }
    });

    expect(host.querySelector('.busy')?.textContent).toContain('Summarising');
    expect(host.querySelector('.step')?.textContent).toContain('2/4');
  });

  it('announces the output politely rather than interrupting a screen reader', () => {
    expect(render().querySelector('.output')?.getAttribute('aria-live')).toBe('polite');
  });

  it('shows the server’s sentence when something failed', () => {
    expect(render({ error: 'You have used your allowance.' }).querySelector('.failed')?.textContent)
      .toContain('allowance');
  });
});
