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

namespace OpenRA.Mods.OpenKrush.Mechanics.DataFromAssets.Graphics;

using Assets.SpriteLoaders;
using Common.Graphics;
using JetBrains.Annotations;
using OpenRA.Graphics;

public class Offset
{
	public readonly int Id;
	public readonly int X;
	public readonly int Y;

	public Offset(int id, int x, int y)
	{
		this.Id = id;
		this.X = x;
		this.Y = y;
	}
}

public class EmbeddedSpriteOffsets
{
	public readonly Dictionary<int, Offset[]> FrameOffsets;

	public EmbeddedSpriteOffsets(Dictionary<int, Offset[]> frameOffsets)
	{
		this.FrameOffsets = frameOffsets;
	}
}

[UsedImplicitly]
public class OffsetsSpriteSequenceLoader : DefaultSpriteSequenceLoader
{
	public OffsetsSpriteSequenceLoader(ModData modData)
		: base(modData)
	{
	}

	public override ISpriteSequence CreateSequence(
		ModData modData,
		string tileset,
		SpriteCache cache,
		string image,
		string sequence,
		MiniYaml data,
		MiniYaml defaults
	)
	{
		return new OffsetsSpriteSequence(cache, this, image, sequence, data, defaults);
	}
}

public sealed class OffsetsSpriteSequence : DefaultSpriteSequence
{
	public readonly Dictionary<Sprite, Offset[]> EmbeddedOffsets = new();

	private readonly List<string> pendingFilenames = new();

	public OffsetsSpriteSequence(SpriteCache cache, ISpriteSequenceLoader loader, string image, string sequence, MiniYaml data, MiniYaml defaults)
		: base(cache, loader, image, sequence, data, defaults)
	{
	}

	// OpenKrush sequences historically embed the asset filename as the YAML node value
	// (`idle: sprites.lvl|Oil.mobd`). The upstream DefaultSpriteSequence requires an
	// explicit `Filename:` field, so bridge the two conventions here.
	protected override IEnumerable<ReservationInfo> ParseFilenames(ModData modData, string tileset, int[] frames, MiniYaml data, MiniYaml defaults)
	{
		var filenamePatternNode = data.NodeWithKeyOrDefault(FilenamePattern.Key) ?? defaults.NodeWithKeyOrDefault(FilenamePattern.Key);
		var filenameField = LoadField(Filename, data, defaults);

		if (!string.IsNullOrEmpty(filenamePatternNode?.Value.Value) || !string.IsNullOrEmpty(filenameField))
		{
			var parsed = base.ParseFilenames(modData, tileset, frames, data, defaults).ToArray();
			foreach (var r in parsed)
				this.pendingFilenames.Add(r.Filename);

			return parsed;
		}

		if (!string.IsNullOrEmpty(data.Value))
		{
			var loadFrames = CalculateFrameIndices(start, length, stride ?? length ?? 0, facings, frames, transpose, reverseFacings, shadowStart);
			this.pendingFilenames.Add(data.Value);

			return new[] { new ReservationInfo(data.Value, loadFrames, frames, default) };
		}

		var fallback = base.ParseFilenames(modData, tileset, frames, data, defaults).ToArray();
		foreach (var r in fallback)
			this.pendingFilenames.Add(r.Filename);

		return fallback;
	}

	public override void ResolveSprites(SpriteCache cache)
	{
		base.ResolveSprites(cache);

		if (this.sprites == null)
			return;

		foreach (var filename in this.pendingFilenames)
		{
			if (string.IsNullOrEmpty(filename))
				continue;

			if (SpriteMetadataCache.MobdOffsets.TryGetValue(filename, out var mobdOffsets))
			{
				if (mobdOffsets.FrameOffsets == null)
					continue;

				for (var i = 0; i < this.sprites.Length; i++)
				{
					if (this.sprites[i] == null)
						continue;

					if (mobdOffsets.FrameOffsets.TryGetValue(i, out var off))
						this.EmbeddedOffsets[this.sprites[i]] = off;
				}
			}
			else if (SpriteMetadataCache.PngMetadata.TryGetValue(filename, out var pngMeta))
			{
				for (var i = 0; i < this.sprites.Length; i++)
				{
					if (this.sprites[i] == null || !pngMeta.Metadata.ContainsKey($"Offsets[{i}]"))
						continue;

					var lines = pngMeta.Metadata[$"Offsets[{i}]"].Split('|');
					var convertOffsets = new Func<string[], Offset>(d => new(int.Parse(d[0]), int.Parse(d[1]), int.Parse(d[2])));
					this.EmbeddedOffsets[this.sprites[i]] = lines.Select(t => t.Split(',')).Select(convertOffsets).ToArray();
				}
			}
		}
	}
}
