import { MAX_FONT_SIZE, MIN_FONT_SIZE } from '../../constants/limits';
import { ReaderAppearance } from './reader-appearance';

describe('ReaderAppearance', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  describe('font size', () => {
    it('should grow and shrink by the delta', () => {
      const a = new ReaderAppearance();
      const start = a.fontSize;

      a.changeFontSize(2);
      expect(a.fontSize).toBe(start + 2);

      a.changeFontSize(-2);
      expect(a.fontSize).toBe(start);
    });

    it('should not grow past the maximum', () => {
      const a = new ReaderAppearance();

      a.changeFontSize(1000);

      expect(a.fontSize).toBe(MAX_FONT_SIZE);
    });

    it('should not shrink past the minimum', () => {
      const a = new ReaderAppearance();

      a.changeFontSize(-1000);

      expect(a.fontSize).toBe(MIN_FONT_SIZE);
    });

    it('should report a change so the caller repaginates', () => {
      expect(new ReaderAppearance().changeFontSize(1)).toBe(true);
    });

    it('should not report a change when already at the ceiling', () => {
      // Repagination re-measures rendered text; doing it for a size that did
      // not move is pure cost.
      const a = new ReaderAppearance();
      a.changeFontSize(1000);

      expect(a.changeFontSize(2)).toBe(false);
    });

    it('should not report a change for a zero delta', () => {
      expect(new ReaderAppearance().changeFontSize(0)).toBe(false);
    });
  });

  describe('sidebar', () => {
    it('should report a change when it opens or closes', () => {
      const a = new ReaderAppearance();

      expect(a.setSidebar(false)).toBe(true);
      expect(a.showSidebar).toBe(false);
    });

    it('should not report a change when set to what it already is', () => {
      expect(new ReaderAppearance().setSidebar(true)).toBe(false);
    });
  });

  describe('fullscreen', () => {
    it('should enter, exit and toggle', () => {
      const a = new ReaderAppearance();

      a.enterFullscreen();
      expect(a.isFullscreen).toBe(true);

      a.exitFullscreen();
      expect(a.isFullscreen).toBe(false);

      a.toggleFullscreen();
      expect(a.isFullscreen).toBe(true);
    });
  });

  describe('text styles', () => {
    it('should give each font its own stack', () => {
      const a = new ReaderAppearance();

      a.setFontFamily('serif');
      expect(a.textStyles['font-family']).toContain('Georgia');

      a.setFontFamily('mono');
      expect(a.textStyles['font-family']).toContain('Consolas');

      a.setFontFamily('sans');
      expect(a.textStyles['font-family']).toContain('Inter');
    });

    it('should report the current size in the shape ngStyle wants', () => {
      const a = new ReaderAppearance();
      a.changeFontSize(3);

      expect(a.textStyles['font-size.px']).toBe(a.fontSize);
    });
  });

  // ─── Persistence ─────────────────────────────────────────────────────

  describe('remembering how you read', () => {
    it('should restore the typeface, size and theme', () => {
      const first = new ReaderAppearance();
      first.setFontFamily('mono');
      first.setTheme('dark');
      first.changeFontSize(4);

      const second = new ReaderAppearance();

      expect(second.fontFamily).toBe('mono');
      expect(second.theme).toBe('dark');
      expect(second.fontSize).toBe(first.fontSize);
    });

    it('should not remember which panels were open', () => {
      // Session view state, not a reading preference.
      const first = new ReaderAppearance();
      first.setSidebar(false);
      first.enterFullscreen();
      first.showSettingsSection = true;

      const second = new ReaderAppearance();

      expect(second.showSidebar).toBe(true);
      expect(second.isFullscreen).toBe(false);
      expect(second.showSettingsSection).toBe(false);
    });

    it('should fall back to defaults when storage holds nonsense', () => {
      localStorage.setItem('reader-appearance', '{not json');

      const a = new ReaderAppearance();

      expect(a.fontFamily).toBe('serif');
      expect(a.theme).toBe('sepia');
    });

    it('should reject a theme it does not know', () => {
      // Storage is user-editable; an unknown value becomes a CSS class that
      // styles nothing, so the reader would open unthemed.
      localStorage.setItem('reader-appearance', JSON.stringify({ theme: 'neon', fontFamily: 'comic' }));

      const a = new ReaderAppearance();

      expect(a.theme).toBe('sepia');
      expect(a.fontFamily).toBe('serif');
    });

    it('should clamp a stored size that is out of range', () => {
      localStorage.setItem('reader-appearance', JSON.stringify({ fontSize: 9999 }));

      expect(new ReaderAppearance().fontSize).toBe(MAX_FONT_SIZE);
    });

    it('should ignore a stored size that is not a number', () => {
      localStorage.setItem('reader-appearance', JSON.stringify({ fontSize: 'large' }));

      expect(new ReaderAppearance().fontSize).toBe(14);
    });

    it('should work without any storage at all', () => {
      const a = new ReaderAppearance(null);

      expect(() => a.changeFontSize(2)).not.toThrow();
      expect(a.fontSize).toBe(16);
    });
  });
});
