using System;
using System.Collections.Generic;
using Robust.Shared.Collections;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

/// <summary>
/// Class for storing session specific PVS data.
/// </summary>
internal sealed class SessionPvsData(ICommonSession session)
{
    /// <summary>
    /// All <see cref="EntityUid"/>s that this session saw during the last <see cref="PvsSystem.DirtyBufferSize"/> ticks.
    /// </summary>
    public readonly OverflowDictionary<GameTick, List<EntityData>> SentEntities = new(PvsSystem.DirtyBufferSize);

    public readonly Dictionary<NetEntity, EntityData> EntityData = new();

    /// <summary>
    /// <see cref="SentEntities"/> overflow in case a player's last ack is more than
    /// <see cref="PvsSystem.DirtyBufferSize"/> ticks behind the current tick.
    /// </summary>
    public (GameTick Tick, List<EntityData> SentEnts)? Overflow;

    /// <summary>
    /// If true, the client has explicitly requested a full state. Unlike the first state, we will send them all data,
    /// not just data that cannot be implicitly inferred from entity prototypes.
    /// </summary>
    public bool RequestedFull = false;

    public GameTick LastReceivedAck;

    public readonly ICommonSession Session = session;
}

/// <summary>
/// Class for storing session-specific information about when an entity was last sent to a player.
/// </summary>
internal sealed class EntityData(Entity<MetaDataComponent> entity) : IEquatable<EntityData>
{
    public readonly Entity<MetaDataComponent> Entity = entity;
    public readonly NetEntity NetEntity = entity.Comp.NetEntity;

    /// <summary>
    /// Tick at which this entity was last sent to a player.
    /// </summary>
    public GameTick LastSent;

    /// <summary>
    /// Tick at which an entity last left a player's PVS view.
    /// </summary>
    public GameTick LastLeftView;

    /// <summary>
    /// Stores the last tick at which a given entity was acked by a player. Used to avoid re-sending the whole entity
    /// state when an item re-enters PVS. This is only the same as the player's last acked tick if the entity was
    /// present in that state.
    /// </summary>
    public GameTick EntityLastAcked;

    /// <summary>
    /// Entity visibility state when it was last sent to this player.
    /// </summary>
    public PvsEntityVisibility Visibility;

    public bool Equals(EntityData? other)
    {
#if DEBUG
        // Each this class should be unique for each entity-session combination, and should never be getting compared
        // across sessions.
        if (Entity.Owner == other?.Entity.Owner)
            DebugTools.Assert(ReferenceEquals(this, other));
#endif
        return Entity.Owner == other?.Entity.Owner;
    }

    public override int GetHashCode()
    {
        return Entity.Owner.GetHashCode();
    }

    public override string ToString()
    {
        var rep = new EntityStringRepresentation(Entity);
        return $"PVS Entity: {rep} - {LastSent}/{LastLeftView}/{EntityLastAcked} - {Visibility}";
    }
}
