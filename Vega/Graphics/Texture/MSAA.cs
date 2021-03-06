﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020-2021 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using Vulkan;

namespace Vega.Graphics
{
	/// <summary>
	/// The available sample counts for multi-sample anti-aliasing. Note that specific platforms may not support
	/// specific counts.
	/// </summary>
	public enum MSAA : uint
	{
		/// <summary>
		/// One sample per pixel (no anti-aliasing).
		/// </summary>
		X1  = VkSampleCountFlags.E1,
		/// <summary>
		/// Two samples per pixel.
		/// </summary>
		X2  = VkSampleCountFlags.E2,
		/// <summary>
		/// Four samples per pixel.
		/// </summary>
		X4  = VkSampleCountFlags.E4,
		/// <summary>
		/// Eight samples per pixel.
		/// </summary>
		X8  = VkSampleCountFlags.E8,
		/// <summary>
		/// Sixteen samples per pixel.
		/// </summary>
		X16 = VkSampleCountFlags.E16
	}

	/// <summary>
	/// Utility functionality for <see cref="MSAA"/> values.
	/// </summary>
	public static class MSAAUtils
	{
		/// <summary>
		/// Checks if the given MSAA is supported by the current platform. <see cref="Core.Instance"/> must be
		/// populated.
		/// </summary>
		/// <param name="msaa">The MSAA level to check.</param>
		public static bool IsSupported(this MSAA msaa) => Core.Instance?.Graphics.Limits.IsMSAASupported(msaa) ?? false;
	}
}
