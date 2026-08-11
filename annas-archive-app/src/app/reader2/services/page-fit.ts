import { FitAt, MIN_PAGE_WORDS, wordsPerPage } from './pagination';

/**
 * The real fit: how many words actually land on the page, measured by laying
 * them out.
 *
 * <p>The estimate this replaces either ran text past the bottom edge — words
 * the reader could not read — or stopped short and left a gap. There is no
 * arithmetic that predicts line breaking across fonts, sizes, and word lengths;
 * the only honest answer is to lay the words out and look, so this clones the
 * real reading surface (same classes, same scoped styles, same width), fills it
 * offscreen, and binary-searches the largest word count whose rendered height
 * still fits.</p>
 *
 * <p>Cost: one hidden element and ~10 relayouts per page boundary, run once per
 * (chapter, size, font) — not per page turn, because the boundaries are cached
 * by the store's <code>computed</code> until something they depend on
 * changes.</p>
 */
/**
 * @param page  A word range as the paragraphs it would be drawn as. Not a flat
 *   string: the surface puts a gap between paragraphs, and a probe that laid one
 *   block where the reader will see four would measure a page several gaps too
 *   tall and let that much text run off the bottom of every one.
 */
export function measuredFit(
  surface: HTMLElement, total: () => number, page: (start: number, count: number) => string[]
): FitAt {
  return start => {
    const remaining = total() - start;
    if (remaining <= 0) return MIN_PAGE_WORDS;

    const probe = cloneSurface(surface);

    try {
      return search(probe, page, start, remaining, surface.clientHeight);
    } finally {
      probe.root.remove();
    }
  };
}

/** The estimate, for when there is no surface to measure yet. */
export function estimatedFit(
  heightPx: number, widthPx: number, fontSizePx: number
): FitAt {
  return () => wordsPerPage(heightPx, widthPx, fontSizePx);
}

/**
 * Largest count whose rendered height fits, found by doubling out of the
 * estimate and then bisecting. The floor is {@link MIN_PAGE_WORDS}: a page that
 * technically overflows still beats one that advances nowhere.
 */
function search(
  probe: ProbeParts, page: (start: number, count: number) => string[],
  start: number, remaining: number, height: number
): number {
  const fits = (count: number): boolean => {
    probe.fill(page(start, count));
    return probe.root.scrollHeight <= height;
  };

  let low = MIN_PAGE_WORDS;
  let high = Math.min(remaining, Math.max(low + 1, Math.floor(remaining / 4) || remaining));

  while (high < remaining && fits(high)) {
    low = high;
    high = Math.min(remaining, high * 2);
  }

  if (high === remaining && fits(high)) return remaining;

  while (low < high - 1) {
    const mid = Math.floor((low + high) / 2);

    if (fits(mid)) low = mid;
    else high = mid;
  }

  return Math.max(MIN_PAGE_WORDS, low);
}

interface ProbeParts {
  root: HTMLElement;

  /** Replaces whatever prose the probe held with these paragraphs. */
  fill: (paragraphs: string[]) => void;
}

/**
 * A hidden copy of the reading surface, child structure included, so Angular's
 * scoped styles apply to it exactly as they do to the real one. Width is pinned
 * and height freed: the question is "how tall would this content be at the real
 * width", and the answer is compared against the real height.
 *
 * <p>The prose is stripped out and one paragraph kept as the mould. Cloning a
 * real one is what carries Angular's <c>_ngcontent</c> attribute across, and
 * without that attribute the scoped <c>.body</c> rules — including the gap
 * between paragraphs this exists to account for — would not apply to the probe
 * at all.</p>
 */
function cloneSurface(surface: HTMLElement): ProbeParts {
  const root = surface.cloneNode(false) as HTMLElement;
  let mould: HTMLElement | null = null;

  for (const child of Array.from(surface.children)) {
    if (child.classList.contains('body')) {
      mould ??= child.cloneNode(false) as HTMLElement;
      continue;
    }

    root.appendChild(child.cloneNode(true));
  }

  root.style.position = 'fixed';
  root.style.left = '-99999px';
  root.style.top = '0';

  // clientWidth includes the padding, so the pinned width must too — content-box
  // would add the padding twice and measure lines wider than the real ones.
  root.style.boxSizing = 'border-box';
  root.style.width = `${surface.clientWidth}px`;
  root.style.height = 'auto';
  root.style.maxWidth = 'none';
  root.style.visibility = 'hidden';

  surface.parentElement?.appendChild(root);

  // Only when the surface is showing no prose at all — an empty chapter, or the
  // paint before the first page arrives. There is nothing to paginate then, so
  // the missing scoped styles cost the measurement nothing.
  const blank = mould ?? plainParagraph();

  return {
    root,
    fill: paragraphs => {
      for (const old of Array.from(root.querySelectorAll('.body'))) old.remove();

      for (const text of paragraphs) {
        const copy = blank.cloneNode(false) as HTMLElement;
        copy.textContent = text;
        root.appendChild(copy);
      }
    }
  };
}

function plainParagraph(): HTMLElement {
  const paragraph = document.createElement('p');
  paragraph.className = 'body';

  return paragraph;
}
