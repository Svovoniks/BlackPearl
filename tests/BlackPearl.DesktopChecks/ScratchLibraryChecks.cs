using System.IO;
using BlackPearl.Core;

// A scratch copy (ScratchLibrary, scripts/scratch-library.ps1) is a separate PC and a separate person to the room server:
// it keeps no phone-remote key, no paired phones and no room keys, while the original keeps all of them and the rest of
// the copy (settings, stored paths) is as before.
static class ScratchLibraryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "blackpearl-scratch-check-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var source = Path.Combine(root, "library"); var copy = Path.Combine(root, "copy");
            var hostKey = new string('a', 64); var phones = "[{\"id\":\"phone-1\",\"name\":\"Phone\"}]";
            var rooms = new[] { new SavedRoom("r1", "Owned room", "https://rooms.example", "ROOM1", new string('b', 64)), new SavedRoom("r2", "Joined room", "https://rooms.example", "ROOM2", new string('c', 64)), new SavedRoom("r3", "Keyless room", "https://rooms.example", "ROOM3") };
            var original = new LibraryStore(source);
            original.Setting("phoneRemote", "on"); original.Setting("phoneRemoteHostKey", hostKey); original.Setting("phoneRemotePhones", phones);
            original.Setting("rooms", Wire.Serialize(rooms)); original.Setting("name", "Viewer");
            original.Setting("folders", Wire.Serialize(new[] { new LibraryFolder(Path.Combine(source, "videos")) }));

            check(ScratchLibrary.Run(source, copy) == 0, "a library with phone and room keys copies");
            var copied = new LibraryStore(copy);
            check(copied.Setting("phoneRemoteHostKey") is null, "the copy has no phone-remote key, so the server sees another PC");
            check(copied.Setting("phoneRemotePhones") is null, "the copy has no paired phones");
            var copiedRooms = Wire.Read<SavedRoom[]>(copied.Setting("rooms")!);
            check(copiedRooms.Select(room => room.Code).SequenceEqual(rooms.Select(room => room.Code)) && copiedRooms.Select(room => room.Name).SequenceEqual(rooms.Select(room => room.Name)),
                "the copy keeps the saved rooms and their names");
            check(copiedRooms.All(room => room.HostKey is null), "the copy enters saved rooms without the user's room keys");
            check(copied.Setting("phoneRemote") == "on" && copied.Setting("name") == "Viewer", "other settings are copied as they were");
            check(copied.Setting("folders")!.Contains(Wire.Serialize(Path.Combine(copy, "videos"))[1..^1]), "stored paths still point at the copy");
            check(original.Setting("phoneRemoteHostKey") == hostKey && original.Setting("phoneRemotePhones") == phones && original.Setting("rooms") == Wire.Serialize(rooms),
                "the original keeps its phone key, phones and room keys");

            var unreadable = Path.Combine(root, "unreadable"); var unreadableCopy = Path.Combine(root, "unreadable-copy");
            new LibraryStore(unreadable).Setting("rooms", "{not a list");
            check(ScratchLibrary.Run(unreadable, unreadableCopy) == 0 && new LibraryStore(unreadableCopy).Setting("rooms") is null,
                "a rooms list the copy cannot read is left out rather than copied with its keys");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
