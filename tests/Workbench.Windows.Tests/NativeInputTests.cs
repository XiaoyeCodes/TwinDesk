using System.Runtime.InteropServices;
using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class NativeInputTests
{
    private sealed class Environment : INativeInputEnvironment
    {
        public int Calls,FailAt=int.MaxValue;
        public NativeInputCheck Check(OwnedWindowScene scene,ScreenPoint? point)=>new(++Calls<FailAt,"test",new(-1920,0,4480,1440));
    }
    private sealed class Transport : INativeInputTransport
    {
        public readonly List<NativeInputEvent[]> Batches=[];
        public Func<NativeInputEvent[],uint>? Behavior;
        public uint Send(NativeInputEvent[] events){Batches.Add(events);return Behavior?.Invoke(events)??(uint)events.Length;}
    }
    private static InputCommand Command(InputKind kind)=>new(new(Guid.NewGuid(),1),1,new(Guid.NewGuid(),1,1,1),1,kind);
    [Fact] public void NativeAbiMatchesWindowsX64()
    {
        Assert.Equal(8,IntPtr.Size);Assert.Equal(40,Marshal.SizeOf<NativeInputEvent>());
        Assert.Equal(8,Marshal.OffsetOf<NativeInputEvent>(nameof(NativeInputEvent.Data)).ToInt32());
        Assert.Equal(24,Marshal.SizeOf<NativeKeyboardInput>());Assert.Equal(32,Marshal.SizeOf<NativeMouseInput>());
    }
    [Theory] [InlineData("ControlLeft",8u)] [InlineData("ControlRight",9u)] [InlineData("NumpadEnter",9u)]
    public void KeyboardPackingUsesScanCodeAndExtendedFlag(string code,uint flags)
    {
        var down=NativeInputEvents.PhysicalKey(code,false,123);var up=NativeInputEvents.PhysicalKey(code,true);
        Assert.Equal(1u,down.Type);Assert.Equal((ushort)0,down.Data.Keyboard.VirtualKey);Assert.Equal(flags,down.Data.Keyboard.Flags);
        Assert.Equal((nuint)123,down.Data.Keyboard.ExtraInfo);Assert.Equal(flags|2,up.Data.Keyboard.Flags);
    }
    [Theory] [InlineData(InputButton.Left,2u,4u)] [InlineData(InputButton.Right,8u,16u)] [InlineData(InputButton.Middle,32u,64u)]
    public void ButtonPackingPreservesDistinctButtons(InputButton button,uint down,uint up)
    {
        Assert.Equal(down,NativeInputEvents.Button(button,false).Data.Mouse.Flags);
        Assert.Equal(up,NativeInputEvents.Button(button,true).Data.Mouse.Flags);
        Assert.Equal(up,NativeInputEvents.Release(HeldInput.ForButton(button)).Data.Mouse.Flags);
    }
    [Fact] public void AbsoluteMouseIncludesVirtualDesktopFlagAndDoesNotWrapNegativeOrigin()
    {
        var input=NativeInputEvents.Move(new(-1920,-200),new(-1920,-200,4480,1640));
        Assert.Equal(0u,input.Type);Assert.Equal(0xc001u,input.Data.Mouse.Flags);Assert.Equal(0,input.Data.Mouse.X);Assert.Equal(0,input.Data.Mouse.Y);
    }
    [Fact] public void UnicodeIsPairedUtf16NotTruncatedScalarsOrPhysicalKeys()
    {
        var packets=NativeInputEvents.Unicode("中🧪",77);Assert.Equal(6,packets.Length);
        Assert.Equal(new ushort[]{'中','中',0xd83e,0xd83e,0xddea,0xddea},packets.Select(p=>p.Data.Keyboard.Scan));
        Assert.Equal(new uint[]{4,6,4,6,4,6},packets.Select(p=>p.Data.Keyboard.Flags));
        Assert.All(packets,p=>{Assert.Equal((ushort)0,p.Data.Keyboard.VirtualKey);Assert.Equal((nuint)77,p.Data.Keyboard.ExtraInfo);});
        Assert.Throws<ArgumentException>(()=>NativeInputEvents.Unicode("\ud800"));
    }
    [Fact] public void GuardIsRecheckedImmediatelyBeforeNativeSendAndPermitIsOneUse()
    {
        var env=new Environment {FailAt=2};var transport=new Transport();var backend=new NativeInputBackend(env,transport);
        Assert.True(backend.IsTargetReady(InputSessionTests.Geometry(),null));
        Assert.False(backend.TrySend(Command(InputKind.KeyDown) with {Key="KeyA"},null));Assert.Empty(transport.Batches);
        Assert.False(backend.TrySend(Command(InputKind.KeyDown) with {Key="KeyA"},null));
    }
    [Fact] public void TextBatchesNeverSplitSurrogatePairAndStayBounded()
    {
        var transport=new Transport();var backend=new NativeInputBackend(new Environment(),transport);
        backend.IsTargetReady(InputSessionTests.Geometry(),null);
        Assert.True(backend.TrySend(Command(InputKind.Text) with {Text=new string('a',63)+"🧪中文"+new string('b',64)},null));
        Assert.Equal(3,transport.Batches.Count);Assert.All(transport.Batches,b=>Assert.InRange(b.Length,2,128));
        Assert.Equal(126,transport.Batches[0].Length);Assert.Equal((ushort)0xd83e,transport.Batches[1][0].Data.Keyboard.Scan);
        Assert.False(backend.HasPendingTransient);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ShortOrThrowingUnicodeSendKeepsOnlyReleaseWork(bool throwing)
    {
        var transport=new Transport {Behavior=_=>throwing?throw new InvalidOperationException("synthetic"):1u};
        var backend=new NativeInputBackend(new Environment(),transport);backend.IsTargetReady(InputSessionTests.Geometry(),null);
        var cmd=Command(InputKind.Text) with {Text="中文"};
        if(throwing)Assert.Throws<InvalidOperationException>(()=>backend.TrySend(cmd,null));else Assert.False(backend.TrySend(cmd,null));
        Assert.True(backend.HasPendingTransient);Assert.False(backend.IsTargetReady(InputSessionTests.Geometry(),null));
        Assert.Equal("NATIVE_RELEASE_PENDING",backend.LastCode);
        transport.Behavior=events=>(uint)events.Length;Assert.True(backend.TryRelease([]));Assert.False(backend.HasPendingTransient);
        Assert.All(transport.Batches.Skip(1).SelectMany(b=>b),e=>Assert.Equal(6u,e.Data.Keyboard.Flags));
    }
    [Fact] public void FailedUnicodeReleaseCanBeRetriedWithoutReplayingText()
    {
        var transport=new Transport {Behavior=_=>0};var backend=new NativeInputBackend(new Environment(),transport);
        backend.IsTargetReady(InputSessionTests.Geometry(),null);Assert.False(backend.TrySend(Command(InputKind.Text) with {Text="中"},null));
        Assert.False(backend.TryRelease([]));Assert.True(backend.HasPendingTransient);
        transport.Behavior=_=>1;Assert.True(backend.TryRelease([]));Assert.False(backend.HasPendingTransient);
        Assert.All(transport.Batches.Skip(1).SelectMany(b=>b),e=>Assert.Equal(6u,e.Data.Keyboard.Flags));
    }
    [Fact] public void SessionReleasesUnicodeEvenWhenNoPersistentKeyIsHeld()
    {
        var transport=new Transport {Behavior=_=>1u};var backend=new NativeInputBackend(new Environment(),transport);
        var command=Command(InputKind.Text) with {Text="中"};var session=new InputSession(command.Lease,InputSessionTests.Root(),backend);
        session.UpdateScene(command.Stamp,InputSessionTests.Geometry());session.FrameSent(command.Stamp,1);session.Displayed(command.Lease,command.Stamp,1);
        Assert.False(session.Dispatch(command).Accepted);Assert.False(session.Status.Active);Assert.False(backend.HasPendingTransient);
        Assert.Equal(2,transport.Batches.Count);Assert.Equal(6u,Assert.Single(transport.Batches[1]).Data.Keyboard.Flags);
    }
    [Fact] public void MissingOrMismatchedPointerCannotClickCurrentCursorByAccident()
    {
        var transport=new Transport();var backend=new NativeInputBackend(new Environment(),transport);
        backend.IsTargetReady(InputSessionTests.Geometry(),null);
        Assert.False(backend.TrySend(Command(InputKind.ButtonDown) with {Button=InputButton.Left,U=0.5,V=0.5},null));
        Assert.Empty(transport.Batches);
    }
    [Fact] public void ReleasePacketsNeverCarryMoveDownOrTextCommit()
    {
        var transport=new Transport();var backend=new NativeInputBackend(new Environment(),transport);
        Assert.True(backend.TryRelease([HeldInput.ForButton(InputButton.Right),HeldInput.ForKey("ControlRight")]));
        Assert.Equal(16u,transport.Batches[0][0].Data.Mouse.Flags);Assert.Equal(11u,transport.Batches[1][0].Data.Keyboard.Flags);
    }
}
