using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MiniC.Services;

internal sealed record GpuGlassPose(float X, float Y, int Width, int Height, float Scale,
    float Radius, float Strength, float Angle, float RippleStrength, float RippleScale)
{
    internal GlassWindowMove? Move { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuGlassConstants
{
    public Vector4 Box, Output, Optics, Ripple;

    public static GpuGlassConstants Create(GpuGlassPose pose, System.Windows.Rect output, ModeRotation rotation) => new()
    {
        Box = new(pose.X, pose.Y, pose.Width, pose.Height),
        Output = new((float)output.X, (float)output.Y, (float)output.Width, (float)output.Height),
        Optics = new(pose.Scale, pose.Radius, pose.Strength, pose.Angle * MathF.PI / 180),
        Ripple = new(pose.RippleStrength, pose.RippleScale, (float)rotation, 0)
    };
}

/// <summary>Only background pixels enter this shader. All calls belong to the GPU worker.</summary>
internal sealed class GpuGlassPipeline : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;

    public GpuGlassPipeline(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        try
        {
            _vertex = device.CreateVertexShader(ReadShader("vs"));
            _pixel = device.CreatePixelShader(ReadShader("ps"));
            _constants = device.CreateBuffer(64, BindFlags.ConstantBuffer);
            _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            _rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        }
        catch { Dispose(); throw; }
    }

    public void Draw(ID3D11RenderTargetView target, ID3D11ShaderResourceView source,
        GpuGlassConstants constants, int width, int height)
    {
        _context.OMSetRenderTargets(target);
        _context.OMSetBlendState(null);
        _context.RSSetState(_rasterizer);
        _context.RSSetViewport(new Viewport(0, 0, width, height));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex);
        _context.PSSetShader(_pixel);
        _context.UpdateSubresource(in constants, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetSampler(0, _sampler);
        _context.PSSetShaderResource(0, source);
        _context.Draw(3, 0);
        _context.PSSetShaderResource(0, null!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private static byte[] ReadShader(string stage)
    {
        using var stream = typeof(GpuGlassPipeline).Assembly.GetManifestResourceStream($"MiniC.Effects.NativeCrystal.{stage}.cso")
            ?? throw new InvalidOperationException("Missing native crystal shader.");
        using var bytes = new System.IO.MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    public void Dispose()
    {
        _rasterizer?.Dispose(); _sampler?.Dispose(); _constants?.Dispose(); _pixel?.Dispose(); _vertex?.Dispose();
    }
}
