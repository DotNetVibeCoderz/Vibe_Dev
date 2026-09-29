package com.gravicode.dotcode;

/**
 * Metadata of one inline tool parameter for {@link ToolDefinition#from(String, String, Param, java.util.function.Function)}.
 *
 * <pre>{@code Param<String> city = Param.of(String.class, "city", "City name");}</pre>
 */
public record Param<T>(Class<T> type, String name, String description, boolean required) {
    public static <T> Param<T> of(Class<T> type, String name, String description) {
        return new Param<>(type, name, description, true);
    }

    /** An optional parameter (null when the model omits it). */
    public static <T> Param<T> optional(Class<T> type, String name, String description) {
        return new Param<>(type, name, description, false);
    }
}
