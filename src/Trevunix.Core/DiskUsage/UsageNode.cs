namespace Trevunix.Core.DiskUsage;

public enum UsageNodeKind
{
    Directory,
    File,

    /// <summary>Many small files of one folder folded into a single row.</summary>
    Group,
}

/// <summary>One row of the usage tree. Sizes of a directory include everything below it.</summary>
public sealed class UsageNode
{
    private readonly List<UsageNode> _children = [];

    public UsageNode(string name, string fullPath, UsageNodeKind kind, UsageNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public UsageNodeKind Kind { get; }
    public UsageNode? Parent { get; }

    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    public long FolderCount { get; internal set; }
    public DateTime LastWriteUtc { get; internal set; }

    /// <summary>True when part of this folder could not be read (access denied, in use).</summary>
    public bool Incomplete { get; internal set; }

    public IReadOnlyList<UsageNode> Children => _children;

    public bool IsDirectory => Kind == UsageNodeKind.Directory;

    public double ShareOfParent => Parent is { Size: > 0 } p ? (double)Size / p.Size : 1d;

    internal void AddChild(UsageNode child) => _children.Add(child);

    internal void SortChildrenBySize() => _children.Sort((a, b) => b.Size.CompareTo(a.Size));

    internal void RemoveChild(UsageNode child) => _children.Remove(child);

    /// <summary>Detaches this node and subtracts its totals from every ancestor.</summary>
    public void RemoveFromTree()
    {
        if (Parent is null)
            return;

        Parent.RemoveChild(this);
        for (var p = Parent; p is not null; p = p.Parent)
        {
            p.Size -= Size;
            p.FileCount -= FileCount;
            if (IsDirectory)
                p.FolderCount -= FolderCount + 1;
        }
    }
}
