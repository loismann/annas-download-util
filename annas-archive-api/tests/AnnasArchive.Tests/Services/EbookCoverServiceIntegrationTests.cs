using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AnnasArchive.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace AnnasArchive.Tests.Services;

/// <summary>
/// Integration tests for EbookCoverService to test actual EPUB file manipulation
/// </summary>
public class EbookCoverServiceIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly HttpClient _httpClient;
    private readonly EbookCoverService _service;

    public EbookCoverServiceIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _httpClient = new HttpClient();
        _service = new EbookCoverService(_httpClient);
    }

    [Fact(Skip = "Manual test - requires specific EPUB file")]
    public async Task TestCoverReplacement_OnRealEpub_CreatesValidOutput()
    {
        // Arrange
        var inputPath = "/Users/paulferrer/Downloads/Worth Dying For (Jack Reacher 15).epub";
        var outputPath = "/tmp/worth-dying-for-modified.epub";
        var coverUrl = "https://covers.openlibrary.org/b/id/8235892-L.jpg";

        if (!File.Exists(inputPath))
        {
            _output.WriteLine($"Skipping test - input file not found: {inputPath}");
            return;
        }

        try
        {
            // Act
            using (var inputStream = File.OpenRead(inputPath))
            {
                var resultStream = await _service.ReplaceCoverAsync(inputStream, coverUrl, "epub");

                // Save the modified EPUB
                using (var outputFile = File.Create(outputPath))
                {
                    await resultStream.CopyToAsync(outputFile);
                }
            }

            // Assert - file should exist and be a valid ZIP
            Assert.True(File.Exists(outputPath));

            var fileInfo = new FileInfo(outputPath);
            _output.WriteLine($"Modified EPUB created: {outputPath}");
            _output.WriteLine($"File size: {fileInfo.Length} bytes");

            _output.WriteLine("\nTo validate, run:");
            _output.WriteLine($"python3 validate-epub-kindle.py '{outputPath}'");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"ERROR: {ex.Message}");
            _output.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Creates a standalone executable test that can be run manually
    /// </summary>
    [Fact(Skip = "Manual test - creates test executable")]
    public void GenerateStandaloneTestScript()
    {
        var scriptPath = "/Users/paulferrer/Documents/personal_dev/annas-download-util/test-cover-service.sh";
        var script = @"#!/bin/bash
# Standalone test script for EbookCoverService

set -e

echo '==================================================================='
echo 'EPUB Cover Replacement Integration Test'
echo '==================================================================='
echo ''

INPUT_EPUB=""/Users/paulferrer/Downloads/Worth Dying For (Jack Reacher 15).epub""
OUTPUT_EPUB=""/tmp/worth-dying-for-modified.epub""
OUTPUT_ORIGINAL=""/tmp/worth-dying-for-original.epub""

if [ ! -f ""$INPUT_EPUB"" ]; then
    echo ""❌ Input file not found: $INPUT_EPUB""
    exit 1
fi

# Copy original for comparison
echo ""Step 1: Saving original for comparison...""
cp ""$INPUT_EPUB"" ""$OUTPUT_ORIGINAL""

# Run the test
echo ""Step 2: Running cover replacement test...""
cd annas-archive-api/tests/AnnasArchive.Tests
dotnet test --filter ""FullyQualifiedName~TestCoverReplacement_OnRealEpub"" --logger ""console;verbosity=detailed""

if [ -f ""$OUTPUT_EPUB"" ]; then
    echo """"
    echo ""Step 3: Validating original EPUB...""
    echo ""-------------------------------------------------------------------""
    python3 ../../../validate-epub-kindle.py ""$OUTPUT_ORIGINAL""

    echo """"
    echo ""Step 4: Validating modified EPUB...""
    echo ""-------------------------------------------------------------------""
    python3 ../../../validate-epub-kindle.py ""$OUTPUT_EPUB""

    echo """"
    echo ""==================================================================""
    echo ""COMPARISON""
    echo ""==================================================================""
    ORIG_SIZE=$(stat -f%z ""$OUTPUT_ORIGINAL"")
    MOD_SIZE=$(stat -f%z ""$OUTPUT_EPUB"")
    echo ""Original size: $ORIG_SIZE bytes""
    echo ""Modified size: $MOD_SIZE bytes""
    echo """"
    echo ""Files saved for inspection:""
    echo ""  Original: $OUTPUT_ORIGINAL""
    echo ""  Modified: $OUTPUT_EPUB""
    echo """"
    echo ""You can also unzip and compare:""
    echo ""  unzip -l '$OUTPUT_ORIGINAL' > /tmp/original-contents.txt""
    echo ""  unzip -l '$OUTPUT_EPUB' > /tmp/modified-contents.txt""
    echo ""  diff /tmp/original-contents.txt /tmp/modified-contents.txt""
else
    echo ""❌ Modified EPUB was not created""
    exit 1
fi
";
        File.WriteAllText(scriptPath, script);
        _output.WriteLine($"Test script created: {scriptPath}");
        _output.WriteLine("Run with: chmod +x test-cover-service.sh && ./test-cover-service.sh");
    }
}
