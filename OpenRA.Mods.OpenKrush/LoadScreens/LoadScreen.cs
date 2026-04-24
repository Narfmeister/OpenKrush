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

namespace OpenRA.Mods.OpenKrush.LoadScreens;

using Common.LoadScreens;
using Graphics;
using JetBrains.Annotations;

[UsedImplicitly]
public class LoadScreen : BlankLoadScreen
{
	private Sheet? sheet;
	private Sprite? logo;

	// BlankLoadScreen only draws the first time; the engine's SpriteCache, PrepareMap, etc. call
	// LoadScreen.Display() often. We must not repaint the static image every time or it flashes
	// over the UI (e.g. when lobby options touch map / asset loads). Reset when the artwork changes.
	private bool hasDisplayedForCurrentImage;

	public override void Init(ModData modData, Dictionary<string, string> info)
	{
		// Large enough for big MAPD layers in the asset browser; 8192×8192 is four times the surface (and memory) of 4096².
		Game.Settings.Graphics.SheetSize = 4096;

		base.Init(modData, info);

		this.sheet = new(SheetType.BGRA, modData.DefaultFileSystem.Open("uibits/splashscreen.png"));
		this.logo = new(this.sheet, new(0, 0, 640, 480), TextureChannel.RGBA);
	}

	public override void StartGame(Arguments args)
	{
		base.StartGame(args);

		this.sheet?.Dispose();

		this.sheet = new(SheetType.BGRA, this.ModData.DefaultFileSystem.Open("uibits/loadscreen.png"));
		this.logo = new(this.sheet, new(0, 0, 640, 480), TextureChannel.RGBA);
		this.hasDisplayedForCurrentImage = false;
	}

	public override void Display()
	{
		if (Game.Renderer == null || this.logo == null || this.hasDisplayedForCurrentImage)
			return;

		var logoPos = new float2(Game.Renderer.Resolution.Width / 2 - 320, Game.Renderer.Resolution.Height / 2 - 240);

		Game.Renderer.BeginUI();
		Game.Renderer.RgbaSpriteRenderer.DrawSprite(this.logo, logoPos);
		Game.Renderer.EndFrame(new NullInputHandler());

		this.hasDisplayedForCurrentImage = true;
	}

	protected override void Dispose(bool disposing)
	{
		this.sheet?.Dispose();

		base.Dispose(disposing);
	}
}
