import { TestBed } from '@angular/core/testing';
import { ReaderSseService } from './reader-sse.service';
import { LoggerService } from '../../services/logger.service';

describe('ReaderSseService', () => {
  let service: ReaderSseService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ReaderSseService,
        { provide: LoggerService, useValue: jasmine.createSpyObj('LoggerService', ['log', 'error', 'warn']) }
      ]
    });
    service = TestBed.inject(ReaderSseService);
  });

  /**
   * Builds a reader that yields each string as one network chunk. Splitting a
   * message across chunks is the whole point of the buffering, so tests control
   * the split explicitly rather than feeding one tidy blob.
   */
  function readerFrom(chunks: string[]): ReadableStreamDefaultReader<Uint8Array> {
    const encoder = new TextEncoder();
    let i = 0;
    return {
      read: () =>
        Promise.resolve(
          i < chunks.length
            ? { done: false, value: encoder.encode(chunks[i++]) }
            : { done: true, value: undefined }
        )
    } as unknown as ReadableStreamDefaultReader<Uint8Array>;
  }

  async function collect(chunks: string[]): Promise<{ data: any; name: string }[]> {
    const seen: { data: any; name: string }[] = [];
    await service.readStream(readerFrom(chunks), (data, name) => seen.push({ data, name }));
    return seen;
  }

  describe('readStream', () => {
    it('should emit one event per data line', async () => {
      const seen = await collect(['data: {"a":1}\ndata: {"a":2}\n']);
      expect(seen.map(s => s.data.a)).toEqual([1, 2]);
    });

    it('should reassemble a message split across network chunks', async () => {
      // The realistic failure: a JSON payload arrives in two pieces.
      const seen = await collect(['data: {"stage":"chu', 'nks","stepNumber":1}\n']);
      expect(seen.length).toBe(1);
      expect(seen[0].data).toEqual({ stage: 'chunks', stepNumber: 1 });
    });

    it('should not emit a trailing line that has no newline yet', async () => {
      const seen = await collect(['data: {"a":1}\ndata: {"a":2}']);
      expect(seen.map(s => s.data.a)).toEqual([1]);
    });

    it('should attach the most recent event name', async () => {
      const seen = await collect(['event: progress\ndata: {"a":1}\n']);
      expect(seen[0].name).toBe('progress');
    });

    it('should carry an event name across a chunk boundary', async () => {
      // Regression guard: the old inline copy reset the name on every chunk, so
      // a name split from its payload was lost.
      const seen = await collect(['event: complete\n', 'data: {"a":1}\n']);
      expect(seen[0].name).toBe('complete');
    });

    it('should report an empty name when the stream sends none', async () => {
      const seen = await collect(['data: {"a":1}\n']);
      expect(seen[0].name).toBe('');
    });

    it('should skip a malformed payload and keep reading', async () => {
      const seen = await collect(['data: not-json\ndata: {"a":2}\n']);
      expect(seen.map(s => s.data.a)).toEqual([2]);
    });

    it('should ignore blank data lines', async () => {
      const seen = await collect(['data:\ndata: {"a":1}\n']);
      expect(seen.length).toBe(1);
    });

    it('should ignore comment and unknown field lines', async () => {
      const seen = await collect([': keep-alive\nid: 7\ndata: {"a":1}\n']);
      expect(seen.map(s => s.data.a)).toEqual([1]);
    });

    it('should resolve without emitting on an empty stream', async () => {
      const seen = await collect([]);
      expect(seen).toEqual([]);
    });

    it('should handle many chunks without losing events', async () => {
      const chunks = Array.from({ length: 50 }, (_, i) => `data: {"n":${i}}\n`);
      const seen = await collect(chunks);
      expect(seen.length).toBe(50);
      expect(seen[49].data.n).toBe(49);
    });
  });

  describe('getStageLabel', () => {
    it('should label each known stage', () => {
      expect(service.getStageLabel('chunks')).toBe('Analyzing Chunks');
      expect(service.getStageLabel('sections')).toBe('Synthesizing Sections');
      expect(service.getStageLabel('final')).toBe('Final Summary');
      expect(service.getStageLabel('complete')).toBe('Complete');
      expect(service.getStageLabel('error')).toBe('Error');
    });

    it('should fall back for an unrecognised stage', () => {
      expect(service.getStageLabel('something-new')).toBe('Processing');
      expect(service.getStageLabel('')).toBe('Processing');
    });
  });
});
