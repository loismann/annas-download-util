import { MAX_FONT_SIZE, MIN_FONT_SIZE } from '../../constants/limits';

export type ReaderFont = 'serif' | 'sans' | 'mono';
export type ReaderTheme = 'light' | 'sepia' | 'dark';

const STORAGE_KEY = 'reader-appearance';

const FONT_STACKS: Record<ReaderFont, string> = {
  serif: '"Georgia", "Times New Roman", serif',
  mono: '"SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace',
  sans: '"Inter", "Segoe UI", system-ui, -apple-system, sans-serif'
};

/**
 * How the reader looks: typeface, size, theme, and which chrome is showing.
 *
 * Of the six clusters in `BookReaderComponent` this was the only one whose
 * methods almost never touch anything else — 13 of 15 pure, the two exceptions
 * both being "the text got bigger, repaginate". That is why it came out and the
 * larger summary cluster did not.
 *
 * A plain class rather than an `@Injectable`, for the same reason as
 * `ReaderSplitter`: the sibling services are `providedIn: 'root'` and would
 * share one instance across readers.
 */
export class ReaderAppearance {
  fontFamily: ReaderFont = 'serif';
  fontSize = 14;
  theme: ReaderTheme = 'sepia';

  /**
   * View state, deliberately not persisted. Which panels are open is about the
   * session you are in; the typeface is about how you read.
   */
  showSidebar = true;
  isFullscreen = false;
  showSettingsSection = false;
  showReadingToolsSection = false;

  constructor(private readonly storage: Storage | null = defaultStorage()) {
    this.restore();
  }

  /** Bound with `[ngStyle]`, so the shape has to match what that expects. */
  get textStyles(): { 'font-family': string; 'font-size.px': number } {
    return {
      'font-family': FONT_STACKS[this.fontFamily],
      'font-size.px': this.fontSize
    };
  }

  /**
   * Returns whether the text actually changed size — the caller repaginates on
   * true, and page size is measured from rendered text, so a no-op change must
   * not trigger a re-measure.
   */
  changeFontSize(delta: number): boolean {
    const next = Math.min(MAX_FONT_SIZE, Math.max(MIN_FONT_SIZE, this.fontSize + delta));
    if (next === this.fontSize) return false;

    this.fontSize = next;
    this.persist();
    return true;
  }

  setFontFamily(font: ReaderFont): void {
    this.fontFamily = font;
    this.persist();
  }

  setTheme(theme: ReaderTheme): void {
    this.theme = theme;
    this.persist();
  }

  /** Returns whether the pane width changed, for the same reason as above. */
  setSidebar(show: boolean): boolean {
    if (this.showSidebar === show) return false;

    this.showSidebar = show;
    return true;
  }

  enterFullscreen(): void {
    this.isFullscreen = true;
  }

  exitFullscreen(): void {
    this.isFullscreen = false;
  }

  toggleFullscreen(): void {
    this.isFullscreen = !this.isFullscreen;
  }

  // ─── Persistence ─────────────────────────────────────────────────────

  /**
   * Reading preferences outlive a session. The reader already remembers which
   * books you have open and where you were in them; forgetting that you read at
   * 18pt in dark mode was an inconsistency, not a decision.
   */
  private persist(): void {
    if (!this.storage) return;
    try {
      this.storage.setItem(STORAGE_KEY, JSON.stringify({
        fontFamily: this.fontFamily,
        fontSize: this.fontSize,
        theme: this.theme
      }));
    } catch {
      // A full or blocked quota must not stop somebody reading.
    }
  }

  private restore(): void {
    if (!this.storage) return;
    try {
      const raw = this.storage.getItem(STORAGE_KEY);
      if (!raw) return;

      const saved = JSON.parse(raw) as Partial<Record<string, unknown>>;
      // Each field is validated rather than trusted: this is user-editable
      // storage, and an unknown theme would render as an unstyled class name.
      if (isFont(saved['fontFamily'])) this.fontFamily = saved['fontFamily'];
      if (isTheme(saved['theme'])) this.theme = saved['theme'];
      if (typeof saved['fontSize'] === 'number' && Number.isFinite(saved['fontSize'])) {
        this.fontSize = Math.min(MAX_FONT_SIZE, Math.max(MIN_FONT_SIZE, saved['fontSize']));
      }
    } catch {
      // Corrupt JSON means the defaults, not a reader that will not open.
    }
  }
}

function isFont(value: unknown): value is ReaderFont {
  return value === 'serif' || value === 'sans' || value === 'mono';
}

function isTheme(value: unknown): value is ReaderTheme {
  return value === 'light' || value === 'sepia' || value === 'dark';
}

function defaultStorage(): Storage | null {
  return typeof localStorage !== 'undefined' ? localStorage : null;
}
