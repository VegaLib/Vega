﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;
using System.Collections.Generic;
using Vulkan;

namespace Vega.Graphics
{
	/// <summary>
	/// Contains the set of known optional features that may be enabled on a graphics device.
	/// </summary>
	public unsafe struct GraphicsFeatures
	{
		private const int FEATURE_COUNT = 1;
		private static readonly GraphicsFeature[] FEATURES = new GraphicsFeature[FEATURE_COUNT] { 
			new("", null, null)
		};

		#region Fields
		// The array of feature flags
		private fixed bool _features[FEATURE_COUNT];
		#endregion // Fields

		// Populates the features with support flags
		internal GraphicsFeatures(in VkPhysicalDeviceFeatures afeats, IReadOnlyList<string> aexts)
		{
			for (int i = 0; i < FEATURE_COUNT; ++i) {
				_features[i] = FEATURES[i].Check(afeats, aexts);
			}
		}

		// Checks the features, populates the objects to create a device
		internal bool TryBuild(
			in VkPhysicalDeviceFeatures afeats, IReadOnlyList<string> aexts,
			out VkPhysicalDeviceFeatures efeats, List<string> eexts,
			out string? missing)
		{
			missing = null;
			efeats = new();

			// Check features and populate objects
			for (int i = 0; i < FEATURE_COUNT; ++i) {
				if (!_features[i]) {
					continue;
				}

				var feat = FEATURES[i];
				var check = feat.Check(afeats, aexts);
				if (!check) {
					missing = feat.Name;
					return false;
				}
				feat.Enable(ref efeats, eexts);
			}

			return true;
		}
	}
}
