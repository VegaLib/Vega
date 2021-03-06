﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020-2021 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vega.Graphics;
using Vulkan;

namespace Vega.Render
{
	/// <summary>
	/// Represents a target (either offscreen or on a window) for rendering commands. Manages rendering state and
	/// command submission.
	/// <para>
	/// Renderer instances targeting windows will cause the window surface buffer swap when ended.
	/// </para>
	/// </summary>
	public unsafe sealed class Renderer : IDisposable
	{
		// Default per-frame size for uniform buffers
		internal const ulong DEFAULT_UNIFORM_SIZE = 512 * 1_024; // 512 kB (~2000 min uniform updates per frame)

		#region Fields
		// The graphics device 
		internal readonly GraphicsDevice Graphics;
		/// <summary>
		/// The window associated with the renderer.
		/// </summary>
		public readonly Window Window;
		/// <summary>
		/// If the renderer is targeting an offscreen image.
		/// </summary>
		public bool IsOffscreen => Window is null;

		/// <summary>
		/// The current size of the images being rendered to.
		/// </summary>
		public Extent2D Size => RenderTarget.Size;
		/// <summary>
		/// The current MSAA setting for the renderer.
		/// </summary>
		public MSAA MSAA => RenderTarget.MSAA;
		/// <summary>
		/// The number of subpasses in this renderer.
		/// </summary>
		public uint SubpassCount => Layout.SubpassCount;

		/// <summary>
		/// The color used to clear the renderer target image.
		/// </summary>
		public readonly ClearValue[] ClearValues;

		// The render pass objects
		internal readonly RenderLayout Layout;
		internal readonly RenderLayout? MSAALayout;
		internal VkRenderPass RenderPass;
		// The render target
		internal readonly RenderTarget RenderTarget;

		// The shared uniform buffer for all recordings
		private readonly UniformPushBuffer _uniformBuffer;

		#region Recording
		/// <summary>
		/// Gets if the renderer is currently recording commands.
		/// </summary>
		public bool IsRecording => _cmd is not null;
		/// <summary>
		/// Gets the current subpass being recorded. Returns zero if not recording.
		/// </summary>
		public uint CurrentSubpass { get; private set; } = 0;
		/// <summary>
		/// Gets the value of <see cref="AppTime.FrameCount"/> when the renderer was last ended.
		/// </summary>
		public ulong LastRenderFrame { get; private set; } = 0;
		
		// The current primary command buffer recording render commands
		private CommandBuffer? _cmd = null;
		#endregion // Recording

		#region Pipelines
		// The pipelines that have been created for this renderer
		internal IReadOnlyList<Pipeline> Pipelines => _pipelines;
		private readonly List<Pipeline> _pipelines = new();
		private readonly object _pipelineLock = new();
		#endregion // Pipelines

		#region Bindings
		// Descriptors for uniform buffer and subpass inputs
		private readonly VkDescriptorPool _descriptorPool;
		internal readonly VkDescriptorSet UniformDescriptor;
		internal readonly VkDescriptorSetLayout?[] SubpassLayouts;
		internal readonly VkDescriptorSet?[] SubpassDescriptors;
		#endregion // Bindings

		/// <summary>
		/// Disposal flag.
		/// </summary>
		public bool IsDisposed { get; private set; } = false;
		#endregion // Fields

		/// <summary>
		/// Creates a new renderer targeting the passed window. It is an error to create more than one renderer for
		/// a single window.
		/// </summary>
		/// <param name="window">The window to target with the renderer.</param>
		/// <param name="description">A description of the pass layout and attachments for the renderer.</param>
		/// <param name="initialMSAA">The initial MSAA setting for the renderer, if supported.</param>
		public Renderer(Window window, RendererDescription description, MSAA initialMSAA = MSAA.X1)
		{
			// Validate
			if (window.HasRenderer) {
				throw new InvalidOperationException("Cannot create more than one renderer for a single window");
			}

			// Set objects
			Graphics = Core.Instance!.Graphics;
			Window = window;

			// Validate and create layout and pass
			if (description.Attachments[0].Format != window.SurfaceFormat) {
				throw new ArgumentException("The given renderer description does not match the window surface format");
			}
			if (!description.Attachments[0].Preserve) {
				throw new ArgumentException("Attachment 0 must be preserved in window renderers");
			}
			Layout = new(description, true, false);
			if (description.SupportsMSAA) {
				MSAALayout = new(description, true, true);
			}
			if ((initialMSAA != MSAA.X1) && (MSAALayout is null)) {
				throw new ArgumentException($"Renderer does not support MSAA operations");
			}
			RenderPass = ((initialMSAA == MSAA.X1) ? Layout : MSAALayout!).CreateRenderpass(Graphics, initialMSAA);

			// Create render target
			ClearValues = description.Attachments.Select(att =>
				att.IsColor ? new ClearValue(0f, 0f, 0f, 1f) : new ClearValue(1f, 0)).ToArray();
			RenderTarget = new RenderTarget(this, window, initialMSAA);

			// Create the uniform buffer
			_uniformBuffer = new(DEFAULT_UNIFORM_SIZE);

			// Create descriptor objects
			CreateDescriptorObjects(Graphics, Layout, 
				out _descriptorPool, out UniformDescriptor, out SubpassLayouts, out SubpassDescriptors);
			UpdateDescriptorSets(Graphics, UniformDescriptor, _uniformBuffer.Handle, 
				SubpassDescriptors, Layout, RenderTarget); // Non-MSAA is okay for both, since subpass inputs are fixed
		}
		~Renderer()
		{
			dispose(false);
		}

		#region Size/MSAA
		/// <summary>
		/// Sets the size of the images targeted by the renderer. Note that it is an error to do this for window
		/// renderers.
		/// </summary>
		/// <param name="newSize">The new size of the renderer.</param>
		public void SetSize(Extent2D newSize)
		{
			// Skip expensive rebuild
			if (newSize == Size) {
				return;
			}

			// Check for validity
			if (IsRecording) {
				throw new InvalidOperationException("Cannot change renderer size while it is recording");
			}
			if (Window is not null) {
				throw new InvalidOperationException("Cannot set the size of a window renderer - it is tied to the window size");
			}

			// TODO: Rebuild at new size once offscreen renderers are implemented
		}

		/// <summary>
		/// Sets the multisample anti-aliasing level of the renderer. If the renderer does not support MSAA operations,
		/// then an exception is thrown.
		/// <para>
		/// Note that this is a very expensive operation, and should be avoided unless necessary.
		/// </para>
		/// </summary>
		/// <param name="msaa">The MSAA level to apply to the renderer.</param>
		public void SetMSAA(MSAA msaa)
		{
			// Skip expensive rebuild
			if (msaa == MSAA) {
				return;
			}

			// Validate
			if (IsRecording) {
				throw new InvalidOperationException("Cannot change renderer MSAA while it is recording");
			}
			if ((msaa != MSAA.X1) && (MSAALayout is null)) {
				throw new InvalidOperationException("Cannot enable MSAA operations on a non-MSAA renderer");
			}
			if (!msaa.IsSupported()) {
				throw new ArgumentException($"MSAA level {msaa} is not supported on the current system");
			}

			// Wait for rendering operations to complete before messing with a core object
			Graphics.VkDevice.DeviceWaitIdle();

			// Destroy old MSAA renderpass, then build new one
			RenderPass.DestroyRenderPass(null);
			RenderPass = ((msaa != MSAA.X1) ? MSAALayout! : Layout).CreateRenderpass(Graphics, msaa);

			// Rebuild the render target
			RenderTarget.Rebuild(msaa);

			// Rebuild all associated pipelines
			foreach (var pipeline in _pipelines) {
				pipeline.Rebuild();
			}

			// Update the descriptors with the new rendertarget
			UpdateDescriptorSets(Graphics,
				UniformDescriptor, _uniformBuffer.Handle,
				SubpassDescriptors, Layout, RenderTarget);
		}
		#endregion // Size/MSAA

		#region Recording State
		/// <summary>
		/// Begins recording a new set of rendering commands to be submitted to the device.
		/// </summary>
		public void Begin()
		{
			// Validate
			if (IsRecording) {
				throw new InvalidOperationException("Cannot call Begin() on a renderer that is recording");
			}
			if (LastRenderFrame == AppTime.FrameCount) {
				throw new InvalidOperationException("Cannot call Begin() on a window renderer in the same frame as the last submission");
			}

			// Get a new command buffer (transient works because these can't cross frame boundaries)
			_cmd = Graphics.Resources.AllocateTransientCommandBuffer(VkCommandBufferLevel.Primary);
			VkCommandBufferBeginInfo cbbi = new(
				flags: VkCommandBufferUsageFlags.OneTimeSubmit,
				inheritanceInfo: null
			);
			_cmd.Cmd.BeginCommandBuffer(&cbbi).Throw("Failed to start renderer command recording");

			// Start the render pass
			var clears = stackalloc VkClearValue[ClearValues.Length];
			for (int i = 0; i < ClearValues.Length; ++i) {
				clears[i] = ClearValues[i].ToVk();
			}
			VkRenderPassBeginInfo rpbi = new(
				renderPass: RenderPass,
				framebuffer: RenderTarget.CurrentFramebuffer,
				renderArea: new(default, new(Size.Width, Size.Height)),
				clearValueCount: (uint)ClearValues.Length,
				clearValues: clears
			);
			_cmd.Cmd.CmdBeginRenderPass(&rpbi, VkSubpassContents.SecondaryCommandBuffers);

			// Set values
			CurrentSubpass = 0;
			_uniformBuffer.NextFrame();
		}

		/// <summary>
		/// Moves the renderer into recording the next subpass.
		/// </summary>
		public void NextSubpass()
		{
			// Validate
			if (!IsRecording) {
				throw new InvalidOperationException("Cannot call NextSubpass() on a renderer that is not recording");
			}
			if (CurrentSubpass == (SubpassCount - 1)) {
				throw new InvalidOperationException("Cannot call NextSubpass() on the last subpass");
			}

			// Move next
			_cmd!.Cmd.CmdNextSubpass(VkSubpassContents.SecondaryCommandBuffers);
			CurrentSubpass += 1;
		}

		/// <summary>
		/// Ends the current command recording process, and submits the commands to be executed. If this renderer is
		/// attached to a window, this also performs a surface swap for the window.
		/// </summary>
		public void End()
		{
			// Validate
			if (!IsRecording) {
				throw new InvalidOperationException("Cannot call End() on a renderer that is not recording");
			}
			if (CurrentSubpass != (SubpassCount - 1)) {
				throw new InvalidOperationException("Cannot call End() on a renderer that has not visited all subpasses");
			}

			// End the pass and commands
			_cmd!.Cmd.CmdEndRenderPass();
			_cmd.Cmd.EndCommandBuffer().Throw("Failed to record commands for renderer");

			// Swap buffers (also submits the commands for execution)
			RenderTarget.Swap(_cmd);

			// End objects
			_cmd = null;
			CurrentSubpass = 0;
			LastRenderFrame = AppTime.FrameCount;
		}
		#endregion // Recording State

		#region Commands
		/// <summary>
		/// Submits the given command list to be executed at the current recording location of the renderer. The
		/// command list is invalidated and cannot be reused after this call.
		/// </summary>
		/// <param name="task">The list of commands to execute.</param>
		public void Submit(RenderTask task)
		{
			// Validate
			if (!IsRecording) {
				throw new InvalidOperationException("Cannot call Submit() on a renderer that is not recording");
			}
			if (!task.IsValid) {
				throw new InvalidOperationException("Cannot submit an invalid RenderTask to a renderer");
			}
			if (!ReferenceEquals(task.Renderer, this)) {
				throw new InvalidOperationException("Cannot submit a RenderTask to the renderer it was not recorded for");
			}
			if (task.Subpass != CurrentSubpass) {
				throw new InvalidOperationException("Cannot submit a RenderTask in a subpass it was not recorded for");
			}

			// Submit
			var handle = task.Buffer!.Cmd.Handle;
			_cmd!.Cmd.CmdExecuteCommands(1, &handle);
			task.Invalidate();
		}

		/// <summary>
		/// Submits the given set of command lists to be executed at the current recording location of the renderer.
		/// All commands lists will be invalidated and cannot be reused after this call.
		/// <para>
		/// The submited command lists will be executed in the order they are given.
		/// </para>
		/// </summary>
		/// <param name="tasks">The set of render tasks to submit.</param>
		public void Submit(params RenderTask[] tasks)
		{
			// Validate
			if (tasks.Length == 0) {
				return;
			}
			if (!IsRecording) {
				throw new InvalidOperationException("Cannot call Submit() on a renderer that is not recording");
			}
			foreach (var list in tasks) {
				if (!list.IsValid) {
					throw new InvalidOperationException("Cannot submit an invalid RenderTask to a renderer");
				}
				if (!ReferenceEquals(list.Renderer, this)) {
					throw new InvalidOperationException("Cannot submit a RenderTask to the renderer it was not recorded for");
				}
				if (list.Subpass != CurrentSubpass) {
					throw new InvalidOperationException("Cannot submit a RenderTask in a subpass it was not recorded for");
				}
			}

			// Submit
			var handles = stackalloc VulkanHandle<VkCommandBuffer>[tasks.Length];
			for (int i = 0; i < tasks.Length; ++i) {
				handles[i] = tasks[i].Buffer!.Cmd.Handle;
				tasks[i].Invalidate();
			}
			_cmd!.Cmd.CmdExecuteCommands((uint)tasks.Length, handles);
		}
		#endregion // Commands

		// Called by the connected swapchain (if any) when it resizes
		// The swapchain will have already waited for device idle at this point
		internal void OnSwapchainResize()
		{
			// Rebuild the render target
			RenderTarget.Rebuild(MSAA);

			// Update the descriptors with the new rendertarget
			UpdateDescriptorSets(Graphics,
				UniformDescriptor, _uniformBuffer.Handle,
				SubpassDescriptors, Layout, RenderTarget);
		}

		#region Pipelines
		// Adds a new pipeline to be tracked by this renderer
		internal void AddPipeline(Pipeline pipeline)
		{
			if (!ReferenceEquals(this, pipeline.Renderer)) {
				throw new ArgumentException("LIBRARY BUG - renderer instance mismatch for pipeline", nameof(pipeline));
			}

			lock (_pipelineLock) {
				_pipelines.Add(pipeline);
			}
		}

		// Removes the pipeline from being tracked and managed by this renderer
		internal void RemovePipeline(Pipeline pipeline)
		{
			if (!ReferenceEquals(this, pipeline.Renderer)) {
				throw new ArgumentException("LIBRARY BUG - renderer instance mismatch for pipeline", nameof(pipeline));
			}

			lock (_pipelineLock) {
				_pipelines.Remove(pipeline);
			}
		}
		#endregion // Pipelines

		#region Uniform Data
		// Pushes uniform data into the renderer push buffer
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal bool PushUniformData(void* data, ulong size, out ulong offset) => 
			_uniformBuffer.TryPushData(size, data, out offset);
		#endregion // Uniform Data

		#region IDisposable
		public void Dispose()
		{
			dispose(true);
			GC.SuppressFinalize(this);
		}

		private void dispose(bool disposing)
		{
			if (!IsDisposed) {
				if (disposing) {
					Graphics.VkDevice.DeviceWaitIdle();
					RenderTarget.Dispose();

					while (_pipelines.Count > 0) {
						_pipelines[^1].Dispose(); // Removes this pipeline from the list
					}

					foreach (var layout in SubpassLayouts) {
						layout?.DestroyDescriptorSetLayout(null);
					}
					_descriptorPool.DestroyDescriptorPool(null);

					_uniformBuffer.Dispose();
				}
				RenderPass.DestroyRenderPass(null);
			}
			IsDisposed = true;
		}
		#endregion // IDisposable

		#region Bindings
		// Creates a pool and sets for the uniform and subpass input objects
		private static void CreateDescriptorObjects(GraphicsDevice gd, RenderLayout layout,
			out VkDescriptorPool pool, out VkDescriptorSet uniformSet, out VkDescriptorSetLayout?[] subpassLayouts,
			out VkDescriptorSet?[] subpassSets)
		{
			// Create the pool
			var passWithInputCount = layout.Subpasses.Sum(subpass => (subpass.InputCount > 0) ? 1 : 0);
			var totalInputCount = layout.Subpasses.Sum(subpass => subpass.InputCount);
			var sizes = stackalloc VkDescriptorPoolSize[2];
			sizes[0] = new(VkDescriptorType.UniformBufferDynamic, 1);
			sizes[1] = new(VkDescriptorType.InputAttachment, (uint)totalInputCount);
			VkDescriptorPoolCreateInfo dpci = new(
				flags: VkDescriptorPoolCreateFlags.NoFlags,
				maxSets: 1 + (uint)passWithInputCount,
				poolSizeCount: (totalInputCount > 0) ? 2 : 1,
				poolSizes: sizes
			);
			VulkanHandle<VkDescriptorPool> poolHandle;
			gd.VkDevice.CreateDescriptorPool(&dpci, null, &poolHandle)
				.Throw("Failed to create renderer descriptor pool");
			pool = new(poolHandle, gd.VkDevice);

			// Allocate the uniform descriptor
			var uniformLayout = gd.BindingTable.UniformLayoutHandle.Handle;
			VkDescriptorSetAllocateInfo udsai = new(
				descriptorPool: poolHandle,
				descriptorSetCount: 1,
				setLayouts: &uniformLayout
			);
			VulkanHandle<VkDescriptorSet> uniformSetHandle;
			gd.VkDevice.AllocateDescriptorSets(&udsai, &uniformSetHandle)
				.Throw("Failed to allocate descriptor set for renderer uniforms");
			uniformSet = new(uniformSetHandle, pool);

			// Exit out early for no subpass inputs
			if (totalInputCount == 0) {
				subpassLayouts = Array.Empty<VkDescriptorSetLayout>();
				subpassSets = Array.Empty<VkDescriptorSet>();
				return;
			}

			// Create the subpass input layouts and sets
			subpassLayouts = new VkDescriptorSetLayout?[layout.SubpassCount];
			subpassSets = new VkDescriptorSet?[layout.SubpassCount];
			var spbindings = stackalloc VkDescriptorSetLayoutBinding[(int)VSL.MAX_INPUT_ATTACHMENTS];
			for (uint i = 0; i < VSL.MAX_INPUT_ATTACHMENTS; ++i) {
				spbindings[i] = new(i, VkDescriptorType.InputAttachment, 1, VkShaderStageFlags.Fragment, null);
			}
			for (uint i = 0; i < layout.SubpassCount; ++i) {
				ref readonly var subpass = ref layout.Subpasses[i];
				if (subpass.InputCount == 0) {
					subpassLayouts[i] = null;
					subpassSets[i] = null;
					continue;
				}

				// Create the layout handle for the subpass
				VkDescriptorSetLayoutCreateInfo dslci = new(
					flags: VkDescriptorSetLayoutCreateFlags.NoFlags,
					bindingCount: subpass.InputCount,
					bindings: spbindings
				);
				VulkanHandle<VkDescriptorSetLayout> layoutHandle;
				gd.VkDevice.CreateDescriptorSetLayout(&dslci, null, &layoutHandle)
					.Throw("Failed to create descriptor layout for renderer subpass inputs");
				subpassLayouts[i] = new(layoutHandle, gd.VkDevice);

				// Allocate the descriptor set
				VulkanHandle<VkDescriptorSet> siSetHandle;
				VkDescriptorSetAllocateInfo dsai = new(
					descriptorPool: poolHandle,
					descriptorSetCount: 1,
					setLayouts: &layoutHandle
				);
				gd.VkDevice.AllocateDescriptorSets(&dsai, &siSetHandle);
				subpassSets[i] = new(siSetHandle, pool);
			}
		}

		// Update the descriptor sets
		private static void UpdateDescriptorSets(GraphicsDevice gd,
			VkDescriptorSet uniformSet, VkBuffer uniformBuffer,
			VkDescriptorSet?[] subpassSets, RenderLayout layout, RenderTarget rtarget)
		{
			// Update the uniform set
			VkDescriptorBufferInfo bufferInfo = new(
				buffer: uniformBuffer,
				offset: 0,
				range: UniformPushBuffer.PADDING
			);
			VkWriteDescriptorSet bufferWrite = new(
				dstSet: uniformSet,
				dstBinding: 0,
				dstArrayElement: 0,
				descriptorCount: 1,
				descriptorType: VkDescriptorType.UniformBufferDynamic,
				imageInfo: null,
				bufferInfo: &bufferInfo,
				texelBufferView: null
			);
			gd.VkDevice.UpdateDescriptorSets(1, &bufferWrite, 0, null);

			// Update the subpass input sets
			var imageInfos = stackalloc VkDescriptorImageInfo[(int)VSL.MAX_INPUT_ATTACHMENTS];
			var writes = stackalloc VkWriteDescriptorSet[(int)VSL.MAX_INPUT_ATTACHMENTS];
			for (int si = 0; si < subpassSets.Length; ++si) {
				if (subpassSets[si] is null) {
					continue;
				}

				ref readonly var subpass = ref layout.Subpasses[si];
				var inputs = layout.Attachments.Where(att => att.Uses[si] == (byte)AttachmentUse.Input).ToArray();

				// Populate the write handles
				for (int ai = 0; ai < subpass.InputCount; ++ai) {
					var index = inputs[ai].Index;
					imageInfos[ai] = new(default, rtarget.Views[index], VkImageLayout.ShaderReadOnlyOptimal);
					writes[ai] = new(
						dstSet: subpassSets[si]!,
						dstBinding: (uint)ai,
						dstArrayElement: 0,
						descriptorCount: 1,
						descriptorType: VkDescriptorType.InputAttachment,
						imageInfo: imageInfos + ai,
						bufferInfo: null,
						texelBufferView: null
					);
				}

				// Update the descriptor
				gd.VkDevice.UpdateDescriptorSets(subpass.InputCount, writes, 0, null);
			}
		}
		#endregion // Bindings
	}
}
