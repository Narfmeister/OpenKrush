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

namespace OpenRA.Mods.OpenKrush.Mechanics.DataFromAssets.Traits;

using Common.Graphics;
using Common.Traits;
using Common.Traits.Render;
using Graphics;
using JetBrains.Annotations;
using OpenRA.Graphics;

[UsedImplicitly]
[Desc("Use asset provided turret offset.")]
public class WithOffsetsSpriteTurretInfo : WithSpriteTurretInfo, IRenderActorPreviewSpritesInfo
{
	[Desc("While the body plays a multi-frame move sequence, embedded MOBD attachment points can change every tick and make the turret jitter.",
		"If true, use the idle pose's embedded anchor (same facing) for the turret mount instead of the current walk frame.")]
	public readonly bool StableTurretEmbeddedOffsetWhenMoving = false;

	public override object Create(ActorInitializer init)
	{
		return new WithOffsetsSpriteTurret(init.Self, this);
	}

	public new IEnumerable<IActorPreview> RenderPreviewSprites(ActorPreviewInitializer init, string image, int facings, PaletteReference p)
	{
		if (!this.EnabledByDefault)
			yield break;

		var body = init.Actor.TraitInfoOrDefault<BodyOrientationInfo>();
		var turretedInfo = init.Actor.TraitInfos<TurretedInfo>().FirstOrDefault(tt => tt.Turret == this.Turret);

		if (turretedInfo == null)
			yield break;

		// Use the same turret world facing for the body anim as the turret layer and as runtime. Buildings often
		// have no IFacing, so init.GetFacing() is WAngle.Zero while Turreted uses InitialFacing (e.g. 512), which
		// makes embedded turret offset lookups disagree with the drawn body/turret in place-building previews.
		var worldTurretFacing = turretedInfo.WorldFacingFromInit(init);
		var offset = new Func<WVec>(() => body.LocalToWorld(turretedInfo.Offset.Rotate(
			body.QuantizeOrientation(WRot.FromYaw(worldTurretFacing()), facings))));

		var bodyAnim = new Animation(init.World, image, worldTurretFacing);
		bodyAnim.PlayRepeating(RenderSprites.NormalizeSequence(bodyAnim, init.GetDamageState(), "idle"));

		if (bodyAnim.CurrentSequence is OffsetsSpriteSequence bodySequence && bodySequence.EmbeddedOffsets.TryGetValue(bodyAnim.Image, out var imageOffset))
		{
			var point = imageOffset.FirstOrDefault(p1 => p1.Id == 0);

			if (point != null)
				offset = () => new(point.X * 32, point.Y * 32, 0);
		}

		if (this.IsPlayerPalette)
			p = init.WorldRenderer.Palette(this.Palette + init.Get<OwnerInit>().InternalName);
		else if (this.Palette != null)
			p = init.WorldRenderer.Palette(this.Palette);

		var anim = new Animation(init.World, image, worldTurretFacing);
		anim.Play(RenderSprites.NormalizeSequence(anim, init.GetDamageState(), this.Sequence));

		yield return new SpriteActorPreview(
			anim,
			offset,
			() =>
			{
				var tmpOffset = offset();

				return -(tmpOffset.Y + tmpOffset.Z) + 1;
			},
			p
		);
	}
}

public class WithOffsetsSpriteTurret : WithSpriteTurret
{
	private readonly WithOffsetsSpriteTurretInfo offsetsInfo;
	private readonly WithSpriteBody wsb;

	public WithOffsetsSpriteTurret(Actor self, WithSpriteTurretInfo info)
		: base(self, info)
	{
		this.offsetsInfo = (WithOffsetsSpriteTurretInfo)info;
		this.wsb = self.TraitOrDefault<WithSpriteBody>();
	}

	internal bool TryResolveBodyEmbeddedAnchor(Actor self, out OffsetsSpriteSequence sequence, out Sprite sprite)
	{
		sequence = null!;
		sprite = default;

		if (this.wsb == null)
			return false;

		var anim = this.wsb.DefaultAnimation;
		if (anim.CurrentSequence is not OffsetsSpriteSequence currentOs)
			return false;

		if (!this.offsetsInfo.StableTurretEmbeddedOffsetWhenMoving
			|| anim.CurrentSequence == null
			|| RenderSprites.UnnormalizeSequence(anim.CurrentSequence.Name) != "move")
		{
			sequence = currentOs;
			sprite = anim.Image;
			return true;
		}

		var idleSeqName = RenderSprites.NormalizeSequence(anim, self.GetDamageState(), this.wsb.Info.Sequence);
		if (!anim.HasSequence(idleSeqName) || anim.GetSequence(idleSeqName) is not OffsetsSpriteSequence idleOs)
		{
			sequence = currentOs;
			sprite = anim.Image;
			return true;
		}

		sequence = idleOs;
		sprite = idleOs.GetSprite(0, RenderSprites.MakeFacingFunc(self)());
		return true;
	}

	protected override WVec TurretOffset(Actor self)
	{
		if (this.wsb == null || !this.TryResolveBodyEmbeddedAnchor(self, out var sequence, out var sprite))
			return base.TurretOffset(self);

		if (!sequence.EmbeddedOffsets.ContainsKey(sprite))
			return base.TurretOffset(self);

		var point = sequence.EmbeddedOffsets[sprite].FirstOrDefault(p => p.Id == 0);

		return point != null ? new(point.X * 32, point.Y * 32, base.TurretOffset(self).Z) : base.TurretOffset(self);
	}
}
