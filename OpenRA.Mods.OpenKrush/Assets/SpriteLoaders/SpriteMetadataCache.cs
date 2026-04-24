#region Copyright & License Information

/*
 * Copyright 2007-2022 The OpenKrush Developers (see AUTHORS)
 * This file is part of OpenKrush, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

#endregion

namespace OpenRA.Mods.OpenKrush.Assets.SpriteLoaders;

using Common.SpriteLoaders;
using Mechanics.DataFromAssets.Graphics;

// The new OpenRA SpriteCache discards per-frame metadata, so capture it here at parse time
// and look it up again from OffsetsSpriteSequence once sprites have been resolved.
public static class SpriteMetadataCache
{
	public static readonly Dictionary<string, EmbeddedSpriteOffsets> MobdOffsets = new();
	public static readonly Dictionary<string, PngSheetMetadata> PngMetadata = new();
}
