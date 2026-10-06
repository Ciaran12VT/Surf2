using System.IO;
using Surf2.Services;

internal static class ScratchChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "Surf2_RegressionScratch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string outside = Path.Combine(root, "source-canary.txt");
            File.WriteAllText(outside, "not an owned scratch file");
            string abandoned;
            using (var lease = new OwnedScratchLease(root))
            {
                abandoned = lease.DirectoryPath;
                string file = Path.Combine(abandoned, Guid.NewGuid().ToString("N") + ".rows");
                lease.RegisterFile(file);
                File.WriteAllText(file, "fixture rows");
                lease.ValidateOwnedPath(file);
                Throws(() => lease.ValidateOwnedPath(outside), check, "Live scratch cleanup rejects unregistered paths");
                string replaced = Path.Combine(abandoned, Guid.NewGuid().ToString("N") + ".sort");
                lease.RegisterFile(replaced);
                Directory.CreateDirectory(replaced);
                Throws(() => lease.ValidateOwnedPath(replaced), check, "Live scratch cleanup rejects a payload replaced by a directory");
                Directory.Delete(replaced);
                check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(file),
                    "Scratch recovery cannot acquire an active operation's marker");
                Throws(() => lease.RegisterFile(outside), check, "Scratch registration rejects paths outside its owned directory");
                Throws(() => lease.RegisterFile(Path.Combine(abandoned, "user.rows")), check, "Scratch registration rejects non-generated names");
                Throws(() => lease.RegisterFile(Path.Combine(abandoned, Guid.NewGuid().ToString("N") + ".txt")), check,
                    "Scratch registration rejects unowned file extensions");
            }
            check(Directory.Exists(abandoned), "Failed payload cleanup retains a bounded recovery record");
            check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.FromDays(2)) == 0, "Fresh scratch records are not expired");
            check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1 && !Directory.Exists(abandoned),
                "Expired closed scratch operation deletes only its registered files and empty directory");
            check(File.ReadAllText(outside) == "not an owned scratch file", "Scratch recovery preserves neighboring source files");

            string unknownDirectory, unknownFile;
            using (var lease = new OwnedScratchLease(root))
            {
                unknownDirectory = lease.DirectoryPath;
                unknownFile = Path.Combine(unknownDirectory, "unregistered.txt");
                File.WriteAllText(unknownFile, "leave untouched");
            }
            check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(unknownFile),
                "Unknown scratch directory contents invalidate automatic deletion ownership");
            File.Delete(unknownFile);
            check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1, "Empty abandoned operation can subsequently recover");

            string empty;
            using (var lease = new OwnedScratchLease(root)) empty = lease.DirectoryPath;
            check(!Directory.Exists(empty), "Normal scratch disposal removes the marker and empty operation directory");

            using (var lease = new OwnedScratchLease(root))
            {
                for (int i = 0; i < 2057; i++)
                    lease.RegisterFile(Path.Combine(lease.DirectoryPath, Guid.NewGuid().ToString("N") + ".sort"));
                check(true, "Scratch ownership accepts a full default sort run set plus grid indexes, rather than imposing the old 2048-file ceiling");
            }

            using (var lease = new OwnedScratchLease(root, maximumManifestEntries: 4))
            {
                for (int i = 0; i < 12; i++)
                {
                    string file = Path.Combine(lease.DirectoryPath, Guid.NewGuid().ToString("N") + ".sort");
                    lease.RegisterFile(file);
                    File.WriteAllText(file, "fixture sort");
                    Throws(() => lease.ForgetDeletedFile(file), check, "Manifest retirement refuses a live payload");
                    check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0,
                        "Repeated sort manifest compaction retains the live operation's exclusive recovery lock");
                    File.Delete(file);
                    lease.ForgetDeletedFile(file);
                }
                check(Directory.GetFiles(lease.DirectoryPath, "*.pending").Length == 0,
                    "Repeated sort manifest compaction leaves no pending replacement files");
            }

            string unmarked = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unmarked);
            File.WriteAllText(Path.Combine(unmarked, "notes.txt"), "not ours");
            check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && Directory.Exists(unmarked),
                "GUID directory names alone never establish cleanup ownership");
            Throws(() => OwnedScratchLease.RecoverAbandoned(root, TimeSpan.FromSeconds(-1)), check, "Scratch expiry rejects invalid ages");
        }
        finally
        {
            string full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("Surf2_RegressionScratch_", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(full)[24..], "N", out _))
                throw new InvalidOperationException("Refusing to clean a non-test scratch fixture.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Throws(Action action, Action<bool, string> check, string name)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or ArgumentOutOfRangeException) { check(true, name); return; }
        check(false, name);
    }
}
