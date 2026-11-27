package com.foundgine.samples.supplychain.jpa;

import com.foundgine.providers.storage.orm.jpa.FoundgineOrm;
import com.foundgine.providers.storage.orm.jpa.JpaEntities;
import com.foundgine.samples.supplychain.Authorization;

import jakarta.persistence.EntityManager;
import jakarta.persistence.EntityManagerFactory;
import jakarta.persistence.Persistence;

/**
 * JPA + PostgreSQL execution for the starter, on the fluent {@link FoundgineOrm} configuration
 * ({@link SupplyChainOrm}). Foundgine, not JPA, owns planning and authorization: every read is
 * authorize -> plan -> ExecutionIR -> JPA Criteria, and the generated models mean no reflective entity access.
 */
public final class SupplyChainJpa implements AutoCloseable {
    private final EntityManagerFactory emf;
    private final FoundgineOrm<Authorization.Context> orm = SupplyChainOrm.build();

    /** Applies pending Flyway migrations first (see {@link SupplyChainDatabase#migrateFromEnvironment}). */
    public SupplyChainJpa() {
        SupplyChainDatabase.migrateFromEnvironment();
        this.emf = Persistence.createEntityManagerFactory("foundgine-supply-chain", SupplyChainDatabase.jpaProperties());
    }

    /** The configured ORM: reader, typed repositories, generic entity gateway, dry-run planning. */
    public FoundgineOrm<Authorization.Context> orm() {
        return orm;
    }

    /**
     * Generic, name-addressed access to every mapped entity for {@code caller}. Reads go through the same
     * authorization as {@code orm().reader(em)}; writes are mechanical, so authorize the capability before calling them.
     * The caller owns {@code em} and closes it.
     */
    public JpaEntities entities(Authorization.Context caller, EntityManager em) {
        return orm.entities(caller, em);
    }

    public EntityManager openEntityManager() {
        return emf.createEntityManager();
    }

    @Override
    public void close() {
        if (emf.isOpen()) emf.close();
    }
}
