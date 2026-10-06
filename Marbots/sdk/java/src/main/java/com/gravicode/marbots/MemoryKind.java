package com.gravicode.marbots;

/** Kind of long-term memory. */
public enum MemoryKind {
    SEMANTIC("Semantic"),
    EPISODIC("Episodic"),
    PROCEDURAL("Procedural"),
    RELATIONAL("Relational"),
    ARTIFACT("Artifact");

    private final String wire;

    MemoryKind(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static MemoryKind from(String value) {
        for (MemoryKind v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
