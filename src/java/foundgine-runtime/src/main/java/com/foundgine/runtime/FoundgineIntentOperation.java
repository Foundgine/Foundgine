package com.foundgine.runtime;

/** Operation requested at the application-owned, protocol-neutral intent boundary. */
public enum FoundgineIntentOperation {
    /** Authorize, plan and execute the supplied intent. */
    EXECUTE,
    /** Resolve and explain the supplied intent without executing provider work. */
    EXPLAIN,
    /** Describe the capabilities visible to the supplied trusted caller context. */
    CAPABILITIES
}
