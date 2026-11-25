using Foundgine.Core.Abstractions;
using Foundgine.Core.Semantic.Metadata;

namespace Foundgine.Core.Semantic.Configuration;

/// <summary>
/// Joins the four kinds of configuration into the existing metadata records. Identities
/// are derived exactly as the attribute generator derives them (entity and field ids from
/// semantic names, column ids from storage name + column name), so metadata produced here
/// is interchangeable with generated metadata. Every problem found is reported together.
/// </summary>
internal sealed class FoundgineModelCompiler
{
    private readonly FoundgineConfigurationContext _context;
    private readonly List<string> _errors = [];

    private readonly Dictionary<Type, CompiledEntity> _byModel = new();
    private readonly List<EntityMetadata> _entities = [];
    private readonly List<ModelMetadata> _models = [];
    private readonly List<RelationshipMetadata> _relationships = [];
    private readonly List<ConnectionMetadata> _modelConnections = [];
    private readonly List<AuthorizationMetadata> _authorizations = [];
    private readonly List<ConversionMetadata> _conversions = [];

    private FoundgineModelCompiler(FoundgineConfigurationContext context) => _context = context;

    public static void Compile(FoundgineConfigurationContext context, MetadataRegistry registry)
    {
        var compiler = new FoundgineModelCompiler(context);
        compiler.Run();

        if (compiler._errors.Count > 0)
            throw new InvalidOperationException(
                "Foundgine model configuration is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, compiler._errors.Select(e => " - " + e)));

        foreach (var entity in compiler._entities) registry.Register(entity);
        foreach (var model in compiler._models) registry.Register(model);
        foreach (var relationship in compiler._relationships) registry.Register(relationship);
        foreach (var conversion in compiler._conversions) registry.Register(conversion);
        foreach (var connection in compiler._modelConnections) registry.Register(connection);
        foreach (var authorization in compiler._authorizations) registry.Register(authorization);
    }

    private void Run()
    {
        AddImplicitConfiguration();

        foreach (var group in _context.Models.Keys.GroupBy(t => t.Name, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            _errors.Add(
                $"Models {string.Join(", ", group.Select(t => t.FullName))} share the simple name '{group.Key}'; semantic model names must be unique.");
        }

        var maps = ValidMaps();
        var connections = WellFormedConnections();
        var connectionMembers = ConnectionMembers(connections);
        AddModelConnectionMembers(connectionMembers);

        foreach (var map in maps)
            CompileEntity(map, connectionMembers);

        foreach (var model in _context.Models.Values)
        {
            EntityId? entityId = _byModel.TryGetValue(model.ModelType, out var compiled) ? compiled.Id : null;
            _models.Add(new ModelMetadata(
                ModelId.Create(model.ModelType.Name),
                model.ModelType.Name,
                entityId,
                model.MinimumWeight));
        }

        var checkedConnections = connections.Where(ConnectionMatchesMaps).ToList();
        CompileRelationships(checkedConnections);
        CompileModelConnections();
    }

    // ---- maps -------------------------------------------------------------------------

    /// <summary>
    /// An entity map is enough to define an entity: a model with no configuration gets its type name as the
    /// semantic entity name, and a storage type with no persistence configuration gets its type name as the table.
    /// Both can still be configured explicitly.
    /// </summary>
    private void AddImplicitConfiguration()
    {
        foreach (var map in _context.Maps)
        {
            if (!_context.Models.ContainsKey(map.ModelType))
                _context.AddModel(map.ModelType);
            if (!_context.Persistence.ContainsKey(map.EntityType))
                _context.AddPersistence(map.EntityType);
        }
    }

    private List<EntityMapDefinition> ValidMaps()
    {
        var valid = new List<EntityMapDefinition>(_context.Maps);

        foreach (var group in valid
                     .GroupBy(m => EntityNameOf(_context.Models[m.ModelType]), StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            _errors.Add(
                $"Models {string.Join(", ", group.Select(m => m.ModelType.Name))} share the semantic entity name '{group.Key}'.");
        }

        return valid;
    }

    private static string EntityNameOf(ModelDefinition model) => model.EntityName ?? model.ModelType.Name;

    private static string StorageNameOf(PersistenceDefinition persistence)
    {
        var table = persistence.Table ?? persistence.EntityType.Name;
        return persistence.Schema is null ? table : persistence.Schema + "." + table;
    }

    // ---- connections ------------------------------------------------------------------

    private List<ConnectionDefinition> WellFormedConnections()
    {
        var result = new List<ConnectionDefinition>();
        foreach (var connection in _context.Connections)
        {
            var name = ConnectionName(connection);
            if (connection.SourceEntity is null || connection.TargetEntity is null)
            {
                _errors.Add($"Connection {name}: call Using<TSourceEntity, TTargetEntity>().");
                continue;
            }

            if (connection.ForeignKey is null || connection.References is null)
            {
                _errors.Add($"Connection {name}: call HasForeignKey(...).References(...).");
                continue;
            }

            result.Add(connection);
        }

        return result;
    }

    private static string ConnectionName(ConnectionDefinition connection) =>
        connection.SourceModel.Name + " -> " + connection.TargetModel.Name +
        (connection.Navigation is null ? "" : " (" + connection.Navigation + ")");

    /// <summary>Storage members that connections use as keys; they become columns even when no other configuration mentions them.</summary>
    private static Dictionary<Type, List<MemberRef>> ConnectionMembers(IEnumerable<ConnectionDefinition> connections)
    {
        var result = new Dictionary<Type, List<MemberRef>>();

        void Need(Type entityType, MemberRef member)
        {
            if (!result.TryGetValue(entityType, out var list))
                result[entityType] = list = [];
            list.Add(member);
        }

        foreach (var connection in connections)
        {
            var foreignKeyOwner = connection.ForeignKeyOnTarget ? connection.TargetEntity! : connection.SourceEntity!;
            var principalOwner = connection.ForeignKeyOnTarget ? connection.SourceEntity! : connection.TargetEntity!;
            Need(foreignKeyOwner, connection.ForeignKey!.Value);
            Need(principalOwner, connection.References!.Value);
        }

        return result;
    }

    private void AddModelConnectionMembers(Dictionary<Type, List<MemberRef>> result)
    {
        foreach (var connection in _context.ModelConnections)
        {
            if (!result.TryGetValue(connection.EntityType, out var list))
                result[connection.EntityType] = list = [];
            list.AddRange(connection.Fields.Select(f => f.Target));
        }
    }

    private bool ConnectionMatchesMaps(ConnectionDefinition connection)
    {
        var name = ConnectionName(connection);
        if (!_byModel.TryGetValue(connection.SourceModel, out var source) ||
            !_byModel.TryGetValue(connection.TargetModel, out var target))
        {
            _errors.Add($"Connection {name}: both models need a model configuration and an entity map.");
            return false;
        }

        if (source.EntityType != connection.SourceEntity || target.EntityType != connection.TargetEntity)
        {
            _errors.Add(
                $"Connection {name}: Using<{connection.SourceEntity!.Name}, {connection.TargetEntity!.Name}> does not match the entity maps ({source.EntityType.Name}, {target.EntityType.Name}).");
            return false;
        }

        var foreignKey = connection.ForeignKey!.Value;
        var principal = connection.References!.Value;
        if ((Nullable.GetUnderlyingType(foreignKey.Type) ?? foreignKey.Type) !=
            (Nullable.GetUnderlyingType(principal.Type) ?? principal.Type))
        {
            _errors.Add(
                $"Connection {name}: foreign key {foreignKey.Name} ({foreignKey.Type.Name}) and referenced key {principal.Name} ({principal.Type.Name}) must have the same type.");
            return false;
        }

        return true;
    }

    // ---- model connections --------------------------------------------------------------

    private void CompileModelConnections()
    {
        var conversions = new Dictionary<(Type, Type), ConversionMetadata>();
        var seenConnections = new HashSet<ConnectionId>();
        var seenAuthorizations = new HashSet<AuthorizationId>();

        foreach (var definition in _context.ModelConnections)
        {
            var label = $"{definition.ModelType.Name}.{definition.Member}";

            if (!_context.Models.ContainsKey(definition.ModelType) ||
                !_byModel.ContainsKey(definition.ModelType))
            {
                _errors.Add($"Connection {label}: {definition.ModelType.Name} needs a model configuration and an entity map.");
                continue;
            }

            var target = _byModel.Values.FirstOrDefault(e => e.EntityType == definition.EntityType);
            if (target is null)
            {
                _errors.Add(
                    $"Connection {label}: {definition.EntityType.Name} is not the storage type of any entity map.");
                continue;
            }

            var modelName = definition.ModelType.Name;
            var connectionId = definition.ExplicitId is { } explicitId
                ? new ConnectionId(explicitId)
                : ConnectionId.Create(modelName, definition.EffectiveName);

            if (!seenConnections.Add(connectionId))
            {
                _errors.Add($"Connection {label}: connection name '{definition.EffectiveName}' (id {connectionId.Value}) is used more than once.");
                continue;
            }

            var fields = new List<ConnectionFieldMetadata>();
            foreach (var field in definition.Fields)
            {
                var sourceType = field.Source.Type;
                var targetType = field.Target.Type;

                if (field.Conversion is null && sourceType != targetType)
                {
                    _errors.Add(
                        $"Connection {label}: {field.Source.Name} ({sourceType.Name}) and {field.Target.Name} ({targetType.Name}) differ in type; use Maps(source, target, convert).");
                    continue;
                }

                if (field.Conversion is not null)
                {
                    var pair = (field.Conversion.SourceType, field.Conversion.TargetType);
                    if (!conversions.TryAdd(pair, field.Conversion))
                    {
                        // The same type pair may be reused only with the same delegate.
                        if (!ReferenceEquals(conversions[pair].Convert, field.Conversion.Convert) &&
                            !Equals(conversions[pair].Convert?.Method, field.Conversion.Convert?.Method))
                        {
                            _errors.Add(
                                $"Connection {label}: a different conversion {pair.Item1.Name} -> {pair.Item2.Name} is already declared; one conversion exists per type pair.");
                            continue;
                        }
                    }
                }

                fields.Add(new ConnectionFieldMetadata(
                    field.Source.Name,
                    field.Target.Name,
                    sourceType,
                    targetType,
                    field.Conversion?.Method));
            }

            _modelConnections.Add(new ConnectionMetadata(
                connectionId,
                ModelId.Create(modelName),
                target.Id,
                definition.EffectiveName,
                definition.Member,
                fields.Count == 0 ? null : fields));

            foreach (var authorization in definition.Authorizations)
            {
                if (authorization.Predicate is null)
                {
                    _errors.Add(
                        $"Connection {label}: authorization '{authorization.Name}' is not supported. {authorization.PredicateError}");
                    continue;
                }

                var authorizationId = authorization.ExplicitId is { } id
                    ? new AuthorizationId(id)
                    : AuthorizationId.Create($"{modelName}.{definition.EffectiveName}", authorization.Name);

                if (!seenAuthorizations.Add(authorizationId))
                {
                    _errors.Add($"Connection {label}: authorization '{authorization.Name}' is declared more than once.");
                    continue;
                }

                _authorizations.Add(new AuthorizationMetadata(
                    authorizationId,
                    connectionId,
                    authorization.Name,
                    authorization.Name,
                    authorization.ContextType,
                    authorization.ResourceType,
                    authorization.Expression,
                    authorization.Predicate));
            }
        }

        _conversions.AddRange(conversions.Values);
    }

    // ---- entities ---------------------------------------------------------------------

    private void CompileEntity(EntityMapDefinition map, Dictionary<Type, List<MemberRef>> connectionMembers)
    {
        var model = _context.Models[map.ModelType];
        var persistence = _context.Persistence[map.EntityType];
        var entityName = EntityNameOf(model);
        var entityId = EntityId.Create(entityName);
        var storageName = StorageNameOf(persistence);
        var label = $"{map.ModelType.Name} -> {map.EntityType.Name}";

        // Columns come from the persistence configuration plus anything the map or a
        // connection refers to. The persistence definition itself is never mutated.
        var columns = new List<ColumnDefinition>(persistence.Columns);

        // Key convention: with no HasKey, a mapped storage member named Id is the primary key. Its column goes
        // first, where HasKey(x => x.Id) called right after ToTable would have put it.
        MemberRef? conventionKey = null;
        if (persistence.Key is null)
        {
            foreach (var (_, entityMember) in map.Properties)
            {
                if (entityMember.Name == "Id")
                {
                    conventionKey = entityMember;
                    break;
                }
            }

            if (conventionKey is { } idMember && !columns.Exists(c => c.Member == idMember.Name))
                columns.Insert(0, new ColumnDefinition(idMember));
        }

        void EnsureColumn(MemberRef member)
        {
            if (!columns.Exists(c => c.Member == member.Name))
                columns.Add(new ColumnDefinition(member));
        }

        foreach (var (_, entityMember) in map.Properties)
            EnsureColumn(entityMember);
        if (connectionMembers.TryGetValue(map.EntityType, out var extra))
            foreach (var member in extra)
                EnsureColumn(member);

        foreach (var group in columns.GroupBy(c => c.EffectiveColumnName, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            _errors.Add(
                $"{map.EntityType.Name}: members {string.Join(", ", group.Select(c => c.Member))} share the column name '{group.Key}'.");
        }

        var compiled = new CompiledEntity(map.ModelType, map.EntityType, entityName, entityId);
        var columnMetadata = new List<ColumnMetadata>();
        foreach (var column in columns)
        {
            var columnId = ColumnId.Create(storageName, column.EffectiveColumnName);
            compiled.Columns[column.Member] = columnId;
            compiled.ColumnTypes[column.Member] = column.Type;
            columnMetadata.Add(new ColumnMetadata(columnId, column.EffectiveColumnName)
            {
                MaxLength = column.MaxLength,
                IsRequired = column.IsRequired
            });
        }

        var indexedMembers = persistence.Indexes.Select(i => i[0].Name).ToHashSet(StringComparer.Ordinal);
        var fields = new List<FieldMetadata>();
        foreach (var (modelMember, entityMember) in map.Properties)
        {
            model.Dimensions.TryGetValue(modelMember.Name, out var dimension);
            model.FieldAliases.TryGetValue(modelMember.Name, out var aliases);

            fields.Add(new FieldMetadata(
                FieldId.Create(entityName, modelMember.Name),
                modelMember.Name,
                modelMember.Type,
                new ColumnReference(entityId, compiled.Columns[entityMember.Name]),
                dimension,
                indexedMembers.Contains(entityMember.Name),
                aliases is { Count: > 0 } ? aliases : null));
        }

        var mappedMembers = map.Properties.Select(p => p.Model.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var member in model.Dimensions.Keys.Where(k => !mappedMembers.Contains(k)))
            _errors.Add($"{label}: dimension '{member}' refers to a member that is not mapped.");
        foreach (var member in model.FieldAliases.Keys.Where(k => !mappedMembers.Contains(k)))
            _errors.Add($"{label}: aliases on '{member}' refer to a member that is not mapped.");

        ColumnReference? primaryKey = null;
        if ((persistence.Key ?? conventionKey) is not { } key)
        {
            _errors.Add($"{map.EntityType.Name}: no key declared; call HasKey(x => x.Id) or map a member named Id.");
        }
        else
        {
            var keyColumn = compiled.Columns[key.Name];
            primaryKey = new ColumnReference(entityId, keyColumn);
            if (!fields.Exists(f => f.Column?.ColumnId == keyColumn))
                _errors.Add($"{label}: key member '{key.Name}' is not mapped; map the model member that holds it.");
        }

        ColumnReference? temporalColumn = null;
        if (model.IsEvent && model.OccurredAtMember is { } occurredAt)
        {
            var field = fields.Find(f => f.Name == occurredAt);
            if (field is null)
                _errors.Add($"{label}: event timestamp '{occurredAt}' is not mapped.");
            else
                temporalColumn = field.Column;
        }

        _byModel[map.ModelType] = compiled;
        _entities.Add(new EntityMetadata(
            entityId,
            entityName,
            columnMetadata,
            storageName,
            fields,
            primaryKey,
            map.ModelType,
            model.IsEvent,
            temporalColumn,
            model.Aliases.Count > 0 ? model.Aliases.ToArray() : null));
    }

    // ---- relationships ----------------------------------------------------------------

    private void CompileRelationships(List<ConnectionDefinition> connections)
    {
        var relationships = new List<ResolvedRelationship>();

        foreach (var model in _context.Models.Values)
            foreach (var declared in model.Relationships)
                relationships.Add(new ResolvedRelationship(
                    model.ModelType,
                    declared.TargetModelType,
                    declared.Member,
                    declared.InverseMember,
                    ConfigurationExpression.IsCollection(declared.MemberType),
                    declared.Aliases.ToArray(),
                    declared.Name));

        // A relationship that names its inverse also defines the inverse navigation, unless
        // the target model declared that navigation itself (an explicit declaration wins).
        foreach (var model in _context.Models.Values)
            foreach (var declared in model.Relationships)
            {
                if (declared.InverseMember is null)
                    continue;
                if (relationships.Exists(r => r.SourceModel == declared.TargetModelType &&
                                              r.Member == declared.InverseMember))
                    continue;

                relationships.Add(new ResolvedRelationship(
                    declared.TargetModelType,
                    model.ModelType,
                    declared.InverseMember,
                    declared.Member,
                    declared.InverseIsCollection,
                    [],
                    null));
            }

        var used = new HashSet<ConnectionDefinition>();

        foreach (var relationship in relationships)
        {
            var label = relationship.SourceModel.Name + "." + relationship.Member;
            if (!_byModel.TryGetValue(relationship.SourceModel, out var source))
            {
                _errors.Add($"Relationship {label}: {relationship.SourceModel.Name} has no entity map.");
                continue;
            }

            if (!_byModel.TryGetValue(relationship.TargetModel, out var target))
            {
                _errors.Add($"Relationship {label}: target {relationship.TargetModel.Name} has no entity map.");
                continue;
            }

            var matches = new List<(ConnectionDefinition Connection, Direction Direction)>();
            var ambiguous = false;
            foreach (var connection in connections)
            {
                var direction = Match(connection, relationship);
                if (direction == Direction.Ambiguous)
                    ambiguous = true;
                else if (direction != Direction.None)
                    matches.Add((connection, direction));
            }

            if (ambiguous)
            {
                _errors.Add(
                    $"Relationship {label}: a connection between the same model on both ends cannot tell the directions apart; pass the navigation, e.g. HasRelationship<{relationship.SourceModel.Name}, {relationship.TargetModel.Name}>(x => x.{relationship.Member}).");
                continue;
            }

            if (matches.Count == 0 && InferConnection(relationship, relationships) is { } inferred)
            {
                connections.Add(inferred);
                matches.Add((inferred, Match(inferred, relationship)));
            }

            if (matches.Count == 0)
            {
                _errors.Add(
                    $"Relationship {label}: no connection configured and no foreign key found by convention. Add connections.HasRelationship<{relationship.SourceModel.Name}, {relationship.TargetModel.Name}>().Using<...>().HasForeignKey(...).References(...).");
                continue;
            }

            if (matches.Count > 1)
            {
                _errors.Add(
                    $"Relationship {label}: matched by several connections ({string.Join("; ", matches.Select(m => ConnectionName(m.Connection)))}); pass a navigation to tell them apart.");
                continue;
            }

            var (match, matchDirection) = matches[0];
            used.Add(match);

            var connectionSource = _byModel[match.SourceModel];
            var connectionTarget = _byModel[match.TargetModel];
            var foreignKey = match.ForeignKey!.Value;
            var principal = match.References!.Value;

            var foreignKeyRef = KeyRef(match.ForeignKeyOnTarget ? connectionTarget : connectionSource, foreignKey);
            var principalRef = KeyRef(match.ForeignKeyOnTarget ? connectionSource : connectionTarget, principal);

            // Keys always describe each side of the semantic relationship, whichever
            // side physically owns the foreign key.
            var connectionSourceKey = match.ForeignKeyOnTarget ? principalRef : foreignKeyRef;
            var connectionTargetKey = match.ForeignKeyOnTarget ? foreignKeyRef : principalRef;
            var (sourceKey, targetKey) = matchDirection == Direction.Forward
                ? (connectionSourceKey, connectionTargetKey)
                : (connectionTargetKey, connectionSourceKey);

            _relationships.Add(new RelationshipMetadata(
                RelationshipId.Create(source.Name, relationship.Member),
                source.Id,
                target.Id,
                relationship.Name ?? relationship.Member,
                sourceKey,
                targetKey,
                relationship.IsCollection,
                relationship.Aliases.Count > 0 ? relationship.Aliases : null));
        }

        foreach (var connection in connections.Where(c => !used.Contains(c)))
            _errors.Add($"Connection {ConnectionName(connection)} does not match any semantic relationship.");
    }

    /// <summary>
    /// Foreign key convention for a relationship with no explicit connection. The principal key is the mapped member
    /// <c>Id</c>. A single-valued navigation N from S to T keeps its key on S, named N + "Id" or T + "Id"; a collection
    /// navigation from S to T keeps its key on T, named after T's single-valued navigation back to S (N' + "Id") or
    /// S + "Id". The first candidate that is a mapped member of the principal key's type wins. Nothing is inferred
    /// when the two models are related more than once in either direction, or to themselves; those need an explicit
    /// connection naming the navigation.
    /// </summary>
    private ConnectionDefinition? InferConnection(ResolvedRelationship relationship, List<ResolvedRelationship> all)
    {
        var sourceModel = relationship.SourceModel;
        var targetModel = relationship.TargetModel;
        if (sourceModel == targetModel)
            return null;
        if (all.Count(r => r.SourceModel == sourceModel && r.TargetModel == targetModel) != 1 ||
            all.Count(r => r.SourceModel == targetModel && r.TargetModel == sourceModel) > 1)
            return null;
        if (!_byModel.TryGetValue(sourceModel, out var source) || !_byModel.TryGetValue(targetModel, out var target))
            return null;

        var names = new List<string>();
        CompiledEntity foreignKeyOwner;
        CompiledEntity principalOwner;
        if (relationship.IsCollection)
        {
            foreach (var back in all.Where(r => r.SourceModel == targetModel && r.TargetModel == sourceModel && !r.IsCollection))
                names.Add(back.Member + "Id");
            names.Add(sourceModel.Name + "Id");
            foreignKeyOwner = target;
            principalOwner = source;
        }
        else
        {
            names.Add(relationship.Member + "Id");
            names.Add(targetModel.Name + "Id");
            foreignKeyOwner = source;
            principalOwner = target;
        }

        if (!principalOwner.ColumnTypes.TryGetValue("Id", out var principalType))
            return null;

        foreach (var name in names)
        {
            if (foreignKeyOwner.ColumnTypes.TryGetValue(name, out var foreignKeyType) &&
                (Nullable.GetUnderlyingType(foreignKeyType) ?? foreignKeyType) ==
                (Nullable.GetUnderlyingType(principalType) ?? principalType))
            {
                return new ConnectionDefinition(sourceModel, targetModel, null)
                {
                    SourceEntity = source.EntityType,
                    TargetEntity = target.EntityType,
                    ForeignKeyOnTarget = relationship.IsCollection,
                    ForeignKey = new MemberRef(name, foreignKeyType),
                    References = new MemberRef("Id", principalType)
                };
            }
        }

        return null;
    }

    private static ColumnReference KeyRef(CompiledEntity entity, MemberRef member) =>
        new(entity.Id, entity.Columns[member.Name]);

    private static Direction Match(ConnectionDefinition connection, ResolvedRelationship relationship)
    {
        var forward = relationship.SourceModel == connection.SourceModel &&
                      relationship.TargetModel == connection.TargetModel &&
                      (connection.Navigation is null || connection.Navigation == relationship.Member);

        var reverse = relationship.SourceModel == connection.TargetModel &&
                      relationship.TargetModel == connection.SourceModel &&
                      (connection.Navigation is null || connection.Navigation == relationship.OppositeMember);

        return (forward, reverse) switch
        {
            (true, true) => Direction.Ambiguous,
            (true, false) => Direction.Forward,
            (false, true) => Direction.Reverse,
            _ => Direction.None
        };
    }

    private enum Direction
    {
        None,
        Forward,
        Reverse,
        Ambiguous
    }

    private sealed record ResolvedRelationship(
        Type SourceModel,
        Type TargetModel,
        string Member,
        string? OppositeMember,
        bool IsCollection,
        IReadOnlyList<AliasDeclaration> Aliases,
        string? Name);

    private sealed class CompiledEntity(Type modelType, Type entityType, string name, EntityId id)
    {
        public Type ModelType { get; } = modelType;
        public Type EntityType { get; } = entityType;
        public string Name { get; } = name;
        public EntityId Id { get; } = id;

        /// <summary>Storage member name to physical column id.</summary>
        public Dictionary<string, ColumnId> Columns { get; } = new(StringComparer.Ordinal);

        /// <summary>Storage member name to CLR type, for the same members as <see cref="Columns"/>.</summary>
        public Dictionary<string, Type> ColumnTypes { get; } = new(StringComparer.Ordinal);
    }
}
