package com.gravicode.marbots;

/** The learning evaluation's conclusion about a skill. */
public enum SkillVerdict {
    COLLECTING_EVIDENCE("CollectingEvidence"),
    HEALTHY("Healthy"),
    UNDERPERFORMING("Underperforming"),
    ROLLBACK_RECOMMENDED("RollbackRecommended"),
    READY_TO_PROMOTE("ReadyToPromote"),
    DISCARD_RECOMMENDED("DiscardRecommended");

    private final String wire;

    SkillVerdict(String wire) { this.wire = wire; }

    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static SkillVerdict from(String value) {
        for (SkillVerdict v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
