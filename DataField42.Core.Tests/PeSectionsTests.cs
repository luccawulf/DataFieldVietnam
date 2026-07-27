namespace DataField42.Core.Tests;

/// <summary>
/// Appending a code-cave section to the game executable.
/// </summary>
/// <remarks>
/// Run against the real retail binary rather than a synthetic PE, because the thing worth proving is
/// that a cave lands at 0xE25000 on <em>this</em> executable — the address the author's existing
/// widescreen cave code and its hook displacements are written against. A hand-made PE would prove the
/// arithmetic and miss the point. The tests skip themselves where the game is not installed.
/// </remarks>
public class PeSectionsTests
{
    private static string? PristineExe => GameExecutables.Pristine;

    private const uint CaveVirtualAddress = 0xE25000;

    private static bool Available => PristineExe != null;

    /// <summary>Copies the retail exe somewhere disposable so nothing touches the real install.</summary>
    private static string CopyPristine()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),$"bfv_pe_{Guid.NewGuid():N}.exe");
        File.Copy(PristineExe, path);
        return path;
    }

    [Fact]
    public void A_cave_section_lands_exactly_where_the_patches_expect_it()
    {
        if (!Available) return;   // no game installed on this machine; nothing to verify against
        var path = CopyPristine();
        try
        {
            var lengthBefore = new System.IO.FileInfo(path).Length;
            using (var exe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                var appended = PeSections.EnsureCaveSection(exe, ".dfvcave", CaveVirtualAddress, 0x400);
                Assert.True(appended, "a stock executable has no cave section yet");
            }

            using (var exe = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var cave = PeSections.Read(exe).Single(section => section.Name == ".dfvcave");
                // Section VAs are stored relative to the image base.
                Assert.Equal(CaveVirtualAddress - 0x400000, cave.VirtualAddress);
                Assert.True(cave.VirtualSize >= 0x400);

                // The cave has to be reachable by file offset, or nothing can be written into it.
                var offset = PeSections.ToFileOffset(exe, CaveVirtualAddress);
                Assert.NotNull(offset);
                Assert.True(offset < exe.Length);
            }

            Assert.True(new System.IO.FileInfo(path).Length > lengthBefore, "the section body must be on disk");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Applying twice must not staple a second section on.</summary>
    [Fact]
    public void Adding_a_cave_twice_reuses_the_first_one()
    {
        if (!Available) return;   // no game installed on this machine; nothing to verify against
        var path = CopyPristine();
        try
        {
            using var exe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            Assert.True(PeSections.EnsureCaveSection(exe, ".dfvcave", CaveVirtualAddress, 0x400));

            var countAfterFirst = PeSections.Read(exe).Count;
            Assert.False(PeSections.EnsureCaveSection(exe, ".dfvcave", CaveVirtualAddress, 0x400),
                "the second call should find the existing section");
            Assert.Equal(countAfterFirst, PeSections.Read(exe).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The author's own exe already carries a .ctfsnd section at this address. We must reuse it rather
    /// than adding a competing one on top.
    /// </summary>
    [Fact]
    public void An_existing_section_at_that_address_is_reused()
    {
        var patched = GameExecutables.Patched;
        if (patched == null) return;   // not present on this machine

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),$"bfv_ctfsnd_{Guid.NewGuid():N}.exe");
        File.Copy(patched, path);
        try
        {
            using var exe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            Assert.Contains(PeSections.Read(exe), section => section.Name == ".ctfsnd");
            Assert.False(PeSections.EnsureCaveSection(exe, ".dfvcave", CaveVirtualAddress, 0x400),
                ".ctfsnd already covers this address, so nothing should be appended");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
