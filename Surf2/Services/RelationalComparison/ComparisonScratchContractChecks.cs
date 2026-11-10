using System.IO;
using System.Globalization;
using System.Text;

namespace Surf2.Services.RelationalComparison;

public static class ComparisonScratchContractChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Surf2ComparisonScratchChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        var limits = new ComparisonLimits { StagingDirectory = root };
        var scratches = new List<ComparisonScratch>();
        var fixtures = new List<(string Directory, string File)>();
        var leases = new List<OwnedScratchLease>();
        var directoryLinks = new List<string>();
        const string markerName = ".surf2-owned-v1";
        try
        {
            var live = NewScratch();
            string livePath = live.NewFile(); File.WriteAllText(livePath, "live");
            Check(Guid.TryParseExact(Path.GetFileName(live.DirectoryPath), "N", out _) &&
                Guid.TryParseExact(Path.GetFileNameWithoutExtension(livePath), "N", out _),
                "Comparison operations and payloads use direct generated GUID child names");
            Throws<IOException>(() => { using var read = File.OpenRead(Path.Combine(live.DirectoryPath, markerName)); },
                "A live comparison holds an exclusive ownership marker lease");
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(livePath),
                "Recovery cannot reclaim even immediately expired scratch with a live lease");
            live.Dispose();

            var freshRecord = Abandoned(root, TimeSpan.FromHours(1));
            var expired = Abandoned(root, TimeSpan.FromDays(3));
            using (var operation = NewScratch())
                Check(!Directory.Exists(expired.Directory) && File.Exists(freshRecord.File),
                    "Comparison construction recovers abandoned records older than two days and preserves fresh records");
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1 && !Directory.Exists(freshRecord.Directory),
                "An abandoned manifest reclaims only its explicitly registered generated payload");

            var future = Abandoned(root, TimeSpan.FromHours(1));
            SetAge(future.Directory, TimeSpan.FromDays(-1));
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(future.File),
                "Future ownership timestamps cannot authorize even zero-age recovery");
            var invalidTime = Abandoned(root, TimeSpan.FromHours(1));
            SetTimestamp(invalidTime.Directory, "-1");
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(invalidTime.File),
                "Out-of-range ownership timestamps leave registered payloads untouched");
            Throws<ArgumentOutOfRangeException>(() => OwnedScratchLease.RecoverAbandoned(root, TimeSpan.FromTicks(-1)),
                "Recovery rejects negative expiry rather than weakening age validation");
            Throws<ArgumentOutOfRangeException>(() => OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero, maximumCandidates: 0),
                "Recovery rejects an invalid candidate scan bound");

            var lockedRecord = Abandoned(root, TimeSpan.FromDays(3));
            using (var heldMarker = new FileStream(Path.Combine(lockedRecord.Directory, markerName), FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(lockedRecord.File),
                    "A separately locked expired marker prevents payload recovery");
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.FromDays(2)) == 1 && !Directory.Exists(lockedRecord.Directory),
                "An expired known marker recovers after its exclusive-open conflict ends");

            using (var lease = new OwnedScratchLease(root))
            {
                leases.Add(lease);
                string registered = Path.Combine(lease.DirectoryPath, Guid.NewGuid().ToString("N") + ".spool");
                lease.RegisterFile(registered); lease.RegisterFile(registered);
                lease.ValidateOwnedPath(registered);
                Check(!File.Exists(registered), "Shared path validation accepts registered payloads before file creation");
                Throws<IOException>(() => lease.ValidateOwnedPath(Path.Combine(root, Path.GetFileName(registered))),
                    "Shared path validation refuses a registered filename outside its exact operation directory");
                Throws<IOException>(() => lease.ValidateOwnedPath(Path.Combine(lease.DirectoryPath, Guid.NewGuid().ToString("N") + ".spool")),
                    "Shared path validation refuses an unregistered GUID filename inside the operation directory");
                File.WriteAllText(registered, "registered after durable marker"); fixtures.Add((lease.DirectoryPath, registered));
                lease.Dispose();
                Check(File.ReadAllLines(Path.Combine(lease.DirectoryPath, markerName)).Count(x => x == Path.GetFileName(registered)) == 1,
                    "Repeated registration creates exactly one durable ownership entry");
                Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1,
                    "A duplicate registration remains a valid recoverable manifest");
            }

            string unmarked = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unmarked);
            string unmarkedFile = Path.Combine(unmarked, Guid.NewGuid().ToString("N") + ".spool");
            File.WriteAllText(unmarkedFile, "unmarked"); fixtures.Add((unmarked, unmarkedFile));
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(unmarkedFile),
                "GUID-shaped directories without an ownership marker are never adopted");
            File.Delete(unmarkedFile); Directory.Delete(unmarked);

            var unknown = Abandoned(root, TimeSpan.FromDays(3));
            string unknownFile = Path.Combine(unknown.Directory, Guid.NewGuid().ToString("N") + ".spool");
            File.WriteAllText(unknownFile, "unregistered"); fixtures.Add((unknown.Directory, unknownFile));
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(unknown.File) && File.Exists(unknownFile),
                "An unregistered GUID spool invalidates recovery ownership without deleting any payload");
            File.Delete(unknownFile);
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1,
                "A valid registered manifest can recover after the unknown entry is removed");

            var traversal = Abandoned(root, TimeSpan.FromDays(3));
            string source = Path.Combine(root, "source.txt"); File.WriteAllText(source, "source sentinel");
            fixtures.Add((root, source));
            File.AppendAllText(Path.Combine(traversal.Directory, markerName), "../source.txt\n", Encoding.ASCII);
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(traversal.File) &&
                File.ReadAllText(source) == "source sentinel", "Traversal entries invalidate manifests and never delete source paths");

            var badHeader = Abandoned(root, TimeSpan.FromDays(3));
            string badMarker = Path.Combine(badHeader.Directory, markerName);
            string[] badLines = File.ReadAllLines(badMarker); badLines[0] = "not Surf ownership";
            File.WriteAllLines(badMarker, badLines, Encoding.ASCII);
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.Exists(badHeader.File),
                "Malformed ownership headers cannot authorize deletion");

            var nonGuid = Abandoned(root, TimeSpan.FromDays(3));
            string folder = Path.Combine(root, "source-folder");
            Directory.Move(nonGuid.Directory, folder);
            fixtures.Add((folder, Path.Combine(folder, Path.GetFileName(nonGuid.File))));
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && Directory.Exists(folder),
                "A valid marker in a non-GUID source folder cannot authorize recovery");

            string boundedRoot = Path.Combine(root, "bounded"); Directory.CreateDirectory(boundedRoot);
            fixtures.Add((boundedRoot, ""));
            var bounded = new List<(string Directory, string File)>();
            for (int i = 0; i < 3; i++) bounded.Add(Abandoned(boundedRoot, TimeSpan.FromHours(1)));
            foreach (var item in bounded) SetAge(item.Directory, TimeSpan.FromDays(3));
            Check(OwnedScratchLease.RecoverAbandoned(boundedRoot, TimeSpan.Zero, maximumCandidates: 1) == 1 &&
                Directory.EnumerateDirectories(boundedRoot).Count() == 2,
                "Abandoned comparison recovery respects the candidate scan bound");
            Check(OwnedScratchLease.RecoverAbandoned(boundedRoot, TimeSpan.Zero) == 2,
                "Remaining abandoned records can recover in a later bounded pass");
            Directory.Delete(boundedRoot);

            using (var lease = new OwnedScratchLease(root))
            {
                Throws<IOException>(() => lease.RegisterFile(Path.Combine(root, Guid.NewGuid().ToString("N") + ".spool")),
                    "Ownership registration refuses generated files outside the operation directory");
                Throws<IOException>(() => lease.RegisterFile(Path.Combine(lease.DirectoryPath, "source.txt")),
                    "Ownership registration refuses non-GUID source filenames");
            }
            string redirectTarget = Path.Combine(root, "redirect-source"); Directory.CreateDirectory(redirectTarget);
            string redirectSource = Path.Combine(redirectTarget, "source.txt"); File.WriteAllText(redirectSource, "redirect sentinel");
            fixtures.Add((redirectTarget, redirectSource));
            string redirect = Path.Combine(root, Guid.NewGuid().ToString("N"));
            bool linked = false;
            try
            {
                Directory.CreateSymbolicLink(redirect, redirectTarget);
                directoryLinks.Add(redirect); linked = true;
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("SKIPPED reparse filesystem checks: link creation is not permitted."); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
            { Console.WriteLine("SKIPPED reparse filesystem checks: Windows link privilege is unavailable."); }
            if (linked)
            {
                Throws<IOException>(() => { using var operation = new ComparisonScratch(limits with { StagingDirectory = redirect }); },
                    "Comparison staging refuses a redirected root");
                Throws<IOException>(() => { using var operation = new ComparisonScratch(limits with { StagingDirectory = Path.Combine(redirect, "must-not-create") }); },
                    "Comparison staging refuses redirected ancestors before creating child paths");
                Check(!Directory.Exists(Path.Combine(redirectTarget, "must-not-create")),
                    "Rejecting a redirected ancestor makes no writes in its source target");
                string markerOnlyRoot = Path.Combine(root, "marker-source"); Directory.CreateDirectory(markerOnlyRoot);
                string markerSentinel = Path.Combine(markerOnlyRoot, markerName); File.WriteAllText(markerSentinel, "unowned source marker");
                fixtures.Add((markerOnlyRoot, markerSentinel));
                var replacedOperation = NewScratch();
                string movedOperation = Path.Combine(root, Guid.NewGuid().ToString("N"));
                bool moved = false;
                try { Directory.Move(replacedOperation.DirectoryPath, movedOperation); moved = true; }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 5)
                { Check(true, "The live marker handle prevents operation-directory replacement on this filesystem"); }
                if (moved)
                {
                    try
                    {
                        Directory.CreateSymbolicLink(replacedOperation.DirectoryPath, markerOnlyRoot);
                        directoryLinks.Add(replacedOperation.DirectoryPath);
                        Throws<IOException>(replacedOperation.Dispose, "Disposal refuses an operation directory replaced with a reparse point");
                        Check(File.Exists(markerSentinel) && File.ReadAllText(markerSentinel) == "unowned source marker",
                            "Disposal never removes a source marker through a replaced operation directory");
                    }
                    finally
                    {
                        if (Directory.Exists(replacedOperation.DirectoryPath)) Directory.Delete(replacedOperation.DirectoryPath);
                        directoryLinks.Remove(replacedOperation.DirectoryPath);
                        Directory.Move(movedOperation, replacedOperation.DirectoryPath);
                    }
                    replacedOperation.Dispose();
                    Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1,
                        "Refusing a replaced operation directory still releases the original live marker handle");
                }
                else replacedOperation.Dispose();
                Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 0 && File.ReadAllText(redirectSource) == "redirect sentinel",
                    "Recovery refuses a GUID-named reparse child without following its target");
                var redirectedPayload = NewScratch();
                string linkedPayload = redirectedPayload.NewFile(); File.CreateSymbolicLink(linkedPayload, redirectSource);
                try
                {
                    Throws<IOException>(() => redirectedPayload.Delete(linkedPayload), "Registered comparison payloads cannot be replaced with reparse files");
                    Throws<IOException>(redirectedPayload.Dispose, "Normal comparison disposal surfaces reparse payload refusal");
                    Check(File.ReadAllText(redirectSource) == "redirect sentinel", "Refusing a reparse payload leaves the source file untouched");
                }
                finally { File.Delete(linkedPayload); }
                redirectedPayload.Dispose();
                Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1,
                    "A refused reparse payload releases its live lease and can recover after link removal");
                Directory.Delete(redirect); directoryLinks.Remove(redirect);
            }
            CleanupFixturePaths();

            var scratch = NewScratch();
            string path = scratch.NewFile(); File.WriteAllText(path, "locked owned spool");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Throws<IOException>(scratch.Dispose, "Normal scratch disposal surfaces a locked-file cleanup failure");
                Check(File.Exists(path), "Failed scratch cleanup retains the blocked owned file");
                Throws<ObjectDisposedException>(() => scratch.NewFile(), "Failed disposal still refuses new scratch allocations");
            }
            scratch.Dispose(); scratch.Dispose();
            Check(!File.Exists(path), "Scratch payload cleanup can retry after the lock is released and is idempotent");
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1 && !Directory.Exists(scratch.DirectoryPath),
                "Failed cleanup releases its live lease and retains a recoverable ownership marker");

            var resultScratch = NewScratch();
            string spool = resultScratch.NewFile();
            using (var output = File.Open(spool, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                SpoolBinary.Append(output, spool, new ComparisonResultRow("selected", "Identical", "", "", ""), ComparisonCodecs.Result, resultScratch);
            var result = ComparisonResultStore.Complete(resultScratch, spool, 1, new() { ["Identical"] = 1 });
            using (var held = new FileStream(spool, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await ThrowsAsync<IOException>(() => result.DisposeAsync().AsTask(), "Result disposal reports scratch deletion failure to its caller");
                await ThrowsAsync<ObjectDisposedException>(() => result.ReadPageAsync(), "A failed result cleanup cannot resume reads");
            }
            await result.DisposeAsync(); await result.DisposeAsync();
            Check(!File.Exists(spool), "Result cleanup retries rather than silently abandoning a failed payload deletion");
            Check(OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero) == 1 && !Directory.Exists(resultScratch.DirectoryPath),
                "Result cleanup failures leave their metadata reclaimable without a leaked live handle");

            string sentinel = Path.Combine(root, "source-sentinel.txt"); File.WriteAllText(sentinel, "untouched source");
            var guarded = NewScratch(); guarded.Delete(sentinel); guarded.Dispose();
            Check(File.ReadAllText(sentinel) == "untouched source", "Comparison cleanup never deletes a path outside its registered operation files");
            File.Delete(sentinel);
            Check(!Directory.EnumerateFileSystemEntries(root).Any(), "Filesystem comparison checks leave no owned scratch artifacts");
            return passed;
        }
        finally
        {
            foreach (string link in directoryLinks) Directory.Delete(link);
            foreach (var lease in leases) lease.Dispose();
            foreach (var scratch in scratches) scratch.Dispose();
            CleanupFixturePaths();
            OwnedScratchLease.RecoverAbandoned(root, TimeSpan.Zero);
            Directory.Delete(root); // Exact UUID-owned test root; failure remains visible, never recursive.
        }
        void CleanupFixturePaths()
        {
            // These are exact fixture paths created above, not discovered source files.
            foreach (var fixture in fixtures.AsEnumerable().Reverse())
            {
                if (fixture.File.Length != 0 && File.Exists(fixture.File)) File.Delete(fixture.File);
                if (fixture.Directory == root || !Directory.Exists(fixture.Directory)) continue;
                File.Delete(Path.Combine(fixture.Directory, markerName));
                if (!Directory.EnumerateFileSystemEntries(fixture.Directory).Any()) Directory.Delete(fixture.Directory);
            }
        }
        (string Directory, string File) Abandoned(string parent, TimeSpan elapsed)
        {
            var lease = new OwnedScratchLease(parent); leases.Add(lease);
            string file = Path.Combine(lease.DirectoryPath, Guid.NewGuid().ToString("N") + ".spool");
            lease.RegisterFile(file); File.WriteAllText(file, "abandoned comparison payload");
            fixtures.Add((lease.DirectoryPath, file)); lease.Dispose();
            SetAge(lease.DirectoryPath, elapsed);
            return (lease.DirectoryPath, file);
        }
        void SetAge(string directory, TimeSpan elapsed)
            => SetTimestamp(directory, DateTime.UtcNow.Subtract(elapsed).Ticks.ToString(CultureInfo.InvariantCulture));
        void SetTimestamp(string directory, string ticks)
        {
            string marker = Path.Combine(directory, markerName);
            string[] lines = File.ReadAllLines(marker);
            lines[1] = ticks;
            File.WriteAllLines(marker, lines, Encoding.ASCII);
        }
        ComparisonScratch NewScratch()
        { var scratch = new ComparisonScratch(limits); scratches.Add(scratch); return scratch; }
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); passed.Add(name); }
        void Throws<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
        {
            try { await action(); }
            catch (T) { Check(true, name); return; }
            throw new InvalidOperationException(name);
        }
    }
}
