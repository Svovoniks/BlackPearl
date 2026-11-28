namespace BlackPearl.Core;

// Another program can hold a file open for a moment: an antivirus scanning a script or picture that was just written,
// the search indexer, or a reader in this app. While it does, Windows refuses to replace that file, with "Access to the
// path is denied" even when the other program shares it. Directory.Delete then deletes the files in a folder but leaves
// the folder itself, without an error, because a file still open is only marked to go once it's closed. Both settle as
// soon as the other program lets go, so these try again for a few seconds before giving up.
public static class FileWrites
{
    private const int PatienceMilliseconds = 3000, PauseMilliseconds = 50;

    // Moves source over target, as File.Move with overwrite does.
    public static void Replace(string source, string target)
    {
        var deadline = Environment.TickCount64 + PatienceMilliseconds;
        while (true)
        {
            try { File.Move(source, target, true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ex is not FileNotFoundException and not DirectoryNotFoundException
                && Environment.TickCount64 < deadline) { Thread.Sleep(PauseMilliseconds); }
        }
    }

    // Deletes a folder with everything in it, and returns once the folder itself is gone.
    public static void DeleteFolder(string path)
    {
        var deadline = Environment.TickCount64 + PatienceMilliseconds;
        while (Directory.Exists(path))
        {
            try { Directory.Delete(path, true); }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && Environment.TickCount64 < deadline) { }
            if (!Directory.Exists(path)) return;
            if (Environment.TickCount64 >= deadline) throw new IOException($"{Path.GetFileName(path)} couldn't be deleted because another program is using it.");
            Thread.Sleep(PauseMilliseconds);
        }
    }
}
