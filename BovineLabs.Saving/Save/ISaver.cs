﻿// <copyright file="ISaver.cs" company="BovineLabs">
// Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Saving
{
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;

    /// <summary> The base interface for implementing serialization and deserialization routines. </summary>
    public interface ISaver
    {
        /// <summary> Gets the key for this saver, this should nearly always be a <see cref="TypeManager.TypeInfo.StableTypeHash"/>. </summary>
        ulong Key { get; }

        /// <summary> Implementation of the serializer routine for this saver. </summary>
        /// <param name="chunks"> The list of all chunks. This is an async list and can not be used outside a job. </param>
        /// <param name="dependency"> The dependency of the chunks. </param>
        /// <returns> A new serializer that will contain the serialized data once the accompanying dependency is completed. </returns>
        (Serializer Serializer, JobHandle Dependency) Serialize(NativeList<ArchetypeChunk> chunks, JobHandle dependency);

        /// <summary> Implementation of the deserializer routine for this saver. </summary>
        /// <param name="deserializer"> The serialized data that this saver previous produced. </param>
        /// <param name="entityMap"> The remap of entities. </param>
        /// <param name="dependency"> The dependency of the chunks. </param>
        /// <returns> The job dependency for the deserialization. </returns>
        JobHandle Deserialize(Deserializer deserializer, EntityMap entityMap, JobHandle dependency);
    }
}
