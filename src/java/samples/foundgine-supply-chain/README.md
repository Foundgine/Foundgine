# Foundgine Supply Chain (Java) — Starter Sample

The Java counterpart to [`Foundgine.SupplyChain`](../../../csharp/samples/Foundgine.SupplyChain/README.md), on the same
fluent pattern as the C# starter. Each domain class is both the semantic model and the JPA entity: there are no
separate records, storage classes or `@FoundgineEntity` annotations. It is a smaller scope than the C# starter
(three entities; no MCP tools or order workflows).

**Caller → application → fluent semantic model → authorization → Foundgine planning/execution → JPA → PostgreSQL**

> **Verification note.** Run `mvn verify` (below) before relying on changes: the Flyway Java integration and the
> Hibernate/JPA path need a local build and PostgreSQL.

## Layout

| Path | Role |
| --- | --- |
| `domain/` | `Supplier`, `Warehouse`, `Product`: domain classes and JPA entities in one (`@FoundgineOrmEntity` generates the ORM maps and direct-access models). |
| `model/SupplyChainFoundgineModel.java` | The single registration point (`FoundgineModel`), used by the application and by migration generation. |
| `model/SemanticConfigurations.java` | `FoundgineModelConfiguration<T>`: names, aliases (`Vendor`, `Seller`), dimensions. |
| `model/PersistenceConfigurations.java` | `FoundginePersistenceConfiguration<T>`: authoritative tables, columns, keys and indexes. |
| `model/EntityMaps.java` | `FoundgineEntityMap<T, T>`: one-to-one member maps (Java has no `IdentityEntityMap`). |
| `jpa/SupplyChainOrm.java` | `FoundgineOrm` configuration: fluent metadata + generated maps/models + mandatory authorization. |
| `jpa/SupplyChainPolicy.java` | Tenant predicate, `riskScore` field rule, customer entity limits; writes and relationships denied. |
| `jpa/SupplyChainDatabase.java` | Connection settings and Flyway migrations. |
| `application/SupplyChainReader.java` | Semantic reads with method references (the Java twin of the C# lambda reader). |
| `application/SupplyChainCrud.java` | Create/read/update/delete for the three entities, authorization-gated. |
| `src/main/resources/db/migration/V20261114161434__InitialCreate.sql` | Generated Flyway schema migration. |
| `src/main/foundgine/migrations/model-snapshot.json` | Snapshot of the fluent persistence model used for drift detection. |
| `src/main/foundgine/migrations/history/` | Generator history and rollback metadata. |
| `migrations.sh` / `migrations.cmd` | Cross-platform wrappers for the EF-style migration CLI. |
| `foundgine-migrations.properties` | Model class and demo-data location for the CLI. |

Table and column names appear in two places: `model/PersistenceConfigurations.java` (drives migrations and
Foundgine) and the JPA annotations in `domain/` (read by Hibernate). Unlike the C# EF provider, the Java JPA
provider does not derive its mapping from the fluent model, so `SupplyChainOrmTest` fails if the two differ.

## Reads with method references

```java
var reader = new SupplyChainReader(orm.reader(em), caller);
var rows = reader.from(Supplier.class)
        .eq(Supplier::getCountry, "US")
        .select(Supplier::getId, Supplier::getName, Supplier::getRiskScore)
        .orderBy(Supplier::getName, SemanticSortDirection.ASC)
        .rows();
```

Java lambdas expose no expression trees, so only getter **method references** are accepted (a plain
lambda is rejected). Each becomes a semantic field name. Every read goes through authorize → plan → ExecutionIR →
JPA, so the caller's tenant predicate and field rules apply whatever the filter says.

## CRUD

`SupplyChainCrud` wraps the generic `JpaEntities` gateway:

- **Read/list** are gated by Foundgine (role, tenant, field rules).
- **Create/update/delete** need `Authorization.canWrite` (a writing role and not read-only). Created
  suppliers/warehouses are stamped with the caller's tenant; `tenantId` cannot be changed; update/delete affect only
  rows the caller can read (0 rows otherwise).

## Migrations (EF-style model-derived migrations)

The starter is the reference sample for `foundgine-migrations`. The fluent persistence model in
`model/PersistenceConfigurations.java` is the schema source of truth. The migrations CLI builds
`SupplyChainFoundgineModel` (set in `foundgine-migrations.properties`), diffs its schema against the committed
snapshot, and writes a versioned Flyway migration, rollback SQL and an updated snapshot.

From this sample directory:

```bash
# Add a migration after changing the fluent persistence model
./migrations.sh migrations add AddSupplierEmail

# Review migration SQL and rollback SQL, then inspect model drift
./migrations.sh migrations list
./migrations.sh migrations has-pending-model-changes

# Apply pending Flyway migrations and inspect database history
./migrations.sh database update
./migrations.sh database status

# Print the SQL after a given version (omit the version for the full script)
./migrations.sh migrations script 20261114161434

# Remove the newest, unapplied migration and restore its prior snapshot
./migrations.sh migrations remove
```

On Windows, use `migrations.cmd` with the same arguments. The CLI reads
`foundgine-migrations.properties`; the model class is configured there, while connection settings
can be supplied through `FOUNDGINE_JDBC_URL`, `FOUNDGINE_JDBC_USER` and
`FOUNDGINE_JDBC_PASSWORD` (the wrapper also accepts the sample's `SUPPLYCHAIN_*` variables).

Generated files are written under `src/main/resources/db/migration`, with rollback scripts and
snapshot/history under `src/main/foundgine/migrations`. **Always review generated SQL before
applying it.** Drops and other destructive changes produce warnings; the generated rollback is a
review aid, not a substitute for a tested backup/restore plan.

Hibernate remains in validate-only mode and does not create or mutate the schema. Demo data is
separate from versioned schema migrations and is configured as a repeatable Flyway location.
Existing databases initialized from the former handwritten V1 have a different Flyway history;
for this sample, recreate the local development database volume before using the generated
baseline (for example, `docker compose down -v` followed by `docker compose up -d`).

The starter currently declares three tables, primary keys and indexes. The shared compiler and
PostgreSQL schema pipeline also support identity, defaults, foreign keys, CHECK constraints,
PostgreSQL extensions and index methods/operator classes; add those declarations to the fluent
persistence configurations when the starter schema needs them. The committed migration remains
unchanged until generated SQL has been reviewed and verified against PostgreSQL 16.

## Run

```bash
cd src/java/samples/foundgine-supply-chain
docker compose up -d          # empty PostgreSQL on localhost:4429; the app migrates it
cd ../..
mvn -pl samples/foundgine-supply-chain -am compile exec:java \
  -Dexec.mainClass=com.foundgine.samples.supplychain.Main
mvn -pl samples/foundgine-supply-chain -am test      # no database needed
```

Connection settings: `SUPPLYCHAIN_JDBC_URL`, `SUPPLYCHAIN_JDBC_USER`, `SUPPLYCHAIN_JDBC_PASSWORD`. If PostgreSQL is not
reachable, `Main` prints a "JPA demo skipped" line instead of failing.

## Tests

`SimpleSchemaModelParityTest` locks the three tables and their key columns/indexes; `ModelMigrationsTest` checks
that no model change is missing a migration. `SupplyChainOrmTest` (fail-closed validation, tenant isolation, field
authorization, JPA annotation parity, plan fingerprints, against the in-memory ORM source),
`SupplyChainPolicyWriteTest` (write rules) and `SupplyChainApplicationTest` (method-reference names, write gate)
cover the application path.

## Next sample

For the complete MCP and mutation path, see [`../foundgine-supply-chain-advanced`](../foundgine-supply-chain-advanced/README.md).
