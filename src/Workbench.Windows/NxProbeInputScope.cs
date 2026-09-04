namespace Workbench.Windows;

/// <summary>Local diagnostic admission only. A window title is not proof of the opened file's path.
/// The operator must first verify the isolated copy in NX's native UI. Not a product file policy.</summary>
public sealed class NxProbeInputScope
{
    public string CopyPath { get; }
    public string PartName { get; }
    private readonly WindowInfo root;
    public NxProbeInputScope(string copyPath,string verificationDirectory,WindowInfo root)
    {
        CopyPath=Path.GetFullPath(copyPath);
        string directory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(verificationDirectory));
        if(!CopyPath.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(CopyPath).Equals(".prt",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NX input probe requires a .prt copy beneath artifacts/verification.");
        var file=new FileInfo(CopyPath);
        if(!file.Exists || file.Length==0 || file.IsReadOnly)throw new ArgumentException("An existing writable nonempty NX copy is required.");
        FileSystemInfo? item=file;
        while(item is not null)
        {
            if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Reparse paths are not admitted for this diagnostic copy.");
            item=item is FileInfo f?f.Directory:((DirectoryInfo)item).Parent;
        }
        PartName=file.Name;this.root=root;
        if(!Allows(root))throw new ArgumentException("Selected NX root does not show the named isolated copy.");
    }
    public bool Allows(WindowInfo live)=>OwnedWindowScene.SameIdentity(root,live)
        && live.Owner==0 && live.ProcessName.Equals("ugraf",StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(live.ExecutablePath)?.Equals("ugraf.exe",StringComparison.OrdinalIgnoreCase)==true
        && live.Title.StartsWith("NX ",StringComparison.Ordinal)
        && (live.Title.EndsWith("["+PartName+"]",StringComparison.Ordinal)
            // Observed in NX 10 Chinese during an existing feature's parameter preview.
            || live.Title.EndsWith("["+PartName+" （修改的） ]",StringComparison.Ordinal)
            || live.Title.EndsWith("["+PartName+"*]",StringComparison.Ordinal)
            || live.Title.EndsWith("["+PartName+"]*",StringComparison.Ordinal));
}
