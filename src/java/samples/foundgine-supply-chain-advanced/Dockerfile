# Advanced Java sample: HTTP / GraphQL / MCP server (AdvancedMcpServer). Build context is src/java:
#   docker build -f samples/foundgine-supply-chain-advanced/Dockerfile -t foundgine-supplychain-advanced-java:3.4.2 .
FROM maven:3.10.0-eclipse-temurin-27 AS build
WORKDIR /workspace
COPY . .
RUN mvn -q -pl samples/foundgine-supply-chain-advanced -am -DskipTests package \
 && mvn -q -pl samples/foundgine-supply-chain-advanced dependency:copy-dependencies -DoutputDirectory=/workspace/out/lib

FROM eclipse-temurin:27-jre
WORKDIR /app
COPY --from=build /workspace/samples/foundgine-supply-chain-advanced/target/classes ./classes
COPY --from=build /workspace/out/lib ./lib
# Bind to all interfaces so Kubernetes probes and Services can reach the server (the default is localhost).
ENV FOUNDGINE_MCP_HOST=0.0.0.0 FOUNDGINE_MCP_PORT=4783
EXPOSE 4783
ENTRYPOINT ["java","-cp","/app/classes:/app/lib/*","com.foundgine.samples.supplychain.advanced.mcp.AdvancedMcpServer"]
