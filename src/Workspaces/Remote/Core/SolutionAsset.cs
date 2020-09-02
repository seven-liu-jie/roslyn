﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using Microsoft.CodeAnalysis.Serialization;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Remote
{
    /// <summary>
    /// Represents a part of solution snapshot along with its checksum.
    /// </summary>
    internal sealed class SolutionAsset
    {
        public static readonly SolutionAsset Null = new SolutionAsset(value: null, Checksum.Null, WellKnownSynchronizationKind.Null);

        /// <summary>
        /// Indicates what kind of object it.
        /// 
        /// Used in tranportation framework and deserialization service
        /// to hand shake how to send over data and deserialize serialized data.
        /// </summary>
        public readonly WellKnownSynchronizationKind Kind;

        /// <summary>
        /// Checksum of <see cref="Value"/>.
        /// </summary>
        public readonly Checksum Checksum;

        public readonly object? Value;

        public SolutionAsset(object? value, Checksum checksum, WellKnownSynchronizationKind kind)
        {
            Contract.ThrowIfTrue(kind is WellKnownSynchronizationKind.SourceText
                && value is not SerializableSourceText);

            Checksum = checksum;
            Kind = kind;
            Value = value;
        }

        public SolutionAsset(Checksum checksum, object value)
            : this(value, checksum, value.GetWellKnownSynchronizationKind())
        {
        }
    }
}
