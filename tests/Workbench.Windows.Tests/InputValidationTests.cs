using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class InputValidationTests
{
    private static InputCommand Move(double u=0.5,double v=0.5)=>new(new(Guid.NewGuid(),1),1,new(Guid.NewGuid(),1,1,1),1,InputKind.Move,U:u,V:v);
    [Theory] [InlineData(double.NaN,0.5)] [InlineData(double.PositiveInfinity,0.5)] [InlineData(-0.001,0)] [InlineData(1,1.001)]
    public void NonfiniteOrOutOfContentRejected(double u,double v)
    {
        Assert.False(InputCommandValidation.IsValid(Move(u,v)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SceneInputCoordinates.ToScreen(InputSessionTests.Geometry(),u,v));
    }
    [Fact] public void ExtraFieldsAndInvalidEnumsDoNotReachBackend()
    {
        Assert.False(InputCommandValidation.IsValid(Move() with {Text="unexpected"}));
        Assert.False(InputCommandValidation.IsValid(Move() with {Button=(InputButton)99}));
        Assert.False(InputCommandValidation.IsValid(Move() with {Kind=(InputKind)99}));
        Assert.False(InputCommandValidation.IsValid(Move() with {WheelY=120}));
        Assert.False(InputCommandValidation.IsValid(Move() with {Repeat=true}));
        Assert.False(InputCommandValidation.IsValid(Move() with {Sequence=InputCommandValidation.MaximumSequence+1}));
        Assert.False(InputCommandValidation.IsValid(Move() with {Kind=InputKind.ButtonDown,Button=(InputButton)99}));
    }
    [Fact] public void UnicodeScalarLimitsAndInvalidSurrogatesAreChecked()
    {
        Assert.True(InputCommandValidation.ValidText("中文 NX，123 🧪"));
        Assert.True(InputCommandValidation.ValidText(string.Concat(Enumerable.Repeat("🧪",4096))));
        Assert.False(InputCommandValidation.ValidText(string.Concat(Enumerable.Repeat("🧪",4097))));
        Assert.False(InputCommandValidation.ValidText(new string('中',4097)));
        Assert.False(InputCommandValidation.ValidText("a\ud800b"));Assert.False(InputCommandValidation.ValidText("\udc00"));
        Assert.False(InputCommandValidation.ValidText("a\0b"));Assert.False(InputCommandValidation.ValidText(""));
    }
    [Theory] [InlineData("ControlLeft",0x1d,false)] [InlineData("ControlRight",0x1d,true)]
    [InlineData("Enter",0x1c,false)] [InlineData("NumpadEnter",0x1c,true)] [InlineData("ArrowUp",0x48,true)]
    [InlineData("Numpad8",0x48,false)] [InlineData("KeyA",0x1e,false)] [InlineData("F12",0x58,false)]
    public void PhysicalKeysKeepNumpadAndSideIdentity(string code,int scan,bool extended)
    {
        Assert.True(PhysicalKeyMap.TryGet(code,out var key));Assert.Equal(new PhysicalKey((ushort)scan,extended),key);
    }
    [Theory] [InlineData("MetaLeft")] [InlineData("Pause")] [InlineData("PrintScreen")] [InlineData("a")] [InlineData("keyA")]
    public void UnsupportedKeysAreExplicitlyRejected(string code)=>Assert.False(PhysicalKeyMap.TryGet(code,out _));
    [Fact] public void PhysicalCoordinatesUseCaptureBoundsNotClientOrDpiScaling()
    {
        var root=InputSessionTests.Root() with {CaptureBounds=new(-1913,50,986,693)};
        var scene=OwnedWindowScene.Arrange([root]);
        Assert.Equal(new ScreenPoint(-1913,50),SceneInputCoordinates.ToScreen(scene,0,0));
        Assert.Equal(new ScreenPoint(-928,742),SceneInputCoordinates.ToScreen(scene,1,1));
        Assert.Equal(new ScreenPoint(-1420,396),SceneInputCoordinates.ToScreen(scene,0.5,0.5));
        Assert.True(SceneInputCoordinates.AllowsNativeHit(scene,new(-1420,396),root));
    }
    [Fact] public void NativeHitIdentityAndDisabledRootAreNotGuessedFromPixels()
    {
        var root=InputSessionTests.Root();var scene=OwnedWindowScene.Arrange([root]);var p=new ScreenPoint(-1800,100);
        Assert.False(SceneInputCoordinates.AllowsNativeHit(scene,p,root with {Handle=9}));
        Assert.False(SceneInputCoordinates.AllowsNativeHit(scene,p,root with {Enabled=false}));
        Assert.False(SceneInputCoordinates.AllowsNativeHit(scene,p,root with {BindingGeneration=2}));
        Assert.False(SceneInputCoordinates.AllowsNativeHit(scene,p,root with {ProcessStartedAtUtc=root.ProcessStartedAtUtc.AddSeconds(1)}));
        Assert.False(SceneInputCoordinates.AllowsNativeHit(scene,p,root with {CaptureBounds=new(-1921,50,1000,700)}));
        Assert.False(SceneInputCoordinates.AllowsNativeHit(scene,new(0,0),root));
    }
    [Fact] public void VirtualDesktopAbsoluteCoordinatesIncludeNegativeOrigins()
    {
        var desktop=new WindowBounds(-1920,-200,4480,1640);
        Assert.Equal(new ScreenPoint(0,0),SceneInputCoordinates.ToAbsolute(new(-1920,-200),desktop));
        Assert.Equal(new ScreenPoint(65535,65535),SceneInputCoordinates.ToAbsolute(new(2559,1439),desktop));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SceneInputCoordinates.ToAbsolute(new(2560,0),desktop));
        Assert.Equal(new ScreenPoint(0,0),SceneInputCoordinates.ToAbsolute(new(-1,5),new(-1,5,1,1)));
    }
}
