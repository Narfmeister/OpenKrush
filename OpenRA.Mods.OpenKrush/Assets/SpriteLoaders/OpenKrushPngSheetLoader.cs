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
using JetBrains.Annotations;
using OpenRA.Graphics;
using Primitives;
using System.IO;

// Wraps the engine PngSheetLoader to capture the per-file PngSheetMetadata in the static
// SpriteMetadataCache, since the new SpriteCache discards loader metadata after parsing.
[UsedImplicitly]
public class OpenKrushPngSheetLoader : ISpriteLoader
{
	private readonly PngSheetLoader inner = new();

	public bool TryParseSprite(Stream s, string filename, out ISpriteFrame[]? frames, out TypeDictionary? metadata)
	{
		if (!inner.TryParseSprite(s, filename, out frames, out metadata))
			return false;

		var pngMeta = metadata?.GetOrDefault<PngSheetMetadata>();
		if (pngMeta != null)
			SpriteMetadataCache.PngMetadata[filename] = pngMeta;

		return true;
	}
}
