#!/usr/bin/env python3
"""
PDF to EPUB Converter with OCR
Converts scanned PDFs to EPUB format using Tesseract OCR
"""

import argparse
import logging
import sys
import os
from pathlib import Path
from typing import List, Optional
import tempfile
import shutil

try:
    import pytesseract
    from pdf2image import convert_from_path
    from PIL import Image
    from ebooklib import epub
    from tqdm import tqdm
    from colorama import Fore, Style, init as colorama_init
except ImportError as e:
    print(f"Error: Missing required dependency: {e}")
    print("Please install requirements: pip install -r requirements.txt")
    sys.exit(1)


class ColoredFormatter(logging.Formatter):
    """Custom formatter with colors for terminal output"""

    COLORS = {
        'DEBUG': Fore.CYAN,
        'INFO': Fore.GREEN,
        'WARNING': Fore.YELLOW,
        'ERROR': Fore.RED,
        'CRITICAL': Fore.RED + Style.BRIGHT,
    }

    def format(self, record):
        levelname = record.levelname
        if levelname in self.COLORS:
            record.levelname = f"{self.COLORS[levelname]}{levelname}{Style.RESET_ALL}"
        return super().format(record)


def setup_logging(verbose: bool = False) -> logging.Logger:
    """Configure logging with colored output"""
    colorama_init(autoreset=True)

    logger = logging.getLogger('pdf2epub')
    logger.setLevel(logging.DEBUG if verbose else logging.INFO)

    # Console handler with colors
    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setLevel(logging.DEBUG if verbose else logging.INFO)

    formatter = ColoredFormatter(
        '%(asctime)s - %(levelname)s - %(message)s',
        datefmt='%H:%M:%S'
    )
    console_handler.setFormatter(formatter)

    logger.addHandler(console_handler)
    return logger


class PDFToEPubConverter:
    """Convert PDF files to EPUB using OCR"""

    def __init__(self,
                 pdf_path: str,
                 output_path: Optional[str] = None,
                 title: Optional[str] = None,
                 author: Optional[str] = None,
                 language: str = 'eng',
                 dpi: int = 300,
                 logger: Optional[logging.Logger] = None):
        """
        Initialize the converter

        Args:
            pdf_path: Path to input PDF file
            output_path: Path for output EPUB file (optional)
            title: Book title (optional, defaults to filename)
            author: Book author (optional)
            language: OCR language code (default: 'eng')
            dpi: DPI for PDF rendering (default: 300)
            logger: Logger instance (optional)
        """
        self.pdf_path = Path(pdf_path)
        self.output_path = Path(output_path) if output_path else self.pdf_path.with_suffix('.epub')
        self.title = title or self.pdf_path.stem
        self.author = author or 'Unknown'
        self.language = language
        self.dpi = dpi
        self.logger = logger or logging.getLogger('pdf2epub')

        if not self.pdf_path.exists():
            raise FileNotFoundError(f"PDF file not found: {pdf_path}")

    def _extract_pages_as_images(self, temp_dir: str) -> List[Path]:
        """
        Convert PDF pages to images

        Args:
            temp_dir: Temporary directory for storing images

        Returns:
            List of image file paths
        """
        self.logger.info(f"📄 Converting PDF to images (DPI: {self.dpi})...")

        try:
            images = convert_from_path(
                self.pdf_path,
                dpi=self.dpi,
                output_folder=temp_dir,
                fmt='png'
            )

            image_paths = []
            for i, image in enumerate(images, 1):
                img_path = Path(temp_dir) / f'page_{i:04d}.png'
                image.save(img_path, 'PNG')
                image_paths.append(img_path)

            self.logger.info(f"✅ Extracted {len(image_paths)} pages")
            return image_paths

        except Exception as e:
            self.logger.error(f"Failed to convert PDF to images: {e}")
            raise

    def _ocr_image(self, image_path: Path) -> str:
        """
        Perform OCR on a single image

        Args:
            image_path: Path to image file

        Returns:
            Extracted text
        """
        try:
            image = Image.open(image_path)
            text = pytesseract.image_to_string(image, lang=self.language)
            return text.strip()
        except Exception as e:
            self.logger.warning(f"OCR failed for {image_path.name}: {e}")
            return ""

    def _process_pages(self, image_paths: List[Path]) -> List[str]:
        """
        Process all pages with OCR

        Args:
            image_paths: List of image file paths

        Returns:
            List of extracted text for each page
        """
        self.logger.info(f"🔍 Running OCR on {len(image_paths)} pages...")

        page_texts = []
        with tqdm(total=len(image_paths), desc="OCR Progress", unit="page") as pbar:
            for img_path in image_paths:
                text = self._ocr_image(img_path)
                page_texts.append(text)

                # Log progress
                word_count = len(text.split())
                self.logger.debug(f"Page {len(page_texts)}: {word_count} words extracted")

                pbar.update(1)

        total_words = sum(len(text.split()) for text in page_texts)
        self.logger.info(f"✅ OCR complete - {total_words} total words extracted")

        return page_texts

    def _create_epub(self, page_texts: List[str]) -> None:
        """
        Create EPUB file from extracted text

        Args:
            page_texts: List of text content for each page
        """
        self.logger.info(f"📚 Creating EPUB: {self.output_path.name}")

        # Create EPUB book
        book = epub.EpubBook()

        # Set metadata
        book.set_identifier(f'id_{self.pdf_path.stem}')
        book.set_title(self.title)
        book.set_language(self.language[:2])  # Use 2-letter language code
        book.add_author(self.author)

        # Create chapters (combine pages into chapters of ~10 pages each)
        chapters = []
        pages_per_chapter = 10

        for i in range(0, len(page_texts), pages_per_chapter):
            chapter_num = (i // pages_per_chapter) + 1
            chapter_pages = page_texts[i:i + pages_per_chapter]

            # Combine pages with page markers
            content = []
            for j, page_text in enumerate(chapter_pages, 1):
                page_num = i + j
                if page_text:
                    content.append(f'<div class="page" id="page{page_num}">')
                    content.append(f'<p class="page-number">Page {page_num}</p>')
                    # Convert plain text to HTML paragraphs
                    paragraphs = page_text.split('\n\n')
                    for para in paragraphs:
                        if para.strip():
                            content.append(f'<p>{para.strip()}</p>')
                    content.append('</div>')

            # Create chapter
            chapter = epub.EpubHtml(
                title=f'Chapter {chapter_num}',
                file_name=f'chap_{chapter_num:03d}.xhtml',
                lang=self.language[:2]
            )
            chapter.content = '<html><body>' + ''.join(content) + '</body></html>'

            book.add_item(chapter)
            chapters.append(chapter)

        # Add CSS for styling
        style = '''
        @namespace epub "http://www.idpf.org/2007/ops";
        body {
            font-family: Georgia, serif;
            margin: 20px;
        }
        p {
            text-align: justify;
            margin: 0.5em 0;
            line-height: 1.6;
        }
        .page {
            page-break-after: always;
            margin-bottom: 2em;
        }
        .page-number {
            font-size: 0.8em;
            color: #666;
            text-align: center;
            margin: 1em 0;
        }
        '''
        nav_css = epub.EpubItem(
            uid="style_nav",
            file_name="style/nav.css",
            media_type="text/css",
            content=style
        )
        book.add_item(nav_css)

        # Add navigation
        book.toc = chapters
        book.add_item(epub.EpubNcx())
        book.add_item(epub.EpubNav())

        # Define spine
        book.spine = ['nav'] + chapters

        # Write EPUB file
        epub.write_epub(self.output_path, book)

        file_size = self.output_path.stat().st_size / (1024 * 1024)  # MB
        self.logger.info(f"✅ EPUB created successfully ({file_size:.2f} MB)")

    def convert(self) -> Path:
        """
        Perform the complete conversion process

        Returns:
            Path to created EPUB file
        """
        self.logger.info(f"{'='*60}")
        self.logger.info(f"PDF to EPUB Converter with OCR")
        self.logger.info(f"{'='*60}")
        self.logger.info(f"Input:  {self.pdf_path}")
        self.logger.info(f"Output: {self.output_path}")
        self.logger.info(f"Title:  {self.title}")
        self.logger.info(f"Author: {self.author}")
        self.logger.info(f"{'='*60}")

        # Create temporary directory for images
        temp_dir = tempfile.mkdtemp(prefix='pdf2epub_')

        try:
            # Step 1: Extract PDF pages as images
            image_paths = self._extract_pages_as_images(temp_dir)

            # Step 2: OCR all pages
            page_texts = self._process_pages(image_paths)

            # Step 3: Create EPUB
            self._create_epub(page_texts)

            self.logger.info(f"{'='*60}")
            self.logger.info(f"✅ Conversion complete!")
            self.logger.info(f"{'='*60}")

            return self.output_path

        except Exception as e:
            self.logger.error(f"Conversion failed: {e}")
            raise

        finally:
            # Clean up temporary directory
            if os.path.exists(temp_dir):
                shutil.rmtree(temp_dir)
                self.logger.debug(f"Cleaned up temporary directory: {temp_dir}")


def main():
    """Main entry point for CLI"""
    parser = argparse.ArgumentParser(
        description='Convert PDF files to EPUB using OCR',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog='''
Examples:
  %(prog)s input.pdf
  %(prog)s input.pdf -o output.epub
  %(prog)s input.pdf --title "My Book" --author "John Doe"
  %(prog)s input.pdf --language fra --dpi 400 -v
        '''
    )

    parser.add_argument('pdf_file', help='Input PDF file path')
    parser.add_argument('-o', '--output', help='Output EPUB file path (default: same as input with .epub extension)')
    parser.add_argument('-t', '--title', help='Book title (default: filename)')
    parser.add_argument('-a', '--author', help='Book author (default: Unknown)')
    parser.add_argument('-l', '--language', default='eng',
                       help='OCR language code (default: eng). Examples: eng, fra, deu, spa')
    parser.add_argument('-d', '--dpi', type=int, default=300,
                       help='DPI for PDF rendering (default: 300, higher = better quality but slower)')
    parser.add_argument('-v', '--verbose', action='store_true',
                       help='Enable verbose debug logging')

    args = parser.parse_args()

    # Setup logging
    logger = setup_logging(verbose=args.verbose)

    try:
        # Check if Tesseract is installed
        try:
            pytesseract.get_tesseract_version()
        except Exception:
            logger.error("Tesseract OCR is not installed!")
            logger.error("Install it with: brew install tesseract (macOS) or apt-get install tesseract-ocr (Linux)")
            sys.exit(1)

        # Create converter and run
        converter = PDFToEPubConverter(
            pdf_path=args.pdf_file,
            output_path=args.output,
            title=args.title,
            author=args.author,
            language=args.language,
            dpi=args.dpi,
            logger=logger
        )

        output_path = converter.convert()

        logger.info(f"📖 Output file: {output_path}")

    except KeyboardInterrupt:
        logger.warning("\n⚠️  Conversion interrupted by user")
        sys.exit(1)
    except Exception as e:
        logger.error(f"❌ Error: {e}")
        if args.verbose:
            logger.exception("Detailed error:")
        sys.exit(1)


if __name__ == '__main__':
    main()
