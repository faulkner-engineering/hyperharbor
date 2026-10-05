namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// JSON bytes for System.Text.Json, which rejects a UTF-8 byte order mark when reading raw bytes. HyperHarbor
/// never writes one, but a file saved by hand (Notepad, PowerShell 5's Set-Content) or a manifest from another
/// tool may start with it.
/// </summary>
public static class Utf8Json
{
    public static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("﻿"u8) ? bytes[3..] : bytes;

    public static byte[] ReadFile(string path) => WithoutBom(File.ReadAllBytes(path)).ToArray();
}
