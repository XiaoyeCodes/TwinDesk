namespace Workbench.Windows;

// Explicit finite M1 profiles, not an arbitrary network-controlled allocation or a performance claim.
public sealed record ProbeVideoProfile
{
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    private ProbeVideoProfile(string name,int width,int height){Name=name;Width=width;Height=height;}
    public static ProbeVideoProfile Hd { get; }=new("720p",1280,720);
    public static ProbeVideoProfile FullHd { get; }=new("1080p",1920,1080);
    public static ProbeVideoProfile Parse(string name)=>name switch
    {
        "720p"=>Hd,"1080p"=>FullHd,_=>throw new ArgumentException("Only explicit 720p and 1080p probe profiles are supported.",nameof(name))
    };
    public void RequireFrame(int width,int height)
    {
        if(width!=Width || height!=Height)throw new InvalidDataException("Frame dimensions differ from the bound profile.");
    }
}
