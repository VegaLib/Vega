﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using Vulkan;

namespace Vega.Graphics
{
	/// <summary>
	/// Manages the process of recording graphics commands to be submitted to a <see cref="Renderer"/> instance.
	/// <para>
	/// An instance of this class cannot be shared between threads while recording.
	/// </para>
	/// <para>
	/// While this type implements <see cref="IDisposable"/>, it can be reused after <c>Dispose()</c>, because the
	/// disposal call will only discard the current rendering process, instead of the entire object.
	/// </para>
	/// </summary>
	public unsafe sealed class CommandRecorder : IDisposable
	{
		#region Fields
		/// <summary>
		/// The renderer currently bound to this recorder.
		/// </summary>
		public Renderer? BoundRenderer { get; private set; }
		/// <summary>
		/// The subpass index currently bound to this recorder.
		/// </summary>
		public uint? BoundSubpass { get; private set; }
		/// <summary>
		/// Gets the value of <see cref="AppTime.FrameCount"/> when the current recording process started.
		/// </summary>
		public ulong? RecordingFrame { get; private set; }

		/// <summary>
		/// Gets if commands are currently being recorded into the recorder.
		/// </summary>
		public bool IsRecording => BoundRenderer is not null;

		// The current command buffer for recording
		private CommandBuffer? _cmd = null;
		#endregion // Fields

		/// <summary>
		/// Creates a new command recorder
		/// </summary>
		public CommandRecorder()
		{

		}
		~CommandRecorder()
		{
			dispose(false);
		}

		#region Recording State
		/// <summary>
		/// Begins recording a new set of commands for submission to the given renderer in the given subpass index.
		/// </summary>
		/// <param name="renderer">The renderer the commands will be recorded for.</param>
		/// <param name="subpass">The subpass index in the renderer which the commands will be recorded for.</param>
		public void Begin(Renderer renderer, uint subpass = 0)
		{
			// Validate
			if (IsRecording) {
				throw new InvalidOperationException("Cannot begin a command recorder that is already recording");
			}
			if (subpass >= renderer.SubpassCount) {
				throw new ArgumentException(nameof(subpass), "Invalid subpass index for given renderer");
			}

			// Grab available secondard command buffer
			_cmd = renderer.Graphics.Resources.AllocateTransientCommandBuffer(VkCommandBufferLevel.Secondary);

			// Start a new secondary command buffer
			VkCommandBufferInheritanceInfo cbii = new(
				renderPass: renderer.RenderPass,
				subpass: subpass,
				framebuffer: renderer.RenderTarget.CurrentFramebuffer,
				occlusionQueryEnable: VkBool32.False,
				queryFlags: VkQueryControlFlags.NoFlags,
				pipelineStatistics: VkQueryPipelineStatisticFlags.NoFlags
			);
			VkCommandBufferBeginInfo cbbi = new(
				VkCommandBufferUsageFlags.RenderPassContinue | VkCommandBufferUsageFlags.OneTimeSubmit, &cbii
			);
			_cmd.Cmd.BeginCommandBuffer(&cbbi).Throw("Failed to start recording commands");

			// Set values
			BoundRenderer = renderer;
			BoundSubpass = subpass;
			RecordingFrame = AppTime.FrameCount;
		}

		/// <summary>
		/// Completes the current recording process and prepares the recorded commands for submission to
		/// <see cref="BoundRenderer"/>.
		/// </summary>
		/// <returns>The set of recorded commands to submit to the renderer.</returns>
		public CommandList End()
		{
			// Validate
			if (!IsRecording) {
				throw new InvalidOperationException("Cannot end a command recorder that is not recording");
			}
			if (AppTime.FrameCount != RecordingFrame) {
				// This will not be an issue for non-transient command buffers
				throw new InvalidOperationException("CommandRecorder crossed frame boundary, and is no longer valid");
			}

			// End buffer
			_cmd!.Cmd.EndCommandBuffer().Throw("Failed to record commands");
			CommandList list = new(BoundRenderer!, BoundSubpass!.Value, _cmd);

			// Set values
			BoundRenderer = null;
			BoundSubpass = null;
			RecordingFrame = null;
			_cmd = null;

			// Return list
			return list;
		}

		/// <summary>
		/// Discards the running list of recorded commands, and returns the recorder to a non-recording state.
		/// </summary>
		public void Discard()
		{
			// Validate
			if (!IsRecording) {
				throw new InvalidOperationException("Cannot discard a command recorder that is not recording");
			}

			// Immediately return non-transient buffers
			if (!_cmd!.Transient) {
				_cmd.SourcePool.Return(_cmd);
			}

			// Set values
			BoundRenderer = null;
			BoundSubpass = null;
			RecordingFrame = null;
			_cmd = null;
		}
		#endregion // Recording State

		#region IDisposable
		public void Dispose()
		{
			dispose(true);
		}

		private void dispose(bool disposing)
		{
			if (IsRecording) {
				Discard();
			}
		}
		#endregion // IDisposable
	}
}