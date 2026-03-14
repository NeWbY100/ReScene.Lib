# ReScene.Lib

A .NET library for working with ReScene (SRR/SRS) and RAR archive formats, used for scene release preservation and reconstruction.

## Structure

```
ReScene.Lib/
├── ReScene.Lib/          # Single library project
│   ├── RAR/              # RAR 4.x/5.x header parsing, patching, decompression
│   ├── SRR/              # SRR/SRS file format reading and writing
│   └── Core/             # Reconstruction, comparison, brute-force orchestration
└── ReScene.Lib.Tests/    # xUnit tests (784 tests)
    └── TestData/         # Real-world SRR/RAR/SRS test files
```

### RAR (`namespace RAR`)

Low-level RAR archive header parsing and patching.

- Parses RAR 4.x and RAR 5.0 block headers (archive, file, service, comment, recovery record, etc.)
- Reads file metadata: filenames, sizes, CRCs, timestamps (DOS + NTFS precision), host OS, compression method/dictionary
- Decompresses RAR archive comments (RAR 2.x, 3.x, 5.0, and PPMd algorithms)
- In-place binary patching of RAR 4.x headers (host OS, file attributes, LARGE flag) with automatic CRC recalculation
- Transparent multi-volume RAR streaming (`RarStream`)
- Detects custom scene packer signatures

### SRR (`namespace SRR`)

SRR and SRS file format support for scene release reconstruction.

- **SRR (Scene Release Reconstruction):** Parses and creates SRR files, a RAR-like container that stores only headers (no file data). Supports embedded RAR 4.x/5.0 headers, stored files (NFO, SFV), OSO hash blocks, and volume size metadata.
- **SRS (Sample ReScene):** Parses and creates SRS files for reconstructing sample media files. Supports AVI, MKV, MP4, WMV, FLAC, MP3, and M2TS containers. Includes MP3 tag parsing (ID3v2, ID3v1, Lyrics3, APE), FLAC metadata block parsing, and MKV EBML lacing decompression.

### Core (`namespace Core`)

High-level reconstruction and comparison logic. Orchestrates brute-force WinRAR version discovery, RAR archive reconstruction from SRR metadata, SRS sample rebuilding, and file-level comparison between SRR/SRS/RAR archives.

## Requirements

- .NET 10.0

## Building

```bash
dotnet build ReScene.Lib/ReScene.Lib.csproj
```

## Testing

```bash
dotnet test ReScene.Lib.Tests/ReScene.Lib.Tests.csproj
```

## Dependencies

| Package | Version |
|---|---|
| [Crc32.NET](https://www.nuget.org/packages/Crc32.NET) | 1.2.0 |
| [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) | 9.0.4 |
| [CliWrap](https://www.nuget.org/packages/CliWrap) | 3.10.0 |

## License

See [LICENSE](LICENSE) for details.
