﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020-2021 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using System.Runtime.CompilerServices;

namespace Vega
{
	/// <summary>
	/// Represents an infinitely extending plane in 3D space. <see cref="Plane3D.Normal"/> is assumed to be normalized 
	/// in all calculations, and all planes constructed by the library will be normalized.
	/// </summary>
	public struct Plane3D : IEquatable<Plane3D>
	{
		#region Fields
		/// <summary>
		/// The normal vector (perpendicular to the surface) describing the plane orientation.
		/// </summary>
		public Vec3 Normal;
		/// <summary>
		/// The distance of the plane from the origin, describing the plane position.
		/// </summary>
		public float D;

		/// <summary>
		/// Gets an identical plane, but with a flipped normal vector.
		/// </summary>
		public readonly Plane3D Flipped => new(-Normal, -D);
		/// <summary>
		/// Gets the version of the plane with the normal pointing away from the origin (positive distance).
		/// </summary>
		public readonly Plane3D Positive => (D < 0) ? new(-Normal, -D) : this;
		/// <summary>
		/// Gets the normalized version of the plane.
		/// </summary>
		public readonly Plane3D Normalized
		{
			get {
				var ilen = 1 / Normal.Length;
				return new(Normal.X * ilen, Normal.Y * ilen, Normal.Z * ilen, D * ilen);
			}
		}
		#endregion // Fields

		#region Ctor
		/// <summary>
		/// Constructs a new 3D plane.
		/// </summary>
		/// <param name="normal">The plane normal vector (orientation), will be normalized.</param>
		/// <param name="d">The plane distance from origin (position).</param>
		public Plane3D(in Vec3 normal, float d)
		{
			Normal = normal.Normalized;
			D = d;
		}

		/// <summary>
		/// Constructs a new plane passing through the given point, with the given normal.
		/// </summary>
		/// <param name="point">The point to pass the plane through.</param>
		/// <param name="normal">The plane normal, will be normalized.</param>
		public Plane3D(in Vec3 point, in Vec3 normal)
		{
			Normal = normal.Normalized;
			D = Vec3.Dot(point, Normal);
		}

		/// <summary>
		/// Constructs a new plane from normal components and distance.
		/// </summary>
		/// <param name="x">The x-component of the normal.</param>
		/// <param name="y">The y-component of the normal.</param>
		/// <param name="z">The z-component of the normal</param>
		/// <param name="d">The distance from the origin.</param>
		public Plane3D(float x, float y, float z, float d)
		{
			float len = MathF.Sqrt(x * x + y * y + z * z);
			Normal = new(x / len, y / len, z / len);
			D = d;
		}

		/// <summary>
		/// Constructs a new plane that passes through the three points. The plane normal is calculated with right-hand
		/// winding from points 1 -> 2 -> 3.
		/// </summary>
		/// <param name="p1">The first point to define the plane.</param>
		/// <param name="p2">The second point to define the plane.</param>
		/// <param name="p3">The third point to define the plane.</param>
		public static Plane3D FromPoints(in Vec3 p1, in Vec3 p2, in Vec3 p3)
		{
			var v12 = p2 - p1;
			var v13 = p3 - p1;
			Vec3.Cross(v12, v12, out var normal);
			normal = normal.Normalized;
			return new(normal, Vec3.Dot(normal, p1));
		}
		#endregion // Ctor

		#region Overrides
		readonly bool IEquatable<Plane3D>.Equals(Plane3D other) => other == this;

		public readonly override bool Equals(object? obj) => (obj is Plane3D p) && (p == this);

		public readonly override int GetHashCode() => HashCode.Combine(Normal, D);

		public readonly override string ToString() => $"{{{Normal}, {D}}}";
		#endregion // Overrides

		#region Plane Functions
		/// <summary>
		/// Calculates the dot product of the plane and coordinate. The product sign can be used to detect if the
		/// coordinate is in front of, or behind, the plane.
		/// </summary>
		/// <param name="plane">The plane to calculate against.</param>
		/// <param name="coord">The coordinate to dot with the plane.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float Dot(in Plane3D plane, in Vec3 coord) =>
			(plane.Normal.X * coord.X) + (plane.Normal.Y * coord.Y) + (plane.Normal.Z * coord.Z) - plane.D;

		/// <summary>
		/// Calculates the dot product of the plane and a normal.
		/// </summary>
		/// <param name="plane">The plane to calculate against.</param>
		/// <param name="normal">The normal vector to dot with the plane.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float DotNormal(in Plane3D plane, in Vec3 normal) =>
			(plane.Normal.X * normal.X) + (plane.Normal.Y * normal.Y) + (plane.Normal.Z * normal.Z);

		/// <summary>
		/// Calculates the point on the plane that is closest to the given point.
		/// </summary>
		/// <param name="plane">The plane to find the point on.</param>
		/// <param name="point">The point to get closest to the plane.</param>
		public static Vec3 ClosestPoint(in Plane3D plane, in Vec3 point)
		{
			var dot = Dot(plane, point);
			return (dot < 0) ? (point + (dot * plane.Normal)) : (point - (dot * plane.Normal));
		}

		/// <summary>
		/// Calculates the distance from the point to the closest point on the plane.
		/// </summary>
		/// <param name="plane">The plane to get the distance to.</param>
		/// <param name="point">The point to get the distance to.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float Distance(in Plane3D plane, in Vec3 point) => MathF.Abs(Dot(plane, point));
		#endregion // Plane Functions

		#region Operators
		public static bool operator == (in Plane3D l, in Plane3D r) => l.Normal == r.Normal && l.D == r.D;
		public static bool operator != (in Plane3D l, in Plane3D r) => l.Normal != r.Normal || l.D != r.D;
		#endregion // Operators

		public readonly void Deconstruct(out Vec3 normal, out float d)
		{
			normal = Normal;
			d = D;
		}
	}
}
