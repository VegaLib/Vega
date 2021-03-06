﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020-2021 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using System.Runtime.CompilerServices;

namespace Vega.Graphics
{
	/// <summary>
	/// Abstract base type for graphics resource objects.
	/// </summary>
	public abstract class ResourceBase : IEquatable<ResourceBase>, IDisposable
	{
		#region Fields
		// The unique identifier for this resource object
		internal readonly RUID RUID;
		// The resource type
		internal ResourceType ResourceType => RUID.Type;

		public GraphicsDevice Graphics { 
			get {
				if (_graphics.TryGetTarget(out var gd) && !gd.IsDisposed) {
					return gd;
				}
				throw new ObjectDisposedException(RUID.ToString() + ".Graphics");
			}
		}
		private readonly WeakReference<GraphicsDevice> _graphics;

		/// <summary>
		/// If the resource has been disposed.
		/// </summary>
		public bool IsDisposed { get; private set; } = false;
		#endregion // Fields

		private protected ResourceBase(ResourceType type)
		{
			RUID = new(type);
			_graphics = new(Core.Instance?.Graphics 
				?? throw new InvalidOperationException("Cannot create graphics resources before Core"));
		}
		~ResourceBase()
		{
			if (!IsDisposed) {
				OnDispose(false);
			}
			IsDisposed = true;
		}

		#region Overrides
		public override int GetHashCode() => RUID.GetHashCode();

		public override string ToString() => RUID.ToString();

		public override bool Equals(object? obj) => (obj is ResourceBase rb) && (rb.RUID == RUID);

		bool IEquatable<ResourceBase>.Equals(ResourceBase? other) => (other is not null) && (other.RUID == RUID);
		#endregion // Overrides

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ThrowIfDisposed()
		{
			if (IsDisposed) {
				throw new ObjectDisposedException(RUID.ToString());
			}
		}

		#region IDisposable
		/// <summary>
		/// Mark the resource for disposal. Because of the parallel nature of graphics operations, some resources will
		/// not be disposed until they are no longer in use (within a few frames in most cases).
		/// </summary>
		public virtual void Dispose()
		{
			if (!IsDisposed) {
				OnDispose(true);
				GC.SuppressFinalize(this);
			}
			IsDisposed = true;
		}

		protected abstract void OnDispose(bool disposing);

		// Called by the graphics resource manager after the resource is disposed, and no longer in use.
		internal protected abstract void Destroy();
		#endregion // IDisposable
	}
}
