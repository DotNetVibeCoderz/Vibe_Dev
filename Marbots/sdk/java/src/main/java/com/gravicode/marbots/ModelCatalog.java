package com.gravicode.marbots;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * The workspace default model, the provider/model choices and the named profiles.
 *
 * @param defaultModel the workspace default provider/model
 */
public record ModelCatalog(String defaultModel, List<String> choices, List<Profile> profiles) {
    /** A named model profile. */
    public record Profile(String name, String provider, String model, List<String> fallbacks) {}

    static ModelCatalog from(Map<String, Object> d) {
        List<Profile> ps = new ArrayList<>();
        for (Map<String, Object> p : W.objs(d.get("profiles")))
            ps.add(new Profile(W.str(p, "name"), W.str(p, "provider"), W.str(p, "model"), W.strs(p, "fallbacks")));
        return new ModelCatalog(W.str(d, "default"), W.strs(d, "choices"), List.copyOf(ps));
    }
}
