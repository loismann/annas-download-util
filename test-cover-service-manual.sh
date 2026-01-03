#!/bin/bash
# Manual test script to apply cover replacement and validate the result

set -e

echo "=================================================================="
echo "EPUB Cover Replacement Manual Test"
echo "=================================================================="
echo ""

INPUT_EPUB='/Users/paulferrer/Downloads/Worth Dying For (Jack Reacher 15).epub'
OUTPUT_DIR="/tmp/epub-test-$(date +%s)"
ORIGINAL_COPY="$OUTPUT_DIR/original.epub"
MODIFIED_EPUB="$OUTPUT_DIR/modified.epub"

# Check if input exists
if [ ! -f "$INPUT_EPUB" ]; then
    echo "❌ Input file not found: $INPUT_EPUB"
    echo ""
    echo "Please update the INPUT_EPUB variable in this script to point to your EPUB file."
    exit 1
fi

# Create output directory
mkdir -p "$OUTPUT_DIR"

echo "Input:  $INPUT_EPUB"
echo "Output: $OUTPUT_DIR"
echo ""

# Copy original for comparison
echo "Step 1: Copying original EPUB..."
cp "$INPUT_EPUB" "$ORIGINAL_COPY"
echo "  ✅ Saved to: $ORIGINAL_COPY"

# Extract both EPUBs for comparison
echo ""
echo "Step 2: Extracting original EPUB..."
mkdir -p "$OUTPUT_DIR/original-extracted"
cd "$OUTPUT_DIR/original-extracted"
unzip -q "$ORIGINAL_COPY"
cd - > /dev/null
echo "  ✅ Extracted to: $OUTPUT_DIR/original-extracted"

# Now we need to manually apply cover replacement
# Since this is complex, let's use the API endpoint instead
echo ""
echo "Step 3: To test cover replacement, you have two options:"
echo ""
echo "Option A: Use the API endpoint (RECOMMENDED)"
echo "  1. Re-enable cover replacement in Program.cs (uncomment lines 3427-3441)"
echo "  2. Start API: cd annas-archive-api/src/AnnasArchive.API && dotnet run"
echo "  3. Send the book 'Worth Dying For' to Kindle via your app"
echo "  4. The API will save the file to a temp location - copy it before it's deleted"
echo "  5. Run: python3 validate-epub-kindle.py <temp-file-path>"
echo ""
echo "Option B: Test with a simple Python script"
echo "  (Creating a Python script to manipulate the EPUB...)"
echo ""

# Create a Python script to do the cover replacement
cat > "$OUTPUT_DIR/apply-cover-replacement.py" << 'PYTHON_SCRIPT'
#!/usr/bin/env python3
import sys
import zipfile
import os
import urllib.request
from pathlib import Path

def replace_cover(input_path, output_path, cover_url):
    print(f"Downloading cover from: {cover_url}")
    with urllib.request.urlopen(cover_url) as response:
        cover_data = response.read()
    print(f"  Downloaded: {len(cover_data)} bytes")

    print(f"\nProcessing EPUB...")
    with zipfile.ZipFile(input_path, 'r') as zip_in:
        with zipfile.ZipFile(output_path, 'w', zipfile.ZIP_DEFLATED) as zip_out:
            # Find existing cover
            existing_cover = None
            for name in zip_in.namelist():
                if 'cover' in name.lower() and any(name.lower().endswith(ext) for ext in ['.jpg', '.jpeg', '.png', '.gif']):
                    existing_cover = name
                    print(f"  Found existing cover: {existing_cover}")
                    break

            # Copy all files except old cover
            copied = 0
            for item in zip_in.infolist():
                if existing_cover and item.filename == existing_cover:
                    print(f"  Skipping old cover: {item.filename}")
                    continue

                data = zip_in.read(item.filename)
                zip_out.writestr(item, data)
                copied += 1

            print(f"  Copied {copied} files")

            # Add new cover (same path as old cover)
            if existing_cover:
                new_cover_path = existing_cover
            else:
                new_cover_path = "cover.jpg"

            print(f"  Adding new cover: {new_cover_path}")
            zip_out.writestr(new_cover_path, cover_data)

            print(f"  ⚠️  WARNING: content.opf NOT updated - this matches the C# service behavior!")

    print(f"\n✅ Modified EPUB created: {output_path}")

if __name__ == "__main__":
    if len(sys.argv) != 4:
        print("Usage: python3 apply-cover-replacement.py <input.epub> <output.epub> <cover-url>")
        sys.exit(1)

    replace_cover(sys.argv[1], sys.argv[2], sys.argv[3])
PYTHON_SCRIPT

chmod +x "$OUTPUT_DIR/apply-cover-replacement.py"

# Apply cover replacement
echo "Step 4: Applying cover replacement..."
COVER_URL="https://covers.openlibrary.org/b/id/8235892-L.jpg"
python3 "$OUTPUT_DIR/apply-cover-replacement.py" "$ORIGINAL_COPY" "$MODIFIED_EPUB" "$COVER_URL"

if [ ! -f "$MODIFIED_EPUB" ]; then
    echo "❌ Failed to create modified EPUB"
    exit 1
fi

# Extract modified EPUB
echo ""
echo "Step 5: Extracting modified EPUB..."
mkdir -p "$OUTPUT_DIR/modified-extracted"
cd "$OUTPUT_DIR/modified-extracted"
unzip -q "$MODIFIED_EPUB" 2>&1 || echo "  (Some warnings during extraction)"
cd - > /dev/null
echo "  ✅ Extracted to: $OUTPUT_DIR/modified-extracted"

# Validate both
echo ""
echo "=================================================================="
echo "VALIDATION RESULTS"
echo "=================================================================="

echo ""
echo "Validating ORIGINAL EPUB:"
echo "------------------------------------------------------------------"
python3 validate-epub-kindle.py "$ORIGINAL_COPY"
ORIGINAL_RESULT=$?

echo ""
echo ""
echo "Validating MODIFIED EPUB (with cover replacement):"
echo "------------------------------------------------------------------"
python3 validate-epub-kindle.py "$MODIFIED_EPUB"
MODIFIED_RESULT=$?

# Compare OPF files
echo ""
echo "=================================================================="
echo "COMPARING OPF FILES"
echo "=================================================================="

ORIGINAL_OPF=$(find "$OUTPUT_DIR/original-extracted" -name "*.opf" | head -1)
MODIFIED_OPF=$(find "$OUTPUT_DIR/modified-extracted" -name "*.opf" | head -1)

if [ -f "$ORIGINAL_OPF" ] && [ -f "$MODIFIED_OPF" ]; then
    echo "Original OPF: $ORIGINAL_OPF"
    echo "Modified OPF: $MODIFIED_OPF"
    echo ""
    echo "Checking for differences..."
    if diff -q "$ORIGINAL_OPF" "$MODIFIED_OPF" > /dev/null; then
        echo "  ⚠️  OPF files are IDENTICAL - cover replacement did NOT update metadata!"
        echo "  This is likely why Kindle rejects the file!"
    else
        echo "  OPF files are different:"
        diff "$ORIGINAL_OPF" "$MODIFIED_OPF" || true
    fi
fi

# Summary
echo ""
echo "=================================================================="
echo "SUMMARY"
echo "=================================================================="
echo "Original EPUB validation: $([ $ORIGINAL_RESULT -eq 0 ] && echo '✅ PASSED' || echo '❌ FAILED')"
echo "Modified EPUB validation: $([ $MODIFIED_RESULT -eq 0 ] && echo '✅ PASSED' || echo '❌ FAILED')"
echo ""
echo "Files saved to: $OUTPUT_DIR"
echo "  - original.epub"
echo "  - modified.epub"
echo "  - original-extracted/"
echo "  - modified-extracted/"
echo ""

if [ $ORIGINAL_RESULT -eq 0 ] && [ $MODIFIED_RESULT -ne 0 ]; then
    echo "🔴 FOUND THE BUG:"
    echo "   The original EPUB is valid, but cover replacement breaks it!"
    echo "   This confirms the EbookCoverService is causing the Kindle E999 errors."
elif [ $ORIGINAL_RESULT -eq 0 ] && [ $MODIFIED_RESULT -eq 0 ]; then
    echo "🟡 BOTH EPUBs appear valid to our validator"
    echo "   The issue may be more subtle and only detected by Kindle."
    echo "   Check the OPF comparison above for clues."
else
    echo "🔴 The original EPUB itself has issues"
fi

echo ""
echo "Next step: Examine the OPF files to see what's missing/broken"
echo "  cat '$ORIGINAL_OPF'"
echo "  cat '$MODIFIED_OPF'"
