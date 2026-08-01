import { TestBed } from '@angular/core/testing';
import { ReaderSectionsService, SectionChunk } from './reader-sections.service';

describe('ReaderSectionsService', () => {
  let service: ReaderSectionsService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [ReaderSectionsService] });
    service = TestBed.inject(ReaderSectionsService);
  });

  describe('findSectionIndex', () => {
    const chunks: SectionChunk[] = [
      { start: 0, end: 100 },
      { start: 100, end: 250 },
      { start: 250, end: 400 }
    ];

    it('should return null when there are no sections', () => {
      expect(service.findSectionIndex([], 0)).toBeNull();
    });

    it('should find the section containing the offset', () => {
      expect(service.findSectionIndex(chunks, 150)).toBe(1);
    });

    it('should treat a section start as inside that section', () => {
      expect(service.findSectionIndex(chunks, 100)).toBe(1);
    });

    it('should treat a section end as belonging to the next section', () => {
      // Ranges are half-open: end is exclusive.
      expect(service.findSectionIndex(chunks, 250)).toBe(2);
    });

    it('should clamp to the last section past the end', () => {
      expect(service.findSectionIndex(chunks, 9999)).toBe(2);
    });

    it('should return the first section at offset zero', () => {
      expect(service.findSectionIndex(chunks, 0)).toBe(0);
    });

    it('should say "no answer" for an offset in a gap', () => {
      // Gaps mean bad boundary data. undefined tells the caller to keep the
      // section it already had rather than jump the reader somewhere else.
      const gapped: SectionChunk[] = [{ start: 0, end: 50 }, { start: 100, end: 200 }];
      expect(service.findSectionIndex(gapped, 75)).toBeUndefined();
    });
  });

  describe('annotate', () => {
    const base = { wordOffset: 0, pageSizeWords: 10, highlightSectionIndex: null };

    it('should return the text untouched when there are no sections', () => {
      expect(service.annotate('a b c', { ...base, chunks: [] })).toBe('a b c');
    });

    it('should not mark a boundary for a single section', () => {
      const out = service.annotate('a b c', { ...base, chunks: [{ start: 0, end: 3 }] });
      expect(out).toBe('a b c');
    });

    it('should insert a boundary marker between two sections', () => {
      const out = service.annotate('w1 w2 w3 w4', {
        ...base,
        chunks: [{ start: 0, end: 2 }, { start: 2, end: 4 }]
      });
      expect(out).toContain('section-marker');
      expect(out).toContain('1 <span class="section-marker-icon">&#9660;</span> 2');
    });

    it('should place the marker before the first word of the next section', () => {
      const out = service.annotate('w1 w2 w3 w4', {
        ...base,
        chunks: [{ start: 0, end: 2 }, { start: 2, end: 4 }]
      });
      expect(out.indexOf('section-marker')).toBeLessThan(out.indexOf('w3'));
      expect(out.indexOf('w2')).toBeLessThan(out.indexOf('section-marker'));
    });

    it('should omit a boundary that falls before the visible page', () => {
      const out = service.annotate('w6 w7', {
        chunks: [{ start: 0, end: 2 }, { start: 2, end: 10 }],
        wordOffset: 5,
        pageSizeWords: 2,
        highlightSectionIndex: null
      });
      expect(out).not.toContain('section-marker');
    });

    it('should omit a boundary that falls after the visible page', () => {
      const out = service.annotate('w1 w2', {
        chunks: [{ start: 0, end: 8 }, { start: 8, end: 10 }],
        wordOffset: 0,
        pageSizeWords: 2,
        highlightSectionIndex: null
      });
      expect(out).not.toContain('section-marker');
    });

    it('should still emit a boundary landing exactly at the end of the page', () => {
      // No following word to anchor to, so it is appended after the loop.
      const out = service.annotate('w1 w2', {
        chunks: [{ start: 0, end: 2 }, { start: 2, end: 4 }],
        wordOffset: 0,
        pageSizeWords: 2,
        highlightSectionIndex: null
      });
      expect(out).toContain('section-marker');
      expect(out.indexOf('w2')).toBeLessThan(out.indexOf('section-marker'));
    });

    it('should shade only the highlighted section', () => {
      const out = service.annotate('w1 w2 w3 w4', {
        chunks: [{ start: 0, end: 2 }, { start: 2, end: 4 }],
        wordOffset: 0,
        pageSizeWords: 4,
        highlightSectionIndex: 1
      });
      expect(out).toContain('<span class="section-highlight">w3</span>');
      expect(out).toContain('<span class="section-highlight">w4</span>');
      expect(out).not.toContain('<span class="section-highlight">w1</span>');
    });

    it('should shade nothing when no section is highlighted', () => {
      const out = service.annotate('w1 w2', {
        ...base,
        chunks: [{ start: 0, end: 2 }],
        highlightSectionIndex: null
      });
      expect(out).not.toContain('section-highlight');
    });

    it('should clamp shading to the visible page when the section starts earlier', () => {
      // Section 0 spans words 0-10 but the page starts at word 5.
      const out = service.annotate('w6 w7', {
        chunks: [{ start: 0, end: 10 }],
        wordOffset: 5,
        pageSizeWords: 2,
        highlightSectionIndex: 0
      });
      expect(out).toContain('<span class="section-highlight">w6</span>');
      expect(out).toContain('<span class="section-highlight">w7</span>');
    });

    it('should shade nothing when the highlighted section is off-page', () => {
      const out = service.annotate('w1 w2', {
        chunks: [{ start: 0, end: 2 }, { start: 50, end: 60 }],
        wordOffset: 0,
        pageSizeWords: 2,
        highlightSectionIndex: 1
      });
      expect(out).not.toContain('section-highlight');
    });

    it('should ignore an out-of-range highlight index', () => {
      const out = service.annotate('w1 w2', {
        ...base,
        chunks: [{ start: 0, end: 2 }],
        highlightSectionIndex: 99
      });
      expect(out).not.toContain('section-highlight');
    });

    it('should preserve the original whitespace', () => {
      const out = service.annotate('w1  w2\nw3', {
        ...base,
        chunks: [{ start: 0, end: 3 }]
      });
      expect(out).toBe('w1  w2\nw3');
    });

    it('should leave already-escaped entities alone', () => {
      // Input arrives escaped; re-escaping or unescaping here would corrupt it.
      const out = service.annotate('&amp; &lt;b&gt;', {
        ...base,
        chunks: [{ start: 0, end: 2 }]
      });
      expect(out).toBe('&amp; &lt;b&gt;');
    });

    it('should mark several boundaries on one page', () => {
      const out = service.annotate('w1 w2 w3 w4 w5 w6', {
        chunks: [{ start: 0, end: 2 }, { start: 2, end: 4 }, { start: 4, end: 6 }],
        wordOffset: 0,
        pageSizeWords: 6,
        highlightSectionIndex: null
      });
      expect(out.match(/section-marker"/g)?.length).toBe(2);
      expect(out).toContain('2 <span class="section-marker-icon">&#9660;</span> 3');
    });
  });
});
