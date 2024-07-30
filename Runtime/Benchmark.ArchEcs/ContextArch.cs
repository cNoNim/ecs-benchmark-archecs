using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Arch.Buffer;
using Arch.Core;
using Arch.Core.Extensions;
using Arch.Core.Utils;
using Arch.System;
using Benchmark.Core;
using Benchmark.Core.Components;
using Benchmark.Core.Random;
using static Benchmark.Core.ContextBase;
using StableHash32 = Benchmark.Core.Hash.StableHash32;

namespace Benchmark.ArchEcs
{

#if PURE_ECS
public class ContextArchPure : ComplexContextBase
#else
public class ContextArch : ContextBase
#endif
{
	private World?      _world;
	private Group<int>? _ecsSystems;
#if PURE_ECS
	static ContextArchPure()
#else
	static ContextArch()
#endif
	{
		ArrayRegistry.Add<Position>();
		ArrayRegistry.Add<Velocity>();
		ArrayRegistry.Add<Sprite>();
		ArrayRegistry.Add<Unit>();
		ArrayRegistry.Add<Data>();
		ArrayRegistry.Add<Health>();
		ArrayRegistry.Add<Damage>();
		ArrayRegistry.Add<Attack<EntityReference>>();
		ArrayRegistry.Add<Spawn>();
		ArrayRegistry.Add<Dead>();
		ArrayRegistry.Add<Unit.NPC>();
		ArrayRegistry.Add<Unit.Hero>();
		ArrayRegistry.Add<Unit.Monster>();
	}

#if PURE_ECS
	public ContextArchPure()
		: base("Arch Pure")
#else
	public ContextArch()
		: base("Arch")
#endif
	{}

	protected override void DoSetup()
	{
		var world = _world = World.Create();
		_ecsSystems = new Group<int>("Group");
		_ecsSystems.Add(new SpawnSystem(world));
		_ecsSystems.Add(new RespawnSystem(world));
		_ecsSystems.Add(new KillSystem(world));
		_ecsSystems.Add(new RenderSystem(world, Framebuffer));
		_ecsSystems.Add(new SpriteSystem(world));
		_ecsSystems.Add(new DamageSystem(world));
		_ecsSystems.Add(new AttackSystem(world));
		_ecsSystems.Add(new MovementSystem(world));
		_ecsSystems.Add(new UpdateVelocitySystem(world));
		_ecsSystems.Add(new UpdateDataSystem(world));
		_ecsSystems.Initialize();

		world.Reserve(RespawnSystem.SpawnArchetype, EntityCount);
		for (var i = 0; i < EntityCount; ++i)
			world.Create(RespawnSystem.SpawnArchetype)
				 .Set(
					  new Unit
					  {
						  Id   = (uint) i,
						  Seed = (uint) i,
					  });
	}

	protected override void DoRun(int tick) =>
		_ecsSystems?.Update(tick);

	protected override void DoCleanup()
	{
		_ecsSystems?.Dispose();
		_ecsSystems = null;
		_world?.Dispose();
		_world = null;
	}
}

internal partial class SpawnSystem : BaseSystem<World, int>
{
	private readonly CommandBuffer _commandBuffer = new();

	public SpawnSystem(World world)
		: base(world) {}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override void Update(in int t)
	{
		RunQuery(World, _commandBuffer);
		_commandBuffer.Playback(World);
	}

	public override void Dispose() =>
		_commandBuffer.Dispose();

	[Query]
	[All(typeof(Spawn))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run(
		[Data] CommandBuffer commandBuffer,
		ref Unit unit,
		in Data data,
		in Entity entity)
	{
		switch (SpawnUnit(
					in data,
					ref unit,
					out var health,
					out var damage,
					out var sprite,
					out var position,
					out var velocity))
		{
		case UnitType.NPC:
			commandBuffer.Add<Unit.NPC>(entity);
			break;
		case UnitType.Hero:
			commandBuffer.Add<Unit.Hero>(entity);
			break;
		case UnitType.Monster:
			commandBuffer.Add<Unit.Monster>(entity);
			break;
		}

		commandBuffer.Add(entity, health);
		commandBuffer.Add(entity, damage);
		commandBuffer.Add(entity, sprite);
		commandBuffer.Add(entity, position);
		commandBuffer.Add(entity, velocity);
		commandBuffer.Remove<Spawn>(entity);
	}
}

internal partial class UpdateDataSystem : BaseSystem<World, int>
{
	public UpdateDataSystem(World world)
		: base(world) {}

	[Query]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run(ref Data data) =>
		UpdateDataSystemForEach(ref data);
}

internal partial class UpdateVelocitySystem : BaseSystem<World, int>
{
	public UpdateVelocitySystem(World world)
		: base(world) {}

	[Query]
	[None(typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run(
		ref Velocity velocity,
		ref Unit unit,
		in Data data,
		in Position position) =>
		UpdateVelocitySystemForEach(
			ref velocity,
			ref unit,
			in data,
			in position);
}

internal partial class MovementSystem : BaseSystem<World, int>
{
	public MovementSystem(World world)
		: base(world) {}

	[Query]
	[None(typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run(ref Position position, in Velocity velocity) =>
		MovementSystemForEach(ref position, in velocity);
}

internal partial class AttackSystem : BaseSystem<World, int>
{
	private static readonly ComponentType[] AttackArchetype = { typeof(Attack<EntityReference>) };

	private static readonly Comparison<KeyValuePair<uint, Target<Entity>>> Comparison = static (a, b) =>
		a.Key.CompareTo(b.Key);

	private readonly CommandBuffer                            _commandBuffer = new();
	private readonly List<KeyValuePair<uint, Target<Entity>>> _targets       = new();

	public AttackSystem(World world)
		: base(world) {}

	public override void Update(in int t)
	{
		_targets.Clear();
		FillTargetsQuery(World);
		_targets.Sort(Comparison);
		CreateAttacksQuery(World, _commandBuffer, _targets.Count);
		_commandBuffer.Playback(World);
	}

	public override void Dispose() =>
		_targets.Clear();

	[Query]
	[None(typeof(Dead), typeof(Spawn))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void FillTargets(in Unit unit, in Position position, in Entity entity) =>
		_targets.Add(new KeyValuePair<uint, Target<Entity>>(unit.Id, new Target<Entity>(entity, position)));

	[Query]
	[None(typeof(Dead), typeof(Spawn))]
	private void CreateAttacks(
		[Data] CommandBuffer commandBuffer,
		[Data] int count,
		ref Unit unit,
		in Data data,
		in Damage damage,
		in Position position)
	{
		if (damage.Cooldown <= 0)
			return;

		var tick = data.Tick - unit.SpawnTick;
		if (tick % damage.Cooldown != 0)
			return;

		var generator    = new RandomGenerator(unit.Seed);
		var index        = generator.Random(ref unit.Counter, count);
		var target       = _targets[index].Value;
		var attackEntity = commandBuffer.Create(AttackArchetype);
		commandBuffer.Set(
			attackEntity,
			new Attack<EntityReference>
			{
				Target = target.Entity.Reference(),
				Damage = damage.Attack,
				Ticks  = Common.AttackTicks(position.V, target.Position),
			});
	}
}

internal partial class DamageSystem : BaseSystem<World, int>
{
	private readonly CommandBuffer _commandBuffer = new();

	public DamageSystem(World world)
		: base(world) {}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override void Update(in int t)
	{
		RunQuery(World, _commandBuffer);
		_commandBuffer.Playback(World);
	}

	[Query]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run([Data] CommandBuffer commandBuffer, ref Attack<EntityReference> attack, in Entity entity)
	{
		if (attack.Ticks-- > 0)
			return;

		var target       = attack.Target;
		var attackDamage = attack.Damage;

		commandBuffer.Destroy(entity);

		if (!target.IsAlive())
			return;
		var targetEntity = target.Entity;
		if (targetEntity.Has<Dead>())
			return;

		ref var          health      = ref targetEntity.Get<Health>();
		ref readonly var damage      = ref targetEntity.Get<Damage>();
		var              totalDamage = attackDamage - damage.Defence;
		health.Hp -= totalDamage;
	}
}

internal partial class KillSystem : BaseSystem<World, int>
{
	private readonly CommandBuffer _commandBuffer = new();

	public KillSystem(World world)
		: base(world) {}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override void Update(in int t)
	{
		RunQuery(World, _commandBuffer);
		_commandBuffer.Playback(World);
	}

	[Query]
	[None(typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run(
		[Data] CommandBuffer commandBuffer,
		ref Unit unit,
		in Health health,
		in Data data,
		in Entity entity)
	{
		if (health.Hp > 0)
			return;

		commandBuffer.Add<Dead>(entity);
		unit.RespawnTick = data.Tick + RespawnTicks;
	}
}

internal partial class SpriteSystem : BaseSystem<World, int>
{
	public SpriteSystem(World world)
		: base(world) {}

	[Query]
	[All(typeof(Spawn))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ForEachSpawn(ref Sprite sprite) =>
		sprite.Character = SpriteMask.Spawn;

	[Query]
	[All(typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ForEachDead(ref Sprite sprite) =>
		sprite.Character = SpriteMask.Grave;

	[Query]
	[All(typeof(Unit.NPC))]
	[None(typeof(Spawn), typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ForEachNpc(ref Sprite sprite) =>
		sprite.Character = SpriteMask.NPC;

	[Query]
	[All(typeof(Unit.Hero))]
	[None(typeof(Spawn), typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ForEachHero(ref Sprite sprite) =>
		sprite.Character = SpriteMask.Hero;

	[Query]
	[All(typeof(Unit.Monster))]
	[None(typeof(Spawn), typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ForEachMonster(ref Sprite sprite) =>
		sprite.Character = SpriteMask.Monster;
}

internal partial class RenderSystem : BaseSystem<World, int>
{
	private readonly Framebuffer _framebuffer;

	public RenderSystem(World world, Framebuffer framebuffer)
		: base(world) =>
		_framebuffer = framebuffer;

	[Query]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run(
		in Position position,
		in Sprite sprite,
		in Unit unit,
		in Data data) =>
		RenderSystemForEach(
			_framebuffer,
			in position,
			in sprite,
			in unit,
			in data);
}

internal partial class RespawnSystem : BaseSystem<World, int>
{
	public static readonly ComponentType[] SpawnArchetype = { typeof(Spawn), typeof(Data), typeof(Unit) };
	private readonly       CommandBuffer   _commandBuffer = new();

	public RespawnSystem(World world)
		: base(world) {}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override void Update(in int t)
	{
		RunQuery(World, _commandBuffer);
		_commandBuffer.Playback(World);
	}

	[Query]
	[All(typeof(Dead))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Run(
		[Data] CommandBuffer commandBuffer,
		in Unit unit,
		in Data data,
		in Entity entity)
	{
		if (data.Tick < unit.RespawnTick)
			return;

		var newEntity = commandBuffer.Create(SpawnArchetype);
		commandBuffer.Set(newEntity, data);
		commandBuffer.Set(
			newEntity,
			new Unit
			{
				Id   = unit.Id | (uint) data.Tick << 16,
				Seed = StableHash32.Hash(unit.Seed, unit.Counter),
			});
		commandBuffer.Destroy(entity);
	}
}

}
