import { TestBed } from '@angular/core/testing';
import { ReaderTextUtilsService } from './reader-text-utils.service';

describe('ReaderTextUtilsService', () => {
  let service: ReaderTextUtilsService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [ReaderTextUtilsService]
    });
    service = TestBed.inject(ReaderTextUtilsService);
  });

  describe('countWords', () => {
    it('should return 0 for empty string', () => {
      expect(service.countWords('')).toBe(0);
    });

    it('should return 0 for null/undefined', () => {
      expect(service.countWords(null as any)).toBe(0);
      expect(service.countWords(undefined as any)).toBe(0);
    });

    it('should count words correctly', () => {
      expect(service.countWords('hello world')).toBe(2);
      expect(service.countWords('one two three four five')).toBe(5);
    });

    it('should handle multiple spaces between words', () => {
      expect(service.countWords('hello    world')).toBe(2);
    });

    it('should handle tabs and newlines', () => {
      expect(service.countWords('hello\tworld\ntest')).toBe(3);
    });

    it('should handle leading and trailing whitespace', () => {
      expect(service.countWords('  hello world  ')).toBe(2);
    });

    it('should count single word correctly', () => {
      expect(service.countWords('hello')).toBe(1);
    });
  });

  describe('sliceByWords', () => {
    it('should return empty string for empty input', () => {
      expect(service.sliceByWords('', 0, 5)).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.sliceByWords(null as any, 0, 5)).toBe('');
      expect(service.sliceByWords(undefined as any, 0, 5)).toBe('');
    });

    it('should slice from the beginning', () => {
      expect(service.sliceByWords('one two three four five', 0, 3)).toBe('one two three ');
    });

    it('should slice from the middle', () => {
      expect(service.sliceByWords('one two three four five', 2, 2)).toBe('three four ');
    });

    it('should handle offset beyond text length', () => {
      expect(service.sliceByWords('one two', 10, 5)).toBe('');
    });

    it('should return to end if count exceeds remaining words', () => {
      expect(service.sliceByWords('one two three', 1, 10)).toBe('two three');
    });

    it('should preserve whitespace between words', () => {
      const text = 'word1  word2   word3';
      const result = service.sliceByWords(text, 0, 2);
      expect(result.trim()).toContain('word1');
      expect(result.trim()).toContain('word2');
    });
  });

  describe('escapeHtml', () => {
    it('should return empty string for empty input', () => {
      expect(service.escapeHtml('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.escapeHtml(null as any)).toBe('');
      expect(service.escapeHtml(undefined as any)).toBe('');
    });

    it('should escape ampersand', () => {
      expect(service.escapeHtml('A & B')).toBe('A &amp; B');
    });

    it('should escape less than', () => {
      expect(service.escapeHtml('a < b')).toBe('a &lt; b');
    });

    it('should escape greater than', () => {
      expect(service.escapeHtml('a > b')).toBe('a &gt; b');
    });

    it('should escape double quotes', () => {
      expect(service.escapeHtml('say "hello"')).toBe('say &quot;hello&quot;');
    });

    it('should escape single quotes', () => {
      expect(service.escapeHtml("it's")).toBe('it&#39;s');
    });

    it('should escape HTML tags', () => {
      expect(service.escapeHtml('<script>alert("xss")</script>')).toBe('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;');
    });

    it('should handle multiple special characters', () => {
      expect(service.escapeHtml('<a href="test&foo">link</a>')).toBe('&lt;a href=&quot;test&amp;foo&quot;&gt;link&lt;/a&gt;');
    });
  });

  describe('escapeRegExp', () => {
    it('should return empty string for empty input', () => {
      expect(service.escapeRegExp('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.escapeRegExp(null as any)).toBe('');
      expect(service.escapeRegExp(undefined as any)).toBe('');
    });

    it('should escape dots', () => {
      expect(service.escapeRegExp('file.txt')).toBe('file\\.txt');
    });

    it('should escape asterisks', () => {
      expect(service.escapeRegExp('a*b')).toBe('a\\*b');
    });

    it('should escape plus signs', () => {
      expect(service.escapeRegExp('a+b')).toBe('a\\+b');
    });

    it('should escape question marks', () => {
      expect(service.escapeRegExp('what?')).toBe('what\\?');
    });

    it('should escape brackets', () => {
      expect(service.escapeRegExp('[a-z]')).toBe('\\[a-z\\]');
    });

    it('should escape curly braces', () => {
      expect(service.escapeRegExp('{1,3}')).toBe('\\{1,3\\}');
    });

    it('should escape parentheses', () => {
      expect(service.escapeRegExp('(group)')).toBe('\\(group\\)');
    });

    it('should escape caret and dollar', () => {
      expect(service.escapeRegExp('^start end$')).toBe('\\^start end\\$');
    });

    it('should escape pipe', () => {
      expect(service.escapeRegExp('a|b')).toBe('a\\|b');
    });

    it('should escape backslash', () => {
      expect(service.escapeRegExp('path\\to')).toBe('path\\\\to');
    });

    it('should handle complex regex patterns', () => {
      const input = '^[a-z]+\\d{2,}(test|demo)?$';
      const escaped = service.escapeRegExp(input);
      // All special chars should be escaped
      expect(escaped).not.toContain('[a-z]');
      expect(escaped).toContain('\\[');
      expect(escaped).toContain('\\]');
    });
  });

  describe('collapseBlankLines', () => {
    it('should return empty string for empty input', () => {
      expect(service.collapseBlankLines('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.collapseBlankLines(null as any)).toBe('');
      expect(service.collapseBlankLines(undefined as any)).toBe('');
    });

    it('should collapse multiple blank lines to two', () => {
      expect(service.collapseBlankLines('line1\n\n\n\nline2')).toBe('line1\n\nline2');
    });

    it('should trim leading blank lines', () => {
      expect(service.collapseBlankLines('\n\n\ntext')).toBe('text');
    });

    it('should trim trailing blank lines', () => {
      expect(service.collapseBlankLines('text\n\n\n')).toBe('text');
    });

    it('should preserve single blank lines', () => {
      expect(service.collapseBlankLines('line1\n\nline2')).toBe('line1\n\nline2');
    });

    it('should handle lines with only whitespace', () => {
      expect(service.collapseBlankLines('line1\n   \n   \nline2')).toBe('line1\n\nline2');
    });
  });

  describe('capitalizeWords', () => {
    it('should return empty string for empty input', () => {
      expect(service.capitalizeWords('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.capitalizeWords(null as any)).toBe('');
      expect(service.capitalizeWords(undefined as any)).toBe('');
    });

    it('should capitalize first letter of each word', () => {
      expect(service.capitalizeWords('hello world')).toBe('Hello World');
    });

    it('should handle all uppercase input', () => {
      expect(service.capitalizeWords('HELLO WORLD')).toBe('Hello World');
    });

    it('should handle mixed case input', () => {
      expect(service.capitalizeWords('hElLo WoRlD')).toBe('Hello World');
    });

    it('should handle single word', () => {
      expect(service.capitalizeWords('hello')).toBe('Hello');
    });

    it('should handle single character words', () => {
      expect(service.capitalizeWords('a b c')).toBe('A B C');
    });

    it('should preserve multiple spaces', () => {
      expect(service.capitalizeWords('hello  world')).toBe('Hello  World');
    });
  });

  describe('formatAsHtml', () => {
    it('should return empty string for empty input', () => {
      expect(service.formatAsHtml('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.formatAsHtml(null as any)).toBe('');
      expect(service.formatAsHtml(undefined as any)).toBe('');
    });

    it('should convert bold markdown to strong tags', () => {
      expect(service.formatAsHtml('**bold text**')).toBe('<strong>bold text</strong>');
    });

    it('should convert italic markdown to em tags', () => {
      expect(service.formatAsHtml('*italic text*')).toBe('<em>italic text</em>');
    });

    it('should convert bullet points to unordered list', () => {
      const input = '- item 1\n- item 2\n- item 3';
      const result = service.formatAsHtml(input);
      expect(result).toContain('<ul>');
      expect(result).toContain('</ul>');
      expect(result).toContain('<li>item 1</li>');
      expect(result).toContain('<li>item 2</li>');
      expect(result).toContain('<li>item 3</li>');
    });

    it('should convert numbered lists to ordered list', () => {
      const input = '1. first\n2. second\n3. third';
      const result = service.formatAsHtml(input);
      expect(result).toContain('<ol>');
      expect(result).toContain('</ol>');
      expect(result).toContain('<li>first</li>');
      expect(result).toContain('<li>second</li>');
      expect(result).toContain('<li>third</li>');
    });

    it('should handle mixed content', () => {
      const input = 'Some text\n- item 1\n- item 2\nMore text';
      const result = service.formatAsHtml(input);
      expect(result).toContain('<ul>');
      expect(result).toContain('</ul>');
      expect(result).toContain('Some text');
      expect(result).toContain('More text');
    });
  });

  // These three suites pin what the reader actually ships. An earlier version of
  // this file tested a parallel implementation that was never wired up, and its
  // expectations contradicted production on every point that mattered.
  describe('cleanModelHtml', () => {
    it('should return empty string for empty input', () => {
      expect(service.cleanModelHtml('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.cleanModelHtml(null as any)).toBe('');
      expect(service.cleanModelHtml(undefined as any)).toBe('');
    });

    it('should strip code fences the model wraps around HTML', () => {
      expect(service.cleanModelHtml('```html<p>text</p>```')).toBe('<p>text</p>');
      expect(service.cleanModelHtml('```<p>text</p>```')).toBe('<p>text</p>');
    });

    it('should give protocol-relative image URLs an https scheme', () => {
      // Wikipedia serves //upload.wikimedia.org/... which resolves to nothing.
      const result = service.cleanModelHtml('<img src="//upload.wikimedia.org/a.jpg">');
      expect(result).toContain('src="https://upload.wikimedia.org/a.jpg"');
    });

    it('should collapse whitespace in image URLs', () => {
      const result = service.cleanModelHtml('<img src="//upload.wikimedia.org/a b.jpg">');
      expect(result).toContain('src="https://upload.wikimedia.org/a_b.jpg"');
    });

    it('should suppress the referrer so Wikipedia does not block the image', () => {
      const result = service.cleanModelHtml('<img src="https://example.com/a.jpg">');
      expect(result).toContain('referrerpolicy="no-referrer"');
    });

    it('should hide images that fail to load rather than leaving a broken icon', () => {
      const result = service.cleanModelHtml('<img src="https://example.com/a.jpg">');
      expect(result).toContain('onerror=');
    });

    it('should lazy-load and style images', () => {
      const result = service.cleanModelHtml('<img src="https://example.com/a.jpg">');
      expect(result).toContain('loading="lazy"');
      expect(result).toContain('max-width:100%');
    });

    it('should not duplicate attributes that are already present', () => {
      const input = '<img src="https://example.com/a.jpg" loading="eager" style="width:1px">';
      const result = service.cleanModelHtml(input);
      expect(result).toContain('loading="eager"');
      expect(result.match(/loading=/g)?.length).toBe(1);
      expect(result.match(/style=/g)?.length).toBe(1);
    });

    it('should leave non-image markup untouched', () => {
      // Script/attribute stripping is DomSanitizer's job at the call site;
      // doing it here would also strip the onerror this method adds.
      const input = '<p>text</p><a href="https://example.com">link</a>';
      expect(service.cleanModelHtml(input)).toBe(input);
    });
  });

  describe('extractWikipediaUrls', () => {
    it('should return empty array for empty input', () => {
      expect(service.extractWikipediaUrls('')).toEqual([]);
    });

    it('should extract a Wikipedia URL from an href attribute', () => {
      const html = '<a href="https://en.wikipedia.org/wiki/Test">link</a>';
      expect(service.extractWikipediaUrls(html)).toContain('https://en.wikipedia.org/wiki/Test');
    });

    it('should extract multiple URLs in document order', () => {
      const html =
        '<a href="https://en.wikipedia.org/wiki/First">a</a>' +
        '<a href="https://en.wikipedia.org/wiki/Second">b</a>';
      expect(service.extractWikipediaUrls(html)).toEqual([
        'https://en.wikipedia.org/wiki/First',
        'https://en.wikipedia.org/wiki/Second'
      ]);
    });

    it('should deduplicate URLs', () => {
      const html =
        '<a href="https://en.wikipedia.org/wiki/Test">a</a>' +
        '<a href="https://en.wikipedia.org/wiki/Test">b</a>';
      expect(service.extractWikipediaUrls(html).length).toBe(1);
    });

    it('should ignore bare URLs in prose', () => {
      // Only linked articles can be resolved to an image set.
      const html = 'See https://en.wikipedia.org/wiki/First for more';
      expect(service.extractWikipediaUrls(html)).toEqual([]);
    });

    it('should ignore non-English Wikipedias', () => {
      const html = '<a href="https://fr.wikipedia.org/wiki/Article">link</a>';
      expect(service.extractWikipediaUrls(html)).toEqual([]);
    });
  });

  describe('getWikipediaTitleFromUrl', () => {
    it('should return empty string for empty input', () => {
      expect(service.getWikipediaTitleFromUrl('')).toBe('');
    });

    it('should keep underscores, which the article API expects', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Test_Article'))
        .toBe('Test_Article');
    });

    it('should decode URL-encoded characters', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Test%20Article'))
        .toBe('Test Article');
    });

    it('should return empty string for a non-Wikipedia URL', () => {
      expect(service.getWikipediaTitleFromUrl('https://example.com/page')).toBe('');
    });

    // Regression: these used to be carried into the title, so a link to a
    // section resolved to no article and the Learn More panel showed no images.
    it('should drop a section fragment', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Dune#Plot'))
        .toBe('Dune');
    });

    it('should drop a query string', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Article?p=v'))
        .toBe('Article');
    });

    it('should keep underscores in a title that also has a fragment', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Test_Article#Bio'))
        .toBe('Test_Article');
    });
  });

  describe('truncateChapterLabel', () => {
    it('should return empty string for empty input', () => {
      expect(service.truncateChapterLabel('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.truncateChapterLabel(null as any)).toBe('');
      expect(service.truncateChapterLabel(undefined as any)).toBe('');
    });

    it('should not truncate short labels', () => {
      expect(service.truncateChapterLabel('Short')).toBe('Short');
    });

    it('should truncate long labels with ellipsis', () => {
      expect(service.truncateChapterLabel('This is a very long chapter title')).toBe('This is a very long ...');
    });

    it('should respect custom max length', () => {
      expect(service.truncateChapterLabel('Hello World', 5)).toBe('Hello...');
    });

    it('should handle exact length', () => {
      expect(service.truncateChapterLabel('12345', 5)).toBe('12345');
    });
  });

  describe('formatBookmarkLabel', () => {
    // Real DropboxEpubChapter shape. The previous suite used { chapterId, title },
    // a field that does not exist on the model, so nothing it asserted could have
    // matched production.
    const mockChapters = [
      { id: 1, title: 'Chapter 1: Introduction' },
      { id: 2, title: 'Chapter 2: Main Content' },
      { id: 3, title: 'Epilogue' }
    ];

    it('should abbreviate a self-naming chapter and show the page', () => {
      expect(service.formatBookmarkLabel(1, 500, 250, mockChapters)).toBe('Ch. 1 p. 3');
    });

    it('should handle the first page', () => {
      expect(service.formatBookmarkLabel(1, 0, 250, mockChapters)).toBe('Ch. 1 p. 1');
    });

    it('should take the chapter number from the title, not the id', () => {
      expect(service.formatBookmarkLabel(2, 100, 250, mockChapters)).toBe('Ch. 2 p. 1');
    });

    it('should keep a title that does not name itself', () => {
      expect(service.formatBookmarkLabel(3, 100, 250, mockChapters)).toBe('Epilogue • p. 1');
    });

    it('should prefer displayLabel over title', () => {
      const chapters = [{ id: 1, title: 'Chapter 1', displayLabel: 'Prologue' }];
      expect(service.formatBookmarkLabel(1, 0, 250, chapters)).toBe('Prologue • p. 1');
    });

    it('should abbreviate a bare Roman numeral label', () => {
      const chapters = [{ id: 1, title: 'IV' }];
      expect(service.formatBookmarkLabel(1, 0, 250, chapters)).toBe('iv p. 1');
    });

    it('should fall back to a synthesized label for an unknown chapter', () => {
      // Position is unknown, so the number falls back to id + 1.
      expect(service.formatBookmarkLabel(99, 100, 250, mockChapters)).toBe('Ch. 100 p. 1');
    });

    it('should number chapters by position, since EPUB ids are not contiguous', () => {
      const chapters = [{ id: 40, title: 'Opening' }, { id: 90, title: 'Closing' }];
      expect(service.formatBookmarkLabel(90, 0, 250, chapters)).toBe('Closing • p. 1');
    });

    it('should not divide by zero before the first measurement', () => {
      expect(service.formatBookmarkLabel(3, 5, 0, mockChapters)).toBe('Epilogue • p. 6');
    });
  });
});
