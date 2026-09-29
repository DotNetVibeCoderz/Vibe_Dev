package com.gravicode.dotcode;

/** Built-in tool names (for {@link SessionConfig#setAvailableTools} and permission rules). */
public enum BuiltinTool {
    READ("Read"),
    WRITE("Write"),
    EDIT("Edit"),
    NOTEBOOK_EDIT("NotebookEdit"),
    GLOB("Glob"),
    GREP("Grep"),
    BASH("Bash"),
    POWERSHELL("PowerShell"),
    BASH_OUTPUT("BashOutput"),
    KILL_SHELL("KillShell"),
    WEB_FETCH("WebFetch"),
    WEB_SEARCH("WebSearch"),
    TODO_WRITE("TodoWrite"),
    AGENT("Agent"),
    SKILL("Skill"),
    ASK_USER_QUESTION("AskUserQuestion"),
    EXIT_PLAN_MODE("ExitPlanMode"),
    LSP("LSP");

    private final String value;

    BuiltinTool(String value) { this.value = value; }

    /** The protocol value. */
    public String value() { return value; }

    static BuiltinTool fromValue(String v) {
        for (BuiltinTool x : values()) if (x.value.equals(v)) return x;
        return null;
    }

    /** A permission rule for this tool, e.g. {@code BuiltinTool.BASH.rule("npm test:*")} → {@code Bash(npm test:*)}. */
    public String rule(String specifier) { return value + "(" + specifier + ")"; }

    @Override
    public String toString() { return value; }
}
