#region Copyright & License Information

/*
 * Copyright 2007-2022 The OpenKrush Developers (see AUTHORS)
 * This file is part of OpenKrush, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the License, or (at your option) any later version. For
 * more information, see COPYING.
 */

#endregion

namespace OpenRA.Mods.OpenKrush.Mechanics.Ui.Traits;

using System.Linq;
using JetBrains.Annotations;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

/// <summary>
/// Like <see cref="Selectable"/> / <see cref="Interactable"/>, but enable/disable with
/// <see cref="ConditionalTraitInfo.RequiresCondition"/>. When disabled, <see cref="IMouseBounds.MouseoverBounds"/>
/// returns an empty polygon so the actor is not under-cursor; <see cref="IDisabledTrait"/> marks the trait for systems
/// that respect disabled traits. Screen map is refreshed on toggle.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
[Desc("Selectable with Interactable-style bounds, toggled by RequiresCondition. Use when engine Selectable cannot (not a ConditionalTrait).")]
public class ConditionalSelectableInfo : ConditionalTraitInfo, IMouseBoundsInfo, ISelectableInfo
{
	[Desc("Custom rectangle for mouse interaction (WDist width, height, optional x/y offset).")]
	public readonly WDist[]? Bounds = null;

	[Desc("Custom rectangle for decoration/selection box; if null, Bounds is used.")]
	public readonly WDist[]? DecorationBounds = null;

	[Desc("Custom polygon; if set, used instead of Bounds.")]
	public readonly int2[]? Polygon = null;

	public readonly int Priority = 10;

	[Desc("Selection priority modifier hotkey: None, Ctrl, or Alt.")]
	public readonly SelectionPriorityModifiers PriorityModifiers = SelectionPriorityModifiers.None;

	[Desc("Selection class for select-by-type; defaults to the actor name.")]
	public readonly string? Class = null;

	[VoiceReference]
	public readonly string Voice = "Select";

	int ISelectableInfo.Priority => Priority;
	SelectionPriorityModifiers ISelectableInfo.PriorityModifiers => PriorityModifiers;
	string ISelectableInfo.Voice => Voice;

	public override object Create(ActorInitializer init)
	{
		return new ConditionalSelectable(init.Self, this);
	}
}

public class ConditionalSelectable : ConditionalTrait<ConditionalSelectableInfo>, ISelectable, IMouseBounds
{
	readonly int2 polygonCenterOffset;
	readonly string selectionClass;
	IAutoMouseBounds[]? autoBounds;

	public ConditionalSelectable(Actor self, ConditionalSelectableInfo info)
		: base(info)
	{
		if (info.Polygon != null)
		{
			var rect = new Polygon(info.Polygon).BoundingRect;
			polygonCenterOffset = new int2(-rect.Width / 2, -rect.Height / 2);
		}

		selectionClass = string.IsNullOrEmpty(info.Class) ? self.Info.Name : info.Class;
	}

	protected override void Created(Actor self)
	{
		base.Created(self);
		autoBounds = self.TraitsImplementing<IAutoMouseBounds>().ToArray();
	}

	protected override void TraitEnabled(Actor self)
	{
		self.World.ScreenMap.AddOrUpdate(self);
	}

	protected override void TraitDisabled(Actor self)
	{
		self.World.ScreenMap.AddOrUpdate(self);
	}

	Rectangle AutoBounds(Actor self, WorldRenderer wr)
	{
		if (autoBounds == null)
			return Rectangle.Empty;

		return autoBounds.Select(s => s.AutoMouseoverBounds(self, wr)).FirstOrDefault(r => !r.IsEmpty);
	}

	int2[]? PolygonBounds(Actor self, WorldRenderer wr)
	{
		if (Info.Polygon == null)
			return null;

		var p = Info.Polygon;
		var screenVertices = new int2[p.Length];
		for (var i = 0; i < p.Length; i++)
		{
			var vec = p[i] + polygonCenterOffset;
			var offset = new int2(vec.X * wr.TileSize.Width / wr.TileScale, vec.Y * wr.TileSize.Height / wr.TileScale);
			screenVertices[i] = wr.ScreenPxPosition(self.CenterPosition) + offset;
		}

		return screenVertices;
	}

	Polygon ComputeBounds(Actor self, WorldRenderer wr, WDist[]? bounds)
	{
		if (bounds == null)
			return new Polygon(AutoBounds(self, wr));

		var size = new int2(
			bounds[0].Length * wr.TileSize.Width / wr.TileScale,
			bounds[1].Length * wr.TileSize.Height / wr.TileScale);

		var offset = -size / 2;
		if (bounds.Length > 2)
			offset += new int2(bounds[2].Length * wr.TileSize.Width / wr.TileScale, bounds[3].Length * wr.TileSize.Height / wr.TileScale);

		var xy = wr.ScreenPxPosition(self.CenterPosition) + offset;
		return new Polygon(new Rectangle(xy.X, xy.Y, size.X, size.Y));
	}

	Polygon IMouseBounds.MouseoverBounds(Actor self, WorldRenderer wr)
	{
		if (IsTraitDisabled)
			return Polygon.Empty;

		if (Info.Polygon != null)
		{
			var pb = PolygonBounds(self, wr);
			return pb != null ? new Polygon(pb) : Polygon.Empty;
		}

		return ComputeBounds(self, wr, Info.Bounds);
	}

	public Rectangle DecorationBounds(Actor self, WorldRenderer wr)
	{
		if (IsTraitDisabled)
			return Rectangle.Empty;

		return ComputeBounds(self, wr, Info.DecorationBounds ?? Info.Bounds).BoundingRect;
	}

	string ISelectable.Class => selectionClass;
}
