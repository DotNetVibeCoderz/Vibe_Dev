package com.gravicode.dotcode;

/** Answers one {@link UserQuestion}: option label(s) (comma-separated for multi-select) or free text. */
public record UserQuestionAnswer(String question, String answer) {}
