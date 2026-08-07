/** A cover URL that was reachable, with the dimensions the browser reported. */
export interface CoverCandidate {
  url: string;
  width: number;
  height: number;
  /** height / width. Only the book edit dialog uses it, to show the shape. */
  ratio: number;
}

/** Loads an image and reports its size, or null if it could not be loaded. */
export type CoverProbe = (url: string) => Promise<{ width: number; height: number } | null>;

/**
 * The real probe. `naturalWidth` is the decoded size and is what we want;
 * `width` is the fallback for browsers that have not populated it yet.
 */
function probeImage(url: string): Promise<{ width: number; height: number } | null> {
  return new Promise(resolve => {
    const img = new Image();
    img.onload = () => resolve({
      width: img.naturalWidth || img.width,
      height: img.naturalHeight || img.height
    });
    img.onerror = () => resolve(null);
    img.src = url;
  });
}

/**
 * Cover candidates come back as a list of URLs from several sources, and a
 * good proportion of them 404, hotlink-block, or resolve to a 1×1 tracking
 * pixel. The only reliable way to tell is to load each one.
 *
 * This was written out twice — in `BookEditDialogComponent` and again in
 * `CoverPickerComponent` — with the two copies already disagreeing about
 * whether a candidate carries its aspect ratio.
 */
export const CoverCandidates = {
  /**
   * Probes every URL in parallel, drops the ones that fail, and sorts the
   * survivors largest-first by pixel area — the biggest image is almost always
   * the real cover rather than a thumbnail.
   */
  async resolve(urls: string[], probe: CoverProbe = probeImage): Promise<CoverCandidate[]> {
    const probed = await Promise.all(urls.map(async url => {
      const size = await probe(url);
      if (!size || !size.width || !size.height) return null;

      return { url, width: size.width, height: size.height, ratio: size.height / size.width };
    }));

    return probed
      .filter((c): c is CoverCandidate => c !== null)
      .sort((a, b) => (b.width * b.height) - (a.width * a.height));
  },

  /**
   * Deduplicates while preserving order. The sources overlap, and the same
   * cover arriving twice would be probed twice and shown twice.
   */
  unique(urls: string[] | null | undefined): string[] {
    return Array.from(new Set(urls ?? []));
  },

  probeImage
};
