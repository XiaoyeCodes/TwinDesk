using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Workbench.Windows;

/// <summary>Premultiplied BGRA source-over composition, using owned GPU textures on one context.</summary>
public sealed class GpuSceneCompositor : IDisposable
{
    private readonly ID3D11DeviceContext context;
    private ID3D11VertexShader vertex = null!;
    private ID3D11PixelShader pixel = null!;
    private ID3D11BlendState blend = null!;
    private ID3D11SamplerState sampler = null!;
    private ID3D11RasterizerState rasterizer = null!;
    private int width, height;
    private bool disposed;
    private const string Shader = """
        struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        Vertex VS(uint id : SV_VertexID) {
            Vertex o; o.uv = float2((id << 1) & 2, id & 2);
            o.position = float4(o.uv * float2(2,-2) + float2(-1,1), 0, 1); return o;
        }
        Texture2D source : register(t0); SamplerState pointClamp : register(s0);
        float4 PS(Vertex input) : SV_Target { return source.Sample(pointClamp, input.uv); }
        """;

    public GpuSceneCompositor(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.context=context;
        try
        {
            vertex=device.CreateVertexShader(Compile("VS","vs_5_0"));
            pixel=device.CreatePixelShader(Compile("PS","ps_5_0"));
            blend=device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
            sampler=device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint,TextureAddressMode.Clamp));
            rasterizer=device.CreateRasterizerState(RasterizerDescription.CullNone);
        }
        catch { Dispose();throw; }
    }

    public void Begin(ID3D11RenderTargetView output, int width, int height, Color4 background)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(width<=0 || height<=0)throw new ArgumentOutOfRangeException(nameof(width));
        this.width=width;this.height=height;
        context.ClearRenderTargetView(output,background);
        context.OMSetRenderTargets(output);
        context.OMSetBlendState(blend);
        context.RSSetState(rasterizer);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);context.PSSetShader(pixel);context.PSSetSampler(0,sampler);
    }
    public void Draw(ID3D11ShaderResourceView source, WindowBounds destination)
    {
        var d=destination;
        if(d.X<0 || d.Y<0 || d.Width<=0 || d.Height<=0 || (long)d.X+d.Width>width || (long)d.Y+d.Height>height)
            throw new InvalidDataException("GPU composition rectangle out of bounds.");
        context.RSSetViewport(new Viewport(d.X,d.Y,d.Width,d.Height));
        context.PSSetShaderResource(0,source);context.Draw(3,0);
        context.PSSetShaderResource(0,null!); // Native API explicitly permits unbinding.
    }
    public void End() => context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        rasterizer?.Dispose();sampler?.Dispose();blend?.Dispose();pixel?.Dispose();vertex?.Dispose();
    }

    private static unsafe byte[] Compile(string entry, string profile)
    {
        byte[] source=Encoding.ASCII.GetBytes(Shader);
        nint code=0, errors=0;
        try
        {
            fixed(byte* data=source)
                Marshal.ThrowExceptionForHR(D3DCompile(data,(nuint)source.Length,"owned-scene",0,0,entry,profile,1u<<11,0,out code,out errors));
            if(code==0)throw new InvalidDataException("Shader compiler produced no bytecode.");
            var table=*(nint**)code;
            var pointer=((delegate* unmanaged[Stdcall]<nint,nint>)table[3])(code);
            var length=((delegate* unmanaged[Stdcall]<nint,nuint>)table[4])(code);
            if(length is 0 or > 1_048_576)throw new InvalidDataException("Unexpected shader size.");
            var result=new byte[(int)length];Marshal.Copy(pointer,result,0,result.Length);return result;
        }
        finally { if(errors!=0)Marshal.Release(errors);if(code!=0)Marshal.Release(code); }
    }
    [DllImport("d3dcompiler_47.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]
    private static extern unsafe int D3DCompile(void* source,nuint length,string name,nint defines,nint include,
        string entry,string target,uint flags,uint effectFlags,out nint code,out nint errors);
}
