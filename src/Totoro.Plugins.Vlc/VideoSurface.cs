using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace Totoro.Plugins.Vlc;

/// <summary>
/// The swap chain libvlc draws into, on a plain <see cref="SwapChainPanel"/>: what LibVLCSharp's VideoView does, without
/// being a control of its own, which the app's XAML can't resolve when it comes from a plugin. Ported from
/// LibVLCSharp's VideoViewBase (LGPL-2.1).
/// </summary>
internal sealed class VideoSurface
{
	// Where libvlc reads the size to render at, on the swap chain.
	private static readonly Guid s_swapChainWidth = new(0xf1b59347, 0x1643, 0x411a, 0xad, 0x6b, 0xc7, 0x80, 0x17, 0x7a, 0x6, 0xb6);
	private static readonly Guid s_swapChainHeight = new(0x6ea976a0, 0x9d60, 0x4bb7, 0xa5, 0xa9, 0x7d, 0xd1, 0x18, 0x7f, 0xc9, 0xbd);

	private SharpDX.Direct3D11.Device? _device;
	private SwapChain1? _swapChain;
	private SwapChain2? _swapChain2;

	public VideoSurface()
	{
		Panel.SizeChanged += (_, _) =>
		{
			if (_swapChain is null)
			{
				Create();
			}
			else
			{
				UpdateSize();
			}
		};
		Panel.CompositionScaleChanged += (_, _) => UpdateScale();
		Panel.Unloaded += (_, _) => Destroy();
	}

	public SwapChainPanel Panel { get; } = new();

	/// <summary>Raised once there's a swap chain, with the options that have libvlc draw into it.</summary>
	public event EventHandler<string[]>? Initialized;

	private void Create()
	{
		// Not while it has no size (e.g. collapsed).
		if (Panel.ActualHeight == 0)
		{
			return;
		}

		using var factory = new Factory2(false);
		_device = CreateDevice(factory, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport)
			?? CreateDevice(factory, DeviceCreationFlags.BgraSupport)
			?? new SharpDX.Direct3D11.Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);

		var description = new SwapChainDescription1
		{
			Width = (int)(Panel.ActualWidth * Panel.CompositionScaleX),
			Height = (int)(Panel.ActualHeight * Panel.CompositionScaleY),
			Format = Format.B8G8R8A8_UNorm,
			SampleDescription = { Count = 1, Quality = 0 },
			Usage = Usage.RenderTargetOutput,
			BufferCount = 2,
			SwapEffect = SwapEffect.FlipSequential,
			Flags = SwapChainFlags.None,
			AlphaMode = AlphaMode.Unspecified,
		};
		_swapChain = new SwapChain1(factory, _device, ref description);
		using (var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device1>())
		{
			dxgiDevice.MaximumFrameLatency = 1;
		}

		using (var native = ComObject.As<SwapChainPanelNative>(Panel))
		{
			native.SwapChain = _swapChain;
		}

		_swapChain2 = _swapChain.QueryInterface<SwapChain2>();
		UpdateScale();
		UpdateSize();
		Initialized?.Invoke(this,
		[
			$"--winrt-d3dcontext=0x{_device.ImmediateContext.NativePointer:x}",
			$"--winrt-swapchain=0x{_swapChain.NativePointer:x}",
		]);
	}

	private static SharpDX.Direct3D11.Device? CreateDevice(Factory2 factory, DeviceCreationFlags flags)
	{
		for (var i = 0; i < factory.GetAdapterCount(); i++)
		{
			try
			{
				using var adapter = factory.GetAdapter(i);
				return new SharpDX.Direct3D11.Device(adapter, flags);
			}
			catch (SharpDXException)
			{
			}
		}

		return null;
	}

	private void UpdateSize()
	{
		if (_swapChain is null)
		{
			return;
		}

		var size = Marshal.AllocHGlobal(sizeof(int));
		try
		{
			Marshal.WriteInt32(size, (int)(Panel.ActualWidth * Panel.CompositionScaleX));
			_swapChain.SetPrivateData(s_swapChainWidth, sizeof(int), size);
			Marshal.WriteInt32(size, (int)(Panel.ActualHeight * Panel.CompositionScaleY));
			_swapChain.SetPrivateData(s_swapChainHeight, sizeof(int), size);
		}
		finally
		{
			Marshal.FreeHGlobal(size);
		}
	}

	private void UpdateScale()
	{
		if (_swapChain2 is not null)
		{
			_swapChain2.MatrixTransform = new RawMatrix3x2 { M11 = 1.0f / Panel.CompositionScaleX, M22 = 1.0f / Panel.CompositionScaleY };
		}
	}

	private void Destroy()
	{
		_swapChain2?.Dispose();
		_swapChain2 = null;
		if (_swapChain is not null)
		{
			using var native = ComObject.As<SwapChainPanelNative>(Panel);
			native.SwapChain = null;
		}

		_swapChain?.Dispose();
		_swapChain = null;
		_device?.Dispose();
		_device = null;
	}

	/// <summary>WinUI's ISwapChainPanelNative (its own IID, not UWP's).</summary>
	[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
	private sealed class SwapChainPanelNative(IntPtr nativePtr) : ISwapChainPanelNative(nativePtr);
}
