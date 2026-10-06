package com.gravicode.marbots;

import java.util.Map;

/** An installed SKILL.md package. */
public record SkillInfo(String name, String version, String description, String trust, String source, boolean pending) {
    static SkillInfo from(Map<String, Object> d) {
        return new SkillInfo(W.str(d, "name"), W.str(d, "version"), W.str(d, "description"), W.str(d, "trust"), W.str(d, "source"), W.bool(d, "pending"));
    }
}
