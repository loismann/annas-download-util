#!/usr/bin/env python3
"""
Enhanced EPUB Validator - Checks Kindle-specific compatibility issues
Usage: python3 validate-epub-kindle.py <path-to-epub-file>
"""

import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path
import os

def check_kindle_compatibility(epub_path):
    """Checks for common issues that cause Kindle E999 errors"""
    print(f"Validating EPUB for Kindle compatibility: {epub_path}\n")

    if not Path(epub_path).exists():
        print(f"❌ File not found: {epub_path}")
        return False

    errors = []
    warnings = []
    info = []

    try:
        with zipfile.ZipFile(epub_path, 'r') as zip_ref:
            print("=" * 70)
            print("BASIC STRUCTURE CHECKS")
            print("=" * 70)

            # 1. File size check
            file_size = os.path.getsize(epub_path)
            file_size_mb = file_size / (1024 * 1024)
            print(f"📦 File size: {file_size_mb:.2f} MB")
            if file_size_mb > 50:
                errors.append(f"File too large ({file_size_mb:.2f} MB). Kindle limit is 50 MB")
            elif file_size_mb > 25:
                warnings.append(f"File is large ({file_size_mb:.2f} MB). May cause issues.")

            # 2. Check ZIP integrity
            try:
                bad = zip_ref.testzip()
                if bad:
                    errors.append(f"Corrupted file in archive: {bad}")
                else:
                    print("✅ ZIP integrity check passed")
            except Exception as e:
                errors.append(f"ZIP integrity check failed: {e}")

            files = zip_ref.namelist()
            print(f"📁 Contains {len(files)} files\n")

            # 3. Mimetype check (CRITICAL for Kindle)
            print("=" * 70)
            print("MIMETYPE CHECK (Critical for Kindle)")
            print("=" * 70)
            if 'mimetype' not in files:
                errors.append("Missing 'mimetype' file - EPUB will fail on Kindle")
            else:
                # Check mimetype is first file and uncompressed
                mimetype_info = zip_ref.getinfo('mimetype')
                with zip_ref.open('mimetype') as f:
                    mimetype_content = f.read().decode('utf-8').strip()

                print(f"Content: '{mimetype_content}'")
                print(f"Compression: {mimetype_info.compress_type} (should be 0 for uncompressed)")

                if mimetype_content != 'application/epub+zip':
                    errors.append(f"Invalid mimetype: '{mimetype_content}'")
                else:
                    print("✅ Mimetype content correct")

                if mimetype_info.compress_type != 0:
                    errors.append("Mimetype file is compressed - should be stored uncompressed")
                else:
                    print("✅ Mimetype stored uncompressed")

            # 4. Container.xml check
            print("\n" + "=" * 70)
            print("CONTAINER.XML CHECK")
            print("=" * 70)
            container_path = 'META-INF/container.xml'
            if container_path not in files:
                errors.append("Missing META-INF/container.xml")
            else:
                try:
                    with zip_ref.open(container_path) as f:
                        content = f.read()
                        root = ET.fromstring(content)
                        print("✅ Container.xml is valid XML")

                        # Find rootfile
                        ns = {'container': 'urn:oasis:names:tc:opendocument:xmlns:container'}
                        rootfiles = root.findall('.//container:rootfile', ns)
                        if not rootfiles:
                            errors.append("No rootfile found in container.xml")
                        else:
                            for rf in rootfiles:
                                opf_path = rf.get('full-path')
                                print(f"📄 OPF location: {opf_path}")
                except Exception as e:
                    errors.append(f"Failed to parse container.xml: {e}")

            # 5. Find and validate OPF files
            print("\n" + "=" * 70)
            print("CONTENT.OPF VALIDATION (Critical for Kindle)")
            print("=" * 70)

            opf_files = [f for f in files if f.endswith('.opf')]
            if not opf_files:
                errors.append("No .opf file found")
            else:
                for opf_file in opf_files:
                    print(f"\n📄 Analyzing: {opf_file}")
                    print("-" * 70)

                    try:
                        with zip_ref.open(opf_file) as f:
                            content = f.read()
                            root = ET.fromstring(content)

                            ns = {'opf': 'http://www.idpf.org/2007/opf',
                                  'dc': 'http://purl.org/dc/elements/1.1/'}

                            # Check manifest
                            manifest = root.find('.//opf:manifest', ns)
                            if manifest is None:
                                errors.append(f"{opf_file}: No manifest found")
                            else:
                                items = manifest.findall('.//opf:item', ns)
                                print(f"   Manifest items: {len(items)}")

                                # Validate each manifest item
                                opf_dir = str(Path(opf_file).parent)
                                missing_files = []
                                duplicate_ids = {}

                                for item in items:
                                    item_id = item.get('id')
                                    href = item.get('href')
                                    media_type = item.get('media-type')

                                    # Check for duplicate IDs
                                    if item_id:
                                        duplicate_ids[item_id] = duplicate_ids.get(item_id, 0) + 1

                                    # Check if file exists
                                    if href:
                                        # Resolve relative path
                                        if opf_dir and opf_dir != '.':
                                            full_path = f"{opf_dir}/{href}"
                                        else:
                                            full_path = href

                                        # Normalize path
                                        full_path = full_path.replace('\\', '/')

                                        # Check various path formats
                                        found = False
                                        if full_path in files:
                                            found = True
                                        elif href in files:
                                            found = True
                                        elif full_path.lstrip('/') in files:
                                            found = True

                                        if not found:
                                            missing_files.append((item_id, href, full_path))

                                # Report duplicate IDs
                                for item_id, count in duplicate_ids.items():
                                    if count > 1:
                                        errors.append(f"Duplicate manifest ID '{item_id}' appears {count} times")

                                # Report missing files
                                if missing_files:
                                    print(f"\n   ❌ MISSING FILES REFERENCED IN MANIFEST:")
                                    for item_id, href, full_path in missing_files:
                                        print(f"      • ID: {item_id}")
                                        print(f"        Referenced: {href}")
                                        print(f"        Tried path: {full_path}")
                                        errors.append(f"{opf_file}: Referenced file not found: {href}")
                                else:
                                    print(f"   ✅ All manifest items reference existing files")

                                # Check for cover specifically
                                print(f"\n   Cover Image Check:")
                                cover_items = [item for item in items
                                             if 'cover' in item.get('id', '').lower() or
                                                'cover' in item.get('href', '').lower()]

                                if cover_items:
                                    for item in cover_items:
                                        item_id = item.get('id')
                                        href = item.get('href')
                                        media_type = item.get('media-type')
                                        print(f"      • ID: {item_id}")
                                        print(f"        href: {href}")
                                        print(f"        media-type: {media_type}")

                                        # Validate media type for images
                                        if href and any(href.lower().endswith(ext) for ext in ['.jpg', '.jpeg', '.png', '.gif']):
                                            if not media_type or not media_type.startswith('image/'):
                                                errors.append(f"Cover image has invalid media-type: {media_type}")
                                else:
                                    warnings.append("No cover image found in manifest")

                            # Check spine
                            spine = root.find('.//opf:spine', ns)
                            if spine is None:
                                errors.append(f"{opf_file}: No spine found")
                            else:
                                itemrefs = spine.findall('.//opf:itemref', ns)
                                print(f"\n   Spine items: {len(itemrefs)}")

                                # Validate spine references
                                if manifest is not None:
                                    manifest_ids = {item.get('id') for item in manifest.findall('.//opf:item', ns)}
                                    for itemref in itemrefs:
                                        idref = itemref.get('idref')
                                        if idref and idref not in manifest_ids:
                                            errors.append(f"Spine references non-existent ID: {idref}")

                                if len(itemrefs) == 0:
                                    errors.append(f"{opf_file}: Spine is empty")
                                else:
                                    print(f"   ✅ Spine references validate")

                            # Check metadata
                            metadata = root.find('.//opf:metadata', ns)
                            if metadata is None:
                                errors.append(f"{opf_file}: No metadata found")
                            else:
                                # Check for cover meta
                                cover_metas = [m for m in metadata.findall('.//opf:meta', ns)
                                             if m.get('name') == 'cover']
                                if cover_metas:
                                    print(f"\n   Cover metadata:")
                                    for meta in cover_metas:
                                        content_id = meta.get('content')
                                        print(f"      • References ID: {content_id}")

                                        # Validate the ID exists in manifest
                                        if manifest is not None:
                                            manifest_ids = {item.get('id') for item in manifest.findall('.//opf:item', ns)}
                                            if content_id not in manifest_ids:
                                                errors.append(f"Cover metadata references non-existent ID: {content_id}")
                                            else:
                                                print(f"      ✅ ID exists in manifest")

                    except ET.ParseError as e:
                        errors.append(f"Failed to parse {opf_file}: {e}")
                    except Exception as e:
                        errors.append(f"Error processing {opf_file}: {e}")

            # 6. Check for actual cover image files
            print("\n" + "=" * 70)
            print("COVER IMAGE FILES")
            print("=" * 70)
            cover_files = [f for f in files if 'cover' in f.lower() and
                          any(f.lower().endswith(ext) for ext in ['.jpg', '.jpeg', '.png', '.gif'])]

            if cover_files:
                print(f"Found {len(cover_files)} cover image file(s):")
                for cover in cover_files:
                    file_info = zip_ref.getinfo(cover)
                    print(f"   📷 {cover}")
                    print(f"      Size: {file_info.file_size} bytes")
                    print(f"      Compressed: {file_info.compress_type != 0}")
            else:
                warnings.append("No cover image files found in archive")

    except zipfile.BadZipFile:
        errors.append("File is not a valid ZIP archive")
        return False
    except Exception as e:
        errors.append(f"Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        return False

    # Print summary
    print("\n" + "=" * 70)
    print("KINDLE COMPATIBILITY SUMMARY")
    print("=" * 70)

    if info:
        print("\nℹ️  INFORMATION:")
        for i in info:
            print(f"   {i}")

    if warnings:
        print("\n⚠️  WARNINGS:")
        for warning in warnings:
            print(f"   {warning}")

    if errors:
        print("\n❌ ERRORS THAT WILL CAUSE KINDLE REJECTION:")
        for i, error in enumerate(errors, 1):
            print(f"   {i}. {error}")
        print(f"\n🔴 EPUB HAS {len(errors)} CRITICAL ERROR(S) - WILL FAIL ON KINDLE")
        return False
    elif warnings:
        print("\n🟡 EPUB should work on Kindle but has warnings")
        return True
    else:
        print("\n✅ EPUB appears to be Kindle-compatible")
        return True


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("Usage: python3 validate-epub-kindle.py <path-to-epub-file>")
        sys.exit(1)

    epub_path = sys.argv[1]
    is_valid = check_kindle_compatibility(epub_path)
    sys.exit(0 if is_valid else 1)
