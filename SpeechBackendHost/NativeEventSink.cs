using System.Runtime.InteropServices;

internal sealed class NativeEventSink
{
    // A process hosts only one recognition session. Root callbacks until exit:
    // native teardown can retain references past event unregistration.
    private static readonly List<NativeEventSink> Roots = new();
    private readonly Query _query;
    private readonly Ref _addRef;
    private readonly Ref _release;
    private readonly Invoke _invoke;
    private readonly Guid _iid;
    private int _references=1;
    private readonly IntPtr _vtable;
    public IntPtr Pointer { get; private set; }
    public NativeEventSink(Guid iid, Action<IntPtr,IntPtr> callback)
    {
        _iid=iid;
        _query=QueryInterface;
        _addRef=_=>(uint)Interlocked.Increment(ref _references);
        _release=_=>(uint)Interlocked.Decrement(ref _references);
        _invoke=(_,sender,args)=>{try {callback(sender,args);return 0;}catch(Exception e){Program.Write(new {kind="error",message="识别回调失败："+e.Message});return unchecked((int)0x80004005);}};
        _vtable=Marshal.AllocHGlobal(32);
        var delegates=new Delegate[]{_query,_addRef,_release,_invoke};
        for(int i=0;i<4;i++) Marshal.WriteIntPtr(_vtable,i*8,Marshal.GetFunctionPointerForDelegate(delegates[i]));
        Pointer=Marshal.AllocHGlobal(8); Marshal.WriteIntPtr(Pointer,_vtable);
        Roots.Add(this);
    }
    private int QueryInterface(IntPtr self,ref Guid iid,out IntPtr value)
    {
        if(iid==_iid || iid==new Guid("00000000-0000-0000-c000-000000000046") || iid==new Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")) {value=Pointer;_addRef(self);return 0;}
        value=0;return unchecked((int)0x80004002);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(IntPtr self,ref Guid iid,out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Ref(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Invoke(IntPtr self,IntPtr sender,IntPtr args);
}
