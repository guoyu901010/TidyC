using System.Text.Json.Serialization;

namespace TidyC.Core.Scan;

/// <summary>
/// A node in the aggregated space tree. Directory nodes carry the rolled-up
/// numbers of their children; file nodes carry their own allocation. All sizes
/// are cluster-aligned allocated bytes, the same unit the NTFS volume reports.
/// </summary>
public sealed class SpaceNode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string FullPath { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public NodeKind Kind { get; set; } = NodeKind.Directory;

    [JsonPropertyName("allocated_bytes")]
    public long AllocatedBytes { get; set; }

    [JsonPropertyName("logical_bytes")]
    public long LogicalBytes { get; set; }

    [JsonPropertyName("file_count")]
    public long FileCount { get; set; }

    [JsonPropertyName("directory_count")]
    public long DirectoryCount { get; set; }

    [JsonPropertyName("access_denied_count")]
    public long AccessDeniedCount { get; set; }

    [JsonPropertyName("reparse_skipped_count")]
    public long ReparseSkippedCount { get; set; }

    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = string.Empty;

    /// <summary>
    /// Files directly inside this directory are folded into these counters
    /// instead of materialising one node per file: a real C: scan holds ~1M
    /// files and the tree must stay small enough to render.
    /// </summary>
    [JsonIgnore]
    public long DirectAllocatedBytes { get; set; }

    [JsonIgnore]
    public long DirectLogicalBytes { get; set; }

    [JsonIgnore]
    public long DirectFileCount { get; set; }

    [JsonPropertyName("last_write_utc")]
    public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;

    [JsonPropertyName("category")]
    public string Category { get; set; } = StorageCategories.Other;

    [JsonIgnore]
    public SpaceNode? Parent { get; set; }

    [JsonIgnore]
    public List<SpaceNode> Children { get; } = new();

    /// <summary>Treemap layout rectangle, filled in by <c>TreemapTiler</c>.</summary>
    [JsonIgnore] public double LayoutX { get; set; }

    [JsonIgnore] public double LayoutY { get; set; }

    [JsonIgnore] public double LayoutWidth { get; set; }

    [JsonIgnore] public double LayoutHeight { get; set; }

    public SpaceNode AddChild(SpaceNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public SpaceNode AddFile(string name, string fullPath, long allocatedBytes, long logicalBytes, string? fileId = null)
    {
        var node = new SpaceNode
        {
            Name = name,
            FullPath = fullPath,
            Kind = NodeKind.File,
            AllocatedBytes = allocatedBytes,
            LogicalBytes = logicalBytes,
            FileCount = 1,
            DirectAllocatedBytes = allocatedBytes,
            DirectLogicalBytes = logicalBytes,
            DirectFileCount = 1,
            FileId = fileId ?? string.Empty,
        };
        AddChild(node);
        return node;
    }

    public void AddDirectFile(long allocatedBytes, long logicalBytes, DateTime lastWriteUtc = default)
    {
        DirectAllocatedBytes += allocatedBytes;
        DirectLogicalBytes += logicalBytes;
        DirectFileCount += 1;
        if (lastWriteUtc > LastWriteUtc)
        {
            LastWriteUtc = lastWriteUtc;
        }
    }

    /// <summary>Recomputes rolled-up counters from children, bottom-up.</summary>
    public void Aggregate()
    {
        if (Kind == NodeKind.File)
        {
            FileCount = Math.Max(FileCount, 1);
            DirectoryCount = 0;
            return;
        }

        long allocated = DirectAllocatedBytes;
        long logical = DirectLogicalBytes;
        long files = DirectFileCount;
        long dirs = 0;
        long denied = AccessDeniedCount;
        long skipped = ReparseSkippedCount;

        foreach (SpaceNode child in Children)
        {
            child.Aggregate();
            allocated += child.AllocatedBytes;
            logical += child.LogicalBytes;
            files += child.FileCount;
            dirs += child.DirectoryCount;
            denied += child.AccessDeniedCount;
            skipped += child.ReparseSkippedCount;
            if (child.Kind is NodeKind.Directory or NodeKind.Volume)
            {
                dirs++;
            }
        }

        AllocatedBytes = allocated;
        LogicalBytes = logical;
        FileCount = files;
        DirectoryCount = dirs;
        AccessDeniedCount = denied;
        ReparseSkippedCount = skipped;
        Children.Sort(static (a, b) => b.AllocatedBytes.CompareTo(a.AllocatedBytes));
    }

    public IEnumerable<SpaceNode> SelfAndDescendants()
    {
        yield return this;
        foreach (SpaceNode child in Children)
        {
            foreach (SpaceNode node in child.SelfAndDescendants())
            {
                yield return node;
            }
        }
    }

    public IEnumerable<SpaceNode> Descendants()
    {
        foreach (SpaceNode child in Children)
        {
            foreach (SpaceNode node in child.SelfAndDescendants())
            {
                yield return node;
            }
        }
    }

    public int Depth
    {
        get
        {
            int depth = 0;
            SpaceNode? cursor = Parent;
            while (cursor is not null)
            {
                depth++;
                cursor = cursor.Parent;
            }

            return depth;
        }
    }

    public override string ToString() => $"{FullPath} ({AllocatedBytes} bytes, {Kind})";
}
