using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Workbench.Windows;

// Diagnostic A/B only: an agile COM delegate without CsWinRT delegate marshaling.
internal sealed unsafe class NativeClosedCallback : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Instance { public nint Vtable; public int References; public nint State; }
    private static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid Agile = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly Guid Handler = new("e9c610c0-a68c-5bd9-8021-8589346eeee2");
    private static readonly nint Vtable = CreateVtable();
    private static int live;
    private nint pointer;
    public static int LiveInstances => Volatile.Read(ref live);
    public nint Pointer => pointer;

    public NativeClosedCallback(Action action)
    {
        var state=GCHandle.Alloc(action);
        try
        {
            var instance=(Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
            instance->Vtable=Vtable;instance->References=1;instance->State=GCHandle.ToIntPtr(state);
            pointer=(nint)instance;Interlocked.Increment(ref live);
        }
        catch {state.Free();throw;}
    }

    private static nint CreateVtable()
    {
        var table=(nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(NativeClosedCallback),4*sizeof(nint));
        table[0]=(nint)(delegate* unmanaged[Stdcall]<Instance*,Guid*,nint*,int>)&Query;
        table[1]=(nint)(delegate* unmanaged[Stdcall]<Instance*,uint>)&AddRef;
        table[2]=(nint)(delegate* unmanaged[Stdcall]<Instance*,uint>)&Release;
        table[3]=(nint)(delegate* unmanaged[Stdcall]<Instance*,nint,nint,int>)&Invoke;
        return (nint)table;
    }

    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int Query(Instance* self,Guid* iid,nint* result)
    {
        if(result==null)return unchecked((int)0x80004003);
        *result=0;if(iid==null)return unchecked((int)0x80004003);
        if(*iid!=Unknown && *iid!=Agile && *iid!=Handler)return unchecked((int)0x80004002);
        Interlocked.Increment(ref self->References);*result=(nint)self;return 0;
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static uint AddRef(Instance* self)=>(uint)Interlocked.Increment(ref self->References);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static uint Release(Instance* self)=>ReleaseCore(self);
    private static uint ReleaseCore(Instance* self)
    {
        int count=Interlocked.Decrement(ref self->References);
        if(count==0){GCHandle.FromIntPtr(self->State).Free();NativeMemory.Free(self);Interlocked.Decrement(ref live);}
        return (uint)count;
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int Invoke(Instance* self,nint sender,nint args)
    {
        try {((Action)GCHandle.FromIntPtr(self->State).Target!)();return 0;}
        catch(Exception e){return Marshal.GetHRForException(e);}
    }
    public void Dispose()
    {
        nint owned=Interlocked.Exchange(ref pointer,0);if(owned!=0)ReleaseCore((Instance*)owned);
    }
}
