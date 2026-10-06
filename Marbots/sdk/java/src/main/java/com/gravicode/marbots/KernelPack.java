package com.gravicode.marbots;

/** Built-in tool packs a bot can enable. */
public enum KernelPack {
    FILES("files"),
    SEARCH("search"),
    SHELL("shell"),
    WEB("web"),
    MEMORY("memory"),
    TODO("todo"),
    AGENTS("agents");

    private final String wire;

    KernelPack(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static KernelPack from(String value) {
        for (KernelPack v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
