package com.foundgine.samples.supplychain.domain;

import com.foundgine.providers.aot.FoundgineOrmEntity;
import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.Id;
import jakarta.persistence.Table;

import java.math.BigDecimal;

/** A catalog product. Both the Foundgine semantic model and the JPA entity. */
@FoundgineOrmEntity
@Entity
@Table(name = "products")
public class Product {
    @Id @Column(name = "product_id")
    private Integer id;
    @Column(name = "sku")
    private String sku;
    @Column(name = "product_name")
    private String name;
    @Column(name = "category")
    private String category;
    @Column(name = "safety_stock")
    private BigDecimal safetyStock;

    public Integer getId() { return id; }
    public void setId(Integer id) { this.id = id; }

    public String getSku() { return sku; }
    public void setSku(String sku) { this.sku = sku; }

    public String getName() { return name; }
    public void setName(String name) { this.name = name; }

    public String getCategory() { return category; }
    public void setCategory(String category) { this.category = category; }

    public BigDecimal getSafetyStock() { return safetyStock; }
    public void setSafetyStock(BigDecimal safetyStock) { this.safetyStock = safetyStock; }
}
