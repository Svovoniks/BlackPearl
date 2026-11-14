using System.Security.Cryptography;
using System.Text;

namespace BlackPearl.Core;

// HostKeyHash identifies the room's creator. Guests are its members: everyone who has entered with the invitation, and
// people admitted by its host from an app that still has one. Banned are the members the creator removed.
public record PersistentRoom(string Code, string HostKeyHash, string[]? ApprovedGuests = null, bool SharedControls = false, AdmittedGuest[]? Guests = null, string? Name = null,
    AdmittedGuest[]? Banned = null);
public sealed class PersistentRooms
{
    private readonly LibraryStore store;
    private readonly List<PersistentRoom> rooms;
    private readonly object gate = new();
    // Each room is its own settings row, so a change rewrites one room instead of every saved room.
    private const string Prefix = "persistentRoom:";
    public PersistentRooms(string directory)
    {
        store = new(directory);
        rooms = store.SettingsWithPrefix(Prefix).Values.Select(Wire.Read<PersistentRoom>).ToList();
        // Move rooms saved by older servers as one list into their own rows.
        var legacy = Wire.Read<List<PersistentRoom>>(store.Setting("persistentRooms") ?? "[]");
        if (legacy.Count > 0)
        {
            var known = rooms.Select(x => x.Code).ToHashSet();
            var moved = legacy.Where(x => known.Add(x.Code)).ToArray();
            store.SetSettings(moved.Select(room => KeyValuePair.Create(Prefix + room.Code, Wire.Serialize(room))));
            rooms.AddRange(moved);
            store.Setting("persistentRooms", "[]");
        }
    }
    private void Save(PersistentRoom room) => store.Setting(Prefix + room.Code, Wire.Serialize(room));
    public PersistentRoom[] All() { lock (gate) return rooms.ToArray(); }
    public int Count { get { lock (gate) return rooms.Count; } }
    // sharedControls is the setting of protocol 1 apps, whose rooms keep it on the service.
    public void Update(string code, AdmittedGuest[] members, AdmittedGuest[] banned, string? name, bool sharedControls = false)
    {
        lock (gate)
        {
            var index = rooms.FindIndex(x => x.Code == code);
            if (index < 0) return;
            var updated = rooms[index] with { ApprovedGuests = members.Select(x => x.Id).ToArray(), Guests = members, Banned = banned, Name = name ?? rooms[index].Name, SharedControls = sharedControls };
            // Members reconnect often; write only what changed.
            if (Wire.Serialize(updated) == Wire.Serialize(rooms[index])) return;
            rooms[index] = updated;
            Save(updated);
        }
    }
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    // Returns the creator's key; only its hash is stored.
    public string Create(string code, string? name = null)
    {
        lock (gate)
        {
            var key = NewKey();
            rooms.Add(new(code, Hash(key), Name: name));
            Save(rooms[^1]);
            return key;
        }
    }
}
