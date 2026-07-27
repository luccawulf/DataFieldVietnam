using System.Reflection;

namespace DataField42.Core.Tests;

/// <summary>
/// Applying and reverting a patch against a real copy of the game executable.
/// </summary>
/// <remarks>
/// The byte tables are checked elsewhere; what is checked here is the writing. A cave patch has to
/// create a section, put code in it and redirect a hook into it, and then put the original instruction
/// back — and reverting has to leave the executable byte-identical to how it started, apart from the
/// cave section, which is deliberately left behind because nothing jumps to it any more.
///
/// These run against the pristine retail exe, so they also cover the section-append path that a stock
/// installation actually takes. They skip themselves where the game is not installed.
/// </remarks>
public class PatchRoundTripTests
{
    private static string? PristineExe => GameExecutables.Pristine;

    private static bool Available => PristineExe != null;

    private static GamePatch Patch(string id)
    {
        var type = typeof(Bf1942Client).Assembly.GetType("Bf1942ClientPatches")!;
        var all = (GamePatch[])type.GetField("All", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return all.Single(patch => patch.Id == id);
    }

    private static string CopyPristine()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bfv_rt_{Guid.NewGuid():N}.exe");
        File.Copy(PristineExe, path);
        return path;
    }

    private static GamePatchState StateOf(GamePatch patch, string exePath)
    {
        using var exe = new FileStream(exePath, FileMode.Open, FileAccess.Read);
        return patch.GetState(exe);
    }

    [Theory]
    [InlineData("viewmodelaspect")]
    [InlineData("widescreen3d")]
    [InlineData("widescreenres")]
    [InlineData("openspy")]
    [InlineData("playerlimit")]
    [InlineData("nocd")]
    [InlineData("physicswind")]
    [InlineData("ctficons")]
    [InlineData("debugcommands")]
    [InlineData("onlinecompat")]
    [InlineData("datadiffersmodal")]
    [InlineData("rentbutton")]
    [InlineData("largeaddress")]
    public void Apply_then_revert_restores_every_patched_byte(string id)
    {
        if (!Available) return;   // no game installed on this machine

        var patch = Patch(id);
        var path = CopyPristine();
        try
        {
            var before = File.ReadAllBytes(path);
            var client = new Bf1942Client(path);

            Assert.Equal(GamePatchState.NotApplied, StateOf(patch, path));

            client.ApplyPatch(patch);
            Assert.Equal(GamePatchState.Applied, StateOf(patch, path));

            // Every hook must actually be on disk after applying.
            var applied = File.ReadAllBytes(path);
            foreach (var edit in patch.Edits)
                Assert.Equal(edit.Patched, applied.Skip(edit.Offset).Take(edit.Patched.Length).ToArray());

            client.RevertPatch(patch);
            Assert.Equal(GamePatchState.NotApplied, StateOf(patch, path));

            // Every byte the patch touched must be back to exactly what retail had.
            var reverted = File.ReadAllBytes(path);
            foreach (var edit in patch.Edits)
            {
                Assert.Equal(edit.Original, reverted.Skip(edit.Offset).Take(edit.Original.Length).ToArray());
                Assert.Equal(
                    before.Skip(edit.Offset).Take(edit.Original.Length).ToArray(),
                    reverted.Skip(edit.Offset).Take(edit.Original.Length).ToArray());
            }

            // Applying a second time must land in the same place, not compound.
            client.ApplyPatch(patch);
            Assert.Equal(GamePatchState.Applied, StateOf(patch, path));
            Assert.Equal(applied.Length, new System.IO.FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A cave patch has to leave real code behind at the address its hook jumps to.</summary>
    [Theory]
    [InlineData("viewmodelaspect")]
    [InlineData("widescreen3d")]
    [InlineData("widescreenres")]
    public void Applying_a_cave_patch_writes_the_cave_where_the_hook_points(string id)
    {
        if (!Available) return;

        var patch = Patch(id);
        var cave = patch.Cave!;
        var path = CopyPristine();
        try
        {
            new Bf1942Client(path).ApplyPatch(patch);

            using var exe = new FileStream(path, FileMode.Open, FileAccess.Read);
            var offset = PeSections.ToFileOffset(exe, cave.ContentVirtualAddress);
            Assert.NotNull(offset);

            var written = new byte[cave.Contents.Length];
            exe.Seek(offset!.Value, SeekOrigin.Begin);
            exe.ReadExactly(written, 0, written.Length);
            Assert.Equal(cave.Contents, written);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
