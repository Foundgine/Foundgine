#pragma warning disable IL2026, IL2070, IL2075, IL2090, IL3050

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Foundgine.Core.Semantic.Configuration;

/// <summary>
/// Maps a semantic model onto its persistence type, member by member. The mapping lives
/// outside both types so neither needs to reference the other.
/// </summary>
public abstract class FoundgineEntityMap<TModel, TEntity> : IFoundgineConfiguration
{
    public abstract void Configure(FoundgineEntityMapBuilder<TModel, TEntity> map);

    void IFoundgineConfiguration.Apply(FoundgineConfigurationContext context) =>
        Configure(new FoundgineEntityMapBuilder<TModel, TEntity>(context.AddMap(typeof(TModel), typeof(TEntity))));
}

/// <summary>
/// Entity map for a type that is both the semantic model and the persistence type: every public value-typed or
/// string property maps to the member of the same name on the same type. This removes the model-to-storage
/// mapping layer when the domain class is also the storage class.
/// <code>model.Configure&lt;FoundgineIdentityEntityMap&lt;Customer&gt;&gt;();</code>
/// It is shorthand for <c>map.Convention()</c>; see <c>FoundgineEntityMapBuilder.Convention</c>.
/// </summary>
public sealed class FoundgineIdentityEntityMap<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>
    : FoundgineEntityMap<T, T>
{
    public override void Configure(FoundgineEntityMapBuilder<T, T> map) => map.Convention();
}

public sealed class FoundgineEntityMapBuilder<TModel, TEntity>
{
    private readonly EntityMapDefinition _definition;

    internal FoundgineEntityMapBuilder(EntityMapDefinition definition) => _definition = definition;

    /// <summary>Selects a model member to map. Complete the mapping with <c>.To(x =&gt; x.Member)</c> on the storage type.</summary>
    public FoundgineEntityMapPropertyBuilder<TModel, TEntity, TProperty> Property<TProperty>(
        Expression<Func<TModel, TProperty>> modelProperty) =>
        new(_definition, ConfigurationExpression.Member(modelProperty));

    /// <summary>
    /// Maps every model member that is not mapped yet to the storage member with the same name and the same CLR type.
    /// Only value-typed members (numbers, enums, dates, <c>Guid</c>, strongly typed id structs, nullable values) and
    /// <c>string</c> are considered; navigation properties and collections are never mapped.
    /// <para>
    /// Call it after any explicit <c>Property(...).To(...)</c> lines (renames, differing names) so the explicit lines win.
    /// The convention is strict: a model member with no storage member of the same name, or with a different type, makes
    /// this method throw, listing every problem. List deliberate gaps in <paramref name="ignore"/>.
    /// </para>
    /// <para>
    /// This reads the public properties of <typeparamref name="TModel"/> and <typeparamref name="TEntity"/> by reflection
    /// when the configuration is built, unlike the explicit lambdas, which only read member names. Keep the explicit form
    /// where the model must be trimmed or built without reflection.
    /// </para>
    /// </summary>
    public FoundgineEntityMapBuilder<TModel, TEntity> Convention(params Expression<Func<TModel, object?>>[] ignore)
    {
        ArgumentNullException.ThrowIfNull(ignore);

        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in ignore)
            ignored.Add(ConfigurationExpression.Member(selector).Name);

        var entityProperties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
                entityProperties.TryAdd(property.Name, property);

        var problems = new List<string>();
        foreach (var property in typeof(TModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is null ||
                !IsConventionType(property.PropertyType))
                continue;

            var name = property.Name;
            if (ignored.Contains(name) || _definition.Properties.Exists(p => p.Model.Name == name))
                continue;

            if (!entityProperties.TryGetValue(name, out var entityProperty))
                problems.Add($"{typeof(TModel).Name}.{name} has no member named '{name}' on {typeof(TEntity).Name}");
            else if (entityProperty.PropertyType != property.PropertyType)
                problems.Add(
                    $"{typeof(TModel).Name}.{name} ({property.PropertyType.Name}) differs in type from " +
                    $"{typeof(TEntity).Name}.{name} ({entityProperty.PropertyType.Name})");
            else
                _definition.Properties.Add(
                    (new MemberRef(name, property.PropertyType), new MemberRef(name, entityProperty.PropertyType)));
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"Convention mapping {typeof(TModel).Name} -> {typeof(TEntity).Name} failed: " +
                string.Join("; ", problems) +
                ". Map these members explicitly with Property(...).To(...) before Convention(), or pass them to Convention(ignore).");

        return this;
    }

    private static bool IsConventionType(Type type) => type.IsValueType || type == typeof(string);
}

public sealed class FoundgineEntityMapPropertyBuilder<TModel, TEntity, TProperty>
{
    private readonly EntityMapDefinition _definition;
    private readonly MemberRef _model;

    internal FoundgineEntityMapPropertyBuilder(EntityMapDefinition definition, MemberRef model)
    {
        _definition = definition;
        _model = model;
    }

    /// <summary>The storage member that holds this value. Both members must have the same CLR type.</summary>
    public FoundgineEntityMapBuilder<TModel, TEntity> To(Expression<Func<TEntity, TProperty>> entityProperty)
    {
        var entity = ConfigurationExpression.Member(entityProperty);
        if (_definition.Properties.Exists(p => p.Model.Name == _model.Name))
            throw new InvalidOperationException(
                $"{typeof(TModel).Name}.{_model.Name} is mapped more than once.");

        _definition.Properties.Add((_model, entity));
        return new FoundgineEntityMapBuilder<TModel, TEntity>(_definition);
    }
}
