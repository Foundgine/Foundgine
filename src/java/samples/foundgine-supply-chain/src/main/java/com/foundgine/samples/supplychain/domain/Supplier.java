package com.foundgine.samples.supplychain.domain;

import com.foundgine.providers.aot.FoundgineOrmEntity;
import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.Id;
import jakarta.persistence.Table;

import java.math.BigDecimal;

/** A tenant-owned supplier. Both the Foundgine semantic model and the JPA entity. */
@FoundgineOrmEntity
@Entity
@Table(name = "suppliers")
public class Supplier {
    @Id @Column(name = "supplier_id")
    private Integer id;
    @Column(name = "supplier_name")
    private String name;
    @Column(name = "country")
    private String country;
    @Column(name = "risk_score")
    private BigDecimal riskScore;
    @Column(name = "tenant_id")
    private String tenantId;

    public Integer getId() { return id; }
    public void setId(Integer id) { this.id = id; }

    public String getName() { return name; }
    public void setName(String name) { this.name = name; }

    public String getCountry() { return country; }
    public void setCountry(String country) { this.country = country; }

    public BigDecimal getRiskScore() { return riskScore; }
    public void setRiskScore(BigDecimal riskScore) { this.riskScore = riskScore; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }
}
