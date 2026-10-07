package com.gravicode.marbots;

import java.util.Map;

/** Outcomes of the tasks that loaded one skill version. */
public record SkillStats(String name, String version, long loads, long successes, long failures) {
    public long runs() { return successes + failures; }

    public double successRate() { return runs() == 0 ? 0 : (double) successes / runs(); }

    static SkillStats from(Object v) {
        if (!(v instanceof Map<?, ?>)) return null;
        Map<String, Object> d = W.obj(v);
        return new SkillStats(W.str(d, "name"), W.str(d, "version"), W.num(d, "loads"), W.num(d, "successes"), W.num(d, "failures"));
    }
}
