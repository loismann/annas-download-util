#!/bin/bash
# Test script to check if cover replacement is causing EPUB corruption

echo "==================================================================="
echo "EPUB Cover Replacement Test Script"
echo "==================================================================="
echo ""

# Check if EPUB file path is provided
if [ -z "$1" ]; then
    echo "Usage: ./test-cover-replacement.sh <path-to-epub> [cover-image-url]"
    echo ""
    echo "Example:"
    echo "  ./test-cover-replacement.sh '/Users/paulferrer/Downloads/Worth Dying For (Jack Reacher 15).epub' 'https://example.com/cover.jpg'"
    echo ""
    exit 1
fi

EPUB_FILE="$1"
COVER_URL="${2:-https://covers.openlibrary.org/b/id/12345-L.jpg}"

echo "📚 Input EPUB: $EPUB_FILE"
echo "🖼️  Cover URL: $COVER_URL"
echo ""

# Validate original EPUB
echo "Step 1: Validating original EPUB..."
echo "-------------------------------------------------------------------"
python3 validate-epub.py "$EPUB_FILE"
ORIGINAL_RESULT=$?
echo ""

# Create a copy for testing
TEMP_DIR=$(mktemp -d)
TEST_EPUB="$TEMP_DIR/test-book.epub"
cp "$EPUB_FILE" "$TEST_EPUB"

echo "Step 2: Simulating cover replacement..."
echo "-------------------------------------------------------------------"
echo "⚠️  Note: This would require calling your API endpoint"
echo "   To properly test, you need to:"
echo ""
echo "   1. Start your API server:"
echo "      cd annas-archive-api/src/AnnasArchive.API && dotnet run"
echo ""
echo "   2. Send a book to Kindle with a cover URL through your app"
echo ""
echo "   3. Check server logs for:"
echo "      - [send-to-kindle] Attempting cover replacement"
echo "      - [EbookCoverService] Successfully replaced EPUB cover"
echo ""
echo "   4. Save the modified EPUB and run validation on it"
echo ""

# Cleanup
rm -rf "$TEMP_DIR"

echo "==================================================================="
echo "To properly test the cover replacement issue:"
echo "==================================================================="
echo ""
echo "Option A: Test live with your API"
echo "  1. Start API: cd annas-archive-api/src/AnnasArchive.API && dotnet run"
echo "  2. Send a book to Kindle via your app (with cover URL)"
echo "  3. Watch for these logs:"
echo "     - '[send-to-kindle] Attempting cover replacement'"
echo "     - '[EbookCoverService] Successfully replaced EPUB cover'"
echo "  4. Note if Kindle email succeeds or fails"
echo ""
echo "Option B: Temporarily disable cover replacement"
echo "  1. Comment out cover replacement in Program.cs (lines 3427-3440)"
echo "  2. Rebuild and test if Kindle emails work"
echo "  3. If they work → cover service is the problem"
echo "  4. If they still fail → issue is elsewhere"
echo ""
echo "Would you like me to:"
echo "  - Disable cover replacement temporarily? (fastest test)"
echo "  - Create unit tests for the EbookCoverService?"
echo "  - Fix the content.opf update issue in EbookCoverService?"
echo ""
