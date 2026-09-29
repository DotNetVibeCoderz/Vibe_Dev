package com.gravicode.dotcode;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/** A question asked by the model through the AskUserQuestion tool. */
public record UserQuestion(String question, String header, List<Option> options, boolean multiSelect) {
    /** One choice. */
    public record Option(String label, String description) {}

    @SuppressWarnings("unchecked")
    static UserQuestion fromWire(Map<String, Object> m) {
        List<Option> options = new ArrayList<>();
        for (Object o : (List<Object>) m.getOrDefault("options", List.of()))
            if (o instanceof Map<?, ?> om) options.add(new Option(Wire.str((Map<String, Object>) om, "label"), Wire.str((Map<String, Object>) om, "description")));
        return new UserQuestion(Wire.str(m, "question"), Wire.str(m, "header"), options, Boolean.TRUE.equals(m.get("multiSelect")));
    }
}
