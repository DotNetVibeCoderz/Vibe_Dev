package com.gravicode.marbots;

/** Builds a bot's model setting: the workspace default, a provider/model pair, or a named profile. */
public final class ModelRef {
    private ModelRef() {}

    /** Follow the workspace default model. */
    public static final String DEFAULT = "default";

    /** A direct provider/model pair, e.g. {@code ModelRef.of("azure", "gpt-5.6-luna")}. */
    public static String of(String provider, String model) {
        if (provider == null || provider.isBlank() || model == null || model.isBlank() || provider.contains("/"))
            throw new IllegalArgumentException("provider and model are required; provider cannot contain '/'");
        return provider + "/" + model;
    }

    /** A named model profile configured in Settings. */
    public static String profile(String name) { return name; }
}
