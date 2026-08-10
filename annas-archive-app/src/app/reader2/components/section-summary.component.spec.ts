import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SectionSummaryComponent, SectionRequest } from './section-summary.component';
import { SectionInfo } from '../reader2.models';

const SECTIONS: SectionInfo[] = [
  { index: 0, startWord: 0, wordCount: 400 },
  { index: 1, startWord: 400, wordCount: 400 },
  { index: 2, startWord: 800, wordCount: 300 }
];

describe('SectionSummaryComponent', () => {
  let fixture: ComponentFixture<SectionSummaryComponent>;
  let component: SectionSummaryComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SectionSummaryComponent] })
      .compileComponents();

    fixture = TestBed.createComponent(SectionSummaryComponent);
    component = fixture.componentInstance;
    component.sections = SECTIONS;
  });

  function render(extra: Record<string, unknown> = {}): HTMLElement {
    fixture.componentRef.setInput('sections', component.sections);
    for (const [name, value] of Object.entries(extra)) fixture.componentRef.setInput(name, value);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('offers one control per section', () => {
    expect(render().querySelectorAll('.section').length).toBe(3);
  });

  /**
   * "Which of these am I in" is arithmetic the reader should not have to do.
   * The boundaries are half-open, so the first word of a section belongs to it
   * and the last word of the one before does not.
   */
  it('marks the section the reader is actually in', () => {
    expect(render({ wordOffset: 0 }).querySelectorAll('.section')[0].classList).toContain('here');
    expect(render({ wordOffset: 399 }).querySelectorAll('.section')[0].classList).toContain('here');
    expect(render({ wordOffset: 400 }).querySelectorAll('.section')[1].classList).toContain('here');
  });

  it('marks nothing when the offset falls past the last section', () => {
    const host = render({ wordOffset: 5000 });

    expect(host.querySelectorAll('.here').length).toBe(0);
  });

  it('shows no summary pane until a section is opened', () => {
    expect(render({ openIndex: -1 }).querySelector('.summary')).toBeNull();
    expect(render({ openIndex: 1 }).querySelector('.summary')).not.toBeNull();
  });

  it('opens a section without forcing, so a cached summary is free', () => {
    let request: SectionRequest | null = null;
    component.open.subscribe((r: SectionRequest) => { request = r; });

    render().querySelectorAll<HTMLButtonElement>('.section')[2].click();

    expect(request!).toEqual({ index: 2, force: false });
  });

  it('offers regeneration only once there is a summary to replace', () => {
    expect(render({ openIndex: 0, markdown: null }).querySelector('.regenerate')).toBeNull();
    expect(render({ openIndex: 0, markdown: 'a summary' }).querySelector('.regenerate')).not.toBeNull();
  });

  it('regenerates the open section, not the one the reader is standing in', () => {
    let request: SectionRequest | null = null;
    component.open.subscribe((r: SectionRequest) => { request = r; });

    render({ openIndex: 1, markdown: 'a summary', wordOffset: 900 })
      .querySelector<HTMLButtonElement>('.regenerate')!.click();

    expect(request!).toEqual({ index: 1, force: true });
  });

  it('hides the regenerate control while the reader is waiting', () => {
    expect(render({ openIndex: 0, markdown: 'a summary', busy: true }).querySelector('.regenerate'))
      .toBeNull();
  });

  it('says a chapter has no sections rather than rendering an empty strip', () => {
    component.sections = [];

    expect(render().querySelector('.empty')).not.toBeNull();
  });
  it('renders the section summary as formatted prose, not raw markdown', () => {
    const prose = render({ openIndex: 0, markdown: '**What happens:** things.' })
      .querySelector('.prose');

    expect(prose?.querySelector('strong')).not.toBeNull();
    expect(prose?.textContent).not.toContain('**');
  });

});
