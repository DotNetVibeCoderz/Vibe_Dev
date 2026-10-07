package com.gravicode.marbots;

import java.util.Map;

/** Learning evaluation of one skill: outcomes of its current version and a verdict. */
public record SkillEvaluation(String name, String version, boolean pending, SkillStats current, String previousVersion,
                              SkillStats previous, SkillVerdict verdict, String reason) {
    public boolean canRollback() { return previousVersion != null; }

    static SkillEvaluation from(Map<String, Object> d) {
        SkillStats current = SkillStats.from(d.get("current"));
        if (current == null) current = new SkillStats(W.str(d, "name"), W.str(d, "version"), 0, 0, 0);
        return new SkillEvaluation(W.str(d, "name"), W.str(d, "version"), W.bool(d, "pending"), current,
            W.opt(d, "previousVersion"), SkillStats.from(d.get("previous")), SkillVerdict.from(W.str(d, "verdict")), W.str(d, "reason"));
    }
}
