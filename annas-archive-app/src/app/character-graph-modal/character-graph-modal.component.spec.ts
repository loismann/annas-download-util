import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { of, throwError, NEVER } from 'rxjs';
import { CharacterGraphModalComponent, CharacterGraphModalData } from './character-graph-modal.component';
import { AiApiService } from '../services/ai-api.service';
import { LoggerService } from '../services/logger.service';
import { CharacterGraphResponse } from '../models/dropbox-epub.model';

describe('CharacterGraphModalComponent', () => {
  let component: CharacterGraphModalComponent;
  let fixture: ComponentFixture<CharacterGraphModalComponent>;
  let mockDialogRef: jasmine.SpyObj<MatDialogRef<CharacterGraphModalComponent>>;
  let mockAiApi: jasmine.SpyObj<AiApiService>;
  let mockLogger: jasmine.SpyObj<LoggerService>;

  const mockGraphData: CharacterGraphResponse = {
    nodes: [
      { id: 'char1', label: 'Character One', description: 'The protagonist' },
      { id: 'char2', label: 'Character Two', description: 'The antagonist' }
    ],
    edges: [
      { from: 'char1', to: 'char2', label: 'enemies' }
    ],
    summaryCount: 5,
    cachedAt: '2026-01-15T00:00:00Z'
  };

  const mockDialogData: CharacterGraphModalData = {
    dropboxPath: '/Books/test.epub',
    bookTitle: 'Test Book'
  };

  beforeEach(async () => {
    mockDialogRef = jasmine.createSpyObj('MatDialogRef', ['close']);
    mockAiApi = jasmine.createSpyObj('AiApiService', ['getCharacterGraph', 'generateCharacterGraph']);
    mockLogger = jasmine.createSpyObj('LoggerService', ['log', 'warn', 'error']);

    // Default mock - graph needs update
    mockAiApi.getCharacterGraph.and.returnValue(throwError(() => new Error('Not found')));
    mockAiApi.generateCharacterGraph.and.returnValue(of(mockGraphData));

    // Mock anychart global
    (window as any).anychart = {
      graph: () => ({
        data: jasmine.createSpy('data'),
        layout: () => ({
          type: jasmine.createSpy('type'),
          iterationCount: jasmine.createSpy('iterationCount')
        }),
        nodes: () => ({
          normal: () => ({ fill: jasmine.createSpy('fill'), stroke: jasmine.createSpy('stroke') }),
          hovered: () => ({ fill: jasmine.createSpy('fill') }),
          selected: () => ({ fill: jasmine.createSpy('fill') }),
          labels: () => ({
            enabled: jasmine.createSpy('enabled'),
            fontSize: jasmine.createSpy('fontSize'),
            fontWeight: jasmine.createSpy('fontWeight')
          }),
          tooltip: () => ({ useHtml: jasmine.createSpy('useHtml') })
        }),
        edges: () => ({
          normal: () => ({ stroke: jasmine.createSpy('stroke') }),
          hovered: () => ({ stroke: jasmine.createSpy('stroke') }),
          selected: () => ({ stroke: jasmine.createSpy('stroke') }),
          labels: () => ({ enabled: jasmine.createSpy('enabled') }),
          tooltip: () => ({ useHtml: jasmine.createSpy('useHtml') })
        }),
        interactivity: () => ({ edges: jasmine.createSpy('edges') }),
        listen: jasmine.createSpy('listen'),
        title: () => ({
          fontColor: jasmine.createSpy('fontColor'),
          fontSize: jasmine.createSpy('fontSize')
        }),
        container: jasmine.createSpy('container'),
        draw: jasmine.createSpy('draw'),
        dispose: jasmine.createSpy('dispose')
      })
    };

    await TestBed.configureTestingModule({
      imports: [CharacterGraphModalComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData },
        { provide: AiApiService, useValue: mockAiApi },
        { provide: LoggerService, useValue: mockLogger }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CharacterGraphModalComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    delete (window as any).anychart;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should initialize with loading state', () => {
    expect(component.loading).toBe(true);
    expect(component.error).toBeNull();
    expect(component.graphData).toBeNull();
  });

  describe('ngOnInit', () => {
    it('should load graph on init', fakeAsync(() => {
      fixture.detectChanges();
      tick(200);

      expect(mockAiApi.getCharacterGraph).toHaveBeenCalledWith('/Books/test.epub');
    }));

    it('should generate graph if not found', fakeAsync(() => {
      mockAiApi.getCharacterGraph.and.returnValue(throwError(() => new Error('Not found')));
      fixture.detectChanges();
      tick(200);

      expect(mockAiApi.generateCharacterGraph).toHaveBeenCalled();
    }));

    it('should use cached graph if available and up to date', fakeAsync(() => {
      mockAiApi.getCharacterGraph.and.returnValue(of({ ...mockGraphData, needsUpdate: false }));
      fixture.detectChanges();
      tick(200);

      expect(mockAiApi.generateCharacterGraph).not.toHaveBeenCalled();
      expect(component.graphData).toBeTruthy();
    }));

    it('should regenerate if graph needs update', fakeAsync(() => {
      mockAiApi.getCharacterGraph.and.returnValue(of({
        ...mockGraphData,
        needsUpdate: true,
        currentSummaryCount: 10
      }));
      fixture.detectChanges();
      tick(200);

      expect(mockAiApi.generateCharacterGraph).toHaveBeenCalled();
    }));
  });

  describe('generateGraph', () => {
    it('should set loading state', () => {
      // Use NEVER so the observable doesn't emit synchronously before assertions
      mockAiApi.generateCharacterGraph.and.returnValue(NEVER);

      component.generateGraph();

      expect(component.loading).toBe(true);
      expect(component.error).toBeNull();
    });

    it('should set graphData on success', fakeAsync(() => {
      component.generateGraph();
      tick(200);

      expect(component.graphData).toEqual(mockGraphData);
      expect(component.loading).toBe(false);
    }));

    it('should set error on failure', fakeAsync(() => {
      mockAiApi.generateCharacterGraph.and.returnValue(throwError(() => new Error('API Error')));

      component.generateGraph();
      tick(200);

      expect(component.error).toBeTruthy();
      expect(component.loading).toBe(false);
    }));
  });

  describe('node and edge selection', () => {
    beforeEach(() => {
      component.graphData = mockGraphData;
    });

    it('should select node on click', () => {
      component.onNodeClick('char1');

      expect(component.selectedNodeId).toBe('char1');
      expect(component.selectedEdge).toBeNull();
      expect(component.detailsVisible).toBe(true);
    });

    it('should select edge on click', () => {
      component.onEdgeClick('char1', 'char2');

      expect(component.selectedEdge).toEqual({ from: 'char1', to: 'char2' });
      expect(component.selectedNodeId).toBeNull();
      expect(component.detailsVisible).toBe(true);
    });

    it('should get selected node', () => {
      component.selectedNodeId = 'char1';

      const node = component.getSelectedNode();

      expect(node?.id).toBe('char1');
      expect(node?.label).toBe('Character One');
    });

    it('should return undefined for invalid node', () => {
      component.selectedNodeId = 'nonexistent';

      expect(component.getSelectedNode()).toBeUndefined();
    });

    it('should get selected edge', () => {
      component.selectedEdge = { from: 'char1', to: 'char2' };

      const edge = component.getSelectedEdge();

      expect(edge?.label).toBe('enemies');
    });

    it('should find edge in reverse direction', () => {
      component.selectedEdge = { from: 'char2', to: 'char1' };

      const edge = component.getSelectedEdge();

      expect(edge?.label).toBe('enemies');
    });
  });

  describe('getNodeLabel', () => {
    beforeEach(() => {
      component.graphData = mockGraphData;
    });

    it('should return formatted node label', () => {
      const label = component.getNodeLabel('char1');
      expect(label).toBe('Character One');
    });

    it('should return nodeId if node not found', () => {
      const label = component.getNodeLabel('unknown');
      expect(label).toBe('unknown');
    });

    it('should return nodeId if no graphData', () => {
      component.graphData = null;
      const label = component.getNodeLabel('char1');
      expect(label).toBe('char1');
    });
  });

  describe('closeDetails', () => {
    it('should reset selection state', () => {
      component.selectedNodeId = 'char1';
      component.selectedEdge = { from: 'char1', to: 'char2' };
      component.detailsVisible = true;

      component.closeDetails();

      expect(component.detailsVisible).toBe(false);
      expect(component.selectedNodeId).toBeNull();
      expect(component.selectedEdge).toBeNull();
    });
  });

  describe('close', () => {
    it('should close the dialog', () => {
      component.close();

      expect(mockDialogRef.close).toHaveBeenCalled();
    });
  });

  describe('ngOnDestroy', () => {
    it('should dispose chart on destroy', fakeAsync(() => {
      mockAiApi.getCharacterGraph.and.returnValue(of({ ...mockGraphData, needsUpdate: false }));
      fixture.detectChanges();
      tick(200);

      // Manually set chart reference for test
      (component as any).chart = { dispose: jasmine.createSpy('dispose') };

      component.ngOnDestroy();

      expect((component as any).chart.dispose).toHaveBeenCalled();
    }));
  });
});
