import { TestBed } from '@angular/core/testing';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AudiobookSearchComponent } from './audiobook-search.component';
import { AudiobookRequestApiService } from '../services/audiobook-request-api.service';
import { LoggerService } from '../services/logger.service';

describe('AudiobookSearchComponent', () => {
  const api = jasmine.createSpyObj<AudiobookRequestApiService>('AudiobookRequestApiService', ['getStatus', 'search']);
  const logger = jasmine.createSpyObj<LoggerService>('LoggerService', ['log', 'debug', 'warn', 'error']);

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
    api.search.and.returnValue(of({ query: 'Pride', region: 'us', totalResults: 1, results: [{
      asin: 'ASIN1', title: 'Pride and Prejudice', authors: ['Jane Austen'], narrators: ['Rosamund Pike'],
      genres: [], series: [], availability: 'available', requestTracked: false
    }] }));

    await TestBed.configureTestingModule({
      imports: [AudiobookSearchComponent, NoopAnimationsModule],
      providers: [
        provideRouter([]),
        { provide: AudiobookRequestApiService, useValue: api },
        { provide: LoggerService, useValue: logger }
      ]
    }).compileComponents();
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
});
