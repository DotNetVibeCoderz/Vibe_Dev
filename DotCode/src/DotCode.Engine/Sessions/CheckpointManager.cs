namespace DotCode.Engine.Sessions;

/// <summary>File checkpoints for /rewind: before the first modification of a file within a user turn, its original
/// bytes (or non-existence) are snapshotted to disk. Rewinding to a turn restores every file touched since.</summary>
public sealed class CheckpointManager(string storageDir)
{
    private sealed record Snapshot(string TurnId, string Path, string? BackupFile);

    private readonly List<Snapshot> _snapshots = [];
    private readonly HashSet<(string Turn, string Path)> _seen = [];
    private readonly Lock _gate = new();
    public string? CurrentTurnId { get; set; }

    public void BeforeModify(string path)
    {
        var turn = CurrentTurnId;
        if (turn is null) return;
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_seen.Add((turn, full.ToLowerInvariant()))) return;
            string? backup = null;
            if (File.Exists(full))
            {
                Directory.CreateDirectory(storageDir);
                backup = Path.Combine(storageDir, $"{turn}_{_snapshots.Count}_{Path.GetFileName(full)}.bak");
                File.Copy(full, backup, overwrite: true);
            }
            _snapshots.Add(new Snapshot(turn, full, backup));
        }
    }

    public bool HasChanges(string turnId)
    {
        lock (_gate) return _snapshots.Any(s => s.TurnId == turnId);
    }

    public IReadOnlyList<string> FilesChangedSince(IReadOnlyList<string> turnIds)
    {
        lock (_gate) return _snapshots.Where(s => turnIds.Contains(s.TurnId)).Select(s => s.Path).Distinct().ToList();
    }

    /// <summary>Restores all files modified in the given turns (newest first). Returns restored paths.</summary>
    public List<string> Restore(IReadOnlyList<string> turnIds)
    {
        var restored = new List<string>();
        lock (_gate)
        {
            // Earliest snapshot per path holds the pre-change state.
            var earliest = _snapshots.Where(s => turnIds.Contains(s.TurnId))
                .GroupBy(s => s.Path.ToLowerInvariant())
                .Select(g => g.First());
            foreach (var s in earliest)
            {
                try
                {
                    if (s.BackupFile is null)
                    {
                        if (File.Exists(s.Path)) File.Delete(s.Path);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(s.Path)!);
                        File.Copy(s.BackupFile, s.Path, overwrite: true);
                    }
                    restored.Add(s.Path);
                }
                catch (IOException) { }
            }
            _snapshots.RemoveAll(s => turnIds.Contains(s.TurnId));
            _seen.RemoveWhere(x => turnIds.Contains(x.Turn));
        }
        return restored;
    }
}
