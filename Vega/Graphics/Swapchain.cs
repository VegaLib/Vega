﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using System.Diagnostics;
using System.Linq;
using Vk.Extras;
using static Vega.InternalLog;

namespace Vega.Graphics
{
	// Manages the swapchain objects and operations for a single open application window
	internal unsafe sealed class Swapchain : IDisposable
	{
		// The preferred surface formats
		private readonly Vk.KHR.SurfaceFormat[] PREFERRED_FORMATS = {
			new() { Format = Vk.Format.B8g8r8a8Unorm, ColorSpace = Vk.KHR.ColorSpace.SrgbNonlinearKHR },
			new() { Format = Vk.Format.R8g8b8a8Unorm, ColorSpace = Vk.KHR.ColorSpace.SrgbNonlinearKHR }
		};
		// Subresource values
		private readonly Vk.ComponentMapping SWAPCHAIN_MAPPING = new(); // Identity mapping
		private readonly Vk.ImageSubresourceRange SWAPCHAIN_RANGE = new() {
			AspectMask = Vk.ImageAspectFlags.Color, BaseArrayLayer = 0, BaseMipLevel = 0,
			LayerCount = 1, LevelCount = 1
		};
		// Default clear color value
		private readonly Vk.ClearColorValue CLEAR_COLOR = new(0.1f, 0.1f, 0.1f, 1.0f);

		#region Fields
		// The window using this swapchain
		public readonly Window Window;

		// Vulkan objects
		private readonly Vk.PhysicalDevice _physicalDevice;
		private readonly Vk.Device _device;

		// Surface objects
		public readonly Vk.KHR.Surface Surface;
		private SurfaceInfo _surfaceInfo;

		// Swapchain objects
		public Vk.KHR.Swapchain Handle { get; private set; }
		private SwapchainInfo _swapchainInfo;
		private Vk.Image[] _images;
		private Vk.ImageView[] _imageViews;
		private Vk.Semaphore[] _acquireSemaphores;
		private Vk.Fence?[] _mappedFences;

		// Sync objects
		private SyncObjects _sync;

		public bool IsDisposed { get; private set; } = false;
		#endregion // Fields

		public Swapchain(Window window)
		{
			var gs = Core.Instance!.Graphics;
			Window = window;
			_physicalDevice = gs.PhysicalDevice;
			_device = gs.Device;

			// Create the surface
			Glfw.CreateWindowSurface(gs.Instance, window.Handle, out var surfaceHandle)
				.Throw("Failed to create window surface");
			Surface = new(gs.Instance, surfaceHandle);
			_physicalDevice.GetPhysicalDeviceSurfaceSupportKHR(gs.GraphicsQueueIndex, Surface, out var presentSupport);
			if (!presentSupport) {
				throw new PlatformNotSupportedException("Selected device does not support window presentation");
			}

			// Get surface info
			Vk.KHR.SurfaceFormat[] sFmts = { };
			Vk.KHR.PresentMode[] sModes = { };
			{
				uint count = 0;
				_physicalDevice.GetPhysicalDeviceSurfaceFormatsKHR(Surface, &count, null);
				sFmts = new Vk.KHR.SurfaceFormat[count];
				_physicalDevice.GetPhysicalDeviceSurfaceFormatsKHR(Surface, out count, sFmts);
				_physicalDevice.GetPhysicalDeviceSurfacePresentModesKHR(Surface, &count, null);
				sModes = new Vk.KHR.PresentMode[count];
				_physicalDevice.GetPhysicalDeviceSurfacePresentModesKHR(Surface, out count, sModes);
			}
			if (sFmts.Length == 0 || sModes.Length == 0) {
				throw new PlatformNotSupportedException("Window context does not support presentation operations");
			}

			// Select surface info
			foreach (var prefFmt in PREFERRED_FORMATS) {
				if (sFmts.Contains(prefFmt)) {
					_surfaceInfo.Format = prefFmt;
					break;
				}
			}
			if (_surfaceInfo.Format == default) {
				_surfaceInfo.Format = sFmts[0];
			}
			_surfaceInfo.HasImmediate = sModes.Contains(Vk.KHR.PresentMode.ImmediateKHR);
			_surfaceInfo.HasMailbox = sModes.Contains(Vk.KHR.PresentMode.MailboxKHR);
			_surfaceInfo.Mode = Vk.KHR.PresentMode.FifoKHR;
			LINFO($"Created window surface (format={_surfaceInfo.Format.Format}) " +
				$"(imm={_surfaceInfo.HasImmediate}) (mb={_surfaceInfo.HasMailbox})");

			// Build sync objects
			_acquireSemaphores = new Vk.Semaphore[GraphicsService.MAX_FRAMES];
			_mappedFences = new Vk.Fence[GraphicsService.MAX_FRAMES];
			for (uint i = 0; i < GraphicsService.MAX_FRAMES; ++i) {
				Vk.SemaphoreCreateInfo.New(out var sci);
				_device.CreateSemaphore(&sci, null, out _acquireSemaphores[i]!).Throw("Swapchain acquire semaphore");
				_mappedFences[i] = null;
			}

			// Create command objects
			Vk.CommandPoolCreateInfo.New(out var cpci);
			cpci.QueueFamilyIndex = gs.GraphicsQueueIndex;
			_device.CreateCommandPool(&cpci, null, out _sync.Pool!).Throw("Swapchain command pool");
			_sync.ClearSemaphores = new Vk.Semaphore[GraphicsService.MAX_FRAMES];
			_sync.RenderFences = new Vk.Fence[GraphicsService.MAX_FRAMES];
			for (uint i = 0; i < GraphicsService.MAX_FRAMES; ++i) {
				Vk.SemaphoreCreateInfo.New(out var sci);
				_device.CreateSemaphore(&sci, null, out _sync.ClearSemaphores[i]!).Throw("Swapchain clear semaphore");
				Vk.FenceCreateInfo.New(out var fci);
				fci.Flags = Vk.FenceCreateFlags.Signaled;
				_device.CreateFence(&fci, null, out _sync.RenderFences[i]!).Throw("Swapchain render fence");
			}
			{
				Vk.CommandBufferAllocateInfo.New(out var cbai);
				cbai.CommandPool = _sync.Pool;
				cbai.Level = Vk.CommandBufferLevel.Primary;
				cbai.CommandBufferCount = GraphicsService.MAX_FRAMES;
				var cmdPtr = stackalloc Vk.Handle<Vk.CommandBuffer>[(int)GraphicsService.MAX_FRAMES];
				_device.AllocateCommandBuffers(&cbai, cmdPtr).Throw("Swapchain command buffers");
				_sync.Cmds = new Vk.CommandBuffer[GraphicsService.MAX_FRAMES];
				for (uint i = 0; i < GraphicsService.MAX_FRAMES; ++i) {
					_sync.Cmds[i] = new Vk.CommandBuffer(_sync.Pool, cmdPtr[i]);
				}
			}

			// Do initial build
			Handle = Vk.KHR.Swapchain.Null;
			_swapchainInfo = new();
			_images = new Vk.Image[GraphicsService.MAX_FRAMES];
			_imageViews = new Vk.ImageView[GraphicsService.MAX_FRAMES];
			rebuild();
		}
		~Swapchain()
		{
			dispose(false);
		}

		#region Build
		private void rebuild()
		{
			Stopwatch timer = Stopwatch.StartNew();

			_device.DeviceWaitIdle();
			var waitTime = timer.Elapsed;

			// Choose new extent and image count
			_physicalDevice.GetPhysicalDeviceSurfaceCapabilitiesKHR(Surface, out var caps).Throw("Surface caps");
			Extent2D newSize;
			if (caps.CurrentExtent.Width != UInt32.MaxValue) {
				newSize = new(caps.CurrentExtent.Width, caps.CurrentExtent.Height);
			}
			else {
				newSize = Extent2D.Clamp(Window.Size,
					new(caps.MinImageExtent.Width, caps.MinImageExtent.Height),
					new(caps.MaxImageExtent.Width, caps.MaxImageExtent.Height));
			}
			uint icnt = Math.Min(caps.MinImageCount + 1, GraphicsService.MAX_FRAMES);
			if ((caps.MaxImageCount != 0) && (icnt > caps.MaxImageCount)) {
				icnt = caps.MaxImageCount;
			}

			// Cancel rebuild on minimized window
			if (newSize == Extent2D.Zero) {
				return;
			}

			// Prepare swapchain info
			Vk.KHR.SwapchainCreateInfo.New(out var sci);
			sci.Surface = Surface;
			sci.MinImageCount = icnt;
			sci.ImageFormat = _surfaceInfo.Format.Format;
			sci.ImageColorSpace = _surfaceInfo.Format.ColorSpace;
			sci.ImageExtent = new() { Width = newSize.Width, Height = newSize.Height };
			sci.ImageArrayLayers = 1;
			sci.ImageUsage = Vk.ImageUsageFlags.ColorAttachment | Vk.ImageUsageFlags.TransferDst;
			sci.ImageSharingMode = Vk.SharingMode.Exclusive;
			sci.PreTransform = caps.CurrentTransform;
			sci.CompositeAlpha = Vk.KHR.CompositeAlphaFlags.OpaqueKHR;
			sci.PresentMode = _surfaceInfo.Mode;
			sci.OldSwapchain = Handle ? Handle : Vk.Handle<Vk.KHR.Swapchain>.Null;

			// Create swapchain and get images
			_device.CreateSwapchainKHR(&sci, null, out var nsc).Throw("Failed to create window swapchain");
			Array.Clear(_images, 0, _images.Length);
			uint imgCount = 0;
			{
				nsc!.GetSwapchainImagesKHR(&imgCount, null).Throw("Swapchain Images");
				var imgptr = stackalloc Vk.Handle<Vk.Image>[(int)imgCount];
				nsc.GetSwapchainImagesKHR(&imgCount, imgptr).Throw("Swapchain Images");
				for (uint i = 0; i < imgCount; ++i) {
					_images[i] = new(_device, imgptr[i]);
				}
			}

			// Free old image views
			foreach (var view in _imageViews) {
				view?.DestroyImageView(null);
			}
			Array.Clear(_imageViews, 0, _imageViews.Length);

			// Create new image views
			int vidx = 0;
			foreach (var img in _images) {
				if (img is null) break;
				Vk.ImageViewCreateInfo.New(out var ivci);
				ivci.Image = img;
				ivci.ViewType = Vk.ImageViewType.E2D;
				ivci.Format = _surfaceInfo.Format.Format;
				ivci.Components = SWAPCHAIN_MAPPING;
				ivci.SubresourceRange = SWAPCHAIN_RANGE;
				_device.CreateImageView(&ivci, null, out _imageViews[vidx++]!).Throw("Swapchain image view");
			}

			// Destroy the old swapchain
			if (Handle) {
				Handle.DestroySwapchainKHR(null);
			}

			// Update swapchain objects
			Handle = nsc;
			var oldSize = _swapchainInfo.Extent;
			_swapchainInfo.ImageIndex = 0;
			_swapchainInfo.SyncIndex = 0;
			_swapchainInfo.Dirty = false;
			_swapchainInfo.Extent = newSize;
			_swapchainInfo.ImageCount = imgCount;

			// Build the new clear commands
			_sync.Pool.ResetCommandPool(Vk.CommandPoolResetFlags.NoFlags);
			uint iidx = 0;
			foreach (var cmd in _sync.Cmds) {
				Vk.ImageMemoryBarrier.New(out var srcimb);
				srcimb.DstAccessMask = Vk.AccessFlags.MemoryWrite;
				srcimb.OldLayout = Vk.ImageLayout.Undefined;
				srcimb.NewLayout = Vk.ImageLayout.TransferDstOptimal;
				srcimb.SrcQueueFamilyIndex = Vk.Constants.QUEUE_FAMILY_IGNORED;
				srcimb.DstQueueFamilyIndex = Vk.Constants.QUEUE_FAMILY_IGNORED;
				srcimb.Image = _images[iidx];
				srcimb.SubresourceRange = SWAPCHAIN_RANGE;
				Vk.ImageMemoryBarrier dstimb = srcimb;
				dstimb.SrcAccessMask = Vk.AccessFlags.MemoryWrite;
				dstimb.DstAccessMask = Vk.AccessFlags.NoFlags;
				dstimb.OldLayout = Vk.ImageLayout.TransferDstOptimal;
				dstimb.NewLayout = Vk.ImageLayout.PresentSrcKHR;

				Vk.CommandBufferBeginInfo.New(out var cbbi);
				cmd.BeginCommandBuffer(&cbbi);
				cmd.PipelineBarrier(Vk.PipelineStageFlags.TopOfPipe, Vk.PipelineStageFlags.Transfer, 
					Vk.DependencyFlags.NoFlags, 0, null, 0, null, 1, &srcimb);
				cmd.ClearColorImage(_images[iidx], Vk.ImageLayout.TransferDstOptimal, CLEAR_COLOR, 
					new[] { SWAPCHAIN_RANGE });
				cmd.PipelineBarrier(Vk.PipelineStageFlags.Transfer, Vk.PipelineStageFlags.BottomOfPipe,
					Vk.DependencyFlags.NoFlags, 0, null, 0, null, 1, &dstimb);
				cmd.EndCommandBuffer();

				if (++iidx == imgCount) {
					break;
				}
			}

			// TODO: Acquire after rebuild

			LINFO($"Rebuilt swapchain (old={oldSize}) (new={newSize}) (time={timer.Elapsed.TotalMilliseconds}ms) " +
				$"(wait={waitTime.TotalMilliseconds}ms)");

			// TODO: Inform attached renderer of swapchain resize
		}
		#endregion // Build

		#region IDisposable
		public void Dispose()
		{
			dispose(true);
			GC.SuppressFinalize(this);
		}

		private void dispose(bool disposing)
		{
			if (!IsDisposed) {
				_device.DeviceWaitIdle();

				// Swapchain Objects
				foreach (var view in _imageViews) {
					view?.DestroyImageView(null);
				}
				foreach (var sem in _acquireSemaphores) {
					sem.DestroySemaphore(null);
				}
				Handle?.DestroySwapchainKHR(null);
				LINFO("Destroyed window swapchain");

				// Sync/Command objects
				foreach (var sem in _sync.ClearSemaphores) {
					sem.DestroySemaphore(null);
				}
				foreach (var fence in _sync.RenderFences) {
					fence.DestroyFence(null);
				}
				_sync.Pool.DestroyCommandPool(null);

				Surface?.DestroySurfaceKHR(null);
				LINFO("Destroyed window surface");
			}
			IsDisposed = true;
		}
		#endregion // IDisposable

		// Contains values for a swapchain surface
		private struct SurfaceInfo
		{
			public Vk.KHR.SurfaceFormat Format;
			public Vk.KHR.PresentMode Mode;
			public bool HasImmediate;
			public bool HasMailbox;
		}

		// Contains values for the swapchain object
		private struct SwapchainInfo
		{
			public Extent2D Extent;
			public uint ImageCount;
			public uint SyncIndex;
			public uint ImageIndex;
			public bool Dirty;
		}

		// Contains objects for syncronization
		private struct SyncObjects
		{
			public Vk.CommandPool Pool;
			public Vk.CommandBuffer[] Cmds;
			public Vk.Semaphore[] ClearSemaphores;
			public Vk.Fence[] RenderFences;
		}
	}
}