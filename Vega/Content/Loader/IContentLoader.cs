﻿/*
 * Microsoft Public License (Ms-PL) - Copyright (c) 2020-2021 Sean Moss
 * This file is subject to the terms and conditions of the Microsoft Public License, the text of which can be found in
 * the 'LICENSE' file at the root of this repository, or online at <https://opensource.org/licenses/MS-PL>.
 */

using System;

namespace Vega.Content
{
	// Internal non-generic content loader type, for non-generic handles
	internal interface IContentLoader
	{
		Type ContentType { get; }

		object LoadNonGeneric(string fullPath);
	}
}
