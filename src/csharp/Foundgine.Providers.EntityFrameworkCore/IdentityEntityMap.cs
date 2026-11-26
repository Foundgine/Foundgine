using System.Diagnostics.CodeAnalysis;
using Foundgine.Core.Semantic.Configuration;

namespace Foundgine.Providers.EntityFrameworkCore;

/// <summary>
/// Entity map for a type that is both the semantic model and the persistence type: every public value-typed or
/// string property maps to itself. This removes the model -> storage mapping layer for the common case where the
/// domain class is the EF entity.
/// <code>model.Configure&lt;IdentityEntityMap&lt;Customer&gt;&gt;();</code>
/// Table, key and column names still come from a <see cref="FoundginePersistenceConfiguration{TEntity}"/> on the
/// same type; relationships come from a <see cref="FoundgineModelConfiguration{TModel}"/>.
/// This is the same map as <see cref="FoundgineIdentityEntityMap{T}"/> in Foundgine.Core, kept under its original
/// name for existing code; both use the convention in <c>FoundgineEntityMapBuilder.Convention</c>.
/// </summary>
public sealed class IdentityEntityMap<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>
    : FoundgineEntityMap<T, T> where T : class
{
    public override void Configure(FoundgineEntityMapBuilder<T, T> map) => map.Convention();
}
