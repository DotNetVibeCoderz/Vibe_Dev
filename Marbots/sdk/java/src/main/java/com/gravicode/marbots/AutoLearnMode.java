package com.gravicode.marbots;

/** Optional learning after tasks. */
public enum AutoLearnMode {
    OFF("Off"),
    MEMORY_ONLY("MemoryOnly"),
    SUGGEST_SKILLS("SuggestSkills");

    private final String wire;

    AutoLearnMode(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static AutoLearnMode from(String value) {
        for (AutoLearnMode v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
