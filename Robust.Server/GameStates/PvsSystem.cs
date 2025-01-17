using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.ObjectPool;
using Robust.Server.Configuration;
using Robust.Server.Player;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Threading;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

internal sealed partial class PvsSystem : EntitySystem
{
    [Shared.IoC.Dependency] private readonly IConfigurationManager _configManager = default!;
    [Shared.IoC.Dependency] private readonly INetworkedMapManager _mapManager = default!;
    [Shared.IoC.Dependency] private readonly IPlayerManager _playerManager = default!;
    [Shared.IoC.Dependency] private readonly IParallelManager _parallelManager = default!;
    [Shared.IoC.Dependency] private readonly IServerGameStateManager _serverGameStateManager = default!;
    [Shared.IoC.Dependency] private readonly IServerNetConfigurationManager _netConfigManager = default!;
    [Shared.IoC.Dependency] private readonly SharedTransformSystem _transform = default!;

    public const float ChunkSize = 8;

    // TODO make this a cvar. Make it in terms of seconds and tie it to tick rate?
    // Main issue is that I CBF figuring out the logic for handling it changing mid-game.
    public const int DirtyBufferSize = 20;
    // Note: If a client has ping higher than TickBuffer / TickRate, then the server will treat every entity as if it
    // had entered PVS for the first time. Note that due to the PVS budget, this buffer is easily overwhelmed.

    /// <summary>
    /// See <see cref="CVars.NetForceAckThreshold"/>.
    /// </summary>
    public int ForceAckThreshold { get; private set; }

    /// <summary>
    /// Maximum number of pooled objects.
    /// </summary>
    private const int MaxVisPoolSize = 1024;

    /// <summary>
    /// Is view culling enabled, or will we send the whole map?
    /// </summary>
    public bool CullingEnabled { get; private set; }

    /// <summary>
    /// Size of the side of the view bounds square.
    /// </summary>
    private float _viewSize;

    /// <summary>
    /// Per-tick ack data to avoid re-allocating.
    /// </summary>
    private readonly List<ICommonSession> _toAck = new();
    private PvsAckJob _ackJob;

    /// <summary>
    /// If PVS disabled then we'll track if we've dumped all entities on the player.
    /// This way any future ticks can be orders of magnitude faster as we only send what changes.
    /// </summary>
    private HashSet<ICommonSession> _seenAllEnts = new();

    internal readonly Dictionary<ICommonSession, SessionPvsData> PlayerData = new();

    private PVSCollection<NetEntity> _entityPvsCollection = default!;
    public PVSCollection<NetEntity> EntityPVSCollection => _entityPvsCollection;

    private readonly List<IPVSCollection> _pvsCollections = new();

    private readonly ObjectPool<HashSet<NetEntity>> _netUidSetPool
        = new DefaultObjectPool<HashSet<NetEntity>>(new SetPolicy<NetEntity>(), MaxVisPoolSize);

    private readonly ObjectPool<List<NetEntity>> _netUidListPool
        = new DefaultObjectPool<List<NetEntity>>(new ListPolicy<NetEntity>(), MaxVisPoolSize);

    private readonly ObjectPool<List<EntityData>> _entDataListPool
        = new DefaultObjectPool<List<EntityData>>(new ListPolicy<EntityData>(), MaxVisPoolSize);

    private readonly ObjectPool<HashSet<EntityUid>> _uidSetPool
        = new DefaultObjectPool<HashSet<EntityUid>>(new SetPolicy<EntityUid>(), MaxVisPoolSize);

    private readonly ObjectPool<Stack<NetEntity>> _stackPool
        = new DefaultObjectPool<Stack<NetEntity>>(
            new StackPolicy<NetEntity>(), MaxVisPoolSize);

    private readonly ObjectPool<HashSet<int>> _playerChunkPool =
        new DefaultObjectPool<HashSet<int>>(new SetPolicy<int>(), MaxVisPoolSize);

    private readonly ObjectPool<RobustTree<NetEntity>> _treePool =
        new DefaultObjectPool<RobustTree<NetEntity>>(new TreePolicy<NetEntity>(), MaxVisPoolSize);

    private readonly ObjectPool<Dictionary<MapChunkLocation, int>> _mapChunkPool =
        new DefaultObjectPool<Dictionary<MapChunkLocation, int>>(
            new ChunkPoolPolicy<MapChunkLocation>(), MaxVisPoolSize);

    private readonly ObjectPool<Dictionary<GridChunkLocation, int>> _gridChunkPool =
        new DefaultObjectPool<Dictionary<GridChunkLocation, int>>(
            new ChunkPoolPolicy<GridChunkLocation>(), MaxVisPoolSize);

    private readonly Dictionary<int, Dictionary<MapChunkLocation, int>> _mapIndices = new(4);
    private readonly Dictionary<int, Dictionary<GridChunkLocation, int>> _gridIndices = new(4);
    private readonly List<(int, IChunkIndexLocation)> _chunkList = new(64);
    internal readonly HashSet<ICommonSession> PendingAcks = new();

    private readonly Dictionary<(int visMask, IChunkIndexLocation location), RobustTree<NetEntity>?> _previousTrees = new();

    private readonly HashSet<(int visMask, IChunkIndexLocation location)> _reusedTrees = new();

    private EntityQuery<EyeComponent> _eyeQuery;
    private EntityQuery<MetaDataComponent> _metaQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize()
    {
        base.Initialize();

        _ackJob = new PvsAckJob()
        {
            System = this,
            Sessions = _toAck,
        };

        _eyeQuery = GetEntityQuery<EyeComponent>();
        _metaQuery = GetEntityQuery<MetaDataComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        _entityPvsCollection = RegisterPVSCollection<NetEntity>();

        SubscribeLocalEvent<MapChangedEvent>(ev =>
        {
            if (ev.Created)
                OnMapCreated(ev);
            else
                OnMapDestroyed(ev);
        });

        SubscribeLocalEvent<GridInitializeEvent>(OnGridCreated);
        SubscribeLocalEvent<GridRemovalEvent>(OnGridRemoved);

        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
        _transform.OnGlobalMoveEvent += OnEntityMove;
        SubscribeLocalEvent<TransformComponent, TransformStartupEvent>(OnTransformStartup);
        EntityManager.EntityDeleted += OnEntityDeleted;

        _configManager.OnValueChanged(CVars.NetPVS, SetPvs, true);
        _configManager.OnValueChanged(CVars.NetMaxUpdateRange, OnViewsizeChanged, true);
        _configManager.OnValueChanged(CVars.NetForceAckThreshold, OnForceAckChanged, true);

        _serverGameStateManager.ClientAck += OnClientAck;
        _serverGameStateManager.ClientRequestFull += OnClientRequestFull;

        InitializeDirty();
    }

    public override void Shutdown()
    {
        base.Shutdown();

        UnregisterPVSCollection(_entityPvsCollection);
        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
        EntityManager.EntityDeleted -= OnEntityDeleted;
        _transform.OnGlobalMoveEvent -= OnEntityMove;

        _configManager.UnsubValueChanged(CVars.NetPVS, SetPvs);
        _configManager.UnsubValueChanged(CVars.NetMaxUpdateRange, OnViewsizeChanged);
        _configManager.UnsubValueChanged(CVars.NetForceAckThreshold, OnForceAckChanged);

        _serverGameStateManager.ClientAck -= OnClientAck;
        _serverGameStateManager.ClientRequestFull -= OnClientRequestFull;

        ShutdownDirty();
    }

    // TODO PVS rate limit this?
    private void OnClientRequestFull(ICommonSession session, GameTick tick, NetEntity? missingEntity)
    {
        if (!PlayerData.TryGetValue(session, out var sessionData))
            return;

        // Update acked tick so that OnClientAck doesn't get invoked by any late acks.
        var lastAcked = sessionData.LastReceivedAck;
        sessionData.LastReceivedAck = _gameTiming.CurTick;

        var sb = new StringBuilder();
        sb.Append($"Client {session} requested full state on tick {tick}. Last Acked: {lastAcked}. Curtick: {_gameTiming.CurTick}.");

        if (missingEntity != null)
        {
            var entity = GetEntity(missingEntity)!;
            sb.Append($" Apparently they received an entity without metadata: {ToPrettyString(entity.Value)}.");

            if (sessionData.EntityData.TryGetValue(missingEntity.Value, out var data))
                sb.Append($" Entity last seen: {data.EntityLastAcked}");
        }

        Log.Warning(sb.ToString());

        if (sessionData.Overflow != null)
            _entDataListPool.Return(sessionData.Overflow.Value.SentEnts);
        sessionData.Overflow = null;

        foreach (var visSet in sessionData.SentEntities.Values)
        {
            _entDataListPool.Return(visSet);
        }
        sessionData.SentEntities.Clear();

        sessionData.RequestedFull = true;
        sessionData.EntityData.Clear();
    }

    private void OnViewsizeChanged(float obj)
    {
        _viewSize = obj * 2;
    }

    private void OnForceAckChanged(int value)
    {
        ForceAckThreshold = value;
    }

    private void SetPvs(bool value)
    {
        _seenAllEnts.Clear();
        CullingEnabled = value;
    }

    public void ProcessCollections()
    {
        foreach (var collection in _pvsCollections)
        {
            collection.Process();
        }
    }

    public void CullDeletionHistory(GameTick oldestAck)
    {
        _entityPvsCollection.CullDeletionHistoryUntil(oldestAck);
        _mapManager.CullDeletionHistory(oldestAck);
    }

    #region PVSCollection methods to maybe make public someday:tm:

    private PVSCollection<TIndex> RegisterPVSCollection<TIndex>() where TIndex : IComparable<TIndex>, IEquatable<TIndex>
    {
        var collection = new PVSCollection<TIndex>(Log, EntityManager, _transform);
        _pvsCollections.Add(collection);
        return collection;
    }

    private bool UnregisterPVSCollection<TIndex>(PVSCollection<TIndex> pvsCollection) where TIndex : IComparable<TIndex>, IEquatable<TIndex> =>
        _pvsCollections.Remove(pvsCollection);

    #endregion

    #region PVSCollection Event Updates

    private void OnEntityDeleted(EntityUid e, MetaDataComponent metadata)
    {
        _entityPvsCollection.RemoveIndex(EntityManager.CurrentTick, metadata.NetEntity);

        foreach (var sessionData in PlayerData.Values)
        {
            sessionData.EntityData.Remove(metadata.NetEntity);
        }
    }

    private void OnEntityMove(ref MoveEvent ev)
    {
        // GriddUid is only set after init.
        if (!ev.Component._gridInitialized)
            _transform.InitializeGridUid(ev.Sender, ev.Component);

        // since elements are cached grid-/map-relative, we dont need to update a given grids/maps children
        if (ev.Component.GridUid == ev.Sender)
            return;
        DebugTools.Assert(!_mapManager.IsGrid(ev.Sender));

        if (!ev.Component.ParentUid.IsValid())
        {
            // This entity is either a map, terminating, or a rare null-space entity.
            if (Terminating(ev.Sender))
                return;

            if (ev.Component.MapUid == ev.Sender)
                return;
        }

        DebugTools.Assert(!_mapManager.IsMap(ev.Sender));

        var coordinates = _transform.GetMoverCoordinates(ev.Sender, ev.Component);
        UpdateEntityRecursive(ev.Sender, _metaQuery.GetComponent(ev.Sender), ev.Component, coordinates, false, ev.ParentChanged);
    }

    private void OnTransformStartup(EntityUid uid, TransformComponent component, ref TransformStartupEvent args)
    {
        // use Startup because GridId is not set during the eventbus init yet!

        // since elements are cached grid-/map-relative, we dont need to update a given grids/maps children
        if (component.GridUid == uid)
            return;
        DebugTools.Assert(!_mapManager.IsGrid(uid));

        if (component.MapUid == uid)
            return;
        DebugTools.Assert(!_mapManager.IsMap(uid));

        var coordinates = _transform.GetMoverCoordinates(uid, component);
        UpdateEntityRecursive(uid, _metaQuery.GetComponent(uid), component, coordinates, false, false);
    }

    private void UpdateEntityRecursive(EntityUid uid, MetaDataComponent metadata, TransformComponent xform, EntityCoordinates coordinates, bool mover, bool forceDirty)
    {
        if (mover && !xform.LocalPosition.Equals(Vector2.Zero))
        {
            coordinates = _transform.GetMoverCoordinates(uid, xform);
        }

        // since elements are cached grid-/map-relative, we don't need to update a given grids/maps children
        DebugTools.Assert(!_mapManager.IsGrid(uid) && !_mapManager.IsMap(uid));

        var indices = PVSCollection<NetEntity>.GetChunkIndices(coordinates.Position);
        if (xform.GridUid != null)
            _entityPvsCollection.UpdateIndex(metadata.NetEntity, xform.GridUid.Value, indices, forceDirty: forceDirty);
        else
            _entityPvsCollection.UpdateIndex(metadata.NetEntity, xform.MapID, indices, forceDirty: forceDirty);

        // TODO PERFORMANCE
        // Given uid is the parent of its children, we already know that the child xforms will have to be relative to
        // coordinates.EntityId. So instead of calling GetMoverCoordinates() for each child we should just calculate it
        // directly.
        foreach (var child in xform._children)
        {
            UpdateEntityRecursive(child, _metaQuery.GetComponent(child), _xformQuery.GetComponent(child), coordinates, true, forceDirty);
        }
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus == SessionStatus.InGame)
        {
            if (!PlayerData.TryAdd(e.Session, new(e.Session)))
                Log.Error($"Attempted to add player to _playerVisibleSets, but they were already present? Session:{e.Session}");

            foreach (var pvsCollection in _pvsCollections)
            {
                if (!pvsCollection.AddPlayer(e.Session))
                    Log.Error($"Attempted to add player to pvsCollection, but they were already present? Session:{e.Session}");
            }
            return;
        }

        if (e.NewStatus != SessionStatus.Disconnected)
            return;

        if (!PlayerData.Remove(e.Session, out var data))
            return;

        foreach (var pvsCollection in _pvsCollections)
        {
            if (!pvsCollection.RemovePlayer(e.Session))
                Log.Error($"Attempted to remove player from pvsCollection, but they were already removed? Session:{e.Session}");
        }

        if (data.Overflow != null)
            _entDataListPool.Return(data.Overflow.Value.SentEnts);
        data.Overflow = null;

        foreach (var visSet in data.SentEntities.Values)
        {
            _entDataListPool.Return(visSet);
        }
    }

    private void OnGridRemoved(GridRemovalEvent ev)
    {
        foreach (var pvsCollection in _pvsCollections)
        {
            pvsCollection.RemoveGrid(ev.EntityUid);
        }
    }

    private void OnGridCreated(GridInitializeEvent ev)
    {
        var gridId = ev.EntityUid;
        foreach (var pvsCollection in _pvsCollections)
        {
            pvsCollection.AddGrid(gridId);
        }

        _entityPvsCollection.AddGlobalOverride(_metaQuery.GetComponent(gridId).NetEntity, true, false);
    }

    private void OnMapDestroyed(MapChangedEvent e)
    {
        foreach (var pvsCollection in _pvsCollections)
        {
            pvsCollection.RemoveMap(e.Map);
        }
    }

    private void OnMapCreated(MapChangedEvent e)
    {
        foreach (var pvsCollection in _pvsCollections)
        {
            pvsCollection.AddMap(e.Map);
        }

        if(e.Map == MapId.Nullspace) return;
        var uid = _mapManager.GetMapEntityId(e.Map);
        _entityPvsCollection.AddGlobalOverride(_metaQuery.GetComponent(uid).NetEntity, true, false);
    }

    #endregion

    public List<(int, IChunkIndexLocation)> GetChunks(
        ICommonSession[] sessions,
        ref HashSet<int>[] playerChunks,
        ref EntityUid[][] viewerEntities)
    {
        // Pass these in to avoid allocating new ones every tick, 99% of the time sessions length is going to be the same size.
        // These values will get overridden here and the old values have already been returned to the pool by this point.
        Array.Resize(ref playerChunks, sessions.Length);
        Array.Resize(ref viewerEntities, sessions.Length);

        _chunkList.Clear();
        // Keep track of the index of each chunk we use for a faster index lookup.
        // Pool it because this will allocate a lot across ticks as we scale in players.
        foreach (var chunks in _mapIndices.Values)
        {
            _mapChunkPool.Return(chunks);
        }

        foreach (var chunks in _gridIndices.Values)
        {
            _gridChunkPool.Return(chunks);
        }

        _mapIndices.Clear();
        _gridIndices.Clear();

        for (int i = 0; i < sessions.Length; i++)
        {
            var session = sessions[i];
            playerChunks[i] = _playerChunkPool.Get();

            ref var viewers = ref viewerEntities[i];
            GetSessionViewers(session, ref viewers);

            for (var j = 0; j < viewers.Length; j++)
            {
                var eyeEuid = viewers[j];
                var (viewPos, range, mapId) = CalcViewBounds(in eyeEuid);

                if (mapId == MapId.Nullspace) continue;

                int visMask = EyeComponent.DefaultVisibilityMask;
                if (_eyeQuery.TryGetComponent(eyeEuid, out var eyeComp))
                    visMask = eyeComp.VisibilityMask;

                // Get the nyoom dictionary for index lookups.
                if (!_mapIndices.TryGetValue(visMask, out var mapDict))
                {
                    mapDict = _mapChunkPool.Get();
                    _mapIndices[visMask] = mapDict;
                }

                var mapChunkEnumerator = new ChunkIndicesEnumerator(viewPos, range, ChunkSize);

                while (mapChunkEnumerator.MoveNext(out var chunkIndices))
                {
                    var chunkLocation = new MapChunkLocation(mapId, chunkIndices.Value);
                    var entry = (visMask, chunkLocation);

                    if (mapDict.TryGetValue(chunkLocation, out var indexOf))
                    {
                        playerChunks[i].Add(indexOf);
                    }
                    else
                    {
                        playerChunks[i].Add(_chunkList.Count);
                        mapDict.Add(chunkLocation, _chunkList.Count);
                        _chunkList.Add(entry);
                    }
                }

                // Get the nyoom dictionary for index lookups.
                if (!_gridIndices.TryGetValue(visMask, out var gridDict))
                {
                    gridDict = _gridChunkPool.Get();
                    _gridIndices[visMask] = gridDict;
                }

                var state = (i, _xformQuery, viewPos, range, visMask, gridDict, playerChunks, _chunkList, _transform);
                var rangeVec = new Vector2(range, range);

                _mapManager.FindGridsIntersecting(mapId, new Box2(viewPos - rangeVec, viewPos + rangeVec),
                    ref state, static (
                        EntityUid gridUid,
                        MapGridComponent _,
                        ref (int i,
                            EntityQuery<TransformComponent> transformQuery,
                            Vector2 viewPos,
                            float range,
                            int visMask,
                            Dictionary<GridChunkLocation, int> gridDict,
                            HashSet<int>[] playerChunks,
                            List<(int, IChunkIndexLocation)> _chunkList,
                            SharedTransformSystem xformSystem) tuple) =>
                    {
                        {
                            var localPos = tuple.xformSystem.GetInvWorldMatrix(gridUid, tuple.transformQuery).Transform(tuple.viewPos);

                            var gridChunkEnumerator =
                                new ChunkIndicesEnumerator(localPos, tuple.range, ChunkSize);

                            while (gridChunkEnumerator.MoveNext(out var gridChunkIndices))
                            {
                                var chunkLocation = new GridChunkLocation(gridUid, gridChunkIndices.Value);
                                var entry = (tuple.visMask, chunkLocation);

                                if (tuple.gridDict.TryGetValue(chunkLocation, out var indexOf))
                                {
                                    tuple.playerChunks[tuple.i].Add(indexOf);
                                }
                                else
                                {
                                    tuple.playerChunks[tuple.i].Add(tuple._chunkList.Count);
                                    tuple.gridDict.Add(chunkLocation, tuple._chunkList.Count);
                                    tuple._chunkList.Add(entry);
                                }
                            }

                            return true;
                        }
                    });
            }
        }

        return _chunkList;
    }

    public void RegisterNewPreviousChunkTrees(
        List<(int, IChunkIndexLocation)> chunks,
        RobustTree<NetEntity>?[] trees,
        bool[] reuse)
    {
        // For any chunks able to re-used we'll chuck them in a dictionary for faster lookup.
        for (var i = 0; i < chunks.Count; i++)
        {
            var canReuse = reuse[i];
            if (!canReuse) continue;

            _reusedTrees.Add(chunks[i]);
        }

        foreach (var (index, chunk) in _previousTrees)
        {
            // ReSharper disable once InconsistentlySynchronizedField
            if (_reusedTrees.Contains(index))
                continue;

            if (chunk != null)
                _treePool.Return(chunk);

            if (!chunks.Contains(index))
                _previousTrees.Remove(index);
        }

        _previousTrees.EnsureCapacity(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            //this is a redundant assign if the tree has been reused. the assumption is that this is cheaper than a .Contains call
            _previousTrees[chunks[i]] = trees[i];
        }
        // ReSharper disable once InconsistentlySynchronizedField
        _reusedTrees.Clear();
    }

    public bool TryCalculateChunk(
        IChunkIndexLocation chunkLocation,
        int visMask,
        out RobustTree<NetEntity>? tree)
    {
        if (!_entityPvsCollection.IsDirty(chunkLocation)
            && _previousTrees.TryGetValue((visMask, chunkLocation), out tree))
        {
            return true;
        }

        var chunk = chunkLocation switch
        {
            GridChunkLocation gridChunkLocation => _entityPvsCollection.TryGetChunk(gridChunkLocation.GridId,
                gridChunkLocation.ChunkIndices, out var gridChunk)
                ? gridChunk
                : null,
            MapChunkLocation mapChunkLocation => _entityPvsCollection.TryGetChunk(mapChunkLocation.MapId,
                mapChunkLocation.ChunkIndices, out var mapChunk)
                ? mapChunk
                : null,
            _ => null
        };
        if (chunk == null)
        {
            tree = null;
            return false;
        }

        tree = _treePool.Get();
        var set = _netUidSetPool.Get();
        DebugTools.AssertNotNull(tree.RootNodes.Count == 0);
        DebugTools.AssertNotNull(set.Count == 0);

        foreach (var netEntity in chunk)
        {
            var (uid, meta) = GetEntityData(netEntity);
            AddToChunkSetRecursively(in uid, in netEntity, meta, visMask, tree, set);
#if DEBUG
            var xform = _xformQuery.GetComponent(uid);
            if (chunkLocation is MapChunkLocation)
                DebugTools.Assert(xform.GridUid == null || xform.GridUid == uid);
            else if (chunkLocation is GridChunkLocation)
                DebugTools.Assert(xform.ParentUid != xform.MapUid || xform.GridUid == xform.MapUid);
#endif
        }

        DebugTools.Assert(set.Count > 0 || tree.RootNodes.Count == 0);
        _netUidSetPool.Return(set);

        if (tree.RootNodes.Count == 0)
        {
            // This can happen if the only entity in a chunk is invisible
            // (e.g., when a ghost moves from from a grid into empty space).
            _treePool.Return(tree);
            tree = null;
            return true;
        }

        return false;
    }

    public void ReturnToPool(HashSet<int>[] playerChunks)
    {
        for (var i = 0; i < playerChunks.Length; i++)
        {
            _playerChunkPool.Return(playerChunks[i]);
        }
    }

    private void AddToChunkSetRecursively(in EntityUid uid, in NetEntity netEntity, MetaDataComponent mComp,
        int visMask, RobustTree<NetEntity> tree, HashSet<NetEntity> set)
    {
        // If the eye is missing ANY layer that this entity is on, or any layer that any of its parents belongs to, then
        // it is considered invisible.
        if ((visMask & mComp.VisibilityMask) != mComp.VisibilityMask)
            return;

        if (!set.Add(netEntity))
            return; // already sending

        var xform = _xformQuery.GetComponent(uid);

        // is this a map or grid?
        var isRoot = !xform.ParentUid.IsValid() || uid == xform.GridUid;
        if (isRoot)
        {
            DebugTools.Assert(_mapManager.IsGrid(uid) || _mapManager.IsMap(uid));
            tree.Set(netEntity);
            return;
        }

        DebugTools.Assert(!_mapManager.IsGrid(uid) && !_mapManager.IsMap(uid));

        var parent = xform.ParentUid;
        var parentMeta = _metaQuery.GetComponent(parent);
        var parentNetEntity = parentMeta.NetEntity;

        // Child should have all o the same flags as the parent.
        DebugTools.Assert((parentMeta.VisibilityMask & mComp.VisibilityMask) == parentMeta.VisibilityMask);

        // Add our parent.
        AddToChunkSetRecursively(in parent, in parentNetEntity, parentMeta, visMask, tree, set);
        tree.Set(netEntity, parentNetEntity);
    }

    internal (List<EntityState>? updates, List<NetEntity>? deletions, List<NetEntity>? leftPvs, GameTick fromTick)
        CalculateEntityStates(ICommonSession session,
            GameTick fromTick,
            GameTick toTick,
            RobustTree<NetEntity>?[] chunks,
            HashSet<int> visibleChunks,
            EntityUid[] viewers)
    {
        DebugTools.Assert(session.Status == SessionStatus.InGame);
        var newEntityBudget = _netConfigManager.GetClientCVar(session.Channel, CVars.NetPVSEntityBudget);
        var enteredEntityBudget = _netConfigManager.GetClientCVar(session.Channel, CVars.NetPVSEntityEnterBudget);
        var newEntityCount = 0;
        var enteredEntityCount = 0;
        var sessionData = PlayerData[session];
        sessionData.SentEntities.TryGetValue(toTick - 1, out var lastSent);
        var toSend = _entDataListPool.Get();
        var entityData = sessionData.EntityData;

        if (toSend.Count != 0)
            throw new Exception("Encountered non-empty object inside of _netUidSetPool. Was the same object returned to the pool more than once?");

        var deletions = _entityPvsCollection.GetDeletedIndices(fromTick);
        var dirtyEntityCount = 0;

        var stack = _stackPool.Get();

        // TODO reorder chunks to prioritize those that are closest to the viewer? Helps make pop-in less visible.
        foreach (var i in visibleChunks)
        {
            var tree = chunks[i];
            if(tree == null)
                continue;
#if DEBUG
            // Each root nodes should simply be a map or a grid entity.
            DebugTools.Assert(tree.RootNodes.Count == 1,
                $"Root node count is {tree.RootNodes.Count} instead of 1. Session: {session}");
            var nent = tree.RootNodes.FirstOrDefault();
            var ent = GetEntity(nent);
            DebugTools.Assert(Exists(ent), $"Root node does not exist. Node {ent}. Session: {session}");
            DebugTools.Assert(HasComp<MapComponent>(ent) || HasComp<MapGridComponent>(ent));
#endif

            foreach (var rootNode in tree.RootNodes)
            {
                RecursivelyAddTreeNode(in rootNode, tree, toSend, entityData, stack, fromTick, toTick,
                        ref newEntityCount, ref enteredEntityCount, ref dirtyEntityCount, newEntityBudget, enteredEntityBudget);
            }
        }
        _stackPool.Return(stack);

        var globalEnumerator = _entityPvsCollection.GlobalOverridesEnumerator;
        while (globalEnumerator.MoveNext())
        {
            var netEntity = globalEnumerator.Current;
            var uid = GetEntity(netEntity);
            RecursivelyAddOverride(in uid, toSend, entityData, fromTick, toTick,
                ref newEntityCount, ref enteredEntityCount, ref dirtyEntityCount, newEntityBudget, enteredEntityBudget);
        }
        globalEnumerator.Dispose();

        var globalRecursiveEnumerator = _entityPvsCollection.GlobalRecursiveOverridesEnumerator;
        while (globalRecursiveEnumerator.MoveNext())
        {
            var netEntity = globalRecursiveEnumerator.Current;
            var uid = GetEntity(netEntity);
            RecursivelyAddOverride(in uid, toSend, entityData, fromTick, toTick,
                ref newEntityCount, ref enteredEntityCount, ref dirtyEntityCount, newEntityBudget, enteredEntityBudget, true);
        }
        globalRecursiveEnumerator.Dispose();

        var sessionOverrides = _entityPvsCollection.GetSessionOverrides(session);
        while (sessionOverrides.MoveNext())
        {
            var netEntity = sessionOverrides.Current;
            var uid = GetEntity(netEntity);
            RecursivelyAddOverride(in uid, toSend, entityData, fromTick, toTick,
                ref newEntityCount, ref enteredEntityCount, ref dirtyEntityCount, newEntityBudget, enteredEntityBudget, true);
        }
        sessionOverrides.Dispose();

        foreach (var viewerEntity in viewers)
        {
            RecursivelyAddOverride(in viewerEntity, toSend, entityData, fromTick, toTick,
                ref newEntityCount, ref enteredEntityCount, ref dirtyEntityCount, newEntityBudget, enteredEntityBudget);
        }

        var expandEvent = new ExpandPvsEvent(session);

        if (session.AttachedEntity != null)
            RaiseLocalEvent(session.AttachedEntity.Value, ref expandEvent, true);
        else
            RaiseLocalEvent(ref expandEvent);

        if (expandEvent.Entities != null)
        {
            foreach (var entityUid in expandEvent.Entities)
            {
                RecursivelyAddOverride(in entityUid, toSend, entityData, fromTick, toTick,
                    ref newEntityCount, ref enteredEntityCount, ref dirtyEntityCount, newEntityBudget, enteredEntityBudget);
            }
        }

        if (expandEvent.RecursiveEntities != null)
        {
            foreach (var entityUid in expandEvent.RecursiveEntities)
            {
                RecursivelyAddOverride(in entityUid, toSend, entityData, fromTick, toTick,
                    ref newEntityCount, ref enteredEntityCount, ref dirtyEntityCount, newEntityBudget, enteredEntityBudget, true);
            }
        }

        // TODO PVS reduce allocs
        var entityStates = new List<EntityState>(dirtyEntityCount);

#if DEBUG
        // TODO PVS consider removing expensive asserts
        var toSendSet = new HashSet<EntityUid>(toSend.Count);
        foreach (var data in toSend)
        {
            toSendSet.Add(data.Entity.Owner);
        }
        DebugTools.AssertEqual(toSend.Count, toSendSet.Count);

        foreach (var data in CollectionsMarshal.AsSpan(toSend))
        {
            DebugTools.Assert(data.Visibility > PvsEntityVisibility.Unsent);
            DebugTools.AssertEqual(data.LastSent, _gameTiming.CurTick);
            DebugTools.Assert(ReferenceEquals(data, entityData[data.NetEntity]));

            // if an entity is visible, its parents should always be visible.
            if (_xformQuery.GetComponent(data.Entity).ParentUid is not {Valid: true} pUid)
                continue;

            DebugTools.Assert(toSendSet.Contains(pUid),
                $"Attempted to send an entity without sending it's parents. Entity: {ToPrettyString(pUid)}.");
        }

        foreach (var data in CollectionsMarshal.AsSpan(lastSent))
        {
            DebugTools.Assert(data.Visibility > PvsEntityVisibility.Unsent);
            DebugTools.Assert(!entityData.TryGetValue(data.NetEntity, out var old) || ReferenceEquals(data, old));
            DebugTools.Assert(data.LastSent != GameTick.Zero);
            DebugTools.AssertEqual(toSendSet.Contains(data.Entity), data.LastSent == _gameTiming.CurTick);
            DebugTools.Assert(data.LastSent == _gameTiming.CurTick
                              || data.LastSent == _gameTiming.CurTick - 1);
        }
#endif

        // Get entity/component states and update EntityData.LastSent
        GetStateList(entityStates, toSend, sessionData, fromTick);

        // Tell the client to detach entities that have left their view
        // This has to be called after EntityData.LastSent is updated.
        var leftView = ProcessLeavePvs(lastSent, toTick, toSend.Count);

        if (sessionData.SentEntities.Add(toTick, toSend, out var oldEntry))
        {
            if (oldEntry.Value.Key > fromTick && sessionData.Overflow == null)
            {
                // The clients last ack is too late, the overflow dictionary size has been exceeded, and we will no
                // longer have information about the sent entities. This means we would no longer be able to add
                // entities to _ackedEnts.
                //
                // If the client has enough latency, this result in a situation where we must constantly assume that every entity
                // that needs to get sent to the client is being received by them for the first time.
                //
                // In order to avoid this, while also keeping the overflow dictionary limited in size, we keep a single
                // overflow state, so we can at least periodically update the acked entities.

                // This is pretty shit and there is probably a better way of doing this.
                sessionData.Overflow = oldEntry.Value;

#if DEBUG
                // This happens relatively frequently for the current TickBuffer value, and doesn't really provide any
                // useful info when not debugging/testing locally. Hence only enable on DEBUG.
                Log.Debug($"Client {session} exceeded tick buffer.");
#endif
            }
            else
                _entDataListPool.Return(oldEntry.Value.Value);
        }

        DebugTools.Assert(!sessionData.RequestedFull || fromTick == GameTick.Zero);

        if (entityStates.Count == 0)
            entityStates = default;

        return (entityStates, deletions, leftView, fromTick);
    }

    /// <summary>
    ///     Figure out what entities are no longer visible to the client. These entities are sent reliably to the client
    ///     in a separate net message.
    /// </summary>
    private List<NetEntity>? ProcessLeavePvs(List<EntityData>? lastSent, GameTick toTick, int toSendCount)
    {
        // TODO parallelize this with system processing.
        // Note that this requires deferring entity-deletion processing to be applied at the beginning of PVS
        // processing, instead of happening during system ticks. But it also would make it easy to parallelize
        // updating it.

        if (lastSent == null)
            return null;

        var minSize = Math.Max(0, lastSent.Count - toSendCount);

        // TODO PVS reduce allocs
        var leftView = new List<NetEntity>(minSize);

        foreach (var data in CollectionsMarshal.AsSpan(lastSent))
        {
            if (data.LastSent == toTick)
                continue;

            leftView.Add(data.NetEntity);
            data.LastLeftView = toTick;
        }

        return leftView.Count > 0 ? leftView : null;
    }

    private void GetSessionViewers(ICommonSession session, [NotNull] ref EntityUid[]? viewers)
    {
        if (session.Status != SessionStatus.InGame)
        {
            viewers = Array.Empty<EntityUid>();
            return;
        }

        // Fast path
        if (session.ViewSubscriptions.Count == 0)
        {
            if (session.AttachedEntity == null)
            {
                viewers = Array.Empty<EntityUid>();
                return;
            }

            Array.Resize(ref viewers, 1);
            viewers[0] = session.AttachedEntity.Value;
            return;
        }

        int i = 0;
        if (session.AttachedEntity is { } local)
        {
            DebugTools.Assert(!session.ViewSubscriptions.Contains(local));
            Array.Resize(ref viewers, session.ViewSubscriptions.Count + 1);
            viewers[i++] = local;
        }
        else
        {
            Array.Resize(ref viewers, session.ViewSubscriptions.Count);
        }

        foreach (var ent in session.ViewSubscriptions)
        {
            viewers[i++] = ent;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntityData GetOrNewEntityData(Dictionary<NetEntity, EntityData> entityData, NetEntity entity)
    {
        ref var data = ref CollectionsMarshal.GetValueRefOrAddDefault(entityData, entity, out var exists);
        if (!exists)
        {
            if (TryGetEntityData(entity, out var uid, out var meta))
            {
                data = new((uid.Value, meta));
            }
            else
            {
                Log.Error($"Attempted to send deleted entity. NetUid: {entity}");
            }
        }

        DebugTools.AssertEqual(data!.NetEntity, entity);
        DebugTools.AssertEqual(data.LastSent == GameTick.Zero, data.Visibility <= PvsEntityVisibility.Unsent);
        return data!;
    }

    // Read Safe
    private (Vector2 worldPos, float range, MapId mapId) CalcViewBounds(in EntityUid euid)
    {
        var xform = _xformQuery.GetComponent(euid);
        return (_transform.GetWorldPosition(xform, _xformQuery), _viewSize / 2f, xform.MapID);
    }

    public sealed class TreePolicy<T> : PooledObjectPolicy<RobustTree<T>> where T : notnull
    {
        public override RobustTree<T> Create()
        {
            var pool = new DefaultObjectPool<HashSet<T>>(new SetPolicy<T>(), MaxVisPoolSize);
            return new RobustTree<T>(pool);
        }

        public override bool Return(RobustTree<T> obj)
        {
            obj.Clear();
            return true;
        }
    }

    private sealed class ChunkPoolPolicy<T> : PooledObjectPolicy<Dictionary<T, int>> where T : notnull
    {
        public override Dictionary<T, int> Create()
        {
            return new Dictionary<T, int>(32);
        }

        public override bool Return(Dictionary<T, int> obj)
        {
            obj.Clear();
            return true;
        }
    }
}

[ByRefEvent]
public struct ExpandPvsEvent
{
    public readonly ICommonSession Session;

    /// <summary>
    /// List of entities that will get added to this session's PVS set.
    /// </summary>
    public List<EntityUid>? Entities;

    /// <summary>
    /// List of entities that will get added to this session's PVS set. Unlike <see cref="Entities"/> this will also
    /// recursively add all children of the given entity.
    /// </summary>
    public List<EntityUid>? RecursiveEntities;

    public ExpandPvsEvent(ICommonSession session)
    {
        Session = session;
    }
}
