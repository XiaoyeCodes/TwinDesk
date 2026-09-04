using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class NxProbeInputScopeTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"nx-admission-"+Guid.NewGuid().ToString("N"));
    private string Copy()
    {
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"输入副本.prt");File.WriteAllText(path,"Admission test bytes, not an NX part");return path;
    }
    private static WindowInfo Root()=>InputSessionTests.Root() with {ProcessName="ugraf",ExecutablePath=@"C:\NX\ugraf.exe",Title="NX 10 - 建模 - [输入副本.prt]"};
    [Fact] public void RequiresWritableExistingPrtBeneathExactDirectory()
    {
        string copy=Copy();var root=Root();
        Assert.Throws<ArgumentException>(()=>new NxProbeInputScope(copy,directory+"-other",root));
        Assert.Throws<ArgumentException>(()=>new NxProbeInputScope(Path.Combine(directory,"missing.prt"),directory,root));
        File.SetAttributes(copy,FileAttributes.ReadOnly);
        Assert.Throws<ArgumentException>(()=>new NxProbeInputScope(copy,directory,root));
        File.SetAttributes(copy,FileAttributes.Normal);
    }
    [Fact] public void RejectsChangedPartProcessAndRootIdentity()
    {
        var root=Root();var scope=new NxProbeInputScope(Copy(),directory,root);
        Assert.True(scope.Allows(root));
        Assert.True(scope.Allows(root with {Title="NX 10 - 建模 - [输入副本.prt （修改的） ]"}));
        Assert.False(scope.Allows(root with {Title="NX 10 - 建模 - [original.prt （修改的） ]"}));
        Assert.True(scope.Allows(root with {Title="NX 10 - 建模 - [输入副本.prt*]"}));
        Assert.False(scope.Allows(root with {Title="NX 10 - 建模 - [original.prt]"}));
        Assert.False(scope.Allows(root with {Title="NX 10 - 建模 - [输入副本.prt.old]"}));
        Assert.False(scope.Allows(root with {Title="other application [输入副本.prt]"}));
        Assert.False(scope.Allows(root with {ProcessId=root.ProcessId+1}));
        Assert.False(scope.Allows(root with {Handle=root.Handle+1}));
        Assert.False(scope.Allows(root with {ExecutablePath=@"C:\NX\other.exe"}));
        Assert.False(scope.Allows(root with {Owner=1}));
    }
    public void Dispose()
    {
        if(Directory.Exists(directory))
        {
            foreach(string file in Directory.EnumerateFiles(directory))File.SetAttributes(file,FileAttributes.Normal);
            Directory.Delete(directory,true);
        }
    }
}
