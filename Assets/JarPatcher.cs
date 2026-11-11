using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterLC;

public static partial class JarPatcher
{
    public const int Version = 2;

    private static readonly byte[] OverrideMarker = "serviceOverride\u0001"u8.ToArray();
    private static readonly byte?[] OverrideGate = [0xB2, null, null, 0x99, 0x00, 0x05, 0x2B, 0xB0];
    private static readonly byte[][] StringMarkers =
        ["ingest.sentry.io"u8.ToArray(), "analytics.lunarclient"u8.ToArray()];

    [GeneratedRegex(@"^https://[0-9a-f]+@o\d+\.ingest\.sentry\.io/\d+$")]
    private static partial Regex SentryDsn();

    private static string? ReplaceString(string value) =>
        SentryDsn().IsMatch(value) ? "" :
        value is "https://analytics.lunarclientprod.com" ? "http://127.0.0.1:9" : null;

    public static async Task PatchAsync(string sourcePath, string destinationPath,
        bool requireOverrideHook, bool requireSentryOff, CancellationToken token = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and destination must differ.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            int hooks = 0, sentry = 0, endpoints = 0;
            using (var source = ZipFile.OpenRead(sourcePath))
            using (var output = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var copied = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    copied.LastWriteTime = entry.LastWriteTime;
                    await using var input = entry.Open();
                    await using var dest = copied.Open();
                    if (!entry.FullName.EndsWith(".class", StringComparison.Ordinal))
                    {
                        await input.CopyToAsync(dest, token);
                        continue;
                    }
                    using var memory = new MemoryStream();
                    await input.CopyToAsync(memory, token);
                    var bytes = memory.ToArray();
                    if (bytes.AsSpan().IndexOf(OverrideMarker) >= 0 && PatchOverrideGate(bytes)) hooks++;
                    if (StringMarkers.Any(marker => bytes.AsSpan().IndexOf(marker) >= 0))
                    {
                        var (patched, dsns, others) = RewriteStrings(bytes);
                        bytes = patched;
                        sentry += dsns;
                        endpoints += others;
                    }
                    await dest.WriteAsync(bytes, token);
                }
            }
            if (requireOverrideHook && hooks != 1)
                throw new InvalidDataException(
                    $"Lunar's service override hook was not found exactly once (found {hooks}); this Lunar build needs a patcher update.");
            if (requireSentryOff && sentry == 0)
                throw new InvalidDataException(
                    $"No Sentry address found in {Path.GetFileName(sourcePath)}; refusing to launch with crash reporting possibly on. This Lunar build needs a patcher update.");
            File.Move(temporary, destinationPath, overwrite: true);
            Console.WriteLine($"Patched {Path.GetFileName(sourcePath)}: {hooks} override hook, {sentry} Sentry addresses blanked, {endpoints} analytics endpoints disabled.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool PatchOverrideGate(byte[] bytes)
    {
        var found = -1;
        for (var i = 0; i + OverrideGate.Length <= bytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < OverrideGate.Length && match; j++)
                match = OverrideGate[j] is not { } expected || bytes[i + j] == expected;
            if (!match) continue;
            if (found >= 0) throw new InvalidDataException("Ambiguous service override hook.");
            found = i;
        }
        if (found < 0) return false;
        bytes[found] = 0x03;
        bytes[found + 1] = 0x00;
        bytes[found + 2] = 0x00;
        return true;
    }

    private static (byte[] Bytes, int Sentry, int Others) RewriteStrings(byte[] bytes)
    {
        if (bytes.Length < 10 || bytes[0] != 0xCA || bytes[1] != 0xFE || bytes[2] != 0xBA || bytes[3] != 0xBE)
            return (bytes, 0, 0);
        int sentry = 0, others = 0;
        var output = new MemoryStream(bytes.Length);
        output.Write(bytes, 0, 10);
        var poolSize = bytes[8] << 8 | bytes[9];
        var offset = 10;
        for (var index = 1; index < poolSize; index++)
        {
            var start = offset;
            switch (bytes[offset])
            {
                case 1:
                    var length = bytes[offset + 1] << 8 | bytes[offset + 2];
                    offset += 3 + length;
                    var replacement = ReplaceString(Encoding.UTF8.GetString(bytes, start + 3, length));
                    if (replacement is not null)
                    {
                        var data = Encoding.ASCII.GetBytes(replacement);
                        output.WriteByte(1);
                        output.WriteByte((byte)(data.Length >> 8));
                        output.WriteByte((byte)data.Length);
                        output.Write(data);
                        if (data.Length == 0) sentry++;
                        else others++;
                        continue;
                    }
                    break;
                case 3 or 4 or 9 or 10 or 11 or 12 or 17 or 18: offset += 5; break;
                case 5 or 6: offset += 9; index++; break;
                case 7 or 8 or 16 or 19 or 20: offset += 3; break;
                case 15: offset += 4; break;
                default: throw new InvalidDataException("Unknown constant pool tag.");
            }
            output.Write(bytes, start, offset - start);
        }
        output.Write(bytes, offset, bytes.Length - offset);
        return (output.ToArray(), sentry, others);
    }
}
