using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace PKHeX.Core;

/// <summary>
/// Recognizes Pokémon Emerald Imperium v1.3.1 saves before the normal Gen III reader.
/// The v1.3.1 hack uses 28 logical sectors in the main 128 KiB flash region.
/// </summary>
public sealed class EmeraldImperiumReader : ISaveReader
{
    private const int RawSize = 0x20000;
    private const int MGBASizeWithRTC = 0x20010;

    public bool IsRecognized(long dataLength) => dataLength is RawSize or MGBASizeWithRTC;

    public bool TryRead(Memory<byte> data, [NotNullWhen(true)] out SaveFile? result, string? path = null)
    {
        result = null;
        if (!IsRecognized(data.Length))
            return false;

        var raw = data.Span[..RawSize];
        if (!SAV3ImperiumView.IsImperium131(raw))
            return false;

        result = new SAV3ImperiumView(data);
        return true;
    }
}
