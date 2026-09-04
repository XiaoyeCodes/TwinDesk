using System.Runtime.InteropServices;

namespace Workbench.DesktopFixture;

// Diagnostic only. Query THIS process (-1), never another process or object names/contents.
// Native layout: phnt/ntpsapi.h PROCESS_HANDLE_SNAPSHOT_INFORMATION (Windows 8+).
internal static class OwnHandleTypes
{
    public static IReadOnlyDictionary<string,int> Snapshot()
    {
        if(IntPtr.Size!=8)throw new PlatformNotSupportedException("Handle diagnostic currently requires x64.");
        const int size=1024*1024;var buffer=Marshal.AllocHGlobal(size);
        try
        {
            int status=NtQueryInformationProcess(-1,51,buffer,size,out _);
            if(status<0)throw new InvalidOperationException($"Own process handle snapshot failed: 0x{status:X8}");
            long count=Marshal.ReadInt64(buffer);
            if(count<0||count>(size-16)/40)throw new InvalidDataException("Invalid or over-budget handle snapshot.");
            var names=new Dictionary<uint,string>();var counts=new SortedDictionary<string,int>();
            for(int i=0;i<count;i++)
            {
                nint entry=buffer+16+i*40;uint type=unchecked((uint)Marshal.ReadInt32(entry,28));
                if(!names.TryGetValue(type,out var name))names[type]=name=TypeName(Marshal.ReadIntPtr(entry),type);
                counts[name]=counts.GetValueOrDefault(name)+1;
            }
            return counts;
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    private static string TypeName(nint handle,uint type)
    {
        const int size=4096;var buffer=Marshal.AllocHGlobal(size);
        try
        {
            if(NtQueryObject(handle,2,buffer,size,out _)<0)return $"unavailable-type-{type}";
            int length=(ushort)Marshal.ReadInt16(buffer);nint text=Marshal.ReadIntPtr(buffer,8);
            if(length is <2 or >256 || (length&1)!=0 || text<buffer || text+length>buffer+size)
                throw new InvalidDataException("Invalid object type metadata.");
            return Marshal.PtrToStringUni(text,length/2)!;
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("ntdll.dll")]private static extern int NtQueryInformationProcess(nint process,int info,nint buffer,int length,out int returned);
    [DllImport("ntdll.dll")]private static extern int NtQueryObject(nint handle,int info,nint buffer,int length,out int returned);
}
