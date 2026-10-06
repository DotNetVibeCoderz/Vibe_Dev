package com.gravicode.marbots;

import java.util.Map;

/**
 * A bot's model setting and the model it actually runs on.
 *
 * @param effective the provider/model the bot runs on
 */
public record BotModelInfo(String botId, String setting, String effective, boolean usesDefault, String warning) {
    static BotModelInfo from(Map<String, Object> d) {
        return new BotModelInfo(W.str(d, "botId"), W.str(d, "setting"), W.str(d, "effective"), W.bool(d, "usesDefault"), W.opt(d, "warning"));
    }
}
