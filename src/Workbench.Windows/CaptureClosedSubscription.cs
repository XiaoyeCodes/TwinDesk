using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Capture;
using WinRT;

namespace Workbench.Windows;

// Explicit WinRT event token lifetime, using the SDK IGraphicsCaptureItem ABI (slots 8/9).
// Keeps native Closed safety notification; does not replace it with delayed polling.
internal sealed unsafe class CaptureClosedSubscription : IDisposable
{
    private nint pointer;
    private long token;
    private int disposed;
    private readonly TypedEventHandler<GraphicsCaptureItem,object> handler;
    public CaptureClosedSubscription(GraphicsCaptureItem item,Action closed,bool rawDelegate=false)
    {
        handler=(_,_)=>{if(Volatile.Read(ref disposed)==0)closed();};
        var iid=new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        // Runtime class is not an interface type for MarshalInterface<T>. Borrow then acquire our own QI reference.
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(((IWinRTObject)item).NativeObject.ThisPtr,in iid,out pointer));
        GC.KeepAlive(item);
        try
        {
            var add=(delegate* unmanaged[Stdcall]<nint,nint,long*,int>)(*(nint**)pointer)[8];
            long registration=0;
            if(rawDelegate)
            {
                using var callback=new NativeClosedCallback(()=>{if(Volatile.Read(ref disposed)==0)closed();});
                Marshal.ThrowExceptionForHR(add(pointer,callback.Pointer,&registration));
            }
            else
            {
                using var callback=MarshalDelegate.CreateMarshaler(handler,GuidGenerator.GetIID(typeof(TypedEventHandler<GraphicsCaptureItem,object>)),false);
                Marshal.ThrowExceptionForHR(add(pointer,callback.ThisPtr,&registration));
            }
            token=registration;
        }
        catch{Marshal.Release(pointer);pointer=0;throw;}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        nint owned=Interlocked.Exchange(ref pointer,0);if(owned==0)return;
        try
        {
            var remove=(delegate* unmanaged[Stdcall]<nint,long,int>)(*(nint**)owned)[9];
            Marshal.ThrowExceptionForHR(remove(owned,token));GC.KeepAlive(handler);
        }
        finally{Marshal.Release(owned);}
    }
}
