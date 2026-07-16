namespace DataField42.Core.Tests;

/// <summary>
/// The client parses every file-info line the server offers, and rejects anything it does not
/// recognise by throwing -- which aborts the whole sync rather than skipping one file. So the
/// client's allow-list and the server's MOD_MISC_FILES have to agree.
/// </summary>
/// <remarks>
/// These lines are copied verbatim from a real offer by the BFV DataField42 server for
/// DiceCity_V/THE_CITY. A live sync failed with "Illegal file: levelcheck.con" because the manifest
/// was added server-side but not here.
/// </remarks>
public class FileInfoTests
{
    private static FileInfo Parse(string line) => new(line.Split(' '));

    [Theory]
    // mod "path" crc32 size lastModified
    [InlineData("BFVietnam \"LevelCheck.con\" E3F1378D 12498 1780103395")]
    [InlineData("BFVietnam \"init.con\" 3A7AAE44 446 1098104682")]
    [InlineData("BFVietnam \"mod.dll\" 47A7164E 4096 1690648446")]
    [InlineData("BFVietnam \"lexiconAll.dat\" 8B7549B6 1034708 1095764984")]
    [InlineData("BFVietnam \"bfdist.vlu\" 389B69CD 128 1017246068")]
    [InlineData("DiceCity_V \"Archives/BfVietnam/Levels/The_City.rfa\" 692A8093 10108686 1116194414")]
    [InlineData("DiceCity_V \"Archives/texture.rfa\" 0E23D700 180794804 1116127290")]
    [InlineData("BFVietnam \"Archives/BfVietnam/game.rfa\" 33FBBAE8 119115 1095868318")]
    [InlineData("BFVietnam \"Movies/background.bik\" 65C0EFAB 5647744 1076942444")]
    [InlineData("BFVietnam \"Music/menumusic.bik\" B06FA3A1 3097520 1076403688")]
    public void Real_server_offer_lines_parse(string line)
    {
        var fileInfo = Parse(line);
        Assert.NotEqual(Bf1942FileType.None, fileInfo.FileType);
    }

    [Fact]
    public void Levelcheck_con_is_a_mod_misc_file()
    {
        var fileInfo = Parse("BFVietnam \"LevelCheck.con\" E3F1378D 12498 1780103395");

        Assert.Equal(Bf1942FileType.ModMiscFile, fileInfo.FileType);
        Assert.Equal("BFVietnam", fileInfo.Mod);
    }

    [Fact]
    public void Bfv_level_rfa_is_a_level()
    {
        var fileInfo = Parse("DiceCity_V \"Archives/BfVietnam/Levels/The_City.rfa\" 692A8093 10108686 1116194414");

        Assert.Equal(Bf1942FileType.Level, fileInfo.FileType);
        Assert.Equal("DiceCity_V", fileInfo.Mod);
    }

    [Fact]
    public void Bfv_archive_is_an_archive()
    {
        var fileInfo = Parse("BFVietnam \"Archives/texture.rfa\" DEC4AA49 231975663 1077142400");

        Assert.Equal(Bf1942FileType.Archive, fileInfo.FileType);
    }

    /// <summary>
    /// An unknown misc file must still be rejected -- the allow-list is a safety control, not noise.
    /// </summary>
    [Fact]
    public void Unknown_misc_file_is_still_rejected()
    {
        Assert.Throws<Exception>(() => Parse("BFVietnam \"evil.exe\" DEADBEEF 100 1"));
    }
}
