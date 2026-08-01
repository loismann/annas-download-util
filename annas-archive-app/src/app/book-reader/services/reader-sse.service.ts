import { Injectable } from '@angular/core';
import { LoggerService } from '../../services/logger.service';

/**
 * Reads a Server-Sent Events response body and hands each `data:` payload to a
 * callback.
 *
 * Extracted because the reader had two near-identical copies of this loop — one
 * for the chapter-summary stream, one for chunk-boundary detection — and the
 * buffering rule (keep the last, possibly incomplete, line for the next network
 * chunk) is the kind of detail that only gets fixed in one copy.
 *
 * Deliberately limited to parsing: opening the request, handling 409s, choosing
 * between a cached JSON body and a stream, and re-entering the Angular zone all
 * stay with the caller, because those genuinely differ between the two.
 */
@Injectable({ providedIn: 'root' })
export class ReaderSseService {
  constructor(private logger: LoggerService) {}

  /**
   * Consumes `reader` until the stream ends.
   *
   * @param reader Body reader from a `fetch` response.
   * @param onEvent Called once per `data:` line with the parsed JSON. The second
   *   argument is the most recent `event:` name, or '' if the stream sends none.
   *   A payload that fails to parse is logged and skipped — one malformed line
   *   must not abandon the rest of the stream.
   */
  async readStream(
    reader: ReadableStreamDefaultReader<Uint8Array>,
    onEvent: (data: any, eventName: string) => void
  ): Promise<void> {
    const decoder = new TextDecoder();
    let buffer = '';
    // Hoisted out of the read loop so an `event:` line still applies to a
    // `data:` line that arrives in a later network chunk.
    let currentEvent = '';

    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        this.logger.log('SSE stream complete');
        return;
      }

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');

      // Keep the last incomplete line in the buffer
      buffer = lines.pop() || '';

      for (const line of lines) {
        if (line.startsWith('event:')) {
          currentEvent = line.substring(6).trim();
          continue;
        }

        if (!line.startsWith('data:')) continue;

        const data = line.substring(5).trim();
        if (!data) continue;

        try {
          const parsed = JSON.parse(data);
          this.logger.log(`SSE ${currentEvent}:`, parsed);
          onEvent(parsed, currentEvent);
        } catch (e) {
          this.logger.error('Failed to parse SSE data:', data, e);
        }
      }
    }
  }

  /** Human-readable label for a chapter-summary progress stage. */
  getStageLabel(stage: string): string {
    switch (stage) {
      case 'chunks': return 'Analyzing Chunks';
      case 'sections': return 'Synthesizing Sections';
      case 'final': return 'Final Summary';
      case 'complete': return 'Complete';
      case 'error': return 'Error';
      default: return 'Processing';
    }
  }
}
