using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Notch.Setup
{
    /// <summary>
    /// Replaces an installed folder with a freshly unpacked one without ever leaving the user with
    /// neither. The old files are moved aside one by one, the new ones moved in, and only when all of
    /// that has worked is the old copy thrown away. If any file cannot be moved (something still has
    /// it open), everything already moved is put back and the old version is exactly as it was.
    /// Plain System.IO so it can be tested; the test project compiles this same file.
    /// </summary>
    internal static class FolderSwap
    {
        /// <summary>The folders next to the install folder that a swap works in; leftovers of a crashed run are named like these.</summary>
        public static string StagingName(string directory, string id) => directory.TrimEnd('\\', '/') + ".new-" + id;

        public static string BackupName(string directory, string id) => directory.TrimEnd('\\', '/') + ".old-" + id;

        /// <summary>
        /// Moves the contents of <paramref name="staging"/> into <paramref name="directory"/>, with the
        /// previous contents kept in <paramref name="backup"/> until the swap has worked.
        /// </summary>
        /// <param name="retry">Called with the name of a file that is in use before the swap tries it again, so the caller can stop what holds it. Optional.</param>
        /// <exception cref="IOException">
        /// A file could not be moved. The folder is back as it was; the message names the file.
        /// </exception>
        public static void Replace(string directory, string staging, string backup, Action<string>? retry = null, int attempts = 12, int pauseMilliseconds = 300)
        {
            var movedAway = new List<KeyValuePair<string, string>>();   // original path -> where it is now
            var movedIn = new List<string>();

            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList())
                    {
                        string aside = Path.Combine(backup, Relative(directory, file));
                        MoveWithRetry(file, aside, retry, attempts, pauseMilliseconds);
                        movedAway.Add(new KeyValuePair<string, string>(file, aside));
                    }
                }

                foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToList())
                {
                    string target = Path.Combine(directory, Relative(staging, file));
                    MoveWithRetry(file, target, retry, attempts, pauseMilliseconds);
                    movedIn.Add(target);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                RollBack(movedIn, movedAway);

                // Only the empty skeleton of folders is left behind if every file went back; if one did not,
                // the backup holds the only copy of it and must stay.
                if (!Directory.Exists(backup) || !Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any())
                {
                    TryDeleteFolder(backup);
                }

                string file = (e as IOException)?.Message ?? e.Message;
                throw new IOException(
                    "The old version could not be replaced because a file is in use, so it was left as it was. " + file, e);
            }

            // Worked. What is left of the old copy is only in the way; if something still holds it, a later run clears it.
            TryDeleteFolder(backup);
            TryDeleteFolder(staging);
        }

        /// <summary>Removes folders a crashed or interrupted swap left next to <paramref name="directory"/>.</summary>
        public static void CleanLeftovers(string directory)
        {
            string trimmed = directory.TrimEnd('\\', '/');
            string? parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return;
            }

            string name = Path.GetFileName(trimmed);
            foreach (string folder in Directory.GetDirectories(parent, name + ".*"))
            {
                string suffix = Path.GetFileName(folder).Substring(name.Length);
                if (suffix.StartsWith(".new-", StringComparison.Ordinal) || suffix.StartsWith(".old-", StringComparison.Ordinal))
                {
                    TryDeleteFolder(folder);
                }
            }
        }

        private static void MoveWithRetry(string from, string to, Action<string>? retry, int attempts, int pause)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(from, to);
                    return;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    if (attempt >= attempts)
                    {
                        throw new IOException("\"" + Path.GetFileName(from) + "\" cannot be moved: " + e.Message, e);
                    }

                    // A program that is still closing, a hook that just started, a virus scanner: give it a moment.
                    retry?.Invoke(from);
                    Thread.Sleep(pause);
                }
            }
        }

        private static void RollBack(List<string> movedIn, List<KeyValuePair<string, string>> movedAway)
        {
            foreach (string placed in movedIn)
            {
                try
                {
                    File.Delete(placed);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // Left in place; the folder is about to be overwritten by the restored files or retried later.
                }
            }

            for (int i = movedAway.Count - 1; i >= 0; i--)
            {
                KeyValuePair<string, string> pair = movedAway[i];
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                        if (File.Exists(pair.Key))
                        {
                            File.Delete(pair.Key);
                        }

                        File.Move(pair.Value, pair.Key);
                        break;
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        Thread.Sleep(200);
                    }
                }
            }
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : Path.GetFileName(path);
        }

        public static void TryDeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Nothing depends on it going.
            }
        }
    }
}
