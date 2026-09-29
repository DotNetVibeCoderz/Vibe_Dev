package com.gravicode.dotcode;

/** Wire protocol of a model provider. */
public enum ProviderType {
    ANTHROPIC("anthropic"),
    OPENAI("openai"),
    AZURE("azure"),
    GEMINI("gemini"),
    DEEPSEEK("deepseek"),
    OLLAMA("ollama"),
    OPENAI_COMPATIBLE("openai-compatible"),
    BEDROCK("bedrock"),
    VERTEX("vertex"),
    VERTEX_GEMINI("vertex-gemini"),
    MOCK("mock");

    private final String value;

    ProviderType(String value) { this.value = value; }

    /** The protocol value. */
    public String value() { return value; }

    static ProviderType fromValue(String v) {
        for (ProviderType x : values()) if (x.value.equals(v)) return x;
        return null;
    }

    @Override
    public String toString() { return value; }
}
