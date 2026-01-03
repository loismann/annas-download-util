#!/usr/bin/env python3
"""
EPUB Validator - Checks if an EPUB file is properly structured
Usage: python3 validate-epub.py <path-to-epub-file>
"""

import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

def validate_epub(epub_path):
    """Validates an EPUB file structure and reports issues"""
    print(f"Validating EPUB: {epub_path}\n")

    if not Path(epub_path).exists():
        print(f"❌ File not found: {epub_path}")
        return False

    issues = []
    warnings = []

    try:
        with zipfile.ZipFile(epub_path, 'r') as zip_ref:
            # 1. Check if it's a valid ZIP
            print("✅ Valid ZIP archive")

            # 2. List all files
            files = zip_ref.namelist()
            print(f"📁 Contains {len(files)} files")

            # 3. Find cover images
            cover_files = [f for f in files if 'cover' in f.lower() and
                          any(f.lower().endswith(ext) for ext in ['.jpg', '.jpeg', '.png', '.gif'])]

            if cover_files:
                print(f"\n📷 Cover images found:")
                for cover in cover_files:
                    file_info = zip_ref.getinfo(cover)
                    print(f"   - {cover} ({file_info.file_size} bytes)")
            else:
                warnings.append("No cover image files found")

            # 4. Find and parse content.opf
            opf_files = [f for f in files if f.endswith('.opf')]

            if not opf_files:
                issues.append("❌ No content.opf file found")
            else:
                print(f"\n📄 OPF files found: {opf_files}")

                for opf_file in opf_files:
                    print(f"\n   Analyzing {opf_file}:")

                    try:
                        with zip_ref.open(opf_file) as f:
                            content = f.read()
                            root = ET.fromstring(content)

                            # Define XML namespaces
                            ns = {'opf': 'http://www.idpf.org/2007/opf'}

                            # Find manifest items
                            manifest = root.find('.//opf:manifest', ns)
                            if manifest is not None:
                                items = manifest.findall('.//opf:item', ns)
                                print(f"   - Manifest has {len(items)} items")

                                # Find cover references
                                cover_refs = [item for item in items
                                            if 'cover' in item.get('id', '').lower() or
                                               'cover' in item.get('href', '').lower()]

                                if cover_refs:
                                    print(f"   - Cover references in manifest:")
                                    for ref in cover_refs:
                                        href = ref.get('href', 'N/A')
                                        item_id = ref.get('id', 'N/A')
                                        media_type = ref.get('media-type', 'N/A')
                                        print(f"      • id={item_id}, href={href}, type={media_type}")

                                        # Check if referenced file exists
                                        # Need to resolve relative path
                                        opf_dir = str(Path(opf_file).parent)
                                        if opf_dir == '.':
                                            full_path = href
                                        else:
                                            full_path = f"{opf_dir}/{href}"

                                        # Normalize path
                                        full_path = full_path.replace('\\', '/')

                                        if full_path not in files:
                                            # Try without directory prefix
                                            if href not in files:
                                                issues.append(f"❌ Referenced cover '{href}' not found in ZIP")
                                            else:
                                                warnings.append(f"⚠️  Cover reference path mismatch: OPF says '{full_path}', but file is '{href}'")
                                        else:
                                            print(f"      ✅ File exists in archive")
                                else:
                                    warnings.append("⚠️  No cover references found in manifest")

                            # Find metadata cover
                            metadata = root.find('.//opf:metadata', ns)
                            if metadata is not None:
                                # Check for cover in meta tags
                                meta_covers = [meta for meta in metadata.findall('.//opf:meta', ns)
                                             if meta.get('name') == 'cover']
                                if meta_covers:
                                    print(f"   - Cover metadata found:")
                                    for meta in meta_covers:
                                        print(f"      • content={meta.get('content')}")

                    except ET.ParseError as e:
                        issues.append(f"❌ Failed to parse {opf_file}: {e}")
                    except Exception as e:
                        issues.append(f"❌ Error reading {opf_file}: {e}")

            # 5. Check for mimetype file
            if 'mimetype' not in files:
                issues.append("❌ Missing 'mimetype' file")
            else:
                with zip_ref.open('mimetype') as f:
                    mimetype = f.read().decode('utf-8').strip()
                    if mimetype == 'application/epub+zip':
                        print(f"\n✅ Valid EPUB mimetype")
                    else:
                        issues.append(f"❌ Invalid mimetype: {mimetype}")

            # 6. Check for META-INF/container.xml
            if 'META-INF/container.xml' not in files:
                issues.append("❌ Missing META-INF/container.xml")
            else:
                print("✅ META-INF/container.xml exists")

    except zipfile.BadZipFile:
        issues.append("❌ File is not a valid ZIP archive")
        return False
    except Exception as e:
        issues.append(f"❌ Unexpected error: {e}")
        return False

    # Print summary
    print("\n" + "="*60)
    print("VALIDATION SUMMARY")
    print("="*60)

    if warnings:
        print("\n⚠️  WARNINGS:")
        for warning in warnings:
            print(f"   {warning}")

    if issues:
        print("\n❌ ISSUES FOUND:")
        for issue in issues:
            print(f"   {issue}")
        print("\n🔴 EPUB HAS ISSUES - May not work on Kindle")
        return False
    elif warnings:
        print("\n🟡 EPUB is valid but has warnings")
        return True
    else:
        print("\n✅ EPUB appears to be valid")
        return True


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("Usage: python3 validate-epub.py <path-to-epub-file>")
        sys.exit(1)

    epub_path = sys.argv[1]
    is_valid = validate_epub(epub_path)
    sys.exit(0 if is_valid else 1)
