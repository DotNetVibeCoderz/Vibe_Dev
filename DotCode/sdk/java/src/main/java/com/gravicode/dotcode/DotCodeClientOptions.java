package com.gravicode.dotcode;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** How the client starts the DotCode server. */
public final class DotCodeClientOptions {
    String cliPath;
    String cwd;
    final Map<String, String> env = new LinkedHashMap<>();
    final List<String> cliArgs = new ArrayList<>();

    /** dotcode executable or dotcode.dll (default: DOTCODE_CLI_PATH or "dotcode" on PATH). */
    public DotCodeClientOptions setCliPath(String v) { cliPath = v; return this; }
    /** Working directory of the server and default for sessions. */
    public DotCodeClientOptions setCwd(String v) { cwd = v; return this; }
    public DotCodeClientOptions putEnv(String key, String value) { env.put(key, value); return this; }
    /** Extra argument for {@code dotcode serve}. */
    public DotCodeClientOptions addCliArg(String arg) { cliArgs.add(arg); return this; }
}
