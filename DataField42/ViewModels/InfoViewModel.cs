using CommunityToolkit.Mvvm.ComponentModel;
using DataField42.Interfaces;

namespace DataField42.ViewModels;
public partial class InfoViewModel : ObservableObject, IPageViewModel
{
    public string Title => "Information";

    private readonly string _version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
    public string Introduction =>
@$"Welcome to DataField Vietnam
Version: {_version}
Author: LuccaWulf — DataField Vietnam (Battlefield Vietnam port)
Based on DataField42 by Arklyiën

About:
DataField Vietnam retrieves the maps and mods you desire to play either from the server you're joining or, if the server doesn't support DataField Vietnam, from the central database.
If a server is compatible with DataField Vietnam, it syncs your game files to match its specific version. This allows seamless switching between servers featuring various mod and map versions. DataField Vietnam stores data in a cache for reuse, ensuring nothing is removed or needs to be downloaded again.
In the scenario that the central database is down DataField Vietnam should still work when joining servers that support DataField Vietnam.

Limitations:
Joining a server in-game with the wrong version of the mod or map can cause your game to crash or display an error message. To prevent this, it's advisable to connect to the server through DataField Vietnam, provided the server supports it. This precaution is essential because DataField Vietnam isn't used when connecting to a server through the in-game browser if the game files for a particular version are already present.

Synchronization Rules for DataField Vietnam:
- DataField Vietnam ensures that clients have the same files as the Data folder for seamless gameplay.
- All necessary files for the current map/mod are synchronized by DataField Vietnam.
- In the file ""/DataFieldVietnam/Synchronization rules.txt"" you can add rules that define which files to ignore during synchronization.

Applying Rules:
- Define rules to exclude specific archives from synchronization.
- Rules applied to the base RFA also affect all its patches; individual rules for patches are not permitted (files such as: xxxxx_001.rfa).
- During synchronization, the file will adhere to the first rule in the rule file that matches its criteria, ignoring all subsequent rules for that particular file.
- Format: ignore <ignore_sync_scenario> <file_type>, <mod>, <file_name>

Special Considerations:
- The map.rfa within the played mod is synced if the player lacks that map, irrespective of rules.

ignore_sync_scenario values:
- Always: Files are never synced.
- DifferentVersion: Files are synced only if no other version exists in the game directory.
- Never: Files are always synced. This is used to exclude specific files from a group of ignored files by placing this rule above it.

file_type values:
- Movie
- Music
- ModMiscFile
- Archive
- Level

Example of Synchronization rules.txt:
// Never sync BfVietnam archives:
ignore Always Archive bfvietnam *
// Exclude mod.dll from synchronization:
ignore Always ModMiscFile * mod.dll
// Never sync the textures archive if you already have one:
ignore DifferentVersion Archive * texture";
}

