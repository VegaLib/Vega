﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Vega.Content;
using Vulkan;

namespace Vega.Graphics
{
	/// <summary>
	/// Represents a compiled SPIR-V shader program stage with reflection information.
	/// </summary>
	public unsafe sealed class ShaderModule : ResourceBase
	{
		// Magic number for SPIR-V files (little endian)
		private const uint SPIRV_MAGIC = 0x07230203;
		// Collection of all binding groups
		private static readonly BindingGroup[] BIND_GROUPS = Enum.GetValues<BindingGroup>();

		#region Fields
		/// <summary>
		/// The file that the module was loaded from, or <c>null</c> if loaded from raw bytecode.
		/// </summary>
		public readonly string? SourceFile;

		/// <summary>
		/// The pipeline stage that this 
		/// </summary>
		public readonly ShaderStages Stage;
		/// <summary>
		/// The name of the module entry point function.
		/// </summary>
		public readonly string EntryPoint;
		/// <summary>
		/// The size of the module push constant block, in bytes.
		/// </summary>
		public readonly uint PushConstantSize;

		/// <summary>
		/// Layout for <see cref="BindingGroup.Buffers"/> for this module.
		/// </summary>
		public readonly BindingLayout BufferLayout = new(BindingGroup.Buffers);
		/// <summary>
		/// Layout for <see cref="BindingGroup.Samplers"/> for this module.
		/// </summary>
		public readonly BindingLayout SamplerLayout = new(BindingGroup.Samplers);
		/// <summary>
		/// Layout for <see cref="BindingGroup.Textures"/> for this module.
		/// </summary>
		public readonly BindingLayout TextureLayout = new(BindingGroup.Textures);
		/// <summary>
		/// Layout for <see cref="BindingGroup.InputAttachments"/> for this module.
		/// </summary>
		public readonly BindingLayout InputAttachmentLayout = new(BindingGroup.InputAttachments);

		/// <summary>
		/// The number of <see cref="Shader"/> instances referencing this module.
		/// </summary>
		public uint RefCount => _refCount;
		private uint _refCount = 0;

		// Module handle
		internal readonly VkShaderModule Handle;
		// Fixed buffer for entry point name
		internal readonly Vulkan.VVK.NativeString NativeEntryPoint;
		#endregion // Fields

		/// <summary>
		/// Create a new shader module by loading SPIR-V bytecode from a file.
		/// </summary>
		/// <param name="path">The path to the SPIR-V bytecode file to load.</param>
		public ShaderModule(string path)
			: this(LoadBytecodeFile(path))
		{
			SourceFile = Path.GetFullPath(path);
		}

		/// <summary>
		/// Create a new shader module from the passed SPIR-V bytecode.
		/// </summary>
		/// <param name="bytecode">The SPIR-V bytecode to load as the module source.</param>
		public ShaderModule(ReadOnlySpan<uint> bytecode)
			: base(ResourceType.ShaderModule)
		{
			var gd = Core.Instance!.Graphics;
			SourceFile = null;

			// Perform reflection
			ReflectModule(bytecode, out Stage, out EntryPoint, out PushConstantSize, out var bindings);
			foreach (var bind in bindings) {
				var layout = bind.Group switch { 
					BindingGroup.Buffers => BufferLayout,
					BindingGroup.Samplers => SamplerLayout,
					BindingGroup.Textures => TextureLayout,
					BindingGroup.InputAttachments => InputAttachmentLayout,
					_ => throw new Exception("LIBRARY BUG - Invalid binding group reflection")
				};
				layout.SetSlot((NativeContent.BindingInfo*)bind.InfoPtr, Stage);
			}
			NativeEntryPoint = new(EntryPoint);

			// Create handle
			var handle = CreateShaderModule(bytecode);
			Handle = new(handle, gd.VkDevice);
		}

		// Should only be called from Shader
		internal void IncRef() => Interlocked.Increment(ref _refCount);
		internal void DecRef() => Interlocked.Decrement(ref _refCount);

		#region ResourceBase
		protected override void OnDispose(bool disposing)
		{
			if (disposing && (RefCount != 0)) {
				throw new InvalidOperationException("Cannot dispose a shader module that is still in use");
			}

			if (disposing) {
				NativeEntryPoint.Dispose();
			}

			if (Core.Instance is not null) {
				Core.Instance.Graphics.Resources.QueueDestroy(this);
			}
			else {
				Destroy();
			}
		}

		protected internal override void Destroy()
		{
			Handle?.DestroyShaderModule(null);
		}
		#endregion // ResourceBase

		// Loads and performs basic validation on bytecode files
		private static uint[] LoadBytecodeFile(string path)
		{
			// Load the bytes
			try {
				// Open/check
				using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
				if ((file.Length == 0) || ((file.Length % 4) != 0)) {
					throw new ContentLoadException(path, $"Invalid bytecode file ({file.Length} % 4 fail)");
				}

				// Load/check
				uint[] bytecode = new uint[file.Length / 4];
				fixed (uint* codeptr = bytecode) {
					file.Read(new Span<byte>(codeptr, (int)file.Length));
				}
				if (bytecode[0] != SPIRV_MAGIC) {
					throw new ContentLoadException(path, "Invalid bytecode file (magic number fail)");
				}

				// Return
				return bytecode;
			}
			catch (ContentLoadException) {
				throw;
			}
			catch (Exception e) {
				throw new ContentLoadException(path, "Failed to load shader bytecode file", e);
			}
		}

		// Create a shader module from the bytecode
		private static VkShaderModule CreateShaderModule(ReadOnlySpan<uint> code)
		{
			// Check for magic number
			if (code[0] != SPIRV_MAGIC) {
				throw new ArgumentException("Invalid SPIR-V bytecode (magic number fail)");
			}

			// Create module
			fixed (uint* codeptr = code) {
				VkShaderModuleCreateInfo smci = new(
					flags: VkShaderModuleCreateFlags.NoFlags,
					codeSize: (ulong)code.Length * 4, // Length is in bytes
					code: codeptr
				);
				VulkanHandle<VkShaderModule> handle;
				Core.Instance!.Graphics.VkDevice.CreateShaderModule(&smci, null, &handle)
					.Throw("Failed to create shader module");
				return new(handle, Core.Instance!.Graphics.VkDevice);
			}
		}

		// Shader module reflection
		private static void ReflectModule(ReadOnlySpan<uint> code, out ShaderStages stage, out string entryPoint,
			out uint pushSize, out List<(BindingGroup Group, ulong InfoPtr)> bindings)
		{
			IntPtr refmod = IntPtr.Zero;

			try {
				// Create module, check resulting errors
				var cres = NativeContent.SpirvCreateModule(code);
				refmod = cres.Handle;
				if (cres.Error == NativeContent.ReflectError.InvalidBytecode) {
					throw new InvalidSpirvException();
				}
				if (cres.Error.IsBindingError()) {
					var berr = NativeContent.SpirvGetBindingError(refmod);
					throw new InvalidBindingException(berr.Set, berr.Slot, cres.Error.ToString());
				}
				if (cres.Error != NativeContent.ReflectError.None) {
					throw new InvalidModuleException(cres.Error);
				}

				// Top-level reflection
				stage = NativeContent.SpirvGetStage(refmod).Stage.ToShaderStages();
				entryPoint = NativeContent.SpirvGetEntryPoint(refmod).EntryPoint;
				pushSize = NativeContent.SpirvGetPushSize(refmod).Size;
				if (pushSize > Core.Instance!.Graphics.Limits.MaxPushConstantSize) {
					throw new InvalidModuleException("Push constant size is higher than the device limit");
				}

				// Reflect the bindings
				bindings = new();
				foreach (var group in BIND_GROUPS) {
					var mask = NativeContent.SpirvGetSetMask(refmod, (NativeContent.BindingSet)group).Mask;
					if (mask == 0) {
						continue;
					}
					for (uint i = 0; i < BindingLayout.SLOT_COUNT; ++i) {
						if ((mask & (1u << (int)i)) > 0) {
							NativeContent.BindingInfo* info;
							NativeContent.SpirvGetBindingInfo(refmod, (NativeContent.BindingSet)group, i, &info);
							bindings.Add((group, (ulong)info));
						}
					}
				}
			}
			finally {
				if (refmod != IntPtr.Zero) {
					NativeContent.SpirvDestroyModule(refmod);
				}
			}
		}
	}
}
