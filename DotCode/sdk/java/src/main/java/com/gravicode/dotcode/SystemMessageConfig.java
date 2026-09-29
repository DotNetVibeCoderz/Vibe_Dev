package com.gravicode.dotcode;

/** Customizes the system prompt: {@link #append} to the DotCode prompt or {@link #replace} it. */
public record SystemMessageConfig(boolean replace, String content) {
    public static SystemMessageConfig append(String content) { return new SystemMessageConfig(false, content); }
    public static SystemMessageConfig replace(String content) { return new SystemMessageConfig(true, content); }
}
