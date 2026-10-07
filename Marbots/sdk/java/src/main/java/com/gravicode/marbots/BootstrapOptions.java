package com.gravicode.marbots;

import java.util.LinkedHashMap;
import java.util.Map;

/** SSH bootstrap of an agent host. Build with {@link #of(String, String, String)}. The password/key is used once. */
public final class BootstrapOptions {
    private final Map<String, Object> wire = new LinkedHashMap<>();

    private BootstrapOptions(String host, String user, String serverUrl) {
        wire.put("host", host);
        wire.put("port", 22);
        wire.put("user", user);
        wire.put("name", host);
        wire.put("serverUrl", serverUrl);
        wire.put("updateOnly", false);
    }

    /**
     * @param serverUrl address the new host uses to reach this server (LAN address, not localhost)
     */
    public static BootstrapOptions of(String host, String user, String serverUrl) { return new BootstrapOptions(host, user, serverUrl); }

    public BootstrapOptions name(String v) { wire.put("name", v); return this; }
    public BootstrapOptions password(String v) { wire.put("password", v); return this; }
    public BootstrapOptions privateKey(String v) { wire.put("privateKey", v); return this; }
    public BootstrapOptions port(int v) { wire.put("port", v); return this; }
    /** Replace the binary and restart, keeping the enrollment. */
    public BootstrapOptions updateOnly(boolean v) { wire.put("updateOnly", v); return this; }

    Map<String, Object> toWire() { return new LinkedHashMap<>(wire); }
}
