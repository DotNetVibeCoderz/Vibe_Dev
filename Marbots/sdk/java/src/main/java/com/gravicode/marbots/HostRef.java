package com.gravicode.marbots;

/** {@code hostRef} values besides a registered host id. */
public final class HostRef {
    private HostRef() {}

    /** Run the bot's tools on the server itself. */
    public static final String LOCAL = "local-default";
    /** Let placement choose a computer per thread. */
    public static final String AUTO = "auto";
}
