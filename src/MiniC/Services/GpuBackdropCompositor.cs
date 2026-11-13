using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Rect = System.Windows.Rect;

namespace MiniC.Services;

/// <summary>Premultiplied rear-window composition before refraction; never receives MiniC content.</summary>
internal sealed class GpuBackdropCompositor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Constants { internal Vector4 Target, Source, Options; }
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _rasterizer;

    internal GpuBackdropCompositor(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        try
        {
            _vertex = device.CreateVertexShader(Read("vs")); _pixel = device.CreatePixelShader(Read("ps"));
            _constants = device.CreateBuffer(48, BindFlags.ConstantBuffer);
            _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            _rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
            _blend = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
        }
        catch { Dispose(); throw; }
    }

    internal void Draw(ID3D11RenderTargetView target, Rect targetBounds, ID3D11ShaderResourceView? source,
        Rect sourceBounds, float opacity = 1, uint? colorKey = null)
    {
        var constants = new Constants
        {
            Target = new((float)targetBounds.X, (float)targetBounds.Y, (float)targetBounds.Width, (float)targetBounds.Height),
            Source = new((float)sourceBounds.X, (float)sourceBounds.Y, (float)sourceBounds.Width, (float)sourceBounds.Height),
            Options = new(source is null ? 1 : 0, opacity, colorKey.HasValue ? 1 : 0, colorKey.GetValueOrDefault())
        };
        _context.OMSetRenderTargets(target);
        // Missing/blocked windows punch a transparent hole: show actual rear pixels,
        // rather than pretending a deeper window or wallpaper is the visible source.
        _context.OMSetBlendState(source is null ? null : _blend);
        _context.RSSetState(_rasterizer);
        _context.RSSetViewport(new Viewport(0, 0, (float)targetBounds.Width, (float)targetBounds.Height));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex); _context.PSSetShader(_pixel);
        _context.UpdateSubresource(in constants, _constants);
        _context.PSSetConstantBuffer(0, _constants); _context.PSSetSampler(0, _sampler);
        _context.PSSetShaderResource(0, source!);
        _context.Draw(3, 0);
        _context.PSSetShaderResource(0, null!);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private static byte[] Read(string stage)
    {
        using var stream = typeof(GpuBackdropCompositor).Assembly.GetManifestResourceStream($"MiniC.Effects.BackdropLayer.{stage}.cso")
            ?? throw new InvalidOperationException("Missing backdrop compositor shader.");
        using var data = new System.IO.MemoryStream(); stream.CopyTo(data); return data.ToArray();
    }
    public void Dispose()
    { _rasterizer?.Dispose(); _blend?.Dispose(); _sampler?.Dispose(); _constants?.Dispose(); _pixel?.Dispose(); _vertex?.Dispose(); }
}
