using System.Text;

/// <summary>One section header as it exists in the executable.</summary>
public record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize)
{
    public bool Covers(uint virtualAddress, uint length) =>
        virtualAddress >= VirtualAddress &&
        virtualAddress + length <= VirtualAddress + Math.Max(VirtualSize, RawSize);
}

/// <summary>
/// Just enough PE surgery to give a patch somewhere to put its code.
/// </summary>
/// <remarks>
/// Some patches are not byte edits at all: they redirect an instruction into a block of new code, and
/// that code has to live somewhere. BfVietnam.exe has no usable cave in .text (the largest run of
/// padding is 117 bytes) and the .tls tail only has about 70 bytes left once the download hook is in
/// it, which is not enough for the widescreen work. So we append a section.
///
/// The address is not arbitrary. On a stock executable the last section is .rsrc at 0xE24000 + 0xD60,
/// which rounds up to exactly 0xE25000 -- the same base the author's own .ctfsnd section landed on.
/// That means cave code and hook displacements written against .ctfsnd apply here unchanged.
/// </remarks>
public static class PeSections
{
    private const int SectionHeaderSize = 40;

    public static IReadOnlyList<PeSection> Read(Stream exe)
    {
        var peOffset = ReadUInt32(exe, 0x3C);
        var sectionCount = ReadUInt16(exe, peOffset + 6);
        var optionalHeaderSize = ReadUInt16(exe, peOffset + 20);
        var tableOffset = peOffset + 24 + optionalHeaderSize;

        var sections = new List<PeSection>();
        for (var i = 0; i < sectionCount; i++)
        {
            var header = tableOffset + (uint)(i * SectionHeaderSize);
            var name = Encoding.ASCII.GetString(ReadBytes(exe, header, 8)).TrimEnd('\0');
            sections.Add(new PeSection(
                name,
                VirtualAddress: ReadUInt32(exe, header + 12),
                VirtualSize: ReadUInt32(exe, header + 8),
                RawOffset: ReadUInt32(exe, header + 20),
                RawSize: ReadUInt32(exe, header + 16)));
        }
        return sections;
    }

    /// <summary>Maps a virtual address to a file offset, or null if it falls outside every section.</summary>
    public static long? ToFileOffset(Stream exe, uint virtualAddress)
    {
        var imageBase = ImageBase(exe);
        var rva = virtualAddress - imageBase;
        foreach (var section in Read(exe))
        {
            var size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                return section.RawOffset + (rva - section.VirtualAddress);
        }
        return null;
    }

    /// <summary>
    /// Makes sure a writable, executable section of at least <paramref name="virtualSize"/> bytes exists
    /// at <paramref name="expectedVirtualAddress"/>, appending one if it is not there already.
    /// </summary>
    /// <returns>True if a section was appended, false if a suitable one was already present.</returns>
    /// <exception cref="InvalidOperationException">
    /// If the section table has no room, or an existing section already occupies that address but is too
    /// small. Both mean writing anyway would corrupt the file.
    /// </exception>
    public static bool EnsureCaveSection(Stream exe, string name, uint expectedVirtualAddress, uint virtualSize)
    {
        var imageBase = ImageBase(exe);
        var expectedRva = expectedVirtualAddress - imageBase;

        foreach (var existing in Read(exe))
        {
            if (!existing.Covers(expectedRva, virtualSize))
                continue;
            // Something is already mapped there -- the author's own .ctfsnd, or a section we appended on
            // a previous run. Either way it is big enough, so reuse it rather than adding a second one.
            return false;
        }

        var peOffset = ReadUInt32(exe, 0x3C);
        var sectionCount = ReadUInt16(exe, peOffset + 6);
        var optionalHeaderSize = ReadUInt16(exe, peOffset + 20);
        var tableOffset = peOffset + 24 + optionalHeaderSize;
        var newHeader = tableOffset + (uint)(sectionCount * SectionHeaderSize);

        var sectionAlignment = ReadUInt32(exe, peOffset + 24 + 32);
        var fileAlignment = ReadUInt32(exe, peOffset + 24 + 36);

        // The new header has to fit before the first section's raw data, or it would overwrite content.
        var firstRawOffset = Read(exe).Where(s => s.RawOffset > 0).Min(s => s.RawOffset);
        if (newHeader + SectionHeaderSize > firstRawOffset)
            throw new InvalidOperationException(
                "The section table is full, so a code cave cannot be added to this executable.");

        // Land exactly where the existing sections end, which is what makes the address predictable.
        var last = Read(exe).OrderByDescending(s => s.VirtualAddress).First();
        var newRva = Align(last.VirtualAddress + Math.Max(last.VirtualSize, last.RawSize), sectionAlignment);
        if (newRva != expectedRva)
            throw new InvalidOperationException(
                $"A cave section would land at 0x{newRva + imageBase:X8}, not the expected " +
                $"0x{expectedVirtualAddress:X8}, so the patch's addresses would be wrong.");

        var rawSize = Align(virtualSize, fileAlignment);
        var rawOffset = Align((uint)exe.Length, fileAlignment);

        // Pad to the aligned start, then reserve the section body.
        exe.SetLength(rawOffset + rawSize);

        Write(exe, newHeader, Encoding.ASCII.GetBytes(name.PadRight(8, '\0')[..8]));
        WriteUInt32(exe, newHeader + 8, virtualSize);
        WriteUInt32(exe, newHeader + 12, newRva);
        WriteUInt32(exe, newHeader + 16, rawSize);
        WriteUInt32(exe, newHeader + 20, rawOffset);
        WriteUInt32(exe, newHeader + 24, 0);   // relocations
        WriteUInt32(exe, newHeader + 28, 0);   // line numbers
        WriteUInt32(exe, newHeader + 32, 0);   // counts
        WriteUInt32(exe, newHeader + 36, 0xE0000040);  // read | write | execute | initialised data

        WriteUInt16(exe, peOffset + 6, (ushort)(sectionCount + 1));
        WriteUInt32(exe, peOffset + 24 + 56, Align(newRva + virtualSize, sectionAlignment));  // SizeOfImage
        return true;
    }

    private static uint ImageBase(Stream exe) => ReadUInt32(exe, ReadUInt32(exe, 0x3C) + 24 + 28);

    private static uint Align(uint value, uint alignment) =>
        alignment == 0 ? value : (value + alignment - 1) / alignment * alignment;

    private static byte[] ReadBytes(Stream s, long offset, int count)
    {
        s.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        s.ReadExactly(buffer, 0, count);
        return buffer;
    }

    private static uint ReadUInt32(Stream s, long offset) => BitConverter.ToUInt32(ReadBytes(s, offset, 4), 0);
    private static ushort ReadUInt16(Stream s, long offset) => BitConverter.ToUInt16(ReadBytes(s, offset, 2), 0);

    private static void Write(Stream s, long offset, byte[] bytes)
    {
        s.Seek(offset, SeekOrigin.Begin);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUInt32(Stream s, long offset, uint value) => Write(s, offset, BitConverter.GetBytes(value));
    private static void WriteUInt16(Stream s, long offset, ushort value) => Write(s, offset, BitConverter.GetBytes(value));
}
