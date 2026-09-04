using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class ProbeVideoProfileTests
{
    [Theory] [InlineData("720p",1280,720)] [InlineData("1080p",1920,1080)]
    public void ExplicitProfilesHaveExactBoundDimensions(string name,int width,int height)
    {
        var profile=ProbeVideoProfile.Parse(name);Assert.Equal(name,profile.Name);Assert.Equal(width,profile.Width);Assert.Equal(height,profile.Height);
        profile.RequireFrame(width,height);Assert.Throws<InvalidDataException>(()=>profile.RequireFrame(width,height+1));
    }
    [Theory] [InlineData("4k")] [InlineData("1920x1080")] [InlineData("")] [InlineData("1080P")]
    public void ArbitraryProfileNamesCannotRequestAllocation(string name)=>Assert.Throws<ArgumentException>(()=>ProbeVideoProfile.Parse(name));
}
