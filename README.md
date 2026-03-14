# ReScene.LIB

A .NET library for working with ReScene (SRR/SRS) and RAR archive formats, used for scene release preservation and reconstruction.

## Projects

### RARLib

Low-level RAR archive header parsing and patching library.

- Parses RAR 4.x and RAR 5.0 block headers (archive, file, service, comment, recovery record, etc.)
- Reads file metadata: filenames, sizes, CRCs, timestamps (DOS + NTFS precision), host OS, compression method/dictionary
- Decompresses RAR archive comments (RAR 2.x, 3.x, 5.0, and PPMd algorithms)
- In-place binary patching of RAR 4.x headers (host OS, file attributes, LARGE flag) with automatic CRC recalculation
- Detects custom scene packer signatures

### SRRLib

SRR and SRS file format support for scene release reconstruction.

- **SRR (Scene Release Reconstruction):** Parses and creates SRR files, a RAR-like container that stores only headers (no file data). Supports embedded RAR 4.x/5.0 headers, stored files (NFO, SFV), OSO hash blocks, and volume size metadata.
- **SRS (Sample ReScene):** Parses and creates SRS files for reconstructing sample media files. Supports AVI, MKV, MP4, WMV, FLAC, MP3, and M2TS containers.

## Requirements

- .NET 8.0 or .NET 10.0

## Building

```bash
dotnet build RARLib/RARLib.csproj
dotnet build SRRLib/SRRLib.csproj
```

## Testing

```bash
dotnet test RARLib.Tests/RARLib.Tests.csproj
dotnet test SRRLib.Tests/SRRLib.Tests.csproj
```

## Dependencies

| Package | Version | Used By |
|---|---|---|
| [Crc32.NET](https://www.nuget.org/packages/Crc32.NET) | 1.2.0 | RARLib |
| [System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) | 9.0.4 | SRRLib |

## License

See [LICENSE](LICENSE) for details.
"# ReScene.Lib" 
"# ReScene.Lib" 
