package com.gravicode.dotcode;

import java.util.List;
import java.util.Map;

/**
 * A named model provider (BYOK). Values may reference environment variables, e.g. {@code "${env:MY_KEY}"}.
 *
 * <pre>{@code new ProviderConfig(ProviderType.OLLAMA).setBaseUrl("http://localhost:11434")}</pre>
 */
public final class ProviderConfig {
    private final ProviderType type;
    private String baseUrl, apiKey, api, profile, region, awsProfile, project, credentialsFile, auth, tenantId, clientId, clientSecret, script;
    private Map<String, String> headers;
    private List<String> models;
    private Integer timeoutSeconds, numCtx;

    public ProviderConfig(ProviderType type) { this.type = type; }

    public ProviderType getType() { return type; }
    public ProviderConfig setBaseUrl(String v) { baseUrl = v; return this; }
    public ProviderConfig setApiKey(String v) { apiKey = v; return this; }
    /** OpenAI family: "responses" or "chat". */
    public ProviderConfig setApi(String v) { api = v; return this; }
    public ProviderConfig setHeaders(Map<String, String> v) { headers = v; return this; }
    /** Quirk profile for OpenAI-compatible servers (deepseek, openrouter, lmstudio, vllm, litellm, groq, together, azure). */
    public ProviderConfig setProfile(String v) { profile = v; return this; }
    /** Models to advertise when the API cannot list them. */
    public ProviderConfig setModels(List<String> v) { models = v; return this; }
    public ProviderConfig setTimeoutSeconds(int v) { timeoutSeconds = v; return this; }
    /** Ollama context window. */
    public ProviderConfig setNumCtx(int v) { numCtx = v; return this; }
    /** AWS region (bedrock) or Google Cloud location (vertex). */
    public ProviderConfig setRegion(String v) { region = v; return this; }
    public ProviderConfig setAwsProfile(String v) { awsProfile = v; return this; }
    /** Google Cloud project (vertex). */
    public ProviderConfig setProject(String v) { project = v; return this; }
    public ProviderConfig setCredentialsFile(String v) { credentialsFile = v; return this; }
    /** Azure: use Microsoft Entra ID tokens instead of an API key (null values fall back to the environment). */
    public ProviderConfig setEntraAuth(String tenantId, String clientId, String clientSecret) {
        this.auth = "entra";
        this.tenantId = tenantId;
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        return this;
    }
    /** Scripted provider (tests): path to a script file. */
    public ProviderConfig setScript(String v) { script = v; return this; }

    Map<String, Object> toWire() {
        return Wire.map("type", type.value(), "baseUrl", baseUrl, "apiKey", apiKey, "api", api, "headers", headers,
                "profile", profile, "models", models, "timeoutSeconds", timeoutSeconds, "numCtx", numCtx, "region", region,
                "awsProfile", awsProfile, "project", project, "credentialsFile", credentialsFile, "auth", auth,
                "tenantId", tenantId, "clientId", clientId, "clientSecret", clientSecret, "script", script);
    }
}
