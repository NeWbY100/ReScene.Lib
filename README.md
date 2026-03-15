# ReScene.Lib

A .NET library for working with ReScene (SRR/SRS) and RAR archive formats, used for scene release preservation and reconstruction.

## Structure

```
ReScene.Lib/
├── ReScene/              # Library project (net8.0 + net10.0)
│   ├── RAR/              # RAR 4.x/5.x header parsing, patching, decompression
│   ├── SRR/              # SRR file format reading and writing
│   ├── SRS/              # SRS file format reading, writing, and reconstruction
│   └── Core/             # Brute-force orchestration, comparison
└── ReScene.Tests/        # xUnit tests (809 tests)
    └── TestData/         # Real-world SRR/RAR/SRS test files
```

### RAR (`namespace ReScene.RAR`)

Low-level RAR archive header parsing and patching.

- Parses RAR 4.x and RAR 5.0 block headers (archive, file, service, comment, recovery record, etc.)
- Reads file metadata: filenames, sizes, CRCs, timestamps (DOS + NTFS precision), host OS, compression method/dictionary
- Decompresses RAR archive comments (RAR 2.x, 3.x, 5.0, and PPMd algorithms)
- In-place binary patching of RAR 4.x headers (host OS, file attributes, LARGE flag) with automatic CRC recalculation
- Transparent multi-volume RAR streaming (`RarStream`)
- SFX (self-extracting) archive support — scans for RAR markers within executables
- Detects custom scene packer signatures

### SRR (`namespace ReScene.SRR`)

SRR file format support for scene release reconstruction.

- Parses and creates SRR files, a RAR-like container that stores only headers (no file data)
- Supports embedded RAR 4.x/5.0 headers, stored files (NFO, SFV), OSO hash blocks, and volume size metadata
- Extracts stored files with path preservation

### SRS (`namespace ReScene.SRS`)

SRS file format support for media sample reconstruction.

- Parses and creates SRS files across 7 container formats: AVI, MKV, MP4, WMV, FLAC, MP3, and M2TS/Stream
- Reconstructs sample files from SRS metadata and original media files with CRC32 verification
- MP3 tag parsing (ID3v2, ID3v1, Lyrics3, APE), FLAC metadata block parsing, and MKV EBML lacing decompression

### Core (`namespace ReScene.Core`)

High-level reconstruction and comparison logic. Orchestrates brute-force WinRAR version discovery, RAR archive reconstruction from SRR metadata, and file-level comparison between SRR/SRS/RAR archives.

## Requirements

- .NET 8.0 or .NET 10.0

## Building

```bash
dotnet build ReScene/ReScene.csproj
```

## Testing

```bash
dotnet test ReScene.Tests/ReScene.Tests.csproj
```

## Dependencies

| Package | Version |
|---|---|
| [Crc32.NET](https://www.nuget.org/packages/Crc32.NET) | 1.2.0 |
| [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) | 9.0.4 |
| [CliWrap](https://www.nuget.org/packages/CliWrap) | 3.10.0 |

## License

See [LICENSE](LICENSE) for details.
