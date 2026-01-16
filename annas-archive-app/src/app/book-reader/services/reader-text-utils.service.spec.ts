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

  describe('cleanModelHtml', () => {
    it('should return empty string for empty input', () => {
      expect(service.cleanModelHtml('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.cleanModelHtml(null as any)).toBe('');
      expect(service.cleanModelHtml(undefined as any)).toBe('');
    });

    it('should remove script tags', () => {
      const input = '<p>text</p><script>alert("xss")</script><p>more</p>';
      const result = service.cleanModelHtml(input);
      expect(result).not.toContain('<script>');
      expect(result).not.toContain('alert');
      expect(result).toContain('<p>text</p>');
      expect(result).toContain('<p>more</p>');
    });

    it('should remove event handlers', () => {
      const input = '<div onclick="alert(1)">click</div>';
      const result = service.cleanModelHtml(input);
      expect(result).not.toContain('onclick');
      expect(result).toContain('<div');
    });

    it('should add target="_blank" to links', () => {
      const input = '<a href="https://example.com">link</a>';
      const result = service.cleanModelHtml(input);
      expect(result).toContain('target="_blank"');
    });

    it('should add rel="noopener noreferrer" to links', () => {
      const input = '<a href="https://example.com">link</a>';
      const result = service.cleanModelHtml(input);
      expect(result).toContain('rel="noopener noreferrer"');
    });

    it('should limit img attributes', () => {
      const input = '<img src="image.jpg" alt="test" onerror="alert(1)" onclick="evil()">';
      const result = service.cleanModelHtml(input);
      expect(result).toContain('src="image.jpg"');
      expect(result).toContain('alt="test"');
      expect(result).not.toContain('onerror');
      expect(result).not.toContain('onclick');
    });
  });

  describe('extractWikipediaUrls', () => {
    it('should return empty array for empty input', () => {
      expect(service.extractWikipediaUrls('')).toEqual([]);
    });

    it('should return empty array for null/undefined', () => {
      expect(service.extractWikipediaUrls(null as any)).toEqual([]);
      expect(service.extractWikipediaUrls(undefined as any)).toEqual([]);
    });

    it('should extract single Wikipedia URL', () => {
      const html = '<a href="https://en.wikipedia.org/wiki/Test">link</a>';
      expect(service.extractWikipediaUrls(html)).toContain('https://en.wikipedia.org/wiki/Test');
    });

    it('should extract multiple Wikipedia URLs', () => {
      const html = 'See https://en.wikipedia.org/wiki/First and https://en.wikipedia.org/wiki/Second';
      const result = service.extractWikipediaUrls(html);
      expect(result.length).toBe(2);
      expect(result).toContain('https://en.wikipedia.org/wiki/First');
      expect(result).toContain('https://en.wikipedia.org/wiki/Second');
    });

    it('should deduplicate URLs', () => {
      const html = 'https://en.wikipedia.org/wiki/Test https://en.wikipedia.org/wiki/Test';
      const result = service.extractWikipediaUrls(html);
      expect(result.length).toBe(1);
    });

    it('should handle different language Wikipedias', () => {
      const html = 'https://fr.wikipedia.org/wiki/Article';
      const result = service.extractWikipediaUrls(html);
      expect(result).toContain('https://fr.wikipedia.org/wiki/Article');
    });
  });

  describe('getWikipediaTitleFromUrl', () => {
    it('should return empty string for empty input', () => {
      expect(service.getWikipediaTitleFromUrl('')).toBe('');
    });

    it('should return empty string for null/undefined', () => {
      expect(service.getWikipediaTitleFromUrl(null as any)).toBe('');
      expect(service.getWikipediaTitleFromUrl(undefined as any)).toBe('');
    });

    it('should extract title from URL', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Test_Article')).toBe('Test Article');
    });

    it('should handle URL with query string', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Article?param=value')).toBe('Article');
    });

    it('should handle URL with anchor', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Article#Section')).toBe('Article');
    });

    it('should decode URL-encoded characters', () => {
      expect(service.getWikipediaTitleFromUrl('https://en.wikipedia.org/wiki/Test%20Article')).toBe('Test Article');
    });

    it('should return empty string for non-Wikipedia URL', () => {
      expect(service.getWikipediaTitleFromUrl('https://example.com/page')).toBe('');
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
    const mockChapters = [
      { chapterId: 1, title: 'Chapter 1: Introduction' },
      { chapterId: 2, title: 'Chapter 2: Main Content' },
      { chapterId: 3, title: 'Epilogue' }
    ];

    it('should format bookmark with chapter number and page', () => {
      const result = service.formatBookmarkLabel(1, 500, 250, mockChapters);
      expect(result).toBe('Ch 1 - Page 3');
    });

    it('should handle first page', () => {
      const result = service.formatBookmarkLabel(1, 0, 250, mockChapters);
      expect(result).toBe('Ch 1 - Page 1');
    });

    it('should extract chapter number from title', () => {
      const result = service.formatBookmarkLabel(2, 100, 250, mockChapters);
      expect(result).toBe('Ch 2 - Page 1');
    });

    it('should use chapter ID as fallback when no number in title', () => {
      const result = service.formatBookmarkLabel(3, 100, 250, mockChapters);
      expect(result).toBe('Ch 3 - Page 1');
    });

    it('should handle unknown chapter', () => {
      const result = service.formatBookmarkLabel(99, 100, 250, mockChapters);
      expect(result).toContain('Ch 99');
    });

    it('should handle Roman numerals in chapter title', () => {
      const chapters = [{ chapterId: 1, title: 'Chapter IV: The Journey' }];
      const result = service.formatBookmarkLabel(1, 100, 250, chapters);
      expect(result).toBe('Ch IV - Page 1');
    });
  });
});
