﻿// <copyright file="ComponentDataSave.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using System;
    using BovineLabs.Core.Assertions;
    using Unity.Assertions;
    using Unity.Burst;
    using Unity.Burst.CompilerServices;
    using Unity.Burst.Intrinsics;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Profiling;
    using UnityEngine;

    /// <summary> Saves <see cref="IComponentData"/>. </summary>
    public unsafe struct ComponentDataSave : ISaver, IDisposable
    {
        private ComponentSave componentSave;

        private EntityTypeHandle entityHandle;
        private DynamicComponentTypeHandle dynamicHandle;
        private DynamicComponentTypeHandle dynamicHandleRW;

        public ComponentDataSave(SaveBuilder builder, ulong stableTypeHash)
        {
            this.Key = stableTypeHash;
            this.componentSave = new ComponentSave(builder, stableTypeHash);

            Assert.IsTrue(this.componentSave.TypeInfo.Category == TypeManager.TypeCategory.ComponentData);
            Assert.IsTrue(
                this.componentSave.TypeInfo.ElementSize > 0 || this.componentSave.TypeIndex.IsEnableable,
                "Saving zero size component that isn't IsEnableable");

            this.entityHandle = this.componentSave.System.GetEntityTypeHandle();
            this.dynamicHandle = this.componentSave.System.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(this.componentSave.TypeIndex));
            this.dynamicHandleRW = this.componentSave.System.GetDynamicComponentTypeHandle(ComponentType.ReadWrite(this.componentSave.TypeIndex));
        }

        public ulong Key { get; }

        public void Dispose()
        {
            this.componentSave.Dispose();
        }

        /// <inheritdoc/>
        public (Serializer Serializer, JobHandle Dependency) Serialize(NativeList<ArchetypeChunk> chunks, JobHandle dependency)
        {
            this.entityHandle.Update(ref this.componentSave.System);
            this.dynamicHandle.Update(ref this.componentSave.System);

            var serializer = new Serializer(0, this.componentSave.System.WorldUpdateAllocator);

            dependency = new SerializeJob
                {
                    Chunks = chunks.AsDeferredJobArray(),
                    Entity = this.entityHandle,
                    ComponentType = this.dynamicHandle,
                    Key = this.componentSave.TypeInfo.StableTypeHash,
                    ElementSize = this.componentSave.TypeInfo.ElementSize,
                    IsEnableable = this.componentSave.TypeIndex.IsEnableable,
                    Serializer = serializer,
                }
                .Schedule(dependency);

            return (serializer, dependency);
        }

        /// <inheritdoc/>
        public JobHandle Deserialize(Deserializer deserializer, EntityMap entityMap, JobHandle dependency)
        {
            this.entityHandle.Update(ref this.componentSave.System);
            this.dynamicHandleRW.Update(ref this.componentSave.System);

            var deserializedData = new NativeParallelHashMap<Entity, DeserializedEntityData>(128, this.componentSave.System.WorldUpdateAllocator);
            var setEnableable = new NativeReference<bool>(this.componentSave.TypeIndex.IsEnableable, this.componentSave.System.WorldUpdateAllocator);

            dependency = new DeserializeJob
                {
                    Deserializer = deserializer,
                    SetEnableable = setEnableable,
                    Remap = entityMap,
                    DeserializedData = deserializedData,
                    ElementSize = this.componentSave.TypeInfo.ElementSize,
                }
                .Schedule(dependency);

            dependency = new ApplyJob
                {
                    DeserializedData = deserializedData,
                    SetEnableable = setEnableable,
                    EntityType = this.entityHandle,
                    ComponentType = this.dynamicHandleRW,
                    SaveChunks = this.componentSave.SaveChunks,
                    ElementSize = this.componentSave.TypeInfo.ElementSize,
                    EntityOffsets = this.componentSave.EntityOffsets,
                    Remap = entityMap,
                }
                .ScheduleParallel(this.componentSave.QueryWrite, dependency);

            return dependency;
        }

        [BurstCompile]
        private struct SerializeJob : IJob
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;

            [ReadOnly]
            public EntityTypeHandle Entity;

            [ReadOnly]
            public DynamicComponentTypeHandle ComponentType;

            public ulong Key;
            public int ElementSize;
            public bool IsEnableable;

            public Serializer Serializer;

            public void Execute()
            {
                using (new ProfilerMarker("EnsureCapacity").Auto())
                {
                    if (!this.EnsureCapacity())
                    {
                        return;
                    }
                }

                using (new ProfilerMarker("Serialize").Auto())
                {
                    this.Serialize();
                }
            }

            private bool EnsureCapacity()
            {
                var capacity = 0;
                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(ref this.ComponentType))
                    {
                        continue;
                    }

                    capacity += UnsafeUtility.SizeOf<ComponentSave.HeaderChunk>();
                    if (this.IsEnableable)
                    {
                        capacity += chunk.Count * UnsafeUtility.SizeOf<v128>();
                    }

                    capacity += chunk.Count * (UnsafeUtility.SizeOf<int>() + this.ElementSize); // entity + component
                }

                if (capacity == 0)
                {
                    return false;
                }

                capacity += UnsafeUtility.SizeOf<HeaderSaver>() + UnsafeUtility.SizeOf<ComponentSave.HeaderComponent>();

                this.Serializer.EnsureExtraCapacity(capacity);
                return true;
            }

            private void Serialize()
            {
                var saveIdx = this.Serializer.AllocateNoResize<HeaderSaver>();
                var compIdx = this.Serializer.AllocateNoResize<ComponentSave.HeaderComponent>();

                var entityCount = 0;

                foreach (var chunk in this.Chunks)
                {
                    if (!chunk.Has(ref this.ComponentType))
                    {
                        continue;
                    }

                    var entities = chunk.GetNativeArray(this.Entity).Slice().SliceWithStride<int>();

                    entityCount += entities.Length;

                    var chunkHeader = new ComponentSave.HeaderChunk { Length = entities.Length };
                    this.Serializer.AddNoResize(chunkHeader);
                    this.Serializer.AddBufferNoResize(entities);

                    if (this.IsEnableable)
                    {
                        this.Serializer.AddNoResize(chunk.GetEnableableBits(ref this.ComponentType));
                    }

                    if (this.ElementSize > 0)
                    {
                        var components = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref this.ComponentType, this.ElementSize);
                        Check.Assume(components.Length > 0);
                        this.Serializer.AddBufferNoResize(components);
                    }
                }

                var headerSave = this.Serializer.GetAllocation<HeaderSaver>(saveIdx);
                *headerSave = new HeaderSaver
                {
                    Key = this.Key,
                    LengthInBytes = this.Serializer.Data.Length,
                };

                var compSave = this.Serializer.GetAllocation<ComponentSave.HeaderComponent>(compIdx);
                *compSave = new ComponentSave.HeaderComponent
                {
                    Count = entityCount,
                    ElementSize = this.ElementSize,
                    IsEnableable = this.IsEnableable,
                };
            }
        }

        [BurstCompile]
        private struct DeserializeJob : IJob
        {
            [ReadOnly]
            public Deserializer Deserializer;

            [ReadOnly]
            public EntityMap Remap;

            public NativeParallelHashMap<Entity, DeserializedEntityData> DeserializedData;

            public NativeReference<bool> SetEnableable;

            public int ElementSize;

            public void Execute()
            {
                this.Deserializer.Offset<HeaderSaver>();
                var header = this.Deserializer.Read<ComponentSave.HeaderComponent>();

                Check.Assume(header.ElementSize == this.ElementSize, "Critical error, element size does not match expected.");

                // If it's enableable and was saved as enableable
                this.SetEnableable.Value &= header.IsEnableable;

                var index = 0;

                while (index < header.Count)
                {
                    var headerChunk = this.Deserializer.Read<ComponentSave.HeaderChunk>();
                    var entities = this.Deserializer.ReadBuffer<int>(headerChunk.Length);

                    v128 enabledBits = default;
                    if (header.IsEnableable)
                    {
                        enabledBits = this.Deserializer.Read<v128>();
                    }

                    var components = this.ElementSize > 0 ? this.Deserializer.ReadBuffer<byte>(headerChunk.Length * header.ElementSize) : null;

                    for (var i = 0; i < headerChunk.Length; i++)
                    {
                        if (this.Remap.TryGetEntity(entities[i], out var entity))
                        {
                            this.DeserializedData.Add(entity, new DeserializedEntityData
                            {
                                Source = components + (i * header.ElementSize),
                                IsEnabled = ComponentSave.IsSet(enabledBits, i),
                            });
                        }
                        else
                        {
                            Debug.LogError($"Entity {entities[i]} wasn't found");
                        }
                    }

                    index += headerChunk.Length;
                }
            }
        }

        [BurstCompile]
        private struct ApplyJob : IJobChunk
        {
            [ReadOnly]
            public NativeParallelHashMap<Entity, DeserializedEntityData> DeserializedData;

            [ReadOnly]
            public NativeReference<bool> SetEnableable;

            [ReadOnly]
            public EntityTypeHandle EntityType;

            public DynamicComponentTypeHandle ComponentType;

            [ReadOnly]
            public NativeArray<ComponentSave.SaveChunk> SaveChunks;

            public int ElementSize;

            [ReadOnly]
            public NativeArray<int> EntityOffsets;

            [ReadOnly]
            public EntityMap Remap;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var components = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref this.ComponentType, this.ElementSize);
                if (components.Length == 0)
                {
                    return;
                }

                var entities = chunk.GetNativeArray(this.EntityType);

                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];

                    if (!this.DeserializedData.TryGetValue(entity, out var entityData))
                    {
                        continue;
                    }

                    if (this.ElementSize > 0)
                    {
                        var dst = (byte*)components.GetUnsafePtr() + (i * this.ElementSize);
                        var src = entityData.Source;

                        foreach (var element in this.SaveChunks)
                        {
                            UnsafeUtility.MemCpy(dst + element.Index, src + element.Index, element.Length);
                        }

                        foreach (var offset in this.EntityOffsets)
                        {
                            ComponentSave.RemapEntityField(dst, offset, this.Remap);
                        }
                    }

                    if (Hint.Unlikely(this.SetEnableable.Value))
                    {
                        chunk.SetComponentEnabled(ref this.ComponentType, i, entityData.IsEnabled);
                    }
                }
            }
        }

        private struct DeserializedEntityData
        {
            public byte* Source;
            public bool IsEnabled;
        }
    }
}
