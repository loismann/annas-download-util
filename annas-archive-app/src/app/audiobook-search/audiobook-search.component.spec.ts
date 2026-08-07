import { TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { AudiobookResultEntry, AudiobookSearchComponent } from './audiobook-search.component';
import {
  AudiobookRequestApiService,
  AudiobookSearchResult
} from '../services/audiobook-request-api.service';
import { LoggerService } from '../services/logger.service';

function edition(asin: string, title: string, narrator: string): AudiobookSearchResult {
  return {
    asin,
    title,
    authors: ['Jane Austen'],
    narrators: [narrator],
    genres: [],
    series: [],
    availability: 'available',
    requestTracked: false
  };
}

describe('AudiobookSearchComponent', () => {
  const api = jasmine.createSpyObj<AudiobookRequestApiService>('AudiobookRequestApiService', [
    'getStatus', 'search', 'discover', 'previewRequest', 'confirmRequest', 'getRequestStatus'
  ]);
  const logger = jasmine.createSpyObj<LoggerService>('LoggerService', ['log', 'debug', 'warn', 'error']);
  // Stubbed rather than spied after the fact: MatDialog comes from the
  // component's own imported MatDialogModule, so the instance the component
  // holds is not necessarily the one TestBed.inject would return.
  const dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);

  beforeEach(async () => {
    api.getStatus.and.returnValue(of({
      enabled: true,
      configured: true,
      reachable: true,
      ready: true,
      databaseConnected: true,
      migrationsCurrent: true,
      rootFolderCount: 1,
      qualityProfileCount: 1,
      enabledIndexerCount: 2,
      enabledDownloadClientCount: 1,
      libraryItemCount: 0,
      readOnlyGatePassed: true,
      gateFailures: []
    }));
    api.search.and.returnValue(of({
      query: 'Pride',
      region: 'us',
      totalResults: 1,
      results: [edition('ASIN1', 'Pride and Prejudice', 'Rosamund Pike')]
    }));
    api.search.calls.reset();
    api.discover.calls.reset();
    api.previewRequest.calls.reset();
    api.confirmRequest.calls.reset();
    dialog.open.calls.reset();

    await TestBed.configureTestingModule({
      imports: [AudiobookSearchComponent, NoopAnimationsModule],
      providers: [
        provideRouter([]),
        { provide: AudiobookRequestApiService, useValue: api },
        { provide: LoggerService, useValue: logger }
      ]
    })
      // The component imports MatDialogModule itself, so its own injector
      // wins over the testing module — override it at the component level.
      .overrideComponent(AudiobookSearchComponent, {
        add: { providers: [{ provide: MatDialog, useValue: dialog }] }
      })
      .compileComponents();
  });

  it('loads status and renders distinct edition metadata', () => {
    const fixture = TestBed.createComponent(AudiobookSearchComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    component.searchTerm = 'Pride';
    component.search();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent || '';
    expect(text).toContain('Pride and Prejudice');
    expect(text).toContain('Rosamund Pike');
    expect(text).toContain('Available');
  });

  it('does not search while the read-only gate is unavailable', () => {
    const fixture = TestBed.createComponent(AudiobookSearchComponent);
    const component = fixture.componentInstance;
    component.status = null;
    component.statusLoading = false;
    component.searchTerm = 'Pride';
    api.search.calls.reset();

    component.search();

    expect(api.search).not.toHaveBeenCalled();
  });

  it('routes the AI mode to discovery instead of catalog search', () => {
    api.discover.and.returnValue(of({
      region: 'us',
      summary: 'Regency comedies of manners.',
      resolvedCount: 1,
      ambiguousCount: 0,
      notFoundCount: 0,
      ownedCount: 0,
      results: [{
        resolution: 'resolved' as const,
        suggestedTitle: 'Emma',
        suggestedAuthor: 'Jane Austen',
        reason: 'A sharper comedy of manners.',
        match: edition('ASIN2', 'Emma', 'Emma Thompson'),
        choices: []
      }]
    }));

    const fixture = TestBed.createComponent(AudiobookSearchComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    component.toggleAiSearch();
    component.aiSearchQuery = 'Regency comedies of manners';
    component.search();
    fixture.detectChanges();

    expect(api.discover).toHaveBeenCalled();
    expect(api.search).not.toHaveBeenCalled();
    const text = (fixture.nativeElement as HTMLElement).textContent || '';
    expect(text).toContain('Emma');
    expect(text).toContain('A sharper comedy of manners.');
  });

  it('keeps ambiguous and not-found suggestions unrequestable', () => {
    api.discover.and.returnValue(of({
      region: 'us',
      resolvedCount: 0,
      ambiguousCount: 1,
      notFoundCount: 1,
      ownedCount: 0,
      results: [
        {
          resolution: 'ambiguous' as const,
          suggestedTitle: 'The Fellowship of the Ring',
          choices: [
            edition('ASIN3', 'The Fellowship of the Ring', 'Andy Serkis'),
            edition('ASIN4', 'The Fellowship of the Ring', 'Rob Inglis')
          ]
        },
        {
          resolution: 'notFound' as const,
          suggestedTitle: 'The Invented Chronicles',
          choices: []
        }
      ]
    }));

    const fixture = TestBed.createComponent(AudiobookSearchComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    component.toggleAiSearch();
    component.aiSearchQuery = 'epic fantasy';
    component.search();
    fixture.detectChanges();

    expect(component.entries.every(entry => entry.result === null)).toBeTrue();
    const element = fixture.nativeElement as HTMLElement;
    const labels = Array.from(element.querySelectorAll('button')).map(button => button.textContent || '');
    expect(labels.some(label => label.includes('Request'))).toBeFalse();
    expect(labels.some(label => label.includes('Choose edition'))).toBeTrue();
    expect(element.textContent).toContain('Not in catalog');
  });

  it('promotes a chosen edition to a requestable card', () => {
    const fixture = TestBed.createComponent(AudiobookSearchComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();

    const chosen = edition('ASIN3', 'The Fellowship of the Ring', 'Andy Serkis');
    const entry: AudiobookResultEntry = {
      key: 'ai-0',
      resolution: 'ambiguous',
      suggestedTitle: 'The Fellowship of the Ring',
      result: null,
      choices: [chosen]
    };
    dialog.open.and.returnValue({ afterClosed: () => of(chosen) } as MatDialogRef<unknown>);

    component.chooseEdition(entry);

    expect(entry.result).toEqual(entry.choices[0]);
    expect(entry.resolution).toBe('resolved');
  });

  it('completes an auto-search request without a confirmation dialog', () => {
    api.previewRequest.and.returnValue(of({
      previewToken: 'A'.repeat(64),
      expiresAt: new Date().toISOString(),
      asin: 'ASIN1',
      title: 'Pride and Prejudice',
      authors: ['Jane Austen'],
      narrators: ['Rosamund Pike'],
      abridged: false,
      qualityProfile: 'AAC M4B',
      autoSearch: true,
      autoSearchReason: 'Listenarr will search and download the best matching release.',
      alreadyRequested: false,
      releasesAvailable: true
    }));
    api.confirmRequest.and.returnValue(of({
      listenarrId: 42,
      asin: 'ASIN1',
      title: 'Pride and Prejudice',
      status: 'Searching',
      alreadyExisted: false,
      requesterAdded: true
    }));
    api.getRequestStatus.and.returnValue(of({
      listenarrId: 42,
      asin: 'ASIN1',
      title: 'Pride and Prejudice',
      state: 'Searching' as const,
      progress: 0,
      importBlockMessages: [],
      canCancel: false,
      canRetryImport: false,
      updatedAt: new Date().toISOString()
    }));

    const fixture = TestBed.createComponent(AudiobookSearchComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    component.searchTerm = 'Pride';
    component.search();

    component.requestBook(component.entries[0]);

    expect(api.confirmRequest).toHaveBeenCalledWith('A'.repeat(64));
    expect(component.entries[0].result!.listenarrId).toBe(42);
    expect(component.entries[0].result!.availability).toBe('requested');
    component.ngOnDestroy();
  });
});
